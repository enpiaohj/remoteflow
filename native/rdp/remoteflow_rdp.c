/*
 * remoteflow_rdp —— FreeRDP 3.x 的极小 C ABI 封装（方案 v1.3 §8.E / §7.2）。
 *
 * 只暴露 RemoteFlow macOS 端「应用内嵌入式 RDP」所需：连接（带凭据、忽略证书）、
 * BGRX32 帧回调、指针 / 键盘输入、断开。所有 FreeRDP 结构体复杂度留在 C 侧，
 * 托管侧（RemoteFlow.Protocol.Rdp.Mac）只 P/Invoke 这十来个函数。
 */
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#include <freerdp/freerdp.h>
#include <freerdp/client.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/input.h>
#include <freerdp/settings.h>
#include <freerdp/codec/color.h>
#include <freerdp/client/channels.h>
#include <freerdp/client/disp.h>
#include <freerdp/channels/disp.h>
#include <freerdp/channels/channels.h>
#include <winpr/synch.h>
#include <winpr/thread.h>

/* state: 0=connecting 1=connected 2=disconnected 3=failed */
typedef void (*rf_frame_cb)(void* user, const uint8_t* bgrx, int w, int h, int stride);
typedef void (*rf_state_cb)(void* user, int state, const char* message);
/* 证书校验：返回 0=拒绝 1=接受并记录 2=仅本次接受。托管侧据「首次记录 / 变化强警告」决策。 */
typedef int (*rf_cert_cb)(void* user, const char* host, int port, const char* common_name,
                          const char* fingerprint, int changed);

typedef struct
{
	rdpContext context; /* 必须第一个 */
	rf_frame_cb frame_cb;
	rf_state_cb state_cb;
	rf_cert_cb cert_cb;
	void* user;
	volatile int running;
	HANDLE thread;
	/* Display Control 动态通道：真正把新分辨率发给服务端的唯一通路。
	   只改 settings 里的 DesktopWidth/Height 服务端根本收不到。 */
	DispClientContext* disp;
	int pending_w;
	int pending_h;
} rfContext;

static void rf_send_monitor_layout(rfContext* rf, int width, int height)
{
	if (!rf->disp || !rf->disp->SendMonitorLayout || width <= 0 || height <= 0)
		return;

	DISPLAY_CONTROL_MONITOR_LAYOUT layout = { 0 };
	layout.Flags = DISPLAY_CONTROL_MONITOR_PRIMARY;
	layout.Left = 0;
	layout.Top = 0;
	layout.Width = (UINT32)width;
	layout.Height = (UINT32)height;
	layout.Orientation = ORIENTATION_LANDSCAPE;
	layout.PhysicalWidth = 0;
	layout.PhysicalHeight = 0;
	layout.DesktopScaleFactor = 100;
	layout.DeviceScaleFactor = 100;
	rf->disp->SendMonitorLayout(rf->disp, 1, &layout);
}

static void rf_on_channel_connected(void* ctx, const ChannelConnectedEventArgs* e)
{
	rfContext* rf = (rfContext*)ctx;
	if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
	{
		rf->disp = (DispClientContext*)e->pInterface;
		/* 通道就绪时若已有待发的尺寸（连上后视图先变过），立刻补发一次。 */
		if (rf->pending_w > 0 && rf->pending_h > 0)
			rf_send_monitor_layout(rf, rf->pending_w, rf->pending_h);
	}
}

static void rf_on_channel_disconnected(void* ctx, const ChannelDisconnectedEventArgs* e)
{
	rfContext* rf = (rfContext*)ctx;
	if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
		rf->disp = NULL;
}

static BOOL rf_pre_connect(freerdp* instance)
{
	rdpSettings* s = instance->context->settings;
	freerdp_settings_set_bool(s, FreeRDP_SoftwareGdi, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_SupportGraphicsPipeline, FALSE);

	/* 挂上 Display Control 动态通道（"disp"）—— 动态改分辨率全靠它。
	   不显式加的话通道不会建立，rf_rdp_resize 就只是改了本地一个数字。 */
	const char* disp_args[] = { "disp" };
	freerdp_client_add_dynamic_channel(s, 1, disp_args);

	if (!freerdp_client_load_addins(instance->context->channels, s))
		return FALSE;

	return TRUE;
}

static BOOL rf_end_paint(rdpContext* context)
{
	rfContext* rf = (rfContext*)context;
	rdpGdi* gdi = context->gdi;
	if (gdi && gdi->primary_buffer && rf->frame_cb)
		rf->frame_cb(rf->user, gdi->primary_buffer, gdi->width, gdi->height, (int)gdi->stride);
	return TRUE;
}

static BOOL rf_desktop_resize(rdpContext* context)
{
	if (!gdi_resize(context->gdi, freerdp_settings_get_uint32(context->settings, FreeRDP_DesktopWidth),
	                freerdp_settings_get_uint32(context->settings, FreeRDP_DesktopHeight)))
		return FALSE;
	return rf_end_paint(context);
}

static BOOL rf_post_connect(freerdp* instance)
{
	rfContext* rf = (rfContext*)instance->context;
	if (!gdi_init(instance, PIXEL_FORMAT_BGRX32))
	{
		rf->state_cb(rf->user, 3, "gdi_init failed");
		return FALSE;
	}
	rdpUpdate* update = instance->context->update;
	update->EndPaint = rf_end_paint;
	update->DesktopResize = rf_desktop_resize;

	rf->state_cb(rf->user, 1, NULL);
	rf_end_paint(instance->context); /* 首帧 */
	return TRUE;
}

static void rf_post_disconnect(freerdp* instance)
{
	gdi_free(instance);
}

static DWORD rf_verify_cert_ex(freerdp* instance, const char* host, UINT16 port,
                               const char* common_name, const char* subject, const char* issuer,
                               const char* fingerprint, DWORD flags)
{
	(void)subject; (void)issuer;
	rfContext* rf = (rfContext*)instance->context;
	if (!rf->cert_cb)
		return 2; /* 无回调则仅本次接受，绝不落库 */
	int changed = (flags & VERIFY_CERT_FLAG_CHANGED) ? 1 : 0;
	int r = rf->cert_cb(rf->user, host, (int)port, common_name ? common_name : "",
	                    fingerprint ? fingerprint : "", changed);
	return (r == 1) ? 1 : (r == 2) ? 2 : 0; /* 0=拒绝 → freerdp_connect 失败 */
}

static DWORD rf_verify_changed_cert_ex(freerdp* instance, const char* host, UINT16 port,
                                       const char* common_name, const char* subject,
                                       const char* issuer, const char* new_fingerprint,
                                       const char* old_subject, const char* old_issuer,
                                       const char* old_fingerprint, DWORD flags)
{
	(void)subject; (void)issuer; (void)old_subject; (void)old_issuer;
	(void)old_fingerprint; (void)flags;
	rfContext* rf = (rfContext*)instance->context;
	if (!rf->cert_cb)
		return 0; /* 证书已变化且无回调 —— 一律拒绝 */
	int r = rf->cert_cb(rf->user, host, (int)port, common_name ? common_name : "",
	                    new_fingerprint ? new_fingerprint : "", 1 /* changed */);
	return (r == 1) ? 1 : (r == 2) ? 2 : 0;
}

static DWORD WINAPI rf_thread(LPVOID arg)
{
	freerdp* instance = (freerdp*)arg;
	rfContext* rf = (rfContext*)instance->context;

	if (!freerdp_connect(instance))
	{
		rf->state_cb(rf->user, 3, "connect failed");
		return 0;
	}

	HANDLE handles[64];
	while (rf->running && !freerdp_shall_disconnect_context(instance->context))
	{
		DWORD n = freerdp_get_event_handles(instance->context, handles, 64);
		if (n == 0)
			break;
		DWORD w = WaitForMultipleObjects(n, handles, FALSE, 200);
		if (w == WAIT_FAILED)
			break;
		if (!freerdp_check_event_handles(instance->context))
			break;
	}

	freerdp_disconnect(instance);
	rf->state_cb(rf->user, 2, NULL);
	return 0;
}

/* ── C ABI ──────────────────────────────────────────────────── */

void* rf_rdp_create(void* user, rf_frame_cb fcb, rf_state_cb scb, rf_cert_cb ccb)
{
	/* 尽早清代理 env —— FreeRDP 在 context_new / connect 各处都可能读 */
	unsetenv("HTTP_PROXY");  unsetenv("http_proxy");
	unsetenv("HTTPS_PROXY"); unsetenv("https_proxy");
	unsetenv("ALL_PROXY");   unsetenv("all_proxy");
	unsetenv("RDP_PROXY");

	freerdp* instance = freerdp_new();
	if (!instance)
		return NULL;
	instance->ContextSize = sizeof(rfContext);
	instance->PreConnect = rf_pre_connect;
	instance->PostConnect = rf_post_connect;
	instance->PostDisconnect = rf_post_disconnect;
	instance->VerifyCertificateEx = rf_verify_cert_ex;
	instance->VerifyChangedCertificateEx = rf_verify_changed_cert_ex;
	if (!freerdp_context_new(instance))
	{
		freerdp_free(instance);
		return NULL;
	}
	rfContext* rf = (rfContext*)instance->context;
	rf->user = user;
	rf->frame_cb = fcb;
	rf->state_cb = scb;
	rf->cert_cb = ccb;
	rf->running = 1;
	return instance;
}

int rf_rdp_connect(void* h, const char* host, int port, const char* username, const char* domain,
                   const char* password, int width, int height)
{
	freerdp* instance = (freerdp*)h;
	rdpSettings* s = instance->context->settings;

	freerdp_settings_set_string(s, FreeRDP_ServerHostname, host);
	freerdp_settings_set_uint32(s, FreeRDP_ServerPort, (UINT32)port);
	if (username && *username)
		freerdp_settings_set_string(s, FreeRDP_Username, username);
	if (domain && *domain)
		freerdp_settings_set_string(s, FreeRDP_Domain, domain);
	if (password && *password)
		freerdp_settings_set_string(s, FreeRDP_Password, password);

	freerdp_settings_set_uint32(s, FreeRDP_DesktopWidth, (UINT32)width);
	freerdp_settings_set_uint32(s, FreeRDP_DesktopHeight, (UINT32)height);
	freerdp_settings_set_uint32(s, FreeRDP_ColorDepth, 32);
	/* RDP 直连内网 IP，不走本机 HTTP(S)_PROXY：清 env（进程内嵌，FreeRDP 会读）+ 显式无代理 */
	unsetenv("HTTP_PROXY");  unsetenv("http_proxy");
	unsetenv("HTTPS_PROXY"); unsetenv("https_proxy");
	unsetenv("ALL_PROXY");   unsetenv("all_proxy");
	freerdp_settings_set_uint32(s, FreeRDP_ProxyType, PROXY_TYPE_NONE);
	freerdp_settings_set_string(s, FreeRDP_ProxyHostname, NULL);
	freerdp_settings_set_bool(s, FreeRDP_SoftwareGdi, TRUE);
	/* 不设 IgnoreCertificate —— 让 VerifyCertificateEx 回调跑，托管侧做 TOFU / 变化拒绝（§7.4）。 */
	freerdp_settings_set_bool(s, FreeRDP_AutoAcceptCertificate, FALSE);
	freerdp_settings_set_bool(s, FreeRDP_DynamicResolutionUpdate, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_SupportDisplayControl, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_RemoteFxCodec, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_FastPathOutput, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_FastPathInput, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_NlaSecurity, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_TlsSecurity, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_RdpSecurity, TRUE);

	rfContext* rf = (rfContext*)instance->context;
	PubSub_SubscribeChannelConnected(instance->context->pubSub, rf_on_channel_connected);
	PubSub_SubscribeChannelDisconnected(instance->context->pubSub, rf_on_channel_disconnected);
	rf->state_cb(rf->user, 0, NULL);
	rf->thread = CreateThread(NULL, 0, rf_thread, instance, 0, NULL);
	return rf->thread ? 0 : -1;
}

void rf_rdp_send_pointer(void* h, int x, int y, int ptr_flags)
{
	freerdp* instance = (freerdp*)h;
	freerdp_input_send_mouse_event(instance->context->input, (UINT16)ptr_flags, (UINT16)x, (UINT16)y);
}

void rf_rdp_send_wheel(void* h, int x, int y, int delta)
{
	freerdp* instance = (freerdp*)h;
	UINT16 flags = PTR_FLAGS_WHEEL;
	int mag = delta < 0 ? -delta : delta;
	if (mag > 0xFF)
		mag = 0xFF;
	if (delta < 0)
		flags |= PTR_FLAGS_WHEEL_NEGATIVE | (UINT16)(0x100 - mag);
	else
		flags |= (UINT16)mag;
	freerdp_input_send_mouse_event(instance->context->input, flags, (UINT16)x, (UINT16)y);
}

void rf_rdp_send_key(void* h, int scancode, int down, int extended)
{
	freerdp* instance = (freerdp*)h;
	UINT16 flags = down ? KBD_FLAGS_DOWN : KBD_FLAGS_RELEASE;
	if (extended)
		flags |= KBD_FLAGS_EXTENDED;
	freerdp_input_send_keyboard_event(instance->context->input, flags, (UINT8)scancode);
}

void rf_rdp_send_unicode(void* h, uint16_t code, int down)
{
	freerdp* instance = (freerdp*)h;
	UINT16 flags = down ? KBD_FLAGS_DOWN : KBD_FLAGS_RELEASE;
	freerdp_input_send_unicode_keyboard_event(instance->context->input, flags, code);
}

void rf_rdp_resize(void* h, int width, int height)
{
	freerdp* instance = (freerdp*)h;
	rfContext* rf = (rfContext*)instance->context;
	if (width <= 0 || height <= 0)
		return;

	/* 记下最新意图：Display Control 通道可能还没就绪（连接早期），
	   就绪时 rf_on_channel_connected 会补发。 */
	rf->pending_w = width;
	rf->pending_h = height;
	rf_send_monitor_layout(rf, width, height);
}

void rf_rdp_disconnect(void* h)
{
	freerdp* instance = (freerdp*)h;
	rfContext* rf = (rfContext*)instance->context;
	rf->running = 0;
	freerdp_abort_connect_context(instance->context);
}

void rf_rdp_destroy(void* h)
{
	freerdp* instance = (freerdp*)h;
	rfContext* rf = (rfContext*)instance->context;
	rf->running = 0;
	freerdp_abort_connect_context(instance->context);
	if (rf->thread)
	{
		WaitForSingleObject(rf->thread, 3000);
		CloseHandle(rf->thread);
		rf->thread = NULL;
	}
	freerdp_context_free(instance);
	freerdp_free(instance);
}

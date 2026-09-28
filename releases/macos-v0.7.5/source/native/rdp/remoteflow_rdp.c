/*
 * remoteflow_rdp —— FreeRDP 3.x 的极小 C ABI 封装（方案 v1.3 §8.E / §7.2）。
 *
 * 只暴露 RemoteFlow macOS 端「应用内嵌入式 RDP」所需：连接（带凭据、忽略证书）、
 * BGRX32 帧回调、指针 / 键盘输入、断开。所有 FreeRDP 结构体复杂度留在 C 侧，
 * 托管侧（RemoteFlow.Protocol.Rdp.Mac）只 P/Invoke 这十来个函数。
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#include <freerdp/freerdp.h>
#include <freerdp/client.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/gdi/gfx.h>
#include <freerdp/graphics.h>
#include <freerdp/pointer.h>
#include <freerdp/input.h>
#include <freerdp/settings.h>
#include <freerdp/codec/color.h>
#include <freerdp/client/channels.h>
#include <freerdp/client/disp.h>
#include <freerdp/client/rdpgfx.h>
#include <freerdp/channels/disp.h>
#include <freerdp/channels/rdpgfx.h>
#include <freerdp/channels/channels.h>
#include <freerdp/addin.h>
#include <winpr/synch.h>
#include <winpr/sysinfo.h>
#include <winpr/thread.h>

/* state: 0=connecting 1=connected 2=disconnected 3=failed */
/* bgrx 是整块 primary_buffer；(dx,dy,dw,dh) 是本次变化的矩形（脏区），托管侧据此
   只回写这一块，避免每帧全拷 8MB。首帧 / resize 时脏区 = 整幅。 */
typedef void (*rf_frame_cb)(void* user, const uint8_t* bgrx, int w, int h, int stride,
                            int dx, int dy, int dw, int dh);
typedef void (*rf_state_cb)(void* user, int state, const char* message);
/* 证书校验：返回 0=拒绝 1=接受并记录 2=仅本次接受。托管侧据「首次记录 / 变化强警告」决策。 */
typedef int (*rf_cert_cb)(void* user, const char* host, int port, const char* common_name,
                          const char* fingerprint, int changed);
/* 光标（本地渲染，零延迟）：
   rgba != NULL       → 设为该图，(hot_x,hot_y) 是热点
   rgba == NULL, w==0  → 隐藏光标
   rgba == NULL, w<0   → 用系统默认箭头 */
typedef void (*rf_cursor_cb)(void* user, const uint8_t* rgba, int w, int h, int hot_x, int hot_y);

typedef struct
{
	rdpContext context; /* 必须第一个 */
	rf_frame_cb frame_cb;
	rf_state_cb state_cb;
	rf_cert_cb cert_cb;
	rf_cursor_cb cursor_cb;
	void* user;
	volatile int running;
	HANDLE thread;
	/* Display Control 动态通道：真正把新分辨率发给服务端的唯一通路。
	   只改 settings 里的 DesktopWidth/Height 服务端根本收不到。 */
	DispClientContext* disp;
	int pending_w;
	int pending_h;
	int disp_ready;        /* 收到服务端 DISPLAY_CONTROL_CAPS 才算真正可发 */
	UINT32 disp_max_mon;
	UINT64 last_layout_ms; /* 上次发 monitor layout 的时刻，用于限频 */
	/* RDP8+ 图形管线（rdpgfx）：Progressive / ClearCodec / ZGFX + 帧确认背压。
	   SoftwareGdi 下 FreeRDP 的 gdi 自己解进 primary_buffer，rf_end_paint 照常触发。 */
	RdpgfxClientContext* gfx;
	int gfx_pipeline_up;  /* gdi_graphics_pipeline_init 是否已接上（幂等保护） */
} rfContext;

/* MS-RDPEDISP 的硬约束与实践经验：
   - 宽高必须是 4 的倍数（不是 2）；社区里踩过不少「只对齐到 2 导致服务端忽略」的坑。
   - 取值范围 200..8192。
   - 不能刷得太快：有人在 Windows Server 2012 R2 上因为连发布局把会话发僵，
     公认做法是限到每秒 5 次以内。 */
#define RF_DISP_MIN_DIM 200
#define RF_DISP_MAX_DIM 8192
#define RF_DISP_MIN_INTERVAL_MS 200

static UINT64 rf_now_ms(void)
{
	return GetTickCount64();
}

static int rf_align4(int v)
{
	v -= (v % 4);
	if (v < RF_DISP_MIN_DIM)
		v = RF_DISP_MIN_DIM;
	if (v > RF_DISP_MAX_DIM)
		v = RF_DISP_MAX_DIM;
	return v;
}

static void rf_send_monitor_layout(rfContext* rf, int width, int height)
{
	if (!rf->disp || !rf->disp->SendMonitorLayout || !rf->disp_ready)
		return;

	width = rf_align4(width);
	height = rf_align4(height);
	if (width <= 0 || height <= 0)
		return;

	/* 限频：距上次不足 RF_DISP_MIN_INTERVAL_MS 就先不发。
	   最新意图已经存在 pending_w/h 里，托管侧的去抖与重试会把最终尺寸补上。 */
	const UINT64 now = rf_now_ms();
	if (rf->last_layout_ms != 0 && (now - rf->last_layout_ms) < RF_DISP_MIN_INTERVAL_MS)
		return;

	rf->last_layout_ms = now;

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
	const UINT rc = rf->disp->SendMonitorLayout(rf->disp, 1, &layout);
	fprintf(stderr, "[RDP/native] 发送 monitor layout %dx%d → rc=%u\n", width, height, (unsigned)rc);
}

/* 服务端 DISPLAY_CONTROL_CAPS：收到才说明通道真正可用，此时把待发尺寸补上。 */
static UINT rf_disp_caps(DispClientContext* ctx, UINT32 maxNumMonitors, UINT32 maxAreaA,
                         UINT32 maxAreaB)
{
	rfContext* rf = (rfContext*)ctx->custom;
	if (!rf)
		return CHANNEL_RC_OK;

	rf->disp_ready = 1;
	rf->disp_max_mon = maxNumMonitors;
	fprintf(stderr, "[RDP/native] DisplayControl 就绪：最多 %u 个显示器（area %ux%u）\n",
	        (unsigned)maxNumMonitors, (unsigned)maxAreaA, (unsigned)maxAreaB);

	if (rf->pending_w > 0 && rf->pending_h > 0)
		rf_send_monitor_layout(rf, rf->pending_w, rf->pending_h);
	return CHANNEL_RC_OK;
}

/* rdpgfx 通道就绪 + gdi 已 init → 把图形管线接上。两个条件哪个后到都在这里补。 */
static void rf_try_init_gfx(rfContext* rf)
{
	rdpGdi* gdi = ((rdpContext*)rf)->gdi;
	if (rf->gfx && gdi && !rf->gfx_pipeline_up)
	{
		if (gdi_graphics_pipeline_init(gdi, rf->gfx))
		{
			rf->gfx_pipeline_up = 1;
			fprintf(stderr, "[RDP/native] 图形管线已接上（GFX/Progressive）\n");
		}
		else
			fprintf(stderr, "[RDP/native] gdi_graphics_pipeline_init 失败，退化传统位图路径\n");
	}
}

static void rf_on_channel_connected(void* ctx, const ChannelConnectedEventArgs* e)
{
	rfContext* rf = (rfContext*)ctx;
	fprintf(stderr, "[RDP/native] 通道已连接: %s\n", e->name);
	if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
	{
		rf->disp = (DispClientContext*)e->pInterface;
		rf->disp->custom = rf;
		rf->disp->DisplayControlCaps = rf_disp_caps;
		/* 真正可发要等服务端回 CAPS（见 rf_disp_caps），这里只挂钩子。 */
	}
	else if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0)
	{
		rf->gfx = (RdpgfxClientContext*)e->pInterface;
		rf_try_init_gfx(rf);
	}
}

static void rf_on_channel_disconnected(void* ctx, const ChannelDisconnectedEventArgs* e)
{
	rfContext* rf = (rfContext*)ctx;
	if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
	{
		rf->disp = NULL;
		rf->disp_ready = 0;
	}
	else if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0)
	{
		rdpGdi* gdi = ((rdpContext*)rf)->gdi;
		if (rf->gfx && gdi && rf->gfx_pipeline_up)
			gdi_graphics_pipeline_uninit(gdi, rf->gfx);
		rf->gfx = NULL;
		rf->gfx_pipeline_up = 0;
	}
}

/*
 * FreeRDP 3 新增的 LoadChannels 回调 —— 通道**必须**在这里加载。
 * 关键在于 core/utils.c 里的顺序：
 *     context->channels = freerdp_channels_new(instance);   <- 通道对象在这里才创建
 *     IFCALLRET(instance->LoadChannels, rc, instance);       <- 然后才回调我们
 *     freerdp_channels_pre_connect(context->channels, ...);
 * 之前我们在 PreConnect 里调 freerdp_client_load_addins(instance->context->channels, ...)，
 * 那时拿到的根本不是最终那个 channels 对象，于是 addin 灌进了错的地方 ——
 * 表面上 load_addins 返回成功，实际一个通道都没连上，disp 自然发不出 monitor layout。
 * 走标准 freerdp_client_context_new 的客户端由 client common 自动挂好这个回调，
 * 我们是裸 rdpContext，得自己挂。
 */
static BOOL rf_load_channels(freerdp* instance)
{
	rdpSettings* s = instance->context->settings;

	/* 注册静态通道表的查表函数：freerdp_load_channel_addin_entry 靠这个全局函数指针
	   去查 CLIENT_STATIC_ADDIN_TABLE。不注册就只会走 dlopen，而通道是编进
	   libfreerdp-client3 的，磁盘上没有模块文件 —— 一律 "Failed to load channel"。 */
	freerdp_register_addin_provider(freerdp_channels_load_static_addin_entry, 0);

	/* NetworkAutoDetect：让服务端测 RTT / 带宽，据此自适应调图形质量与帧率
	   （微软叫 "adaptive graphics"）。跨 VPN / 公网时它会自动降质而不是硬发导致卡。
	   代价：freerdp_client_load_addins 里这仨 RDP8 特性任一开就强制 DeviceRedirection=TRUE
	   → 拉起 rdpdr 静态通道。rdpdr 已编进库、addin provider 上面也注册了，会空加载
	   （不宣告任何设备），无副作用。
	   Heartbeat（死连接更快发现）也顺带开；Multitransport（UDP）FreeRDP 实现还不稳，保持关。 */
	freerdp_settings_set_bool(s, FreeRDP_NetworkAutoDetect, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_SupportHeartbeatPdu, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_SupportMultitransport, FALSE);

	/* rdpdr 会被 load_addins 强制拉起（见上），但所有具体重定向一律关 ——
	   rdpdr 通道空转，不碰任何本地设备 / 剪贴板 / 打印机。 */
	freerdp_settings_set_bool(s, FreeRDP_RedirectDrives, FALSE);
	freerdp_settings_set_bool(s, FreeRDP_RedirectHomeDrive, FALSE);
	freerdp_settings_set_bool(s, FreeRDP_RedirectPrinters, FALSE);
	freerdp_settings_set_bool(s, FreeRDP_RedirectSmartCards, FALSE);
	freerdp_settings_set_bool(s, FreeRDP_RedirectSerialPorts, FALSE);
	freerdp_settings_set_bool(s, FreeRDP_RedirectParallelPorts, FALSE);
	freerdp_settings_set_bool(s, FreeRDP_RedirectClipboard, FALSE);

	/* 动态通道都跑在 drdynvc 之上；load_addins 见到有动态通道就会自动挂上 drdynvc。
	   - disp   ：Display Control，动态分辨率的唯一通路
	   - rdpgfx ：RDP8+ 图形管线（Progressive / ClearCodec / ZGFX / 帧确认） */
	freerdp_settings_set_bool(s, FreeRDP_SupportDynamicChannels, TRUE);
	const char* disp_args[] = { "disp" };
	const char* gfx_args[] = { "rdpgfx" };
	BOOL added = freerdp_client_add_dynamic_channel(s, 1, disp_args);
	added = freerdp_client_add_dynamic_channel(s, 1, gfx_args) && added;
	BOOL loaded = added ? freerdp_client_load_addins(instance->context->channels, s) : FALSE;
	fprintf(stderr,
	        "[RDP/native] LoadChannels: add=%d load=%d dynRes=%d gfx=%d autoDetect=%d dev=%d(强制)\n",
	        (int)added, (int)loaded,
	        (int)freerdp_settings_get_bool(s, FreeRDP_DynamicResolutionUpdate),
	        (int)freerdp_settings_get_bool(s, FreeRDP_SupportGraphicsPipeline),
	        (int)freerdp_settings_get_bool(s, FreeRDP_NetworkAutoDetect),
	        (int)freerdp_settings_get_bool(s, FreeRDP_DeviceRedirection));

	/* 加载失败也别中止连接：动态分辨率是锦上添花，退化成固定分辨率即可。 */
	return TRUE;
}

/* ── 光标本地渲染 ──────────────────────────────────────────────
   服务端把光标形状经 Pointer PDU 单独下发，我们解成 RGBA 交给 Mac 侧用 NSCursor
   在本地鼠标位置画 —— 光标移动零延迟，且能反映 I 型 / 手型 / 忙等待等各种形状。
   FreeRDP 的 pointer cache 回调在 freerdp_connect 里已挂好，只差 graphics 层的
   Pointer_Prototype，在 rf_post_connect 里 graphics_register_pointer 补上。 */
typedef struct
{
	rdpPointer pointer; /* 必须第一个 */
	BYTE* rgba;
	UINT32 w;
	UINT32 h;
} rfPointer;

static BOOL rf_pointer_new(rdpContext* context, rdpPointer* pointer)
{
	rfPointer* p = (rfPointer*)pointer;
	if (pointer->width == 0 || pointer->height == 0)
		return TRUE;

	size_t n = (size_t)pointer->width * pointer->height * 4;
	p->rgba = (BYTE*)malloc(n);
	if (!p->rgba)
		return FALSE;
	p->w = pointer->width;
	p->h = pointer->height;

	const gdiPalette* pal = context->gdi ? &context->gdi->palette : NULL;
	if (!freerdp_image_copy_from_pointer_data(
	        p->rgba, PIXEL_FORMAT_RGBA32, pointer->width * 4, 0, 0, pointer->width, pointer->height,
	        pointer->xorMaskData, pointer->lengthXorMask, pointer->andMaskData,
	        pointer->lengthAndMask, pointer->xorBpp, pal))
	{
		free(p->rgba);
		p->rgba = NULL;
		return FALSE;
	}
	return TRUE;
}

static void rf_pointer_free(rdpContext* context, rdpPointer* pointer)
{
	(void)context;
	rfPointer* p = (rfPointer*)pointer;
	free(p->rgba);
	p->rgba = NULL;
}

static BOOL rf_pointer_set(rdpContext* context, rdpPointer* pointer)
{
	rfContext* rf = (rfContext*)context;
	rfPointer* p = (rfPointer*)pointer;
	if (rf->cursor_cb && p->rgba)
		rf->cursor_cb(rf->user, p->rgba, (int)p->w, (int)p->h, (int)pointer->xPos,
		              (int)pointer->yPos);
	return TRUE;
}

static BOOL rf_pointer_set_null(rdpContext* context)
{
	rfContext* rf = (rfContext*)context;
	if (rf->cursor_cb)
		rf->cursor_cb(rf->user, NULL, 0, 0, 0, 0);
	return TRUE;
}

static BOOL rf_pointer_set_default(rdpContext* context)
{
	rfContext* rf = (rfContext*)context;
	if (rf->cursor_cb)
		rf->cursor_cb(rf->user, NULL, -1, 0, 0, 0);
	return TRUE;
}

static BOOL rf_pointer_set_position(rdpContext* context, UINT32 x, UINT32 y)
{
	(void)context;
	(void)x;
	(void)y;
	/* 服务端要求把客户端光标挪到 (x,y)：桌面客户端以本地鼠标为准，忽略。 */
	return TRUE;
}

static void rf_register_pointer(rdpContext* context)
{
	rdpPointer proto = { 0 };
	proto.size = sizeof(rfPointer);
	proto.New = rf_pointer_new;
	proto.Free = rf_pointer_free;
	proto.Set = rf_pointer_set;
	proto.SetNull = rf_pointer_set_null;
	proto.SetDefault = rf_pointer_set_default;
	proto.SetPosition = rf_pointer_set_position;
	graphics_register_pointer(context->graphics, &proto);
}

static BOOL rf_pre_connect(freerdp* instance)
{
	rdpSettings* s = instance->context->settings;
	freerdp_settings_set_bool(s, FreeRDP_SoftwareGdi, TRUE);

	/* RDP8+ 图形管线：Progressive（小波 + 渐进细化，滚动 / 大重绘不再卡）、
	   ClearCodec（文本 / UI）、ZGFX 批压缩、帧确认背压。都是 FreeRDP 内建、无外部依赖。
	   H.264（GfxH264 / GfxAVC444）需要 openh264/ffmpeg 后端，暂不开。 */
	freerdp_settings_set_bool(s, FreeRDP_SupportGraphicsPipeline, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_GfxProgressive, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_GfxProgressiveV2, TRUE);
	freerdp_settings_set_bool(s, FreeRDP_GfxH264, FALSE);
	freerdp_settings_set_bool(s, FreeRDP_GfxAVC444, FALSE);
	freerdp_settings_set_bool(s, FreeRDP_GfxAVC444v2, FALSE);
	freerdp_settings_set_bool(s, FreeRDP_GfxSmallCache, FALSE);
	return TRUE;
}

static void rf_push_frame(rfContext* rf, rdpGdi* gdi, int x, int y, int w, int h)
{
	if (!gdi || !gdi->primary_buffer || !rf->frame_cb || w <= 0 || h <= 0)
		return;
	rf->frame_cb(rf->user, gdi->primary_buffer, gdi->width, gdi->height, (int)gdi->stride, x, y, w, h);
}

static BOOL rf_end_paint(rdpContext* context)
{
	rfContext* rf = (rfContext*)context;
	rdpGdi* gdi = context->gdi;
	if (!gdi || !gdi->primary || !gdi->primary->hdc || !gdi->primary->hdc->hwnd)
		return TRUE;

	HGDI_RGN inv = gdi->primary->hdc->hwnd->invalid;
	if (!inv || inv->null)
		return TRUE; /* 本帧无变化，不推 —— 空闲时不打扰托管侧 */

	int x = inv->x, y = inv->y, w = inv->w, h = inv->h;
	if (x < 0) { w += x; x = 0; }
	if (y < 0) { h += y; y = 0; }
	if (x + w > gdi->width)  w = gdi->width - x;
	if (y + h > gdi->height) h = gdi->height - y;
	rf_push_frame(rf, gdi, x, y, w, h);
	return TRUE;
}

static BOOL rf_desktop_resize(rdpContext* context)
{
	if (!gdi_resize(context->gdi, freerdp_settings_get_uint32(context->settings, FreeRDP_DesktopWidth),
	                freerdp_settings_get_uint32(context->settings, FreeRDP_DesktopHeight)))
		return FALSE;
	rdpGdi* gdi = context->gdi;
	rf_push_frame((rfContext*)context, gdi, 0, 0, gdi->width, gdi->height); /* 尺寸变了，整幅 */
	return TRUE;
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

	/* 光标本地渲染：pointer cache 回调 freerdp_connect 里已挂，这里补 graphics 层原型。 */
	rf_register_pointer(instance->context);

	/* rdpgfx 通道可能已在 gdi_init 之前就绪，这里补接一次（rf_try_init_gfx 幂等）。 */
	rf_try_init_gfx(rf);

	rf->state_cb(rf->user, 1, NULL);
	rf_push_frame(rf, instance->context->gdi, 0, 0, instance->context->gdi->width,
	              instance->context->gdi->height); /* 首帧整幅 */
	return TRUE;
}

static void rf_post_disconnect(freerdp* instance)
{
	rfContext* rf = (rfContext*)instance->context;
	/* 正常情况下通道断开事件已 uninit；这里兜底，避免 gdi_free 时管线还挂着。 */
	if (rf->gfx && instance->context->gdi && rf->gfx_pipeline_up)
		gdi_graphics_pipeline_uninit(instance->context->gdi, rf->gfx);
	rf->gfx = NULL;
	rf->gfx_pipeline_up = 0;
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

void* rf_rdp_create(void* user, rf_frame_cb fcb, rf_state_cb scb, rf_cert_cb ccb, rf_cursor_cb curcb)
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
	instance->LoadChannels = rf_load_channels;
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
	rf->cursor_cb = curcb;
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

	/* ── 性能相关（Tier 4）──
	   这些 Disable* 会算进 PerformanceFlags 发给服务端，服务端据此决定不发某类内容。
	   观感优先：壁纸 / 主题 / 字体平滑一律保留 —— LAN + GFX 下它们几乎不耗流量，
	   为省一点带宽把桌面搞得像被扒了皮不值。只关真正无谓的动画。 */
	freerdp_settings_set_uint32(s, FreeRDP_ConnectionType, CONNECTION_TYPE_LAN); /* 内网，给服务端的带宽提示 */
	freerdp_settings_set_bool(s, FreeRDP_DisableWallpaper, FALSE);      /* 壁纸保留（GFX 下第一帧后几乎不耗流量） */
	freerdp_settings_set_bool(s, FreeRDP_DisableThemes, FALSE);         /* 主题保留 */
	freerdp_settings_set_bool(s, FreeRDP_DisableFullWindowDrag, TRUE);  /* 拖窗口只画轮廓（RDP 默认行为，观感无损） */
	freerdp_settings_set_bool(s, FreeRDP_DisableMenuAnims, TRUE);       /* 菜单淡入淡出动画（默认就关） */
	freerdp_settings_set_bool(s, FreeRDP_AllowFontSmoothing, TRUE);     /* ClearType，可读性 */
	freerdp_settings_set_bool(s, FreeRDP_CompressionEnabled, TRUE);     /* 批量压缩（默认就开，显式写明） */
	freerdp_settings_set_bool(s, FreeRDP_BitmapCacheEnabled, TRUE);     /* GFX 未协商上时的传统路径缓存 */

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

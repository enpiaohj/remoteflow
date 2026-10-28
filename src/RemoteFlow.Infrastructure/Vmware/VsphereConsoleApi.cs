using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using RemoteFlow.Core.Vmware;

namespace RemoteFlow.Infrastructure.Vmware;

/// <summary>控制台验证使用的最小 SOAP 客户端。TLS 由独立 handler 负责，默认使用系统校验。</summary>
public sealed class VsphereConsoleApi : IVSphereConsoleApi
{
    private static readonly XNamespace Vim = "urn:vim25";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _sessionManager;
    private string? _propertyCollector;
    private string? _viewManager;
    private bool _loggedIn;

    public VmwareServerInfo? ServerInfo { get; private set; }

    public VsphereConsoleApi(Uri endpoint, HttpMessageHandler? handler = null)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
            || (endpoint.AbsolutePath != "/" && endpoint.AbsolutePath != "/sdk"))
        {
            throw new ArgumentException("ESXi 地址必须是 HTTPS 主机地址，不能包含凭据、查询参数或其他路径。", nameof(endpoint));
        }

        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false
        })
        {
            BaseAddress = new Uri(endpoint, "/sdk"),
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 8 * 1024 * 1024
        };
        _http.DefaultRequestHeaders.Add("SOAPAction", "\"urn:vim25/8.0.0.0\"");
    }

    public async Task LoginAsync(string userName, string password, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_loggedIn) throw new InvalidOperationException("来源已登录，请先注销再更换凭据。");
            var content = await CallAsync("RetrieveServiceContent", [Ref("ServiceInstance", "ServiceInstance")], ct);
            _sessionManager = Required(content, "sessionManager");
            _propertyCollector = Required(content, "propertyCollector");
            _viewManager = Required(content, "viewManager");
            var about = content.Element(Vim + "about");
            ServerInfo = new VmwareServerInfo(
                about?.Element(Vim + "apiType")?.Value == "VirtualCenter" ? VmwareSourceKind.VCenter : VmwareSourceKind.EsxiHost,
                about?.Element(Vim + "fullName")?.Value ?? string.Empty,
                about?.Element(Vim + "version")?.Value ?? string.Empty,
                about?.Element(Vim + "build")?.Value ?? string.Empty);
            await CallAsync("Login", [Ref("SessionManager", _sessionManager),
                new XElement(Vim + "userName", userName), new XElement(Vim + "password", password)], ct);
            _loggedIn = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<VsphereVm>> ListVirtualMachinesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureLoggedIn();
            var service = await CallAsync("RetrieveServiceContent", [Ref("ServiceInstance", "ServiceInstance")], ct);
            var view = await CallAsync("CreateContainerView", [Ref("ViewManager", _viewManager!),
                new XElement(Vim + "container", new XAttribute("type", "Folder"), Required(service, "rootFolder")),
                new XElement(Vim + "type", "VirtualMachine"), new XElement(Vim + "recursive", true)], ct);
            var viewId = view.Value;
            string? token = null;
            try
            {
                XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
                var response = await CallAsync("RetrievePropertiesEx", [Ref("PropertyCollector", _propertyCollector!),
                    new XElement(Vim + "specSet",
                        new XElement(Vim + "propSet", new XElement(Vim + "type", "VirtualMachine"),
                            new XElement(Vim + "pathSet", "name"), new XElement(Vim + "pathSet", "runtime.powerState"),
                            new XElement(Vim + "pathSet", "config.instanceUuid"),
                            new XElement(Vim + "pathSet", "guest.toolsRunningStatus")),
                        new XElement(Vim + "objectSet",
                            new XElement(Vim + "obj", new XAttribute("type", "ContainerView"), viewId),
                            new XElement(Vim + "skip", true),
                            new XElement(Vim + "selectSet", new XAttribute(xsi + "type", "TraversalSpec"),
                                new XElement(Vim + "type", "ContainerView"), new XElement(Vim + "path", "view"),
                                new XElement(Vim + "skip", false)))), new XElement(Vim + "options", new XElement(Vim + "maxObjects", 250))], ct);
                var results = new List<VsphereVm>();
                do
                {
                    token = response.Element(Vim + "token")?.Value;
                    foreach (var item in response.Elements(Vim + "objects"))
                    {
                        var props = item.Elements(Vim + "propSet")
                            .Where(p => p.Element(Vim + "name") is not null)
                            .ToDictionary(p => p.Element(Vim + "name")!.Value, p => p.Element(Vim + "val")?.Value);
                        var reference = Required(item, "obj");
                        results.Add(new VsphereVm(reference, props.GetValueOrDefault("name") ?? reference,
                            props.GetValueOrDefault("runtime.powerState") ?? "unknown", props.GetValueOrDefault("config.instanceUuid"),
                            props.GetValueOrDefault("guest.toolsRunningStatus")));
                    }
                    if (string.IsNullOrEmpty(token)) break;
                    ct.ThrowIfCancellationRequested();
                    response = await CallAsync("ContinueRetrievePropertiesEx", [Ref("PropertyCollector", _propertyCollector!),
                        new XElement(Vim + "token", token)], ct);
                } while (true);
                return results.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            finally
            {
                if (!string.IsNullOrEmpty(token))
                    await TryCleanupAsync("CancelRetrievePropertiesEx", [Ref("PropertyCollector", _propertyCollector!), new XElement(Vim + "token", token)]);
                await TryCleanupAsync("DestroyView", [Ref("ContainerView", viewId)]);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<WebMksTicket> AcquireWebMksTicketAsync(string vmReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmReference);
        await _gate.WaitAsync(ct);
        try
        {
            EnsureLoggedIn();
            var result = await CallAsync("AcquireTicket", [Ref("VirtualMachine", vmReference), new XElement(Vim + "ticketType", "webmks")], ct);
            // 独立 ESXi 的票据可不带 host/port，此时目标就是 API 所在的主机。
            var host = result.Element(Vim + "host")?.Value is { Length: > 0 } h ? h : _http.BaseAddress!.Host;
            var port = _http.BaseAddress!.Port;
            if (result.Element(Vim + "port")?.Value is { Length: > 0 } p
                && !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out port))
                throw new VsphereApiException("InvalidTicketEndpoint");
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535)
                throw new VsphereApiException("InvalidTicketEndpoint");
            return new WebMksTicket(host, port, Required(result, "ticket"), result.Element(Vim + "sslThumbprint")?.Value);
        }
        finally { _gate.Release(); }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loggedIn) return;
            try { await CallAsync("Logout", [Ref("SessionManager", _sessionManager!)], ct); }
            finally { _loggedIn = false; }
        }
        finally { _gate.Release(); }
    }

    public async Task<VmPowerResult> PowerAsync(string vmReference, VmPowerAction action, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmReference);
        await _gate.WaitAsync(ct);
        try
        {
            EnsureLoggedIn();
            var vm = Ref("VirtualMachine", vmReference);
            // 正常关机/重启没有服务端任务：只能确认请求已发给客户机，不能声称已经完成。
            if (action is VmPowerAction.ShutdownGuest or VmPowerAction.RebootGuest)
            {
                await CallAsync(action == VmPowerAction.ShutdownGuest ? "ShutdownGuest" : "RebootGuest", [vm], ct);
                return VmPowerResult.RequestSent;
            }
            var method = action switch
            {
                VmPowerAction.PowerOn => "PowerOnVM_Task",
                VmPowerAction.PowerOff => "PowerOffVM_Task",
                VmPowerAction.Reset => "ResetVM_Task",
                VmPowerAction.Suspend => "SuspendVM_Task",
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
            var task = await CallAsync(method, [vm], ct);
            await WaitForTaskAsync(task.Value, TimeSpan.FromMinutes(2), ct);
            return VmPowerResult.Completed;
        }
        finally { _gate.Release(); }
    }

    public async Task SetScreenResolutionAsync(string vmReference, int width, int height, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmReference);
        if (width is < 320 or > 8192 || height is < 240 or > 8192) throw new ArgumentOutOfRangeException(nameof(width), "分辨率超出合理范围。");
        await _gate.WaitAsync(ct);
        try
        {
            EnsureLoggedIn();
            await CallAsync("SetScreenResolution", [Ref("VirtualMachine", vmReference),
                new XElement(Vim + "width", width), new XElement(Vim + "height", height)], ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<VmSnapshot>> ListSnapshotsAsync(string vmReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmReference);
        await _gate.WaitAsync(ct);
        try
        {
            EnsureLoggedIn();
            var props = await GetPropsAsync("VirtualMachine", vmReference, ["snapshot"], ct);
            if (!props.TryGetValue("snapshot", out var snapshot)) return [];
            var current = snapshot.Element(Vim + "currentSnapshot")?.Value;
            return Flatten(snapshot.Elements(Vim + "rootSnapshotList"), current, 0).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task CreateSnapshotAsync(string vmReference, string name, string description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _gate.WaitAsync(ct);
        try
        {
            EnsureLoggedIn();
            // 默认不含内存、不做文件系统静默：与虚拟机运行状态无关，行为可预期。
            var task = await CallAsync("CreateSnapshot_Task", [Ref("VirtualMachine", vmReference),
                new XElement(Vim + "name", name), new XElement(Vim + "description", description ?? string.Empty),
                new XElement(Vim + "memory", false), new XElement(Vim + "quiesce", false)], ct);
            await WaitForTaskAsync(task.Value, TimeSpan.FromMinutes(10), ct);
        }
        finally { _gate.Release(); }
    }

    public async Task RevertSnapshotAsync(string snapshotReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotReference);
        await _gate.WaitAsync(ct);
        try
        {
            EnsureLoggedIn();
            var task = await CallAsync("RevertToSnapshot_Task", [Ref("VirtualMachineSnapshot", snapshotReference)], ct);
            await WaitForTaskAsync(task.Value, TimeSpan.FromMinutes(10), ct);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveSnapshotAsync(string snapshotReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotReference);
        await _gate.WaitAsync(ct);
        try
        {
            EnsureLoggedIn();
            // 只删这一个快照，子快照保留（合并到上一级）。
            var task = await CallAsync("RemoveSnapshot_Task", [Ref("VirtualMachineSnapshot", snapshotReference),
                new XElement(Vim + "removeChildren", false)], ct);
            await WaitForTaskAsync(task.Value, TimeSpan.FromMinutes(10), ct);
        }
        finally { _gate.Release(); }
    }

    private static IEnumerable<VmSnapshot> Flatten(IEnumerable<XElement> nodes, string? currentRef, int depth)
    {
        foreach (var node in nodes)
        {
            var reference = node.Element(Vim + "snapshot")?.Value ?? string.Empty;
            DateTimeOffset? created = DateTimeOffset.TryParse(node.Element(Vim + "createTime")?.Value,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
            yield return new VmSnapshot(reference, node.Element(Vim + "name")?.Value ?? reference,
                node.Element(Vim + "description")?.Value ?? string.Empty, created,
                reference.Length > 0 && reference == currentRef, depth);
            foreach (var child in Flatten(node.Elements(Vim + "childSnapshotList"), currentRef, depth + 1))
                yield return child;
        }
    }

    /// <summary>轮询服务端任务直到终态。超时只代表不再等待，不代表任务失败或已取消。</summary>
    private async Task WaitForTaskAsync(string taskReference, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var props = await GetPropsAsync("Task", taskReference, ["info.state", "info.error"], limit.Token);
                var state = props.GetValueOrDefault("info.state")?.Value;
                if (state == "success") return;
                if (state == "error")
                {
                    var fault = props.GetValueOrDefault("info.error")?.Elements().FirstOrDefault(e => e.Name.LocalName == "fault");
                    var type = fault?.Attributes().FirstOrDefault(a => a.Name.LocalName == "type")?.Value.Split(':').Last();
                    throw new VsphereApiException(type switch
                    {
                        "NoPermission" => "NoPermission",
                        "InvalidState" or "InvalidPowerState" or "InvalidVmState" => "InvalidState",
                        "ToolsUnavailable" => "ToolsUnavailable",
                        "RestrictedVersion" => "RestrictedVersion",
                        _ => "TaskFailed"
                    });
                }
                await Task.Delay(500, limit.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new VsphereApiException("TaskTimeout");
        }
    }

    private async Task<Dictionary<string, XElement>> GetPropsAsync(string type, string reference, string[] paths, CancellationToken ct)
    {
        var response = await CallAsync("RetrievePropertiesEx", [Ref("PropertyCollector", _propertyCollector!),
            new XElement(Vim + "specSet",
                new XElement(Vim + "propSet", new XElement(Vim + "type", type), paths.Select(p => new XElement(Vim + "pathSet", p))),
                new XElement(Vim + "objectSet", new XElement(Vim + "obj", new XAttribute("type", type), reference), new XElement(Vim + "skip", false))),
            new XElement(Vim + "options")], ct);
        var result = new Dictionary<string, XElement>();
        foreach (var set in response.Elements(Vim + "objects").Elements(Vim + "propSet"))
            if (set.Element(Vim + "name")?.Value is { } name && set.Element(Vim + "val") is { } val) result[name] = val;
        return result;
    }

    private async Task<XElement> CallAsync(string method, IEnumerable<XElement> args, CancellationToken ct)
    {
        var envelope = new XElement(Soap + "Envelope", new XAttribute(XNamespace.Xmlns + "soapenv", Soap),
            new XAttribute("xmlns", Vim), new XElement(Soap + "Body", new XElement(Vim + method, args)));
        using var request = new HttpRequestMessage(HttpMethod.Post, "")
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        using var response = await _http.SendAsync(request, ct);
        // 不把响应、Cookie、票据或登录请求放入异常信息。
        var text = await response.Content.ReadAsStringAsync(ct);
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException) { throw new VsphereApiException("InvalidResponse"); }
        var body = document.Root?.Element(Soap + "Body");
        var fault = body?.Element(Soap + "Fault");
        if (fault is not null)
        {
            var code = fault.Elements().FirstOrDefault(e => e.Name.LocalName == "detail")?.Elements().FirstOrDefault()?.Name.LocalName;
            // fault 名称取自固定白名单，不能回显服务器任意文本。
            throw new VsphereApiException(code switch
            {
                "InvalidLoginFault" => "InvalidLogin",
                "NotAuthenticatedFault" => "NotAuthenticated",
                "NoPermissionFault" => "NoPermission",
                "RestrictedVersionFault" => "RestrictedVersion",
                "InvalidStateFault" or "InvalidPowerStateFault" => "InvalidState",
                "ToolsUnavailableFault" => "ToolsUnavailable",
                "ManagedObjectNotFoundFault" => "ManagedObjectNotFound",
                _ => "ServerFault"
            });
        }
        if (!response.IsSuccessStatusCode) throw new VsphereApiException($"HTTP{(int)response.StatusCode}");
        var payload = body?.Element(Vim + (method + "Response"));
        if (payload is null) throw new VsphereApiException("InvalidResponse");
        return payload.Element(Vim + "returnval") ?? payload;
    }

    private async Task TryCleanupAsync(string method, XElement[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await CallAsync(method, args, timeout.Token); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or VsphereApiException)
        {
            // 清理失败不能覆盖原始读取错误；服务端资源随管理会话注销释放。
        }
    }

    private static XElement Ref(string type, string value) => new(Vim + "_this", new XAttribute("type", type), value);
    private static string Required(XElement value, string name) => value.Element(Vim + name)?.Value is { Length: > 0 } text
        ? text : throw new VsphereApiException("MissingResponseField:" + name);
    private void EnsureLoggedIn()
    {
        if (!_loggedIn) throw new InvalidOperationException("请先连接 ESXi 来源。");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await LogoutAsync(timeout.Token);
        }
        finally { _http.Dispose(); }
    }
}

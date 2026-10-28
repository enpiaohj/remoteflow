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
                            new XElement(Vim + "pathSet", "config.instanceUuid")),
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
                            props.GetValueOrDefault("runtime.powerState") ?? "unknown", props.GetValueOrDefault("config.instanceUuid")));
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
            var host = Required(result, "host");
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown
                || !int.TryParse(Required(result, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
                throw new VsphereApiException("InvalidTicketEndpoint");
            return new WebMksTicket(host, port, Required(result, "ticket"));
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
                "InvalidStateFault" => "InvalidState",
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
        ? text : throw new VsphereApiException("MissingResponseField");
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

using System.Net;
using System.Text;
using RemoteFlow.Core.Vmware;
using RemoteFlow.Infrastructure.Vmware;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class VsphereConsoleApiTests
{
    [Fact]
    public async Task 登录编码密码且票据不会通过ToString泄漏()
    {
        var handler = new SoapHandler();
        await using var api = new VsphereConsoleApi(new Uri("https://esxi.example.test"), handler);
        await api.LoginAsync("demo", "<&sample>");
        Assert.Contains("&lt;&amp;sample&gt;", handler.Requests[1]);
        var ticket = await api.AcquireWebMksTicketAsync("vm-1");
        Assert.Equal("esxi.example.test", ticket.Host);
        Assert.Equal(443, ticket.Port);
        Assert.DoesNotContain("sample-ticket", ticket.ToString());
    }

    [Fact]
    public async Task 独立主机票据缺少主机和端口时使用登录端点()
    {
        var handler = new SoapHandler { TicketWithoutEndpoint = true };
        await using var api = new VsphereConsoleApi(new Uri("https://esxi.example.test:8443"), handler);
        await api.LoginAsync("demo", "sample");
        var ticket = await api.AcquireWebMksTicketAsync("vm-1");
        Assert.Equal("esxi.example.test", ticket.Host);
        Assert.Equal(8443, ticket.Port);
    }

    [Fact]
    public async Task 缺少票据字段时只报告字段名()
    {
        var handler = new SoapHandler { TicketWithoutTicket = true };
        await using var api = new VsphereConsoleApi(new Uri("https://esxi.example.test"), handler);
        await api.LoginAsync("demo", "sample");
        var error = await Assert.ThrowsAsync<VsphereApiException>(() => api.AcquireWebMksTicketAsync("vm-1"));
        Assert.Equal("MissingResponseField:ticket", error.FaultCode);
    }

    [Fact]
    public async Task 列表包含VMwareTools运行状态()
    {
        var handler = new SoapHandler();
        await using var api = new VsphereConsoleApi(new Uri("https://esxi.example.test"), handler);
        await api.LoginAsync("demo", "sample");

        var vms = await api.ListVirtualMachinesAsync();

        Assert.All(vms, vm => Assert.Equal("guestToolsNotRunning", vm.ToolsStatus));
        Assert.All(vms, vm => Assert.False(vm.ToolsRunning));
    }

    [Theory]
    [InlineData("guestToolsRunning", true)]
    [InlineData("guestToolsExecutingScripts", true)]
    [InlineData("guestToolsNotRunning", false)]
    [InlineData(null, false)]
    public void Tools状态未知时按未运行处理(string? status, bool running)
        => Assert.Equal(running, new VsphereVm("1", "vm", "poweredOn", "u", status).ToolsRunning);

    [Fact]
    public async Task 设置分辨率发送宽高并作为独立调用()
    {
        var handler = new SoapHandler();
        await using var api = new VsphereConsoleApi(new Uri("https://esxi.example.test"), handler);
        await api.LoginAsync("demo", "sample");

        await api.SetScreenResolutionAsync("vm-1", 1920, 1080);

        var request = Assert.Single(handler.Requests, r => r.Contains("SetScreenResolution"));
        Assert.Contains("<width>1920</width>", request);
        Assert.Contains("<height>1080</height>", request);
        Assert.Contains("type=\"VirtualMachine\"", request);
    }

    [Fact]
    public async Task 未运行Tools时设置分辨率报ToolsUnavailable且不泄漏服务器文本()
    {
        var handler = new SoapHandler { FailResolution = true };
        await using var api = new VsphereConsoleApi(new Uri("https://esxi.example.test"), handler);
        await api.LoginAsync("demo", "sample");

        var error = await Assert.ThrowsAsync<VsphereApiException>(() => api.SetScreenResolutionAsync("vm-1", 1920, 1080));

        Assert.Equal("ToolsUnavailable", error.FaultCode);
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.Contains("VMware Tools", error.Message);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(1920, 100000)]
    public async Task 不合理的分辨率在发送前被拒绝(int width, int height)
    {
        var handler = new SoapHandler();
        await using var api = new VsphereConsoleApi(new Uri("https://esxi.example.test"), handler);
        await api.LoginAsync("demo", "sample");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => api.SetScreenResolutionAsync("vm-1", width, height));
        Assert.DoesNotContain(handler.Requests, r => r.Contains("SetScreenResolution"));
    }

    [Fact]
    public async Task 读取分页且释放视图()
    {
        var handler = new SoapHandler();
        await using var api = new VsphereConsoleApi(new Uri("https://esxi.example.test"), handler);
        await api.LoginAsync("demo", "sample");
        var vms = await api.ListVirtualMachinesAsync();
        Assert.Equal(2, vms.Count);
        Assert.Equal("VM A", vms[0].Name);
        Assert.Equal("vm-2", vms[1].Reference);
        Assert.Contains(handler.Requests, x => x.Contains("ContinueRetrievePropertiesEx"));
        Assert.Contains(handler.Requests, x => x.Contains("DestroyView"));
    }

    [Fact]
    public async Task 服务端错误不泄漏票据或任意故障文本()
    {
        var handler = new SoapHandler { FailTicket = true };
        await using var api = new VsphereConsoleApi(new Uri("https://esxi.example.test"), handler);
        await api.LoginAsync("demo", "sample");
        var error = await Assert.ThrowsAsync<VsphereApiException>(() => api.AcquireWebMksTicketAsync("vm-1"));
        Assert.Equal("NoPermission", error.FaultCode);
        Assert.DoesNotContain("sensitive", error.ToString());
    }

    [Theory]
    [InlineData("http://esxi.example.test")]
    [InlineData("https://demo:sample@esxi.example.test")]
    [InlineData("https://esxi.example.test/ui/")]
    [InlineData("https://esxi.example.test/?ticket=sample")]
    public void 拒绝非管理端点(string address)
        => Assert.Throws<ArgumentException>(() => new VsphereConsoleApi(new Uri(address)));

    private sealed class SoapHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool FailTicket { get; init; }
        public bool FailResolution { get; init; }
        public bool TicketWithoutEndpoint { get; init; }
        public bool TicketWithoutTicket { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add(body);
            var method = System.Xml.Linq.XDocument.Parse(body).Descendants()
                .First(e => e.Name.LocalName == "Body").Elements().Single().Name.LocalName;
            var payload = method switch
            {
                "RetrieveServiceContent" => "<returnval><sessionManager>SessionManager</sessionManager><propertyCollector>propertyCollector</propertyCollector><viewManager>ViewManager</viewManager><rootFolder>rootFolder</rootFolder></returnval>",
                "CreateContainerView" => "<returnval>view-1</returnval>",
                "RetrievePropertiesEx" => "<returnval><token>page-2</token>" + Vm("vm-2", "VM B") + "</returnval>",
                "ContinueRetrievePropertiesEx" => "<returnval>" + Vm("vm-1", "VM A") + "</returnval>",
                "AcquireTicket" => TicketWithoutTicket ? "<returnval><host>esxi.example.test</host></returnval>"
                    : TicketWithoutEndpoint ? "<returnval><ticket>sample-ticket</ticket></returnval>"
                    : "<returnval><host>esxi.example.test</host><port>443</port><ticket>sample-ticket</ticket></returnval>",
                _ => ""
            };
            var fault = (FailTicket && method == "AcquireTicket") || (FailResolution && method == "SetScreenResolution");
            var faultName = method == "SetScreenResolution" ? "ToolsUnavailableFault" : "NoPermissionFault";
            var response = fault
                ? $"<s:Fault><faultstring>sensitive</faultstring><detail><{faultName}/></detail></s:Fault>"
                : $"<{method}Response>{payload}</{method}Response>";
            return new HttpResponseMessage(fault ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
            {
                Content = new StringContent("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns=\"urn:vim25\"><s:Body>" + response + "</s:Body></s:Envelope>", Encoding.UTF8, "text/xml")
            };
        }

        private static string Vm(string reference, string name) => $"<objects><obj type=\"VirtualMachine\">{reference}</obj><propSet><name>name</name><val>{name}</val></propSet><propSet><name>runtime.powerState</name><val>poweredOn</val></propSet><propSet><name>guest.toolsRunningStatus</name><val>guestToolsNotRunning</val></propSet></objects>";
    }
}

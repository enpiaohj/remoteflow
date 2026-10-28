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
                "AcquireTicket" => "<returnval><host>esxi.example.test</host><port>443</port><ticket>sample-ticket</ticket></returnval>",
                _ => ""
            };
            var fault = FailTicket && method == "AcquireTicket";
            var response = fault
                ? "<s:Fault><faultstring>sensitive</faultstring><detail><NoPermissionFault/></detail></s:Fault>"
                : $"<{method}Response>{payload}</{method}Response>";
            return new HttpResponseMessage(fault ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
            {
                Content = new StringContent("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns=\"urn:vim25\"><s:Body>" + response + "</s:Body></s:Envelope>", Encoding.UTF8, "text/xml")
            };
        }

        private static string Vm(string reference, string name) => $"<objects><obj type=\"VirtualMachine\">{reference}</obj><propSet><name>name</name><val>{name}</val></propSet><propSet><name>runtime.powerState</name><val>poweredOn</val></propSet></objects>";
    }
}

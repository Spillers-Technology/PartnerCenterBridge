using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.Graph.Workflows;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PartnerCenterBridge.Tests;

public class LicenseRepairWorkflowTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    private LicenseRepairWorkflow Workflow() => new(
        new TenantGraphRest(new FakeTokenProvider(), new SingleHttpClientFactory(),
            Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! })));

    private static Tenant Tenant() => new() { TenantId = "t", DisplayName = "Contoso" };
    private static Dictionary<string, string> In() => new() { ["userUpn"] = "user1" };

    [Fact]
    public async Task Diagnose_flags_missing_usage_location_and_errored_sku()
    {
        _server.Given(Request.Create().WithPath("/users/user1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = "user1",
                assignedLicenses = Array.Empty<object>(),
                licenseAssignmentStates = new[] { new { skuId = "sku-1", state = "Error", error = "CountViolation" } }
                // usageLocation intentionally omitted
            }));

        var d = await Workflow().DiagnoseAsync(Tenant(), In());

        Assert.Contains(d.Findings, f => f.Name == "Usage location" && f.Status == FindingStatus.Blocker);
        Assert.Contains(d.Findings, f => f.Name == "License in error" && f.Status == FindingStatus.Blocker);
        Assert.False(d.Healthy);
    }

    [Fact]
    public async Task Remediate_sets_usage_location_and_reissues_errored_sku()
    {
        _server.Given(Request.Create().WithPath("/users/user1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = "user1",
                assignedLicenses = new[] { new { skuId = "sku-1" } },
                licenseAssignmentStates = new[] { new { skuId = "sku-1", state = "Error", error = "CountViolation" } }
            }));
        _server.Given(Request.Create().WithPath("/users/user1").UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(204));
        _server.Given(Request.Create().WithPath("/users/user1/assignLicense").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "user1" }));

        var run = await Workflow().RemediateAsync(Tenant(), In());

        Assert.True(run.Steps.All(s => s.Success), string.Join("; ", run.Steps.Select(s => $"{s.Name}:{s.Detail}")));
        Assert.Contains(run.Steps, s => s.Name == "Set usage location" && s.Detail == "US");
        Assert.Contains(run.Steps, s => s.Name == "Reprocess licenses" && s.Detail!.Contains("1 SKU"));
        Assert.NotNull(run.PostState);

        var log = _server.LogEntries.Select(e => $"{e.RequestMessage.Method} {e.RequestMessage.Path}").ToList();
        Assert.Contains("PATCH /users/user1", log);
        Assert.Contains("POST /users/user1/assignLicense", log);
    }
}

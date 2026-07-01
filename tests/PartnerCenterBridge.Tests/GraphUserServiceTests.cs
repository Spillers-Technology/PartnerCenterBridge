using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Graph;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PartnerCenterBridge.Tests;

public class GraphUserServiceTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    private GraphUserService CreateService() => new(
        new FakeTokenProvider(),
        new SingleHttpClientFactory(),
        Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! }),
        NullLogger<GraphUserService>.Instance);

    private static Tenant Tenant() => new() { TenantId = "contoso.onmicrosoft.com", DisplayName = "Contoso" };

    [Fact]
    public async Task CreateUser_runs_all_steps_and_returns_generated_password()
    {
        _server.Given(Request.Create().WithPath("/users").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id = "u1" }));
        _server.Given(Request.Create().WithPath("/users/u1/assignLicense").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "u1" }));
        _server.Given(Request.Create().WithPath("/groups/g1/members/$ref").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        _server.Given(Request.Create().WithPath("/users/u1/manager/$ref").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = await CreateService().CreateUserAsync(Tenant(), new NewHireRequest
        {
            DisplayName = "Ada Lovelace", UserPrincipalName = "ada@contoso.com", MailNickname = "ada",
            LicenseSkuIds = { "sku-1" }, GroupIds = { "g1" }, ManagerId = "mgr-1"
        });

        Assert.True(result.Succeeded, string.Join("; ", result.Steps.Where(s => !s.Success).Select(s => $"{s.Name}:{s.Detail}")));
        Assert.Equal("u1", result.UserId);
        Assert.NotNull(result.InitialPassword);
        Assert.Equal(4, result.Steps.Count); // create + license + group + manager
    }

    [Fact]
    public async Task CreateUser_continues_and_flags_failure_when_a_group_add_fails()
    {
        _server.Given(Request.Create().WithPath("/users").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id = "u1" }));
        _server.Given(Request.Create().WithPath("/groups/bad/members/$ref").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("nope"));

        var result = await CreateService().CreateUserAsync(Tenant(), new NewHireRequest
        {
            DisplayName = "Grace", UserPrincipalName = "grace@contoso.com", MailNickname = "grace",
            GroupIds = { "bad" }
        });

        Assert.Equal("u1", result.UserId);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Steps, s => s.Name.Contains("group") && !s.Success);
    }

    [Fact]
    public async Task CreateUser_uses_supplied_password_without_returning_it()
    {
        _server.Given(Request.Create().WithPath("/users").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id = "u1" }));

        var result = await CreateService().CreateUserAsync(Tenant(), new NewHireRequest
        {
            DisplayName = "X", UserPrincipalName = "x@contoso.com", MailNickname = "x", Password = "Supplied1!"
        });

        Assert.Null(result.InitialPassword);
    }

    [Fact]
    public async Task Terminate_blocks_revokes_and_strips_licenses_and_groups()
    {
        _server.Given(Request.Create().WithPath("/users/u1").UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(204));
        _server.Given(Request.Create().WithPath("/users/u1/revokeSignInSessions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { value = true }));
        _server.Given(Request.Create().WithPath("/users/u1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { assignedLicenses = new[] { new { skuId = "sku-1" } } }));
        _server.Given(Request.Create().WithPath("/users/u1/assignLicense").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "u1" }));
        _server.Given(Request.Create().WithPath("/users/u1/memberOf").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                value = new[] { new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.group", ["id"] = "g1" } }
            }));
        _server.Given(Request.Create().WithPath("/groups/g1/members/u1/$ref").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = await CreateService().TerminateUserAsync(Tenant(), new TerminationRequest { UserId = "u1" });

        Assert.True(result.Succeeded, string.Join("; ", result.Steps.Where(s => !s.Success).Select(s => $"{s.Name}:{s.Detail}")));
        Assert.Equal(4, result.Steps.Count); // block + revoke + licenses + groups
        Assert.Contains(result.Steps, s => s.Name == "Remove licenses" && s.Detail == "1 removed");
    }

    [Fact]
    public void GeneratePassword_meets_complexity()
    {
        for (var i = 0; i < 20; i++)
        {
            var pw = GraphUserService.GeneratePassword();
            Assert.Equal(16, pw.Length);
            Assert.Contains(pw, char.IsUpper);
            Assert.Contains(pw, char.IsLower);
            Assert.Contains(pw, char.IsDigit);
            Assert.Contains(pw, c => !char.IsLetterOrDigit(c));
        }
    }
}

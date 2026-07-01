using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Exchange;

namespace PartnerCenterBridge.Tests;

public class ExchangeOnlineServiceTests
{
    private static Tenant Tenant() => new() { TenantId = "t-id", DisplayName = "Contoso", DefaultDomain = "contoso.onmicrosoft.com" };

    private static ExchangeOnlineService Service(FakeRunner runner) => new(
        runner,
        Options.Create(new ExchangeOptions { AppId = "app-1", CertificatePath = "/certs/exo.pfx" }),
        NullLogger<ExchangeOnlineService>.Instance);

    [Fact]
    public async Task GetMailbox_parses_data_and_sends_correct_operation()
    {
        var runner = new FakeRunner(new PwshResult(0,
            """
            {"success":true,"steps":[{"name":"Connect","success":true,"detail":"contoso.onmicrosoft.com"}],
             "data":{"userPrincipalName":"ada@contoso.com","displayName":"Ada","recipientTypeDetails":"UserMailbox",
                     "forwardingSmtpAddress":null,"deliverToMailboxAndForward":false}}
            """, ""));

        var mbx = await Service(runner).GetMailboxAsync(Tenant(), "ada@contoso.com");

        Assert.NotNull(mbx);
        Assert.Equal("ada@contoso.com", mbx!.UserPrincipalName);
        Assert.Equal("UserMailbox", mbx.RecipientTypeDetails);

        // The service built the right payload for the script.
        using var payload = JsonDocument.Parse(runner.LastPayload!);
        Assert.Equal("getMailbox", payload.RootElement.GetProperty("operation").GetString());
        Assert.Equal("ada@contoso.com", payload.RootElement.GetProperty("params").GetProperty("identity").GetString());
        Assert.Equal("contoso.onmicrosoft.com", payload.RootElement.GetProperty("connect").GetProperty("organization").GetString());
        Assert.Equal("app-1", payload.RootElement.GetProperty("connect").GetProperty("appId").GetString());
    }

    [Fact]
    public async Task ConvertToShared_maps_steps_and_passes_forwarding()
    {
        var runner = new FakeRunner(new PwshResult(0,
            """
            {"success":true,"steps":[
              {"name":"Connect","success":true,"detail":"contoso"},
              {"name":"Convert to shared","success":true,"detail":"ada"},
              {"name":"Set forwarding","success":true,"detail":"mgr@contoso.com"}],
             "data":null}
            """, ""));

        var result = await Service(runner).ConvertToSharedAsync(Tenant(), "ada@contoso.com", "mgr@contoso.com", true);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Steps.Count);

        using var payload = JsonDocument.Parse(runner.LastPayload!);
        Assert.Equal("convertToShared", payload.RootElement.GetProperty("operation").GetString());
        Assert.Equal("mgr@contoso.com",
            payload.RootElement.GetProperty("params").GetProperty("forwardingSmtpAddress").GetString());
    }

    [Fact]
    public async Task ConvertToShared_surfaces_failure_when_script_yields_no_json()
    {
        var runner = new FakeRunner(new PwshResult(1, "", "Connect-ExchangeOnline: boom"));

        var result = await Service(runner).ConvertToSharedAsync(Tenant(), "ada@contoso.com", null, false);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Steps, s => !s.Success && s.Detail!.Contains("boom"));
    }

    [Theory]
    [InlineData("{\"success\":true}", true)]
    [InlineData("PowerShell banner noise\n{\"success\":true}", true)]
    [InlineData("", false)]
    [InlineData("no json here", false)]
    public void ExtractJson_finds_the_result_object(string stdout, bool expectJson)
    {
        var json = ExchangeOnlineService.ExtractJson(stdout);
        Assert.Equal(expectJson, json is not null);
    }

    private sealed class FakeRunner : IPwshRunner
    {
        private readonly PwshResult _result;
        public string? LastScript { get; private set; }
        public string? LastPayload { get; private set; }

        public FakeRunner(PwshResult result) => _result = result;

        public Task<PwshResult> RunAsync(string scriptPath, string payloadJson, CancellationToken ct = default)
        {
            LastScript = scriptPath;
            LastPayload = payloadJson;
            return Task.FromResult(_result);
        }
    }
}

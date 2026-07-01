using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Abstractions;
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

    [Fact]
    public async Task GetArchiveState_parses_flags_and_blockers()
    {
        var runner = new FakeRunner(new PwshResult(0,
            """
            {"success":true,"steps":[{"name":"Connect","success":true,"detail":"contoso"}],
             "data":{"userPrincipalName":"ada@contoso.com","primarySize":"48 GB","primaryItemCount":120000,
                     "prohibitSendReceiveQuota":"50 GB","archiveEnabled":true,"archiveStatus":"Active",
                     "autoExpandingArchiveEnabled":false,"archiveQuota":"100 GB","archiveWarningQuota":"90 GB",
                     "archiveSize":"2 GB","archiveItemCount":5000,"retentionPolicy":"Default MRM Policy",
                     "retentionHoldEnabled":true,"elcProcessingDisabled":false}}
            """, ""));

        var state = await Service(runner).GetArchiveStateAsync(Tenant(), "ada@contoso.com");

        Assert.NotNull(state);
        Assert.True(state!.ArchiveEnabled);
        Assert.False(state.AutoExpandingArchiveEnabled);   // a fixable warning
        Assert.True(state.RetentionHoldEnabled);           // a hidden blocker
        Assert.Equal(120000, state.PrimaryItemCount);
        Assert.Equal("Default MRM Policy", state.RetentionPolicy);

        using var payload = JsonDocument.Parse(runner.LastPayload!);
        Assert.Equal("getArchiveState", payload.RootElement.GetProperty("operation").GetString());
    }

    [Fact]
    public async Task RemediateArchive_sends_options_and_returns_steps_plus_state()
    {
        var runner = new FakeRunner(new PwshResult(0,
            """
            {"success":true,"steps":[
              {"name":"Connect","success":true,"detail":"contoso"},
              {"name":"Enable archive","success":true,"detail":"already enabled"},
              {"name":"Enable auto-expanding archive","success":true,"detail":"enabled"},
              {"name":"Clear retention hold","success":true,"detail":"disabled"},
              {"name":"Trigger Managed Folder Assistant","success":true,"detail":"processing started"}],
             "data":{"userPrincipalName":"ada@contoso.com","primarySize":"48 GB","primaryItemCount":120000,
                     "prohibitSendReceiveQuota":"50 GB","archiveEnabled":true,"archiveStatus":"Active",
                     "autoExpandingArchiveEnabled":true,"archiveSize":"2 GB","archiveItemCount":5000,
                     "retentionPolicy":"Default MRM Policy","retentionHoldEnabled":false,"elcProcessingDisabled":false}}
            """, ""));

        var result = await Service(runner).RemediateArchiveAsync(Tenant(), "ada@contoso.com",
            new ArchiveRemediationOptions { RetentionPolicyName = "Default MRM Policy" });

        Assert.True(result.Succeeded);
        Assert.NotNull(result.State);
        Assert.True(result.State!.AutoExpandingArchiveEnabled);   // reflects the post-fix state
        Assert.False(result.State.RetentionHoldEnabled);

        using var payload = JsonDocument.Parse(runner.LastPayload!);
        var p = payload.RootElement.GetProperty("params");
        Assert.Equal("remediateArchive", payload.RootElement.GetProperty("operation").GetString());
        Assert.True(p.GetProperty("enableAutoExpandingArchive").GetBoolean());
        Assert.True(p.GetProperty("clearProcessingBlocks").GetBoolean());
        Assert.Equal("Default MRM Policy", p.GetProperty("retentionPolicyName").GetString());
    }

    [Fact]
    public async Task NudgeArchive_triggers_and_refreshes_state()
    {
        var runner = new FakeRunner(new PwshResult(0,
            """
            {"success":true,"steps":[{"name":"Connect","success":true,"detail":"c"},
              {"name":"Trigger Managed Folder Assistant","success":true,"detail":"ada@contoso.com"}],
             "data":{"userPrincipalName":"ada@contoso.com","primarySize":"46 GB","primaryItemCount":118000,
                     "prohibitSendReceiveQuota":"50 GB","archiveEnabled":true,"archiveStatus":"Active",
                     "autoExpandingArchiveEnabled":true,"archiveSize":"4 GB","archiveItemCount":9000,
                     "retentionPolicy":"Default MRM Policy","retentionHoldEnabled":false,"elcProcessingDisabled":false}}
            """, ""));

        var result = await Service(runner).NudgeArchiveAsync(Tenant(), "ada@contoso.com");

        Assert.True(result.Succeeded);
        Assert.Equal(9000, result.State!.ArchiveItemCount);   // archive grew after the nudge
        using var payload = JsonDocument.Parse(runner.LastPayload!);
        Assert.Equal("nudgeArchive", payload.RootElement.GetProperty("operation").GetString());
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

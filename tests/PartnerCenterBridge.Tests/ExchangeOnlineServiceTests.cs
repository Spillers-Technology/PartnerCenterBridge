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

    [Fact]
    public async Task GetMailbox_returns_null_only_on_the_scripts_not_found_marker()
    {
        var runner = new FakeRunner(new PwshResult(0,
            """
            {"success":true,"steps":[{"name":"Connect","success":true,"detail":"contoso"},
              {"name":"Mailbox not found","success":true,"detail":"Exchange Online has no mailbox 'nobody@contoso.com'."}],
             "data":null,"notFound":true}
            """, ""));

        Assert.Null(await Service(runner).GetMailboxAsync(Tenant(), "nobody@contoso.com"));
    }

    [Fact]
    public async Task GetMailbox_throws_when_the_lookup_itself_failed()
    {
        // A connect/auth failure used to come back as null, i.e. "no mailbox" -- a false answer.
        var runner = new FakeRunner(new PwshResult(0,
            """
            {"success":false,"steps":[{"name":"Error","success":false,"detail":"AADSTS700027: Client assertion contains an invalid signature."}],
             "data":null,"notFound":false}
            """, ""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(runner).GetMailboxAsync(Tenant(), "ada@contoso.com"));
        Assert.Contains("AADSTS700027", ex.Message);
    }

    [Fact]
    public async Task GetMailbox_throws_on_a_connect_failure_that_says_could_not_be_found()
    {
        // Not the mailbox lookup: the certificate (or the organization) could not be found. Only the
        // structured marker means "no mailbox", never a message that happens to read like one.
        var runner = new FakeRunner(new PwshResult(0,
            """
            {"success":false,"steps":[{"name":"Error","success":false,"detail":"The certificate file '/certs/exo.pfx' could not be found."}],
             "data":null,"notFound":false}
            """, ""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(runner).GetMailboxAsync(Tenant(), "ada@contoso.com"));
        Assert.Contains("could not be found", ex.Message);
    }

    [Fact]
    public async Task GetMailbox_throws_when_the_result_has_neither_data_nor_a_not_found_marker()
    {
        var runner = new FakeRunner(new PwshResult(0, """{"success":true,"steps":[],"data":null}""", ""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(runner).GetMailboxAsync(Tenant(), "ada@contoso.com"));
        Assert.Contains("did not confirm", ex.Message);
    }

    /// <summary>
    /// The real exo-op.ps1 against a stand-in ExchangeOnlineManagement module: only a lookup that
    /// Exchange reports as "object not found" becomes the notFound marker. Soft-skips without pwsh 7.
    /// </summary>
    [Theory]
    [InlineData("ada@contoso.com", "contoso.onmicrosoft.com", "found")]
    [InlineData("nobody@contoso.com", "contoso.onmicrosoft.com", "null")]      // ObjectNotFound category
    [InlineData("gone@contoso.com", "contoso.onmicrosoft.com", "null")]        // REST error: lookup message fallback
    [InlineData("busy@contoso.com", "contoso.onmicrosoft.com", "throws")]      // throttled lookup
    [InlineData("ada@contoso.com", "nocert.onmicrosoft.com", "throws")]        // connect says "could not be found"
    public async Task Script_marks_not_found_only_for_the_lookup(string identity, string organization, string expected)
    {
        if (!PwshAvailable()) return;
        var dir = Path.Combine(Path.GetTempPath(), "pcb-exo-fake-" + Guid.NewGuid().ToString("N"));
        var module = Path.Combine(dir, "modules", "ExchangeOnlineManagement");
        Directory.CreateDirectory(module);
        await File.WriteAllTextAsync(Path.Combine(module, "ExchangeOnlineManagement.psm1"),
            """
            function Connect-ExchangeOnline { param($AppId, $Organization, $ShowBanner, $CertificateFilePath, $CertificatePassword)
                if ($Organization -like 'nocert*') { throw "The certificate file '$CertificateFilePath' could not be found." } }
            function Disconnect-ExchangeOnline { param($Confirm) }
            function Get-EXOMailbox { [CmdletBinding()] param($Identity, $Properties)
                switch -Wildcard ($Identity) {
                    'nobody@*' { Write-Error -Message "The operation couldn't be performed because object '$Identity' couldn't be found." -Category ObjectNotFound -ErrorAction Stop }
                    'gone@*' { throw "Error while querying REST service. HttpStatusCode=404 ErrorMessage=The operation couldn't be performed because object '$Identity' couldn't be found on 'EURPR01A001.PROD.OUTLOOK.COM'." }
                    'busy@*' { throw 'Server is busy; the request was throttled.' }
                    default { [pscustomobject]@{ UserPrincipalName = $Identity; DisplayName = 'Ada'; RecipientTypeDetails = 'UserMailbox';
                                                 ForwardingSmtpAddress = $null; DeliverToMailboxAndForward = $false } }
                } }
            """);
        try
        {
            var service = new ExchangeOnlineService(new FakeModuleRunner(Path.Combine(dir, "modules"), dir),
                Options.Create(new ExchangeOptions { AppId = "app-1", CertificatePath = "/certs/exo.pfx" }),
                NullLogger<ExchangeOnlineService>.Instance);
            var tenant = new Tenant { TenantId = "t-id", DisplayName = "Contoso", DefaultDomain = organization };

            switch (expected)
            {
                case "found":
                    Assert.Equal(identity, (await service.GetMailboxAsync(tenant, identity))!.UserPrincipalName);
                    break;
                case "null":
                    Assert.Null(await service.GetMailboxAsync(tenant, identity));
                    break;
                default:
                    await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetMailboxAsync(tenant, identity));
                    break;
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Runs the given script through a wrapper that puts the stand-in module first on PSModulePath.</summary>
    private sealed class FakeModuleRunner(string modulePath, string workDir) : IPwshRunner
    {
        public async Task<PwshResult> RunAsync(string scriptPath, string payloadJson, CancellationToken ct = default)
        {
            var wrapper = Path.Combine(workDir, $"wrapper-{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(wrapper,
                "param([string]$PayloadPath)\n" +
                $"$env:PSModulePath = '{modulePath.Replace("'", "''")}' + [IO.Path]::PathSeparator + $env:PSModulePath\n" +
                $"& '{scriptPath.Replace("'", "''")}' -PayloadPath $PayloadPath\n", ct);
            return await new PwshRunner("pwsh", timeoutSeconds: 120).RunAsync(wrapper, payloadJson, ct);
        }
    }

    private static bool PwshAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pwsh", Arguments = "-NoProfile -Command \"exit 0\"",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            });
            if (p is null) return false;
            p.WaitForExit(10000);
            return true;
        }
        catch { return false; }
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

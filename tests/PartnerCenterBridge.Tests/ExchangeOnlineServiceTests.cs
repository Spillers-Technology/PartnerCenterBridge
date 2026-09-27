using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Exchange;

namespace PartnerCenterBridge.Tests;

public class ExchangeOnlineServiceTests
{
    private const string ContosoTenantId = "11111111-2222-3333-4444-555555555555";

    // DefaultDomain deliberately names another organization: it must never reach the connect call.
    private static Tenant Tenant() => new() { TenantId = ContosoTenantId, DisplayName = "Contoso", DefaultDomain = "fabrikam.onmicrosoft.com" };

    private static ExchangeOnlineService Service(IPwshRunner runner, ITenantExchangeOrganizationProvider? organizations = null) => new(
        runner,
        organizations ?? new FixedOrganization("contoso.onmicrosoft.com"),
        Options.Create(new ExchangeOptions { AppId = "app-1", CertificatePath = "/certs/exo.pfx" }),
        NullLogger<ExchangeOnlineService>.Instance);

    [Fact]
    public async Task Connect_uses_the_graph_verified_organization_never_the_default_domain()
    {
        var runner = new FakeRunner(new PwshResult(0, """{"success":true,"steps":[],"data":null,"notFound":false}""", ""));
        var organizations = new FixedOrganization("contoso.onmicrosoft.com");

        await Service(runner, organizations).ConvertToSharedAsync(Tenant(), "ada@contoso.com", null, false);

        using var payload = JsonDocument.Parse(runner.LastPayload!);
        Assert.Equal("contoso.onmicrosoft.com", payload.RootElement.GetProperty("connect").GetProperty("organization").GetString());
        Assert.Equal(ContosoTenantId, payload.RootElement.GetProperty("expectedTenantId").GetString());
        Assert.DoesNotContain("fabrikam", runner.LastPayload!);
        Assert.Equal(ContosoTenantId, organizations.LastTenant!.TenantId);
    }

    [Fact]
    public async Task An_unresolvable_organization_fails_before_pwsh_runs()
    {
        var runner = new FakeRunner(new PwshResult(0, """{"success":true,"steps":[]}""", ""));

        var ex = await Assert.ThrowsAsync<ExchangeOrganizationException>(
            () => Service(runner, new FailingOrganization()).ConvertToSharedAsync(Tenant(), "ada@contoso.com", null, false));

        Assert.Contains("Microsoft Graph", ex.Message);
        Assert.Null(runner.LastPayload);
    }

    [Fact]
    public async Task A_failed_tenant_check_is_a_hard_failure_for_every_operation()
    {
        var runner = new FakeRunner(new PwshResult(0,
            """
            {"success":false,"steps":[{"name":"Connect","success":true,"detail":"contoso.onmicrosoft.com"},
              {"name":"Tenant check","success":false,"detail":"connected to tenant 99999999-0000-0000-0000-000000000000, expected 11111111-2222-3333-4444-555555555555"}],
             "data":null,"notFound":false,"tenantMismatch":true}
            """, ""));
        var service = Service(runner);

        var ex = await Assert.ThrowsAsync<ExchangeTenantMismatchException>(
            () => service.ConvertToSharedAsync(Tenant(), "ada@contoso.com", null, false));
        Assert.StartsWith("Exchange Online connected to a different organization than Contoso; nothing was changed.", ex.Message);
        await Assert.ThrowsAsync<ExchangeTenantMismatchException>(() => service.ListSharedMailboxesAsync(Tenant()));
        await Assert.ThrowsAsync<ExchangeTenantMismatchException>(() => service.GetMailboxAsync(Tenant(), "ada@contoso.com"));
        await Assert.ThrowsAsync<ExchangeTenantMismatchException>(() => service.NudgeArchiveAsync(Tenant(), "ada@contoso.com"));
    }

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
        await WithFakeExchangeAsync(organization, async (service, tenant) =>
        {
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
        });
    }

    /// <summary>
    /// The real exo-op.ps1 remediateArchive: a retention policy already on the mailbox is reported as
    /// unchanged, never as a verified assignment; an assignment is verified against the requested policy.
    /// </summary>
    [Fact]
    public async Task Archive_script_tells_an_existing_retention_policy_from_an_assignment()
    {
        if (!PwshAvailable()) return;
        await WithFakeExchangeAsync("contoso.onmicrosoft.com", async (service, tenant) =>
        {
            var workflow = new PartnerCenterBridge.Exchange.Workflows.MailboxArchiveWorkflow(service);
            Dictionary<string, string> Inputs(string id) => new()
            {
                ["identity"] = id, ["retentionPolicyName"] = "Custom MRM",
                ["enableAutoExpandingArchive"] = "false", ["clearProcessingBlocks"] = "false", ["triggerProcessing"] = "false"
            };

            var existing = await workflow.RemediateAsync(tenant, Inputs("haspolicy@contoso.com"));
            var step = existing.Steps.FindIndex(s => s.Name == "Assign retention policy");
            Assert.Equal("already assigned: Legacy Policy", existing.Steps[step].Detail);
            Assert.Contains(step, existing.UnchangedSteps);
            Assert.DoesNotContain(existing.Verification!, v => v.Name == "Assign retention policy");

            var assigned = await workflow.RemediateAsync(tenant, Inputs("nopolicy@contoso.com"));
            step = assigned.Steps.FindIndex(s => s.Name == "Assign retention policy");
            Assert.Equal("assigned: Custom MRM", assigned.Steps[step].Detail);
            Assert.True(Assert.Single(assigned.Verification!, v => v.Name == "Assign retention policy").Passed);
        });
    }

    /// <summary>
    /// The real exo-op.ps1 when Exchange Online authenticates into a different directory than the
    /// tenant the operation is for: the tenant check fails, no operation cmdlet runs, and the
    /// service throws the tenant-mismatch error.
    /// </summary>
    [Fact]
    public async Task Script_performs_no_operation_when_connected_to_another_tenant()
    {
        if (!PwshAvailable()) return;
        await WithFakeExchangeAsync("fabrikam.onmicrosoft.com", async (service, tenant, calls) =>
        {
            var ex = await Assert.ThrowsAsync<ExchangeTenantMismatchException>(
                () => service.ConvertToSharedAsync(tenant, "ada@contoso.com", "mgr@contoso.com", true));
            Assert.Contains("different organization than Contoso; nothing was changed", ex.Message);
            Assert.Contains("99999999-0000-0000-0000-000000000000", ex.Message);

            await Assert.ThrowsAsync<ExchangeTenantMismatchException>(() => service.RemediateArchiveAsync(
                tenant, "ada@contoso.com", new ArchiveRemediationOptions()));

            var log = calls();
            Assert.Equal(2, log.Count(c => c.StartsWith("Connect-ExchangeOnline")));
            Assert.Equal(2, log.Count(c => c == "Disconnect-ExchangeOnline"));
            Assert.DoesNotContain(log, c => c.StartsWith("Set-Mailbox") || c.StartsWith("Enable-Mailbox")
                || c.StartsWith("Start-ManagedFolderAssistant") || c.StartsWith("Get-Mailbox") || c.StartsWith("Get-EXOMailbox"));
        });
    }

    [Fact]
    public async Task Script_runs_the_operation_when_the_connected_tenant_matches()
    {
        if (!PwshAvailable()) return;
        await WithFakeExchangeAsync("contoso.onmicrosoft.com", async (service, tenant, calls) =>
        {
            var result = await service.ConvertToSharedAsync(tenant, "ada@contoso.com", null, false);

            Assert.True(result.Succeeded);
            Assert.Contains(result.Steps, s => s.Name == "Tenant check" && s.Success);
            Assert.Contains("Set-Mailbox ada@contoso.com", calls());
            Assert.Contains(calls(), c => c.StartsWith("Connect-ExchangeOnline org=contoso.onmicrosoft.com "));
        });
    }

    /// <summary>
    /// End to end over stdin with a PFX password: the script receives the password intact (non-ASCII
    /// included) while no temp file written during the run contains it.
    /// </summary>
    [Fact]
    public async Task Script_gets_the_pfx_password_over_stdin_and_no_temp_file_holds_it()
    {
        if (!PwshAvailable()) return;
        var secret = "pfx-" + Guid.NewGuid().ToString("N") + "-" + (char)0xE4 + (char)0x20AC;   // non-ASCII: a-umlaut, euro sign
        var options = new ExchangeOptions { AppId = "app-1", CertificatePath = "/certs/exo.pfx", CertificatePassword = secret };
        await WithFakeExchangeAsync("contoso.onmicrosoft.com", async (service, tenant, calls) =>
        {
            var result = await service.ConvertToSharedAsync(tenant, "ada@contoso.com", null, false);

            Assert.True(result.Succeeded, string.Join("; ", result.Steps.Select(s => $"{s.Name}: {s.Detail}")));
            var connect = Assert.Single(calls(), c => c.StartsWith("Connect-ExchangeOnline"));
            Assert.EndsWith($"file=/certs/exo.pfx thumb= pwd={secret}", connect);
            Assert.DoesNotContain(calls(), c => c.StartsWith("LEAK"));
        }, options);
    }

    [Fact]
    public async Task Script_connects_by_thumbprint_without_any_password()
    {
        if (!PwshAvailable()) return;
        var options = new ExchangeOptions
        {
            AppId = "app-1", CertificateThumbprint = "83213AEAC56D61C97AEE5C1528F4AC5EBA7321C1",
            CertificatePath = "/certs/exo.pfx", CertificatePassword = "ignored-secret"
        };
        await WithFakeExchangeAsync("contoso.onmicrosoft.com", async (service, tenant, calls) =>
        {
            Assert.True((await service.ConvertToSharedAsync(tenant, "ada@contoso.com", null, false)).Succeeded);
            var connect = Assert.Single(calls(), c => c.StartsWith("Connect-ExchangeOnline"));
            Assert.EndsWith("file= thumb=83213AEAC56D61C97AEE5C1528F4AC5EBA7321C1 pwd=", connect);
        }, options);
    }

    [Fact]
    public async Task Thumbprint_option_sends_no_password_or_pfx_path()
    {
        var runner = new FakeRunner(new PwshResult(0, """{"success":true,"steps":[]}""", ""));
        var service = new ExchangeOnlineService(runner, new FixedOrganization("contoso.onmicrosoft.com"),
            Options.Create(new ExchangeOptions
            {
                AppId = "app-1", CertificateThumbprint = " 83213AEAC56D61C97AEE5C1528F4AC5EBA7321C1 ",
                CertificatePath = "/certs/exo.pfx", CertificatePassword = "do-not-send"
            }),
            NullLogger<ExchangeOnlineService>.Instance);

        await service.ConvertToSharedAsync(Tenant(), "ada@contoso.com", null, false);

        using var payload = JsonDocument.Parse(runner.LastPayload!);
        var connect = payload.RootElement.GetProperty("connect");
        Assert.Equal("83213AEAC56D61C97AEE5C1528F4AC5EBA7321C1", connect.GetProperty("certificateThumbprint").GetString());
        Assert.False(connect.TryGetProperty("certificatePassword", out _));
        Assert.False(connect.TryGetProperty("certificatePath", out _));
        Assert.DoesNotContain("do-not-send", runner.LastPayload!);
    }

    /// <summary>
    /// The script pwsh runs is a fresh, unpredictably named copy that matches the embedded resource,
    /// holds no secret, cannot be rewritten while in use (Windows), and is gone afterwards.
    /// </summary>
    [Fact]
    public async Task Each_run_uses_a_private_verified_script_copy_that_is_deleted_afterwards()
    {
        var embedded = EmbeddedScriptText();
        var seen = new List<string>();
        var runner = new InspectingRunner(path =>
        {
            seen.Add(path);
            Assert.True(File.Exists(path));
            Assert.NotEqual(Path.Combine(Path.GetTempPath(), "pcb-exo-op.ps1"), path);
            Assert.Equal(embedded, File.ReadAllText(path));
            Assert.DoesNotContain("pfx-secret", File.ReadAllText(path));
            if (OperatingSystem.IsWindows())
                Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "Set-Mailbox -Identity everyone"));
        });
        var service = new ExchangeOnlineService(runner, new FixedOrganization("contoso.onmicrosoft.com"),
            Options.Create(new ExchangeOptions { AppId = "app-1", CertificatePath = "/certs/exo.pfx", CertificatePassword = "pfx-secret" }),
            NullLogger<ExchangeOnlineService>.Instance);

        await service.ConvertToSharedAsync(Tenant(), "ada@contoso.com", null, false);
        await service.ConvertToSharedAsync(Tenant(), "ada@contoso.com", null, false);

        Assert.Equal(2, seen.Distinct().Count());
        Assert.All(seen, p => Assert.False(File.Exists(p)));
        Assert.Contains("pfx-secret", runner.LastPayload!);   // the password travels only in the stdin payload
    }

    [Fact]
    public void Extraction_refuses_a_copy_that_does_not_match_the_expected_hash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcb-extract-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var content = System.Text.Encoding.UTF8.GetBytes("'hello'");
            var wrongHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("'tampered'"));

            Assert.Throws<InvalidOperationException>(() => ExtractedScript.Extract(content, wrongHash, dir));
            Assert.Empty(Directory.GetFiles(dir));

            using (var ok = ExtractedScript.Extract(content, System.Security.Cryptography.SHA256.HashData(content), dir))
                Assert.Equal("'hello'", File.ReadAllText(ok.Path));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(true, true, true)]     // Windows, certificate in the store: available without any PFX file
    [InlineData(true, false, false)]   // Windows, thumbprint not in the store
    [InlineData(false, true, false)]   // not Windows: the EXO module only supports thumbprints on Windows
    public void Capability_accepts_a_store_thumbprint_instead_of_a_pfx(bool windows, bool inStore, bool available)
    {
        var options = Options.Create(new ExchangeOptions
        {
            AppId = "app-1", CertificateThumbprint = "83213AEAC56D61C97AEE5C1528F4AC5EBA7321C1", PwshPath = "/bin/pwsh"
        });
        var status = new ExchangeCapability(options, p => p == "/bin/pwsh", () => "/bin", _ => inStore, windows).Check();

        Assert.Equal(available, status.Available);
    }

    private static string EmbeddedScriptText()
    {
        using var stream = typeof(ExchangeOnlineService).Assembly
            .GetManifestResourceStream("PartnerCenterBridge.Exchange.Scripts.exo-op.ps1")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class InspectingRunner(Action<string> inspect) : IPwshRunner
    {
        public string? LastPayload { get; private set; }
        public Task<PwshResult> RunAsync(string scriptPath, string payloadJson, CancellationToken ct = default)
        {
            inspect(scriptPath);
            LastPayload = payloadJson;
            return Task.FromResult(new PwshResult(0, """{"success":true,"steps":[]}""", ""));
        }
    }

    // A stand-in ExchangeOnlineManagement module for running the real exo-op.ps1 without Exchange.
    // Every cmdlet appends a line to $env:PCB_FAKE_EXO_LOG so tests can assert what ran. The
    // connection reports the Contoso tenant id unless the organization is fabrikam.
    private const string FakeExchangeModule =
        """
        function Write-Call($text) { if ($env:PCB_FAKE_EXO_LOG) { Add-Content -LiteralPath $env:PCB_FAKE_EXO_LOG -Value $text } }
        $script:Org = $null
        function Connect-ExchangeOnline { param($AppId, $Organization, $ShowBanner, $CertificateFilePath, $CertificatePassword, $CertificateThumbprint)
            $secret = if ($CertificatePassword) { [Net.NetworkCredential]::new('', $CertificatePassword).Password } else { '' }
            Write-Call "Connect-ExchangeOnline org=$Organization file=$CertificateFilePath thumb=$CertificateThumbprint pwd=$secret"
            # While pwsh holds the password, no recently written temp file may contain it.
            if ($secret) {
                Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Length -lt 1MB -and $_.LastWriteTime -gt (Get-Date).AddMinutes(-10) } |
                    ForEach-Object { try { if ((Get-Content -Raw -LiteralPath $_.FullName -ErrorAction Stop) -like "*$secret*") { Write-Call "LEAK $($_.Name)" } } catch { } } }
            if ($Organization -like 'nocert*') { throw "The certificate file '$CertificateFilePath' could not be found." }
            $script:Org = $Organization }
        function Get-ConnectionInformation {
            $tid = if ($script:Org -like 'fabrikam*') { '99999999-0000-0000-0000-000000000000' } else { '11111111-2222-3333-4444-555555555555' }
            [pscustomobject]@{ State = 'Connected'; Organization = $script:Org; TenantID = $tid } }
        function Disconnect-ExchangeOnline { param($Confirm) Write-Call 'Disconnect-ExchangeOnline' }
        function Get-EXOMailbox { [CmdletBinding()] param($Identity, $Properties, $RecipientTypeDetails, $ResultSize)
            Write-Call "Get-EXOMailbox $Identity"
            switch -Wildcard ($Identity) {
                'nobody@*' { Write-Error -Message "The operation couldn't be performed because object '$Identity' couldn't be found." -Category ObjectNotFound -ErrorAction Stop }
                'gone@*' { throw "Error while querying REST service. HttpStatusCode=404 ErrorMessage=The operation couldn't be performed because object '$Identity' couldn't be found on 'EURPR01A001.PROD.OUTLOOK.COM'." }
                'busy@*' { throw 'Server is busy; the request was throttled.' }
                default { [pscustomobject]@{ UserPrincipalName = $Identity; DisplayName = 'Ada'; RecipientTypeDetails = 'UserMailbox';
                                             ForwardingSmtpAddress = $null; DeliverToMailboxAndForward = $false } }
            } }
        $script:Assigned = $null
        function Get-Mailbox { param($Identity)
            Write-Call "Get-Mailbox $Identity"
            $policy = if ($Identity -like 'haspolicy@*') { 'Legacy Policy' } elseif ($script:Assigned) { $script:Assigned } else { '' }
            [pscustomobject]@{ UserPrincipalName = $Identity; ArchiveGuid = [guid]::NewGuid(); ArchiveStatus = 'Active';
                               AutoExpandingArchiveEnabled = $true; ArchiveQuota = '100 GB'; ArchiveWarningQuota = '90 GB';
                               ProhibitSendReceiveQuota = '50 GB'; RetentionPolicy = $policy; RetentionHoldEnabled = $false;
                               ElcProcessingDisabled = $false } }
        function Get-MailboxStatistics { param($Identity, [switch]$Archive, $ErrorAction)
            [pscustomobject]@{ TotalItemSize = '1 GB'; ItemCount = 10 } }
        function Set-Mailbox { param($Identity, $RetentionPolicy, $Type, $ForwardingSmtpAddress, $DeliverToMailboxAndForward, $RetentionHoldEnabled, $ElcProcessingDisabled)
            Write-Call "Set-Mailbox $Identity"
            if ($RetentionPolicy) { $script:Assigned = $RetentionPolicy } }
        function Enable-Mailbox { param($Identity, [switch]$Archive, [switch]$AutoExpandingArchive) Write-Call "Enable-Mailbox $Identity" }
        function Start-ManagedFolderAssistant { param($Identity) Write-Call "Start-ManagedFolderAssistant $Identity" }
        """;

    private static Task WithFakeExchangeAsync(string organization, Func<ExchangeOnlineService, Tenant, Task> body) =>
        WithFakeExchangeAsync(organization, (service, tenant, _) => body(service, tenant));

    /// <summary>
    /// Runs <paramref name="body"/> against the real script and the stand-in module. The tenant is
    /// Contoso; <paramref name="organization"/> is what the (stubbed) Graph resolution returns, and the
    /// tenant's DefaultDomain names yet another domain that must never be used. The third argument
    /// reads the stand-in's call log.
    /// </summary>
    private static async Task WithFakeExchangeAsync(
        string organization, Func<ExchangeOnlineService, Tenant, Func<string[]>, Task> body, ExchangeOptions? options = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcb-exo-fake-" + Guid.NewGuid().ToString("N"));
        var module = Path.Combine(dir, "modules", "ExchangeOnlineManagement");
        Directory.CreateDirectory(module);
        await File.WriteAllTextAsync(Path.Combine(module, "ExchangeOnlineManagement.psm1"), FakeExchangeModule);
        var log = Path.Combine(dir, "calls.log");
        string[] Calls() => File.Exists(log) ? File.ReadAllLines(log) : Array.Empty<string>();
        try
        {
            var service = new ExchangeOnlineService(new FakeModuleRunner(Path.Combine(dir, "modules"), dir, log),
                new FixedOrganization(organization),
                Options.Create(options ?? new ExchangeOptions { AppId = "app-1", CertificatePath = "/certs/exo.pfx" }),
                NullLogger<ExchangeOnlineService>.Instance);
            await body(service, new Tenant { TenantId = ContosoTenantId, DisplayName = "Contoso", DefaultDomain = "wrong.example.com" }, Calls);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Runs the given script through a wrapper that puts the stand-in module first on PSModulePath.</summary>
    private sealed class FakeModuleRunner(string modulePath, string workDir, string callLog) : IPwshRunner
    {
        public async Task<PwshResult> RunAsync(string scriptPath, string payloadJson, CancellationToken ct = default)
        {
            var wrapper = Path.Combine(workDir, $"wrapper-{Guid.NewGuid():N}.ps1");
            // The payload still reaches the real script on the process's stdin (PwshRunner writes it).
            await File.WriteAllTextAsync(wrapper,
                $"$env:PCB_FAKE_EXO_LOG = '{callLog.Replace("'", "''")}'\n" +
                $"$env:PSModulePath = '{modulePath.Replace("'", "''")}' + [IO.Path]::PathSeparator + $env:PSModulePath\n" +
                $"& '{scriptPath.Replace("'", "''")}'\n", ct);
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

    private sealed class FixedOrganization(string organization) : ITenantExchangeOrganizationProvider
    {
        public Tenant? LastTenant { get; private set; }
        public Task<string> GetOrganizationAsync(Tenant tenant, CancellationToken ct = default)
        {
            LastTenant = tenant;
            return Task.FromResult(organization);
        }
    }

    private sealed class FailingOrganization : ITenantExchangeOrganizationProvider
    {
        public Task<string> GetOrganizationAsync(Tenant tenant, CancellationToken ct = default) =>
            throw new ExchangeOrganizationException($"Could not read the organization of tenant {tenant.TenantId} from Microsoft Graph: forbidden.");
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

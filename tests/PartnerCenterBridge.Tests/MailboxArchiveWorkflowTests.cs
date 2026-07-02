using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Exchange.Workflows;

namespace PartnerCenterBridge.Tests;

public class MailboxArchiveWorkflowTests
{
    private static Tenant Tenant() => new() { TenantId = "t", DisplayName = "Contoso" };

    private static ArchiveState State(
        bool archiveEnabled = true, bool autoExpand = true, string? policy = "Default MRM Policy",
        bool retentionHold = false, bool elcDisabled = false) => new(
        "user@contoso.com", "49.5 GB", 250_000, "50 GB",
        archiveEnabled, archiveEnabled ? "Active" : "None", autoExpand,
        archiveEnabled ? "100 GB" : null, archiveEnabled ? "90 GB" : null,
        archiveEnabled ? "1.2 GB" : null, archiveEnabled ? 10_000 : 0,
        policy, retentionHold, elcDisabled);

    private sealed class StubExo : IExchangeOnlineService
    {
        public ArchiveState? DiagnoseState;
        public ArchiveRemediationResult RemediationResult = new();
        public ArchiveRemediationOptions? SeenOptions;
        public string? SeenIdentity;

        public Task<ArchiveState?> GetArchiveStateAsync(Tenant tenant, string identity, CancellationToken ct = default)
        {
            SeenIdentity = identity;
            return Task.FromResult(DiagnoseState);
        }

        public Task<ArchiveRemediationResult> RemediateArchiveAsync(
            Tenant tenant, string identity, ArchiveRemediationOptions options, CancellationToken ct = default)
        {
            SeenIdentity = identity;
            SeenOptions = options;
            return Task.FromResult(RemediationResult);
        }

        public Task<MailboxInfo?> GetMailboxAsync(Tenant tenant, string identity, CancellationToken ct = default)
            => Task.FromResult<MailboxInfo?>(null);
        public Task<ExoResult> ConvertToSharedAsync(Tenant tenant, string identity, string? fwd, bool deliver, CancellationToken ct = default)
            => Task.FromResult(new ExoResult());
        public Task<IReadOnlyList<MailboxInfo>> ListSharedMailboxesAsync(Tenant tenant, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MailboxInfo>>(Array.Empty<MailboxInfo>());
        public Task<ArchiveRemediationResult> NudgeArchiveAsync(Tenant tenant, string identity, CancellationToken ct = default)
            => Task.FromResult(new ArchiveRemediationResult());
    }

    [Fact]
    public async Task Diagnose_maps_blockers_and_warnings()
    {
        var exo = new StubExo { DiagnoseState = State(archiveEnabled: false, policy: null, retentionHold: true, elcDisabled: true) };

        var d = await new MailboxArchiveWorkflow(exo).DiagnoseAsync(Tenant(), new Dictionary<string, string> { ["identity"] = "user@contoso.com" });

        Assert.Contains(d.Findings, f => f.Name == "Archive" && f.Status == FindingStatus.Blocker);
        Assert.Contains(d.Findings, f => f.Name == "Retention policy" && f.Status == FindingStatus.Warning);
        Assert.Contains(d.Findings, f => f.Name == "Retention hold" && f.Status == FindingStatus.Warning);
        Assert.Contains(d.Findings, f => f.Name == "ELC processing" && f.Status == FindingStatus.Warning);
        Assert.False(d.Healthy);
        Assert.Equal("user@contoso.com", exo.SeenIdentity);
    }

    [Fact]
    public async Task Diagnose_is_healthy_when_posture_is_clean()
    {
        var exo = new StubExo { DiagnoseState = State() };

        var d = await new MailboxArchiveWorkflow(exo).DiagnoseAsync(Tenant(), new Dictionary<string, string> { ["identity"] = "user@contoso.com" });

        Assert.True(d.Healthy);
    }

    [Fact]
    public async Task Diagnose_blocks_on_unknown_mailbox()
    {
        var exo = new StubExo { DiagnoseState = null };

        var d = await new MailboxArchiveWorkflow(exo).DiagnoseAsync(Tenant(), new Dictionary<string, string> { ["identity"] = "ghost@contoso.com" });

        Assert.Contains(d.Findings, f => f.Name == "Mailbox lookup" && f.Status == FindingStatus.Blocker);
    }

    [Fact]
    public async Task Remediate_maps_inputs_to_options_and_state_to_poststate()
    {
        var exo = new StubExo
        {
            RemediationResult = new ArchiveRemediationResult
            {
                Steps = { new ProvisioningStep("Enable archive", true, "done") },
                State = State()
            }
        };

        var run = await new MailboxArchiveWorkflow(exo).RemediateAsync(Tenant(), new Dictionary<string, string>
        {
            ["identity"] = "user@contoso.com",
            ["retentionPolicyName"] = "Custom MRM",
            ["enableAutoExpandingArchive"] = "false",
            ["clearProcessingBlocks"] = "true",
            ["triggerProcessing"] = "true"
        });

        Assert.True(run.Succeeded);
        Assert.Equal("Custom MRM", exo.SeenOptions!.RetentionPolicyName);
        Assert.False(exo.SeenOptions.EnableAutoExpandingArchive);
        Assert.True(exo.SeenOptions.ClearProcessingBlocks);
        Assert.NotNull(run.PostState);
        Assert.True(run.PostState!.Healthy);
    }

    [Fact]
    public async Task Remediate_defaults_missing_flags_to_true()
    {
        var exo = new StubExo { RemediationResult = new ArchiveRemediationResult { Steps = { new ProvisioningStep("x", true) } } };

        await new MailboxArchiveWorkflow(exo).RemediateAsync(Tenant(), new Dictionary<string, string> { ["identity"] = "u" });

        Assert.True(exo.SeenOptions!.EnableAutoExpandingArchive);
        Assert.True(exo.SeenOptions.ClearProcessingBlocks);
        Assert.True(exo.SeenOptions.TriggerProcessing);
    }
}

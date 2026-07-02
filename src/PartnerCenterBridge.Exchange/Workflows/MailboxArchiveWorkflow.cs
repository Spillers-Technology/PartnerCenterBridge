using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Exchange.Workflows;

/// <summary>
/// The "mailbox full / not archiving" fix as a catalog workflow: diagnose the archive posture
/// (enabled, auto-expand, retention policy, the hidden processing blockers), then apply the
/// idempotent remediation and trigger the Managed Folder Assistant. The move runs asynchronously,
/// so re-running remediate doubles as the nudge.
/// </summary>
internal sealed class MailboxArchiveWorkflow : IWorkflow
{
    private readonly IExchangeOnlineService _exo;

    public MailboxArchiveWorkflow(IExchangeOnlineService exo) => _exo = exo;

    public string Id => "mailbox-archive";
    public string Name => "Mailbox archive repair";
    public string Description => "Fix a full mailbox that is not archiving: enable archive + auto-expand, ensure a retention policy, clear processing blockers, and kick the Managed Folder Assistant. Re-run to nudge the asynchronous move.";
    public string Category => "Mailbox";

    public IReadOnlyList<WorkflowInput> Inputs =>
    [
        new("identity", "Mailbox UPN or alias", "user@contoso.com"),
        new("retentionPolicyName", "Retention policy to assign if none", "Default MRM Policy", Required: false, Default: "Default MRM Policy"),
        new("enableAutoExpandingArchive", "Enable auto-expanding archive", Required: false, Default: "true", Type: "bool"),
        new("clearProcessingBlocks", "Clear retention hold / ELC blocks", Required: false, Default: "true", Type: "bool"),
        new("triggerProcessing", "Trigger the Managed Folder Assistant", Required: false, Default: "true", Type: "bool")
    ];

    public async Task<DiagnosisResult> DiagnoseAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var result = new DiagnosisResult();
        var state = await _exo.GetArchiveStateAsync(tenant, inputs["identity"], ct);
        if (state is null)
        {
            result.Findings.Add(new("Mailbox lookup", FindingStatus.Blocker, $"Mailbox '{inputs["identity"]}' not found."));
            return result;
        }
        result.Findings.AddRange(ToFindings(state));
        return result;
    }

    public async Task<WorkflowRunResult> RemediateAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var options = new ArchiveRemediationOptions
        {
            EnableAutoExpandingArchive = Flag(inputs, "enableAutoExpandingArchive"),
            RetentionPolicyName = inputs.TryGetValue("retentionPolicyName", out var p) && !string.IsNullOrWhiteSpace(p) ? p : null,
            ClearProcessingBlocks = Flag(inputs, "clearProcessingBlocks"),
            TriggerProcessing = Flag(inputs, "triggerProcessing")
        };

        var exo = await _exo.RemediateArchiveAsync(tenant, inputs["identity"], options, ct);

        var run = new WorkflowRunResult { Steps = exo.Steps };
        if (exo.State is not null)
        {
            var post = new DiagnosisResult();
            post.Findings.AddRange(ToFindings(exo.State));
            run.PostState = post;
        }
        return run;
    }

    /// <summary>Missing bool inputs default to true - every switch defaults to the safe, full fix.</summary>
    private static bool Flag(IReadOnlyDictionary<string, string> inputs, string key) =>
        !inputs.TryGetValue(key, out var v) || !bool.TryParse(v, out var b) || b;

    private static IEnumerable<Finding> ToFindings(ArchiveState s)
    {
        yield return new("Primary mailbox", FindingStatus.Info,
            $"{s.PrimarySize} / quota {s.ProhibitSendReceiveQuota} ({s.PrimaryItemCount} items)");

        yield return s.ArchiveEnabled
            ? new("Archive", FindingStatus.Ok, $"enabled ({s.ArchiveStatus})")
            : new("Archive", FindingStatus.Blocker, "Archive is not enabled.");

        if (s.ArchiveEnabled)
        {
            yield return new("Archive size", FindingStatus.Info,
                $"{s.ArchiveSize ?? "n/a"} / quota {s.ArchiveQuota ?? "n/a"} ({s.ArchiveItemCount} items)");
            yield return s.AutoExpandingArchiveEnabled
                ? new("Auto-expanding archive", FindingStatus.Ok, "enabled")
                : new("Auto-expanding archive", FindingStatus.Warning, "Not enabled - the archive will hit its quota.");
        }

        yield return string.IsNullOrEmpty(s.RetentionPolicy)
            ? new("Retention policy", FindingStatus.Warning, "None assigned - nothing moves items to the archive.")
            : new("Retention policy", FindingStatus.Ok, s.RetentionPolicy);

        yield return s.RetentionHoldEnabled
            ? new("Retention hold", FindingStatus.Warning, "Enabled - silently blocks the Managed Folder Assistant.")
            : new("Retention hold", FindingStatus.Ok, "off");

        yield return s.ElcProcessingDisabled
            ? new("ELC processing", FindingStatus.Warning, "Disabled - the assistant skips this mailbox.")
            : new("ELC processing", FindingStatus.Ok, "enabled");
    }
}

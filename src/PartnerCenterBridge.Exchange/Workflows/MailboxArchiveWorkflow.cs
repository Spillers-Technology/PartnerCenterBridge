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

        var run = new WorkflowRunResult { Steps = exo.Steps, Verification = new() };
        if (exo.State is not null)
        {
            var post = new DiagnosisResult();
            post.Findings.AddRange(ToFindings(exo.State));
            run.PostState = post;
        }
        VerifySteps(run, exo.State, options.RetentionPolicyName);
        return run;
    }

    /// <summary>
    /// Desired-state verification per step, against the archive state the script re-read after
    /// remediating: each step is checked for exactly the property it sets. Steps that found the
    /// setting already in place changed nothing; triggering the Managed Folder Assistant starts an
    /// asynchronous job nothing can confirm yet. A retention policy that was already assigned is
    /// reported unchanged; an assignment is verified only when the re-read policy is the one requested.
    /// </summary>
    private static void VerifySteps(WorkflowRunResult run, ArchiveState? state, string? requestedPolicy)
    {
        for (var i = 0; i < run.Steps.Count; i++)
        {
            var step = run.Steps[i];
            if (!step.Success) continue;
            var detail = step.Detail ?? "";
            if (step.Name == "Connect" || detail.StartsWith("already", StringComparison.OrdinalIgnoreCase)
                                       || detail.Equals("not set", StringComparison.OrdinalIgnoreCase))
            {
                run.UnchangedSteps.Add(i);
                continue;
            }
            (bool Ok, string Detail)? check = step.Name switch
            {
                "Enable archive" => state is null ? null : (state.ArchiveEnabled, state.ArchiveEnabled ? "Archive is enabled on re-read." : "Archive is not enabled on re-read."),
                "Enable auto-expanding archive" => state is null ? null : (state.AutoExpandingArchiveEnabled, state.AutoExpandingArchiveEnabled ? "Auto-expanding archive is enabled on re-read." : "Auto-expanding archive is not enabled on re-read."),
                "Assign retention policy" => state is null ? null : RetentionPolicyCheck(state, requestedPolicy),
                "Clear retention hold" => state is null ? null : (!state.RetentionHoldEnabled, state.RetentionHoldEnabled ? "Retention hold is still enabled on re-read." : "Retention hold is off on re-read."),
                "Enable ELC processing" => state is null ? null : (!state.ElcProcessingDisabled, state.ElcProcessingDisabled ? "ELC processing is still disabled on re-read." : "ELC processing is enabled on re-read."),
                _ => null
            };
            if (step.Name == "Trigger Managed Folder Assistant")
                run.CannotVerify(i, step.Name, "The Managed Folder Assistant runs asynchronously; starting it was acknowledged but its progress cannot be confirmed yet.");
            else if (check is { } c)
                run.Verify(i, step.Name, c.Ok, c.Detail);
            else if (state is null)
                run.Verify(i, step.Name, false, "The archive state could not be re-read after remediation.");
            // Anything else (an unknown step) has no check and is therefore reported unverified.
        }
    }

    private static (bool Ok, string Detail) RetentionPolicyCheck(ArchiveState state, string? requested)
    {
        var actual = state.RetentionPolicy?.Trim() ?? "";
        if (requested is null)
            return (false, "No retention policy was requested, so the assignment cannot be confirmed.");
        if (string.Equals(actual, requested.Trim(), StringComparison.OrdinalIgnoreCase))
            return (true, $"Retention policy {actual} on re-read.");
        return (false, actual.Length == 0
            ? $"No retention policy on re-read; {requested.Trim()} was requested."
            : $"Retention policy on re-read is '{actual}', not the requested '{requested.Trim()}'.");
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

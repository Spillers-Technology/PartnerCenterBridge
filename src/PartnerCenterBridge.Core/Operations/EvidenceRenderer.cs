using System.Globalization;
using System.Text;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Core.Operations;

/// <summary>
/// Turns structured <see cref="OperationEvidence"/> into human text: short ticket notes and a full
/// Markdown record. Everything is generated from the evidence itself, so the text can never claim
/// more than the recorded facts support.
/// </summary>
public static class EvidenceRenderer
{
    /// <summary>"1 group" / "3 groups" (irregular plurals passed explicitly).</summary>
    public static string Count(int n, string singular, string? plural = null) =>
        $"{n} {(n == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>Human wording for an outcome, used in notes and Markdown.</summary>
    public static string Describe(Outcome outcome) => outcome switch
    {
        Outcome.Succeeded => "Succeeded (all changes verified)",
        Outcome.PartiallySucceeded => "Partially succeeded",
        Outcome.Failed => "Failed",
        Outcome.NoChangeNeeded => "No change needed",
        Outcome.VerificationFailed => "Verification failed (changes reported but not confirmed)",
        Outcome.Planned => "Planned (nothing applied)",
        // Acceptance is all that is known: nothing confirmed the changes took effect (a password
        // value cannot be read back; a device retire may be requested but not complete).
        Outcome.CompletedUnverified =>
            "Completed, unverified (Microsoft accepted the requested changes, but PCB could not confirm their effect yet)",
        _ => outcome.ToString()
    };

    /// <summary>
    /// Generic ticket notes for evidence built by the workflow adapter (diagnose/remediate runs).
    /// Planned operations write their own, more specific notes.
    /// </summary>
    public static string GenericTicketNotes(OperationEvidence e, bool isDiagnosis)
    {
        var sb = new StringBuilder();
        var target = e.Target is null ? "" : $" for {e.Target.DisplayName}";

        if (isDiagnosis)
        {
            if (e.Outcome == Outcome.Failed)
            {
                sb.Append($"Ran {e.OperationName} diagnosis{target}, but it did not complete. No changes were made.");
                AppendFailures(sb, e);
                return sb.ToString().Trim();
            }
            var blockers = e.Preflight.Where(f => f.Status == FindingStatus.Blocker).ToList();
            var warnings = e.Preflight.Where(f => f.Status == FindingStatus.Warning).ToList();
            sb.Append($"Ran {e.OperationName} diagnosis{target}. ");
            if (blockers.Count == 0 && warnings.Count == 0)
                sb.Append("No problems were found. ");
            else
            {
                sb.Append($"Found {Count(blockers.Count, "blocker")} and {Count(warnings.Count, "warning")}: ");
                sb.Append(string.Join("; ", blockers.Concat(warnings).Select(FindingText)));
                sb.Append(". ");
            }
            sb.Append("No changes were made.");
            AppendFailures(sb, e);
            return sb.ToString().Trim();
        }

        sb.Append($"Ran {e.OperationName}{target}. ");
        var attempted = e.Changes.Where(c => c.Attempted).ToList();
        var ok = attempted.Where(c => c.Succeeded).ToList();
        var bad = attempted.Where(c => !c.Succeeded).ToList();
        if (attempted.Count > 0)
        {
            sb.Append($"{ok.Count} of {Count(attempted.Count, "step")} reported success");
            if (ok.Count > 0) sb.Append(": ").Append(string.Join(", ", ok.Select(c => c.Action)));
            sb.Append(". ");
        }
        if (bad.Count > 0)
            sb.Append("Failed: ").Append(string.Join("; ", bad.Select(c => $"{c.Action} ({c.Detail ?? "no detail"})"))).Append(". ");

        AppendVerification(sb, e);
        AppendFailures(sb, e);
        sb.Append($"Outcome: {Describe(e.Outcome)}.");
        return sb.ToString().Trim();
    }

    private static void AppendVerification(StringBuilder sb, OperationEvidence e)
    {
        if (e.Verification.Count == 0)
        {
            sb.Append("No post-change verification was recorded, so the result is unconfirmed. ");
            return;
        }
        var checks = e.Verification.Where(v => !v.Unverifiable).ToList();
        var unverifiable = e.Verification.Where(v => v.Unverifiable).ToList();
        var failed = checks.Where(v => !v.Passed).ToList();
        if (checks.Count > 0 && failed.Count == 0)
            sb.Append($"Post-change verification passed all {Count(checks.Count, "check")}. ");
        else if (failed.Count > 0)
            sb.Append($"Post-change verification: {failed.Count} of {Count(checks.Count, "check")} did not pass (")
              .Append(string.Join("; ", failed.Select(Text)))
              .Append("). ");
        if (unverifiable.Count > 0)
            sb.Append("Acknowledged but not independently verifiable: ")
              .Append(string.Join("; ", unverifiable.Select(Text)))
              .Append(". ");

        static string Text(VerificationCheck v) => v.Detail is null ? v.Name : $"{v.Name}: {v.Detail.TrimEnd('.')}";
    }

    private static void AppendFailures(StringBuilder sb, OperationEvidence e)
    {
        if (e.Failures.Count == 0) return;
        if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        sb.Append("Errors: ").Append(string.Join("; ", e.Failures.Select(f => f.TrimEnd('.')))).Append(". ");
    }

    private static string FindingText(Finding f) => f.Detail is null ? f.Name : $"{f.Name} - {f.Detail}";

    /// <summary>A complete, ticket-attachable Markdown record of the run.</summary>
    public static string Markdown(OperationEvidence e)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine($"# {Escape(e.OperationName)}");
        sb.AppendLine();
        sb.AppendLine($"- **Outcome:** {Describe(e.Outcome)}");
        sb.AppendLine($"- **Tenant:** {Escape(e.Tenant.DisplayName)} (`{e.Tenant.TenantId}`)");
        if (e.Target is not null)
            sb.AppendLine($"- **Target:** {Escape(e.Target.DisplayName)} ({e.Target.Kind} `{e.Target.Id}`)");
        sb.AppendLine($"- **Operator:** {Escape(e.Operator)}");
        sb.AppendLine($"- **Started:** {e.StartedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", inv)}");
        sb.AppendLine($"- **Completed:** {e.CompletedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", inv)}");
        sb.AppendLine($"- **Run id:** `{e.RunId}`");
        sb.AppendLine();
        sb.AppendLine("## Ticket notes");
        sb.AppendLine();
        sb.AppendLine(e.TicketNotes);
        sb.AppendLine();

        if (e.Preflight.Count > 0)
        {
            sb.AppendLine("## Preflight");
            sb.AppendLine();
            sb.AppendLine("| Check | Status | Detail |");
            sb.AppendLine("|---|---|---|");
            foreach (var f in e.Preflight)
                sb.AppendLine($"| {Cell(f.Name)} | {f.Status} | {Cell(f.Detail)} |");
            sb.AppendLine();
        }

        if (e.Plan.Count > 0)
        {
            sb.AppendLine("## Plan");
            sb.AppendLine();
            sb.AppendLine("| Item | Action | Category | Eligible | Destructive | Reason |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var p in e.Plan)
                sb.AppendLine($"| {Cell(p.ObjectName)} | {Cell(p.Action)} | {Cell(p.Category)} | {(p.Eligible ? "yes" : "no")} | {(p.Destructive ? "yes" : "no")} | {Cell(p.Reason)} |");
            sb.AppendLine();
        }

        if (e.Changes.Count > 0)
        {
            sb.AppendLine("## Changes");
            sb.AppendLine();
            sb.AppendLine("| Item | Action | Attempted | Reported | Detail |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var c in e.Changes)
                sb.AppendLine($"| {Cell(c.ObjectName)} | {Cell(c.Action)} | {(c.Attempted ? "yes" : "no")} | {(c.Succeeded ? "ok" : "not done")} | {Cell(c.Detail)} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Verification");
        sb.AppendLine();
        if (e.Verification.Count == 0)
            sb.AppendLine("No post-change verification was recorded.");
        else
        {
            sb.AppendLine("| Check | Result | Detail |");
            sb.AppendLine("|---|---|---|");
            foreach (var v in e.Verification)
                sb.AppendLine($"| {Cell(v.Name)} | {(v.Passed ? "passed" : v.Unverifiable ? "not verifiable" : "FAILED")} | {Cell(v.Detail)} |");
        }
        sb.AppendLine();

        AppendList(sb, "Failures", e.Failures);
        AppendList(sb, "Warnings", e.Warnings);
        AppendList(sb, "Limitations", e.Limitations);
        return sb.ToString().TrimEnd() + "\n";
    }

    private static void AppendList(StringBuilder sb, string title, List<string> items)
    {
        if (items.Count == 0) return;
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        foreach (var i in items) sb.AppendLine($"- {Escape(i)}");
        sb.AppendLine();
    }

    private static string Escape(string? s) => (s ?? "").Replace("\r", " ").Replace("\n", " ");
    private static string Cell(string? s) => Escape(s).Replace("|", "\\|");
}

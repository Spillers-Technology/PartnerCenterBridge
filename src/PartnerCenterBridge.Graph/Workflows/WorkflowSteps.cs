using System.Text.Json;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Graph.Workflows;

/// <summary>Runs a remediation step, recording success/failure without aborting the remaining steps.</summary>
internal static class WorkflowSteps
{
    public static async Task RunAsync(List<ProvisioningStep> steps, string name, Func<Task<string?>> action)
    {
        try { steps.Add(new ProvisioningStep(name, true, await action())); }
        catch (Exception ex) { steps.Add(new ProvisioningStep(name, false, ex.Message)); }
    }
}

/// <summary>A user's signInSessionsValidFromDateTime as read before a revocation.</summary>
/// <param name="Read">False when the read failed; the revocation then cannot be confirmed.</param>
/// <param name="Value">The cutoff (null when Graph returned none).</param>
/// <param name="Error">Why the read failed.</param>
internal sealed record SessionCutoff(bool Read, DateTimeOffset? Value, string? Error);

/// <summary>Desired-state re-reads shared by the identity workflows.</summary>
internal static class WorkflowVerify
{
    /// <summary>Clock skew tolerated between PCB's clock and Entra's when comparing the cutoff with the run's start.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>Reads signInSessionsValidFromDateTime; call it before revoking so the effect can be proven.</summary>
    public static async Task<SessionCutoff> ReadSessionCutoffAsync(GraphRestClient graph, string userId, CancellationToken ct)
    {
        try
        {
            using var doc = await graph.GetAsync($"/users/{Uri.EscapeDataString(userId)}?$select=id,signInSessionsValidFromDateTime", ct);
            return new SessionCutoff(true, ParseCutoff(doc.RootElement), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SessionCutoff(false, null, ex is GraphRequestException ? ex.Message : ex.GetBaseException().Message);
        }
    }

    public static DateTimeOffset? ParseCutoff(JsonElement user) =>
        user.TryGetProperty("signInSessionsValidFromDateTime", out var vf)
        && vf.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(vf.GetString(), out var dt) ? dt : null;

    /// <summary>
    /// Whether a revocation is proven by the cutoff moving: the value re-read after the revoke must be
    /// later than the one read before it (a cutoff that did not move means this revoke changed
    /// nothing -- a recent earlier revocation does not count) and not earlier than the run's own start,
    /// less <see cref="ClockSkew"/>. Unknown before or after values never pass.
    /// </summary>
    public static (bool Passed, string Detail) EvaluateCutoff(SessionCutoff before, DateTimeOffset? after, DateTimeOffset startedAt)
    {
        if (!before.Read)
            return (false, $"Not confirmed: the sign-in cutoff could not be read before the revoke ({before.Error}), so its change cannot be established.");
        if (after is null)
            return (false, "Not confirmed: signInSessionsValidFromDateTime was not returned on re-read.");
        if (before.Value is { } b && after <= b)
            return (false, $"Not confirmed: signInSessionsValidFromDateTime did not move forward on re-read (still {after:yyyy-MM-dd HH:mm:ss} UTC).");
        if (after < startedAt - ClockSkew)
            return (false, $"Not confirmed: signInSessionsValidFromDateTime on re-read ({after:yyyy-MM-dd HH:mm:ss} UTC) is earlier than this run.");
        return (true, $"Sessions valid only from {after:yyyy-MM-dd HH:mm:ss} UTC (was {(before.Value is { } v ? v.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" : "not set")}).");
    }

    /// <summary>
    /// Verifies a revokeSignInSessions step against the cutoff read before it (see
    /// <see cref="EvaluateCutoff"/>). Skipped when the step itself failed.
    /// </summary>
    public static async Task SessionsRevokedAsync(GraphRestClient graph, string userId, SessionCutoff before, DateTimeOffset startedAt,
        WorkflowRunResult run, int step, CancellationToken ct)
    {
        if (!run.Steps[step].Success) return;
        const string name = "Sessions revoked";
        var after = await ReadSessionCutoffAsync(graph, userId, ct);
        if (!after.Read)
        {
            run.Verify(step, name, false, $"Could not re-read the user: {after.Error}");
            return;
        }
        var (ok, detail) = EvaluateCutoff(before, after.Value, startedAt);
        run.Verify(step, name, ok, detail);
    }
}

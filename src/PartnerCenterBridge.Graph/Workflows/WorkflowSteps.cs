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

/// <summary>Desired-state re-reads shared by the identity workflows.</summary>
internal static class WorkflowVerify
{
    /// <summary>
    /// Verifies a revokeSignInSessions step: signInSessionsValidFromDateTime must have moved to (about)
    /// the run's start. Skipped when the step itself failed.
    /// </summary>
    public static async Task SessionsRevokedAsync(GraphRestClient graph, string userId, DateTimeOffset startedAt,
        WorkflowRunResult run, int step, CancellationToken ct)
    {
        if (!run.Steps[step].Success) return;
        const string name = "Sessions revoked";
        try
        {
            using var doc = await graph.GetAsync($"/users/{userId}?$select=id,signInSessionsValidFromDateTime", ct);
            var validFrom = doc.RootElement.TryGetProperty("signInSessionsValidFromDateTime", out var vf)
                            && vf.ValueKind == System.Text.Json.JsonValueKind.String
                            && DateTimeOffset.TryParse(vf.GetString(), out var dt) ? dt : (DateTimeOffset?)null;
            // Allow for clock skew between PCB and Entra.
            var ok = validFrom is not null && validFrom >= startedAt.AddMinutes(-5);
            run.Verify(step, name, ok, ok
                ? $"Sessions valid only from {validFrom:yyyy-MM-dd HH:mm:ss} UTC."
                : "signInSessionsValidFromDateTime did not move forward on re-read.");
        }
        catch (GraphRequestException ex)
        {
            run.Verify(step, name, false, $"Could not re-read the user: {ex.Message}");
        }
    }
}

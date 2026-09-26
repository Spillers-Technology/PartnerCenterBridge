using System.Text.Json;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Graph.Workflows;

/// <summary>
/// First-response containment for a suspected account compromise: diagnose the blast radius
/// (sign-in state, registered MFA, inbox rules that forward/redirect/delete mail - the classic
/// BEC persistence trick), then block sign-in, revoke every session, and disable the risky rules.
/// Rules are disabled rather than deleted so they survive as evidence.
/// </summary>
internal sealed class CompromisedAccountLockdownWorkflow : IWorkflow
{
    private readonly TenantGraphRest _graph;

    public CompromisedAccountLockdownWorkflow(TenantGraphRest graph) => _graph = graph;

    public string Id => "compromised-lockdown";
    public string Name => "Compromised account lockdown";
    public string Description => "Block sign-in, revoke all sessions, and disable inbox rules that forward, redirect, or delete mail.";
    public string Category => "Identity";
    public IReadOnlyList<WorkflowInput> Inputs => [new("userUpn", "User UPN or id", "user@contoso.com")];

    public async Task<DiagnosisResult> DiagnoseAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var result = new DiagnosisResult();
        var graph = await _graph.CreateAsync(tenant, ct);

        JsonDocument user;
        try { user = await graph.GetAsync($"/users/{Uri.EscapeDataString(inputs["userUpn"])}?$select=id,accountEnabled,displayName", ct); }
        catch (GraphRequestException ex)
        {
            result.Findings.Add(new("User lookup", FindingStatus.Blocker, ex.Message));
            return result;
        }

        using (user)
        {
            var userId = user.RootElement.GetProperty("id").GetString()!;
            var enabled = user.RootElement.TryGetProperty("accountEnabled", out var e) && e.GetBoolean();
            result.Findings.Add(enabled
                ? new("Sign-in", FindingStatus.Warning, "Account is enabled - sign-in is currently possible.")
                : new("Sign-in", FindingStatus.Ok, "Sign-in is blocked."));

            using var methods = await graph.GetAsync($"/users/{userId}/authentication/methods", ct);
            var methodCount = methods.RootElement.TryGetProperty("value", out var mv) ? mv.GetArrayLength() : 0;
            result.Findings.Add(new("Auth methods", FindingStatus.Info, $"{methodCount} registered"));

            var risky = await GetRiskyRulesAsync(graph, userId, ct);
            if (risky.Count == 0)
                result.Findings.Add(new("Inbox rules", FindingStatus.Ok, "No forwarding/redirect/delete rules."));
            foreach (var rule in risky)
                result.Findings.Add(new($"Inbox rule: {rule.Name}", FindingStatus.Warning,
                    $"{rule.Behaviour}{(rule.Enabled ? "" : " (disabled)")}"));
        }
        return result;
    }

    public async Task<WorkflowRunResult> RemediateAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var run = new WorkflowRunResult { Verification = new() };
        var graph = await _graph.CreateAsync(tenant, ct);

        using var user = await graph.GetAsync($"/users/{Uri.EscapeDataString(inputs["userUpn"])}?$select=id", ct);
        var userId = user.RootElement.GetProperty("id").GetString()!;
        var startedAt = DateTimeOffset.UtcNow;

        await WorkflowSteps.RunAsync(run.Steps, "Block sign-in", async () =>
        {
            await graph.PatchAsync($"/users/{userId}", new { accountEnabled = false }, ct);
            return "accountEnabled = false";
        });
        var blockStep = run.Steps.Count - 1;

        await WorkflowSteps.RunAsync(run.Steps, "Revoke sign-in sessions", async () =>
        {
            await graph.PostAsync($"/users/{userId}/revokeSignInSessions", new { }, ct);
            return "revoked";
        });
        var revokeStep = run.Steps.Count - 1;

        var risky = await GetRiskyRulesAsync(graph, userId, ct);
        var ruleSteps = new List<(int Step, RiskyRule Rule)>();
        foreach (var rule in risky.Where(r => r.Enabled))
        {
            await WorkflowSteps.RunAsync(run.Steps, $"Disable inbox rule: {rule.Name}", async () =>
            {
                await graph.PatchAsync($"/users/{userId}/mailFolders/inbox/messageRules/{rule.Id}", new { isEnabled = false }, ct);
                return rule.Behaviour;
            });
            ruleSteps.Add((run.Steps.Count - 1, rule));
        }
        if (!risky.Any(r => r.Enabled))
        {
            run.Steps.Add(new("Disable inbox rules", true, "no enabled forwarding/redirect/delete rules"));
            run.UnchangedSteps.Add(run.Steps.Count - 1);
        }

        // Desired-state verification: sign-in blocked, sessions revoked, each rule now disabled.
        // (The post-run diagnosis still lists disabled risky rules as warnings -- they are kept as
        // evidence on purpose -- so it is context, not the verification.)
        if (run.Steps[blockStep].Success)
        {
            try
            {
                using var after = await graph.GetAsync($"/users/{userId}?$select=id,accountEnabled", ct);
                var disabled = after.RootElement.TryGetProperty("accountEnabled", out var ae) && ae.ValueKind == JsonValueKind.False;
                run.Verify(blockStep, "Sign-in blocked", disabled, disabled ? "accountEnabled is false on re-read." : "Account is still enabled on re-read.");
            }
            catch (GraphRequestException ex) { run.Verify(blockStep, "Sign-in blocked", false, $"Could not re-read the user: {ex.Message}"); }
        }
        await WorkflowVerify.SessionsRevokedAsync(graph, userId, startedAt, run, revokeStep, ct);
        if (ruleSteps.Any(r => run.Steps[r.Step].Success))
        {
            List<RiskyRule>? after = null;
            string? error = null;
            try { after = await GetRiskyRulesAsync(graph, userId, ct); }
            catch (GraphRequestException ex) { error = ex.Message; }
            foreach (var (step, rule) in ruleSteps.Where(r => run.Steps[r.Step].Success))
            {
                var name = $"Inbox rule disabled: {rule.Name}";
                var now = after?.FirstOrDefault(r => r.Id == rule.Id);
                if (after is null) run.Verify(step, name, false, $"Could not re-read inbox rules: {error}");
                else if (now is { Enabled: true }) run.Verify(step, name, false, "Rule is still enabled on re-read.");
                else run.Verify(step, name, true, now is null ? "Rule no longer present on re-read." : "Rule is disabled on re-read.");
            }
        }

        run.PostState = await DiagnoseAsync(tenant, inputs, ct);
        return run;
    }

    private sealed record RiskyRule(string Id, string Name, bool Enabled, string Behaviour);

    /// <summary>Inbox rules whose actions exfiltrate or hide mail: forward, forward-as-attachment, redirect, or delete.</summary>
    private static async Task<List<RiskyRule>> GetRiskyRulesAsync(GraphRestClient graph, string userId, CancellationToken ct)
    {
        var risky = new List<RiskyRule>();
        using var doc = await graph.GetAsync($"/users/{userId}/mailFolders/inbox/messageRules", ct);
        if (!doc.RootElement.TryGetProperty("value", out var rules)) return risky;

        foreach (var rule in rules.EnumerateArray())
        {
            if (!rule.TryGetProperty("actions", out var actions)) continue;
            var behaviours = new List<string>();
            foreach (var action in new[] { "forwardTo", "forwardAsAttachmentTo", "redirectTo" })
                if (actions.TryGetProperty(action, out var targets) && targets.GetArrayLength() > 0)
                    behaviours.Add($"{action} {string.Join(", ", targets.EnumerateArray().Select(RecipientAddress))}");
            if (actions.TryGetProperty("delete", out var del) && del.GetBoolean())
                behaviours.Add("deletes messages");
            if (behaviours.Count == 0) continue;

            risky.Add(new(
                rule.GetProperty("id").GetString()!,
                rule.TryGetProperty("displayName", out var n) ? n.GetString() ?? "(unnamed)" : "(unnamed)",
                rule.TryGetProperty("isEnabled", out var en) && en.GetBoolean(),
                string.Join("; ", behaviours)));
        }
        return risky;
    }

    private static string RecipientAddress(JsonElement recipient) =>
        recipient.TryGetProperty("emailAddress", out var e) && e.TryGetProperty("address", out var a)
            ? a.GetString() ?? "?" : "?";
}

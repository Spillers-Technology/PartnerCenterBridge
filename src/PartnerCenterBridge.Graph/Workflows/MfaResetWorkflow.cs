using System.Text.Json;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Graph.Workflows;

/// <summary>
/// Diagnoses a user's registered authentication methods and, on remediation, revokes sessions and
/// removes the registered methods so the user is forced to re-register MFA on next sign-in — the
/// standard "reset MFA" helpdesk fix for a lost/compromised second factor.
/// </summary>
internal sealed class MfaResetWorkflow : IWorkflow
{
    private readonly TenantGraphRest _graph;

    public MfaResetWorkflow(TenantGraphRest graph) => _graph = graph;

    public string Id => "mfa-reset";
    public string Name => "MFA / auth method reset";
    public string Description => "Revoke sessions and clear registered authentication methods so the user re-registers MFA.";
    public string Category => "Identity";
    public IReadOnlyList<WorkflowInput> Inputs => [new("userUpn", "User UPN or id", "user@contoso.com")];

    // Maps an authentication method @odata.type to its Graph collection path segment.
    private static readonly IReadOnlyDictionary<string, string> Collections = new Dictionary<string, string>
    {
        ["#microsoft.graph.phoneAuthenticationMethod"] = "phoneMethods",
        ["#microsoft.graph.microsoftAuthenticatorAuthenticationMethod"] = "microsoftAuthenticatorMethods",
        ["#microsoft.graph.fido2AuthenticationMethod"] = "fido2Methods",
        ["#microsoft.graph.softwareOathAuthenticationMethod"] = "softwareOathMethods",
        ["#microsoft.graph.windowsHelloForBusinessAuthenticationMethod"] = "windowsHelloForBusinessMethods",
        ["#microsoft.graph.temporaryAccessPassAuthenticationMethod"] = "temporaryAccessPassMethods",
        ["#microsoft.graph.emailAuthenticationMethod"] = "emailMethods"
    };

    public async Task<DiagnosisResult> DiagnoseAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var result = new DiagnosisResult();
        var graph = await _graph.CreateAsync(tenant, ct);

        string userId;
        try { userId = await ResolveUserIdAsync(graph, inputs["userUpn"], ct); }
        catch (GraphRequestException ex) { result.Findings.Add(new("User lookup", FindingStatus.Blocker, ex.Message)); return result; }

        var methods = await GetMethodsAsync(graph, userId, ct);
        var strong = methods.Count(m => m is not ("#microsoft.graph.passwordAuthenticationMethod" or "#microsoft.graph.emailAuthenticationMethod"));

        foreach (var group in methods.GroupBy(FriendlyType))
            result.Findings.Add(new(group.Key, FindingStatus.Info, $"{group.Count()} registered"));

        result.Findings.Add(strong > 0
            ? new("Strong MFA", FindingStatus.Ok, $"{strong} strong method(s) registered")
            : new("Strong MFA", FindingStatus.Warning, "No strong MFA method registered."));
        return result;
    }

    public async Task<WorkflowRunResult> RemediateAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var run = new WorkflowRunResult();
        var graph = await _graph.CreateAsync(tenant, ct);
        var userId = await ResolveUserIdAsync(graph, inputs["userUpn"], ct);

        await WorkflowSteps.RunAsync(run.Steps, "Revoke sign-in sessions", async () =>
        {
            await graph.PostAsync($"/users/{userId}/revokeSignInSessions", new { }, ct);
            return "revoked";
        });

        // Remove every deletable registered method so the user must re-register.
        using var doc = await graph.GetAsync($"/users/{userId}/authentication/methods", ct);
        var methods = doc.RootElement.TryGetProperty("value", out var v) ? v.EnumerateArray().ToList() : new();
        var removed = 0;
        foreach (var m in methods)
        {
            var type = m.TryGetProperty("@odata.type", out var t) ? t.GetString() : null;
            if (type is null || !Collections.TryGetValue(type, out var collection)) continue; // skip password / unknown
            var methodId = m.GetProperty("id").GetString();
            await WorkflowSteps.RunAsync(run.Steps, $"Remove {FriendlyType(type)}", async () =>
            {
                await graph.DeleteAsync($"/users/{userId}/authentication/{collection}/{methodId}", ct);
                removed++;
                return "removed";
            });
        }
        if (removed == 0)
            run.Steps.Add(new("Remove methods", true, "no removable methods registered"));

        run.PostState = await DiagnoseAsync(tenant, inputs, ct);
        return run;
    }

    private static async Task<string> ResolveUserIdAsync(GraphRestClient graph, string upn, CancellationToken ct)
    {
        using var doc = await graph.GetAsync($"/users/{Uri.EscapeDataString(upn)}?$select=id", ct);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<List<string>> GetMethodsAsync(GraphRestClient graph, string userId, CancellationToken ct)
    {
        using var doc = await graph.GetAsync($"/users/{userId}/authentication/methods", ct);
        return doc.RootElement.TryGetProperty("value", out var v)
            ? v.EnumerateArray().Select(m => m.TryGetProperty("@odata.type", out var t) ? t.GetString() ?? "" : "").ToList()
            : new();
    }

    private static string FriendlyType(string odataType) => odataType
        .Replace("#microsoft.graph.", "")
        .Replace("AuthenticationMethod", "");
}

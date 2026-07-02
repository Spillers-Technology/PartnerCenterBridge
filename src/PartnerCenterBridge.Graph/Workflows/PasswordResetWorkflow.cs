using System.Security.Cryptography;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Graph.Workflows;

/// <summary>
/// Helpdesk password reset done right: set a generated temporary password (must change at next
/// sign-in) and revoke existing sessions so the old credential is dead everywhere. Diagnose warns
/// when the account is directory-synced, where a cloud-side reset does not stick.
/// The temporary password is returned once via <see cref="WorkflowRunResult.Ephemeral"/> and is
/// never written to run history or notifications.
/// </summary>
internal sealed class PasswordResetWorkflow : IWorkflow
{
    private readonly TenantGraphRest _graph;

    public PasswordResetWorkflow(TenantGraphRest graph) => _graph = graph;

    public string Id => "password-reset";
    public string Name => "Password reset + session revoke";
    public string Description => "Set a temporary must-change password and revoke all sessions so the old credential stops working everywhere.";
    public string Category => "Identity";
    public IReadOnlyList<WorkflowInput> Inputs => [new("userUpn", "User UPN or id", "user@contoso.com")];

    public async Task<DiagnosisResult> DiagnoseAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var result = new DiagnosisResult();
        var graph = await _graph.CreateAsync(tenant, ct);

        System.Text.Json.JsonDocument user;
        try
        {
            user = await graph.GetAsync(
                $"/users/{Uri.EscapeDataString(inputs["userUpn"])}?$select=id,accountEnabled,lastPasswordChangeDateTime,onPremisesSyncEnabled", ct);
        }
        catch (GraphRequestException ex)
        {
            result.Findings.Add(new("User lookup", FindingStatus.Blocker, ex.Message));
            return result;
        }

        using (user)
        {
            var root = user.RootElement;
            var synced = root.TryGetProperty("onPremisesSyncEnabled", out var s)
                         && s.ValueKind == System.Text.Json.JsonValueKind.True;
            result.Findings.Add(synced
                ? new("Directory sync", FindingStatus.Blocker,
                    "Account is synced from on-premises AD - reset the password there (or via SSPR writeback), not in the cloud.")
                : new("Directory sync", FindingStatus.Ok, "Cloud-only account."));

            result.Findings.Add(new("Account enabled", FindingStatus.Info,
                root.TryGetProperty("accountEnabled", out var e) && e.GetBoolean() ? "yes" : "no"));

            result.Findings.Add(new("Last password change", FindingStatus.Info,
                root.TryGetProperty("lastPasswordChangeDateTime", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String
                    ? d.GetString() : "unknown"));
        }
        return result;
    }

    public async Task<WorkflowRunResult> RemediateAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var run = new WorkflowRunResult();
        var graph = await _graph.CreateAsync(tenant, ct);

        using var user = await graph.GetAsync($"/users/{Uri.EscapeDataString(inputs["userUpn"])}?$select=id", ct);
        var userId = user.RootElement.GetProperty("id").GetString()!;

        var password = GeneratePassword();
        await WorkflowSteps.RunAsync(run.Steps, "Set temporary password", async () =>
        {
            await graph.PatchAsync($"/users/{userId}", new
            {
                passwordProfile = new { password, forceChangePasswordNextSignIn = true }
            }, ct);
            return "must change at next sign-in";
        });

        // Only hand the password out if it was actually set.
        if (run.Steps.All(s => s.Success))
            run.Ephemeral["Temporary password"] = password;

        await WorkflowSteps.RunAsync(run.Steps, "Revoke sign-in sessions", async () =>
        {
            await graph.PostAsync($"/users/{userId}/revokeSignInSessions", new { }, ct);
            return "revoked";
        });

        run.PostState = await DiagnoseAsync(tenant, inputs, ct);
        return run;
    }

    /// <summary>16 chars drawn from all four classes via a CSPRNG; unambiguous alphabet.</summary>
    internal static string GeneratePassword()
    {
        const string lower = "abcdefghjkmnpqrstuvwxyz", upper = "ABCDEFGHJKMNPQRSTUVWXYZ",
                     digits = "23456789", symbols = "!@#$%^*-+";
        const string all = lower + upper + digits + symbols;

        var chars = new List<char>
        {
            Pick(lower), Pick(upper), Pick(digits), Pick(symbols)
        };
        while (chars.Count < 16) chars.Add(Pick(all));

        // Fisher-Yates so the guaranteed-class characters are not predictably positioned.
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());

        static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
    }
}

using System.Text.Json;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Graph.Workflows;

/// <summary>
/// Diagnoses and repairs a user's license assignment: the classic "license won't apply" caused by
/// a missing usage location or a stuck assignment error. Fix sets the usage location and re-issues
/// the errored SKUs so the directory reprocesses them.
/// </summary>
internal sealed class LicenseRepairWorkflow : IWorkflow
{
    private readonly TenantGraphRest _graph;

    public LicenseRepairWorkflow(TenantGraphRest graph) => _graph = graph;

    public string Id => "license-repair";
    public string Name => "License assignment repair";
    public string Description => "Fix a user whose license won't apply - sets usage location and reprocesses stuck SKUs.";
    public string Category => "Identity";
    public IReadOnlyList<WorkflowInput> Inputs =>
    [
        new("userUpn", "User UPN or id", "user@contoso.com"),
        new("usageLocation", "Usage location (2-letter)", "US", Required: false, Default: "US")
    ];

    public async Task<DiagnosisResult> DiagnoseAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var result = new DiagnosisResult();
        var graph = await _graph.CreateAsync(tenant, ct);
        var upn = inputs["userUpn"];

        JsonElement user;
        try
        {
            using var doc = await graph.GetAsync($"/users/{Uri.EscapeDataString(upn)}?$select=id,displayName,usageLocation,assignedLicenses,licenseAssignmentStates", ct);
            user = doc.RootElement.Clone();
        }
        catch (GraphRequestException ex)
        {
            result.Findings.Add(new("User lookup", FindingStatus.Blocker, ex.Message));
            return result;
        }

        var usageLocation = user.TryGetProperty("usageLocation", out var ul) ? ul.GetString() : null;
        result.Findings.Add(string.IsNullOrWhiteSpace(usageLocation)
            ? new("Usage location", FindingStatus.Blocker, "Not set - licenses cannot be assigned until this is set.")
            : new("Usage location", FindingStatus.Ok, usageLocation));

        var assigned = user.TryGetProperty("assignedLicenses", out var al) ? al.GetArrayLength() : 0;
        result.Findings.Add(new("Assigned licenses", assigned > 0 ? FindingStatus.Info : FindingStatus.Warning, $"{assigned} SKU(s)"));

        if (user.TryGetProperty("licenseAssignmentStates", out var states) && states.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in states.EnumerateArray())
            {
                var state = s.TryGetProperty("state", out var st) ? st.GetString() : null;
                if (string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var sku = s.TryGetProperty("skuId", out var sid) ? sid.GetString() : "?";
                    var err = s.TryGetProperty("error", out var e) ? e.GetString() : "";
                    result.Findings.Add(new("License in error", FindingStatus.Blocker, $"{sku}: {err}"));
                }
            }
        }
        if (result.Findings.All(f => f.Status != FindingStatus.Blocker && f.Status != FindingStatus.Warning))
            result.Findings.Add(new("Licensing", FindingStatus.Ok, "No assignment errors detected."));
        return result;
    }

    public async Task<WorkflowRunResult> RemediateAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var run = new WorkflowRunResult();
        var graph = await _graph.CreateAsync(tenant, ct);
        var upn = inputs["userUpn"];
        var desiredUsageLocation = inputs.TryGetValue("usageLocation", out var loc) && !string.IsNullOrWhiteSpace(loc) ? loc : "US";

        JsonElement user;
        using (var doc = await graph.GetAsync($"/users/{Uri.EscapeDataString(upn)}?$select=id,usageLocation,assignedLicenses,licenseAssignmentStates", ct))
            user = doc.RootElement.Clone();
        var userId = user.GetProperty("id").GetString()!;

        var currentLocation = user.TryGetProperty("usageLocation", out var ul) ? ul.GetString() : null;
        await WorkflowSteps.RunAsync(run.Steps, "Set usage location", async () =>
        {
            if (!string.IsNullOrWhiteSpace(currentLocation)) return $"already {currentLocation}";
            await graph.PatchAsync($"/users/{userId}", new { usageLocation = desiredUsageLocation }, ct);
            return desiredUsageLocation;
        });

        // Reprocess the SKUs in error (or all assigned SKUs if none are flagged) by re-issuing them.
        var errored = ErroredSkus(user);
        var toReapply = errored.Count > 0 ? errored : AssignedSkus(user);
        await WorkflowSteps.RunAsync(run.Steps, "Reprocess licenses", async () =>
        {
            if (toReapply.Count == 0) return "no SKUs to reprocess";
            await graph.PostAsync($"/users/{userId}/assignLicense", new
            {
                addLicenses = toReapply.Select(s => new { skuId = s, disabledPlans = Array.Empty<string>() }).ToArray(),
                removeLicenses = Array.Empty<string>()
            }, ct);
            return $"{toReapply.Count} SKU(s) reissued";
        });

        run.PostState = await DiagnoseAsync(tenant, inputs, ct);
        return run;
    }

    private static List<string> ErroredSkus(JsonElement user) =>
        user.TryGetProperty("licenseAssignmentStates", out var states) && states.ValueKind == JsonValueKind.Array
            ? states.EnumerateArray()
                .Where(s => s.TryGetProperty("state", out var st) && string.Equals(st.GetString(), "Error", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.GetProperty("skuId").GetString()!)
                .Where(s => s is not null).Distinct().ToList()
            : new();

    private static List<string> AssignedSkus(JsonElement user) =>
        user.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array
            ? al.EnumerateArray().Select(s => s.GetProperty("skuId").GetString()!).Where(s => s is not null).ToList()
            : new();
}

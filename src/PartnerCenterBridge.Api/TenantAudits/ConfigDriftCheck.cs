using System.Net;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Core.ConfigSnapshots;
using PartnerCenterBridge.Core.TenantAudits;
using PartnerCenterBridge.Data;
using PartnerCenterBridge.Graph;

namespace PartnerCenterBridge.Api.TenantAudits;

/// <summary>
/// Configuration drift: re-captures every config snapshot section live (read-only) and diffs it
/// against the tenant's most recent snapshot, reusing the snapshot engine's own capture and differ.
/// Lives in the API project because it reads PCB's database.
/// </summary>
public sealed class ConfigDriftCheck(BridgeDbContext db, ConfigSectionCatalog sections) : ITenantAuditCheck
{
    private static readonly AuditColumn[] Columns =
        [new("section", "Section"), new("change", "Change"), new("fields", "Changed fields")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "config-drift",
        Name = "Configuration drift since last snapshot",
        Category = AuditCategories.Security,
        Description = "Compares live Conditional Access, named location and device compliance configuration with the tenant's most recent configuration snapshot.",
        BusinessImpact = "Unreviewed changes to access and device policy are how protections quietly disappear.",
        Recommendation = "Confirm each change was intended; take a new snapshot once the current state is accepted as the baseline.",
        SeverityRules =
        [
            "Warn: a Conditional Access policy was removed since the snapshot.",
            "Info: other items added, removed or changed since the snapshot.",
            "Pass: live configuration matches the snapshot."
        ],
        Requirements =
        [
            AuditRequirement.Dependency("A configuration snapshot of this tenant", "Take one under Tenants > Snapshots."),
            AuditRequirement.Graph("Policy.Read.All"),
            AuditRequirement.Graph("DeviceManagementConfiguration.Read.All")
        ],
        Limitations = ["Only the sections configuration snapshots capture are compared. Nested settings are compared as whole blocks."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var baseline = await db.ConfigSnapshotRuns.AsNoTracking()
            .Include(r => r.Sections)
            .Where(r => r.TenantId == ctx.Tenant.Id)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ctx.CancellationToken);
        if (baseline is null)
            throw new AuditUnavailableException(
                "No configuration snapshot exists for this tenant yet. Take one under Tenants > Snapshots, then audit again to see drift.",
                ["A configuration snapshot of this tenant"]);

        var output = new AuditCheckOutput();
        var removedCa = new List<AuditSubject>();
        var other = new List<AuditSubject>();
        var compared = 0;
        foreach (var section in sections.All)
        {
            var before = baseline.Sections.FirstOrDefault(s => s.SectionId == section.Id && s.Error is null);
            if (before is null)
            {
                output.Note($"{section.Name}: not in the snapshot (or it failed to capture then); not compared.");
                continue;
            }
            string live;
            try
            {
                live = await section.CaptureAsync(ctx.Tenant, ctx.CancellationToken);
            }
            catch (GraphRequestException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                output.Note($"{section.Name}: could not be read now (Graph {(int)ex.Status}); not compared.");
                continue;
            }
            compared++;
            foreach (var c in ConfigDiffer.Diff(section.Id, section.Name, before.ContentJson, live).Changes)
            {
                var s = new AuditSubject
                {
                    Type = "policy", Id = $"{section.Id}|{c.ItemId}", Name = c.Label ?? c.ItemId,
                    Evidence = c.Kind == ConfigChangeKind.Modified
                        ? $"{section.Name}: changed {string.Join(", ", c.FieldChanges.Select(f => f.Field))}"
                        : $"{section.Name}: {c.Kind.ToString().ToLowerInvariant()}",
                    Properties =
                    {
                        ["section"] = section.Name, ["change"] = c.Kind.ToString(),
                        ["fields"] = c.FieldChanges.Count == 0 ? null : string.Join("; ", c.FieldChanges.Select(f => f.Field))
                    }
                };
                (c.Kind == ConfigChangeKind.Removed && section.Id == "conditional-access-policies" ? removedCa : other).Add(s);
            }
        }
        if (compared == 0)
            throw new AuditUnavailableException("No configuration section could be compared (see notes).", ["Graph permission Policy.Read.All"]);

        var since = AuditFindingBuilder.Date(baseline.StartedAt);
        if (removedCa.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "ca-removed", AuditSeverity.Warn, "Conditional Access policies removed",
                $"{AuditFindingBuilder.Count(removedCa.Count, "Conditional Access policy", "Conditional Access policies")} in the {since} snapshot no longer {(removedCa.Count == 1 ? "exists" : "exist")}.",
                Columns, removedCa));
        if (other.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "changes", AuditSeverity.Info, "Changes since the last snapshot",
                $"{AuditFindingBuilder.Count(other.Count, "item")} added, removed or changed since the {since} snapshot.", Columns, other));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, $"Live configuration matches the {since} snapshot."));
        output.Note($"Baseline: snapshot taken {since} by {baseline.Operator}.");
        return output;
    }
}

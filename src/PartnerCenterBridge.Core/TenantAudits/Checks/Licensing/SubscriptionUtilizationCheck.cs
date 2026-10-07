using System.Globalization;

namespace PartnerCenterBridge.Core.TenantAudits.Checks.Licensing;

/// <summary>Seat counts per subscription: unassigned seats, over-assignment, and subscriptions lapsing.</summary>
public sealed class SubscriptionUtilizationCheck(IAuditDirectoryData directory) : ITenantAuditCheck
{
    private static readonly AuditColumn[] Columns =
    [
        new("sku", "SKU"), new("status", "Status"), new("purchased", "Purchased"), new("assigned", "Assigned"),
        new("available", "Unassigned"), new("warning", "In warning"), new("suspended", "Suspended")
    ];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "subscription-utilization",
        Name = "Subscription seat utilization",
        Category = AuditCategories.Licensing,
        Description = "Purchased versus assigned seats for each subscription, and subscriptions in a warning or suspended state.",
        BusinessImpact = "Unassigned seats are recurring spend with no user; lapsing subscriptions can cut users off.",
        Recommendation = "Reduce seat counts at the next renewal where seats sit unused, and renew or replace lapsing subscriptions.",
        SeverityRules =
        [
            "Fail: more seats assigned than are active (users may lose service).",
            "Warn: seats in a warning (expiring) or suspended state.",
            "Info: paid subscriptions with unassigned seats.",
            "Pass: every paid subscription is fully assigned and active."
        ],
        Requirements = [AuditRequirement.Graph("Organization.Read.All")],
        Limitations =
        [
            "Microsoft Graph does not expose prices or renewal dates; seat counts are reported, not costs.",
            "No-cost and viral SKUs are excluded from the unassigned-seat finding."
        ]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var skus = await directory.GetSubscribedSkusAsync(ctx);
        var output = new AuditCheckOutput();
        var paid = skus.Where(s => !AuditLicenses.NoCostSkuPartNumbers.Contains(s.SkuPartNumber)).ToList();

        var over = paid.Where(s => s.Consumed > s.Enabled + s.Warning).Select(Row).ToList();
        var lapsing = paid.Where(s => s.Warning > 0 || s.Suspended > 0).Select(Row).ToList();
        var unused = paid.Where(s => s.Enabled - s.Consumed > 0).Select(Row).ToList();

        if (over.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "over-assigned", AuditSeverity.Fail, "More seats assigned than purchased",
                $"{AuditFindingBuilder.Count(over.Count, "subscription")} {(over.Count == 1 ? "has" : "have")} more users assigned than active seats.",
                Columns, over, businessImpact: "Users beyond the purchased count can lose access to the service.",
                recommendation: "Buy the missing seats or remove licenses from users who no longer need them."));
        if (lapsing.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "lapsing", AuditSeverity.Warn, "Subscriptions expiring or suspended",
                $"{AuditFindingBuilder.Count(lapsing.Count, "subscription")} {(lapsing.Count == 1 ? "has" : "have")} seats in a warning or suspended state.",
                Columns, lapsing, businessImpact: "Users on these seats can lose service when the grace period ends.",
                recommendation: "Renew, or move users to an active subscription before the seats are disabled."));
        if (unused.Count > 0)
        {
            var seats = unused.Sum(s => int.Parse(s.Properties["available"]!, CultureInfo.InvariantCulture));
            output.Add(AuditFindingBuilder.Create(d, "unassigned", AuditSeverity.Info, "Unassigned paid seats",
                $"{AuditFindingBuilder.Count(seats, "seat")} across {AuditFindingBuilder.Count(unused.Count, "subscription")} {(seats == 1 ? "is" : "are")} purchased but not assigned.",
                Columns, unused));
        }
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, $"All {AuditFindingBuilder.Count(paid.Count, "paid subscription")} are fully assigned and active."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(paid.Count, "subscription")} (excluding {skus.Count - paid.Count} no-cost).");
        return output;
    }

    private static AuditSubject Row(AuditSku s) => new()
    {
        Type = "subscription",
        Id = s.SkuId,
        Name = s.SkuPartNumber,
        Evidence = $"{s.Consumed} assigned of {s.Enabled} active seats ({s.Warning} in warning, {s.Suspended} suspended).",
        Properties =
        {
            ["sku"] = s.SkuPartNumber,
            ["status"] = s.CapabilityStatus,
            ["purchased"] = s.Enabled.ToString(CultureInfo.InvariantCulture),
            ["assigned"] = s.Consumed.ToString(CultureInfo.InvariantCulture),
            ["available"] = Math.Max(0, s.Enabled - s.Consumed).ToString(CultureInfo.InvariantCulture),
            ["warning"] = s.Warning.ToString(CultureInfo.InvariantCulture),
            ["suspended"] = s.Suspended.ToString(CultureInfo.InvariantCulture)
        }
    };
}

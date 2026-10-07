namespace PartnerCenterBridge.Core.TenantAudits.Checks.Devices;

public sealed class NoncompliantDevicesCheck(IAuditDeviceData devices) : ITenantAuditCheck
{
    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "noncompliant-devices",
        Name = "Non-compliant devices",
        Category = AuditCategories.Devices,
        Description = "Managed devices Intune reports as non-compliant, in grace period, or in an error state.",
        BusinessImpact = "Non-compliant devices miss security requirements and, where Conditional Access uses compliance, lose access to company data.",
        Recommendation = "Open each device in Intune to see which setting fails, and fix or retire the device.",
        SeverityRules =
        [
            "Warn: complianceState is noncompliant.",
            "Info: in grace period (will become non-compliant if not fixed).",
            "Unknown: compliance error or conflict, so Intune could not evaluate the device.",
            "Pass: every device is compliant."
        ],
        Requirements = DeviceRows.Requirements,
        Limitations = ["A device counts as compliant when no policy applies to it unless the tenant marks such devices non-compliant (see the compliance policy coverage check)."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var list = await devices.GetManagedDevicesAsync(ctx);
        var output = new AuditCheckOutput();
        List<AuditSubject> With(params string[] states) => list
            .Where(x => states.Contains(x.ComplianceState ?? "", StringComparer.OrdinalIgnoreCase))
            .Select(x => DeviceRows.Subject(x, ctx.Now, $"complianceState = {x.ComplianceState}")).ToList();

        var noncompliant = With("noncompliant");
        var grace = With("inGracePeriod");
        var error = With("error", "conflict");
        if (noncompliant.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "noncompliant", AuditSeverity.Warn, d.Name,
                $"{AuditFindingBuilder.Count(noncompliant.Count, "device")} of {list.Count} {(noncompliant.Count == 1 ? "is" : "are")} non-compliant.", DeviceRows.Columns, noncompliant));
        if (grace.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "grace", AuditSeverity.Info, "Devices in compliance grace period",
                $"{AuditFindingBuilder.Count(grace.Count, "device")} will become non-compliant if not fixed.", DeviceRows.Columns, grace));
        if (error.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "error", AuditSeverity.Unknown, "Devices Intune could not evaluate",
                $"{AuditFindingBuilder.Count(error.Count, "device")} {(error.Count == 1 ? "has" : "have")} a compliance error or conflict.", DeviceRows.Columns, error,
                recommendation: "Check the device's compliance status in Intune for the conflicting or failing policy."));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, $"All {AuditFindingBuilder.Count(list.Count, "managed device")} are compliant."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(list.Count, "managed device")}.");
        return output;
    }
}

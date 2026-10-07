namespace PartnerCenterBridge.Core.TenantAudits.Checks.Devices;

public sealed class CompliancePolicyCoverageCheck(IAuditDeviceData devices) : ITenantAuditCheck
{
    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "compliance-policy-coverage",
        Name = "Compliance policy coverage",
        Category = AuditCategories.Devices,
        Description = "Whether every platform in use has an assigned compliance policy, and how devices without a policy are treated.",
        BusinessImpact = "A device with no compliance policy is reported compliant by default, so compliance-based access controls do not protect it.",
        Recommendation = "Create and assign a compliance policy for each platform in use, and mark devices with no policy as non-compliant.",
        SeverityRules =
        [
            "Fail: devices are managed but there are no compliance policies at all.",
            "Warn: a platform in use has no assigned policy; devices with no policy are marked compliant.",
            "Info: policies that are not assigned to anyone.",
            "Pass: every platform in use is covered and unassigned devices are marked non-compliant."
        ],
        Requirements = [.. DeviceRows.Requirements, AuditRequirement.Graph("DeviceManagementConfiguration.Read.All")],
        Limitations = ["Coverage is per platform and assignment count; whether a policy's assignment actually reaches every device is not evaluated."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var list = await devices.GetManagedDevicesAsync(ctx);
        var policies = await devices.GetCompliancePoliciesAsync(ctx);
        var output = new AuditCheckOutput();

        AuditSubject PolicyRow(AuditCompliancePolicy p) => new()
        {
            Type = "policy", Id = p.Id, Name = p.Name, Evidence = $"{p.Platform} policy with {p.AssignmentCount} assignment(s).",
            Properties = { ["platform"] = p.Platform, ["assignments"] = p.AssignmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
        AuditColumn[] policyColumns = [new("platform", "Platform"), new("assignments", "Assignments")];

        if (policies.Count == 0 && list.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "none", AuditSeverity.Fail, "No compliance policies",
                $"{AuditFindingBuilder.Count(list.Count, "managed device")} but no compliance policies exist."));

        var covered = policies.Where(p => p.AssignmentCount > 0).Select(p => p.Platform).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gaps = list.GroupBy(x => DeviceRows.Platform(x.OperatingSystem))
            .Where(g => g.Key != "Other" && !covered.Contains(g.Key) && policies.Count > 0)
            .Select(g => new AuditSubject
            {
                Type = "platform", Id = g.Key, Name = g.Key,
                Evidence = $"{AuditFindingBuilder.Count(g.Count(), "managed device")}, no assigned {g.Key} compliance policy.",
                Properties = { ["devices"] = g.Count().ToString(System.Globalization.CultureInfo.InvariantCulture) }
            }).ToList();
        if (gaps.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "platform-gaps", AuditSeverity.Warn, "Platforms without an assigned policy",
                $"{AuditFindingBuilder.Count(gaps.Count, "platform")} in use {(gaps.Count == 1 ? "has" : "have")} no assigned compliance policy.",
                [new("devices", "Managed devices")], gaps));

        try
        {
            var settings = await devices.GetDeviceManagementSettingsAsync(ctx);
            if (settings.SecureByDefault == false)
                output.Add(AuditFindingBuilder.Create(d, "not-secure-by-default", AuditSeverity.Warn, "Devices with no policy count as compliant",
                    "The tenant setting 'Mark devices with no compliance policy assigned as' is set to Compliant.",
                    recommendation: "Change the setting to Not compliant once every platform has a policy."));
            else if (settings.SecureByDefault is null)
                output.Note("The 'Mark devices with no compliance policy assigned as' setting could not be read.");
        }
        catch (AuditUnavailableException ex) { output.Note($"The 'no compliance policy' tenant setting was not graded: {ex.Message}"); }

        var unassigned = policies.Where(p => p.AssignmentCount == 0).Select(PolicyRow).ToList();
        if (unassigned.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "unassigned", AuditSeverity.Info, "Unassigned compliance policies",
                $"{AuditFindingBuilder.Count(unassigned.Count, "compliance policy", "compliance policies")} {(unassigned.Count == 1 ? "is" : "are")} not assigned to anyone.",
                policyColumns, unassigned, recommendation: "Assign or delete these policies so the policy list reflects what is enforced."));

        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, $"{AuditFindingBuilder.Count(policies.Count, "compliance policy", "compliance policies")} cover every platform in use."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(policies.Count, "compliance policy", "compliance policies")} and {AuditFindingBuilder.Count(list.Count, "managed device")}.");
        return output;
    }
}

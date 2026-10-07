namespace PartnerCenterBridge.Core.TenantAudits.Checks.Devices;

public sealed class StaleDevicesCheck(IAuditDeviceData devices) : ITenantAuditCheck
{
    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "stale-devices",
        Name = "Stale managed devices",
        Category = AuditCategories.Devices,
        Description = "Intune-managed devices that have not checked in within the device threshold.",
        BusinessImpact = "Devices that stop checking in no longer get policy or app updates, and stale records skew compliance numbers.",
        Recommendation = "Find out whether each device is lost, retired or broken; retire or delete records for devices that are gone.",
        SeverityRules =
        [
            "Warn: last Intune sync at least the threshold ago.",
            "Unknown: no sync time recorded.",
            "Pass: every device synced within the threshold."
        ],
        Requirements = DeviceRows.Requirements,
        Parameters = [AuditParameterKeys.DeviceStaleDays]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var threshold = ctx.Parameters.Get(AuditParameterKeys.DeviceStaleDays);
        var list = await devices.GetManagedDevicesAsync(ctx);
        var output = new AuditCheckOutput();
        var stale = list.Where(x => x.LastSync is { } s && SignInEvaluator.DaysBetween(s, ctx.Now) >= threshold)
            .Select(x => DeviceRows.Subject(x, ctx.Now, $"Last sync {AuditFindingBuilder.Date(x.LastSync)}.")).ToList();
        var never = list.Where(x => x.LastSync is null).Select(x => DeviceRows.Subject(x, ctx.Now, "No sync time recorded.")).ToList();
        if (stale.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "stale", AuditSeverity.Warn, d.Name,
                $"{AuditFindingBuilder.Count(stale.Count, "device")} of {list.Count} {(stale.Count == 1 ? "has" : "have")} not synced in {threshold}+ days.",
                DeviceRows.Columns, stale));
        if (never.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "no-sync", AuditSeverity.Unknown, "Devices with no recorded sync",
                $"{AuditFindingBuilder.Count(never.Count, "device")} {(never.Count == 1 ? "has" : "have")} no last-sync time.", DeviceRows.Columns, never));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, $"All {AuditFindingBuilder.Count(list.Count, "managed device")} synced within the last {threshold} days."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(list.Count, "managed device")}; threshold {threshold} days.");
        return output;
    }
}

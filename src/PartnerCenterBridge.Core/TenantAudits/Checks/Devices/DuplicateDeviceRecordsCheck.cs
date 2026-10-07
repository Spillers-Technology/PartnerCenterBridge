namespace PartnerCenterBridge.Core.TenantAudits.Checks.Devices;

/// <summary>Several Intune records for one serial number -- usually leftovers from re-enrollment.</summary>
public sealed class DuplicateDeviceRecordsCheck(IAuditDeviceData devices) : ITenantAuditCheck
{
    private static readonly HashSet<string> PlaceholderSerials = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "0", "Unknown", "None", "Default string", "To be filled by O.E.M.", "System Serial Number"
    };

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "duplicate-device-records",
        Name = "Duplicate device records",
        Category = AuditCategories.Devices,
        Description = "Serial numbers that appear on more than one managed-device record.",
        BusinessImpact = "Duplicate records inflate device counts and leave stale entries that look like real, unmanaged devices.",
        Recommendation = "Keep the most recently synced record for each serial number and delete the older ones.",
        SeverityRules = ["Info: a serial number has more than one record (older records are listed).", "Pass: no duplicates."],
        Requirements = DeviceRows.Requirements,
        Limitations = ["Devices with a blank or placeholder serial number are not compared."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var list = await devices.GetManagedDevicesAsync(ctx);
        var output = new AuditCheckOutput();
        var older = list
            .Where(x => !PlaceholderSerials.Contains((x.SerialNumber ?? "").Trim()))
            .GroupBy(x => x.SerialNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.OrderByDescending(x => x.LastSync ?? DateTimeOffset.MinValue).Skip(1)
                .Select(x => DeviceRows.Subject(x, ctx.Now, $"Serial {g.Key} has {g.Count()} records; a newer one synced more recently.")))
            .ToList();
        if (older.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "duplicates", AuditSeverity.Info, d.Name,
                $"{AuditFindingBuilder.Count(older.Count, "older duplicate record")} share a serial number with a more recently synced device.",
                [new("serialNumber", "Serial number"), .. DeviceRows.Columns], older));
        else
            output.Add(AuditFindingBuilder.Pass(d, "No serial number appears on more than one device record."));
        return output;
    }
}

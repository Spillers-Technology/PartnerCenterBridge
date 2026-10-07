namespace PartnerCenterBridge.Core.TenantAudits.Checks.Devices;

public sealed class DeviceEncryptionCheck(IAuditDeviceData devices) : ITenantAuditCheck
{
    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "device-encryption",
        Name = "Device encryption coverage",
        Category = AuditCategories.Devices,
        Description = "Managed devices that report their storage as not encrypted.",
        BusinessImpact = "Data on a lost or stolen unencrypted laptop or phone can be read by whoever has it.",
        Recommendation = "Enforce BitLocker/FileVault (or device encryption) through Intune and follow up on these devices.",
        SeverityRules =
        [
            "Warn: the device reports isEncrypted = false.",
            "Unknown: the device has not reported encryption state.",
            "Pass: every device reports encrypted storage."
        ],
        Requirements = DeviceRows.Requirements,
        Limitations = ["isEncrypted is what the device last reported to Intune; it is only as current as the device's last sync."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var list = await devices.GetManagedDevicesAsync(ctx);
        var output = new AuditCheckOutput();
        var off = list.Where(x => x.IsEncrypted == false).Select(x => DeviceRows.Subject(x, ctx.Now, "isEncrypted = false")).ToList();
        var unknown = list.Where(x => x.IsEncrypted is null).Select(x => DeviceRows.Subject(x, ctx.Now, "Encryption state not reported.")).ToList();
        if (off.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "unencrypted", AuditSeverity.Warn, "Unencrypted devices",
                $"{AuditFindingBuilder.Count(off.Count, "device")} of {list.Count} {(off.Count == 1 ? "reports" : "report")} storage that is not encrypted.", DeviceRows.Columns, off));
        if (unknown.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "unknown", AuditSeverity.Unknown, "Encryption state not reported",
                $"{AuditFindingBuilder.Count(unknown.Count, "device")} {(unknown.Count == 1 ? "has" : "have")} not reported encryption state.", DeviceRows.Columns, unknown));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, $"All {AuditFindingBuilder.Count(list.Count, "managed device")} report encrypted storage."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(list.Count, "managed device")}.");
        return output;
    }
}

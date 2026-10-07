namespace PartnerCenterBridge.Core.TenantAudits.Checks.Devices;

internal static class DeviceRows
{
    public static readonly AuditRequirement[] Requirements =
    [
        AuditRequirement.Graph("DeviceManagementManagedDevices.Read.All"),
        AuditRequirement.Product("Microsoft Intune", "Tenants that do not use Intune report device checks as unavailable.")
    ];

    public static readonly AuditColumn[] Columns =
    [
        new("os", "OS"), new("osVersion", "OS version"), new("compliance", "Compliance"), new("lastSync", "Last sync"),
        new("daysSinceSync", "Days since sync"), new("encrypted", "Encrypted"), new("owner", "Ownership"), new("primaryUser", "Primary user")
    ];

    public static AuditSubject Subject(AuditDevice d, DateTimeOffset now, string? evidence = null) => new()
    {
        Type = "device",
        Id = d.Id,
        Name = string.IsNullOrWhiteSpace(d.DeviceName) ? d.Id : d.DeviceName,
        Upn = d.UserPrincipalName,
        Evidence = evidence,
        Properties =
        {
            ["os"] = d.OperatingSystem,
            ["osVersion"] = d.OsVersion,
            ["compliance"] = d.ComplianceState,
            ["lastSync"] = AuditFindingBuilder.Date(d.LastSync),
            ["daysSinceSync"] = d.LastSync is { } s ? SignInEvaluator.DaysBetween(s, now).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            ["encrypted"] = d.IsEncrypted is { } e ? AuditFindingBuilder.YesNo(e) : null,
            ["owner"] = d.OwnerType,
            ["primaryUser"] = d.UserPrincipalName,
            ["serialNumber"] = d.SerialNumber
        }
    };

    /// <summary>Windows, iOS, Android, macOS or Other -- the platforms compliance policies target.</summary>
    public static string Platform(string? operatingSystem)
    {
        var os = operatingSystem ?? "";
        if (os.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (os.Equals("iOS", StringComparison.OrdinalIgnoreCase) || os.Equals("iPadOS", StringComparison.OrdinalIgnoreCase)) return "iOS";
        if (os.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android";
        if (os.StartsWith("mac", StringComparison.OrdinalIgnoreCase)) return "macOS";
        return "Other";
    }
}

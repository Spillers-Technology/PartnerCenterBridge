namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// Per-contract defaults for new-hire provisioning — the "small state machine" for identity.
/// A new-hire form for any tenant on the contract pre-fills from this, so the variance across
/// contracts lives in data, not in the operator's head.
/// </summary>
public class ProvisioningTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ContractId { get; set; }
    public Contract? Contract { get; set; }

    /// <summary>Two-letter usage location required before most licenses can be assigned (e.g. "US").</summary>
    public string UsageLocation { get; set; } = "US";

    /// <summary>Default UPN/mail domain suffix for new users (e.g. "contoso.com").</summary>
    public string? UpnDomain { get; set; }

    public string? DefaultJobTitle { get; set; }
    public string? DefaultDepartment { get; set; }

    /// <summary>License SKU ids (subscribedSku.skuId GUIDs) to assign to every new hire. JSON column.</summary>
    public List<string> LicenseSkuIds { get; set; } = new();

    /// <summary>Entra group ids every new hire should join. JSON column.</summary>
    public List<string> GroupIds { get; set; } = new();

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

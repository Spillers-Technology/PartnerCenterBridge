using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Core.Abstractions;

/// <summary>
/// One capturable slice of a tenant's M365 configuration (e.g. Conditional Access policies) for
/// the config-snapshot/diff/backup engine. Mirrors <c>IWorkflow</c>'s shape deliberately:
/// implementations live in whichever project owns the API they call and register through DI; the
/// catalog lists and dispatches them uniformly, so adding a section needs no controller change.
/// </summary>
public interface IConfigSection
{
    /// <summary>Stable slug, e.g. "conditional-access-policies". Used as a lookup key and a file name under git sync.</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>Grouping for the UI, e.g. "Identity" or "Devices".</summary>
    string Category { get; }

    /// <summary>
    /// Captures the section's current state as a JSON array string. Each element must carry a
    /// stable top-level "id" property (the Graph object's id) -- that's what <c>ConfigDiffer</c>
    /// keys on to tell added/removed/modified apart.
    /// </summary>
    Task<string> CaptureAsync(Tenant tenant, CancellationToken ct);
}

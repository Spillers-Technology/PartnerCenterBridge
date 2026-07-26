namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// One point-in-time capture of a tenant's configuration across every registered
/// <see cref="Abstractions.IConfigSection"/> -- a "backup" in the everyday sense, and the unit
/// two runs are diffed between. Imported runs (see <c>ConfigSnapshotsController.Import</c>) carry
/// <see cref="Imported"/> = true and never touched live Graph.
/// </summary>
public class ConfigSnapshotRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string Operator { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>False if any section failed to capture (see each <see cref="ConfigSnapshotSection.Error"/>); the sections that did succeed are still stored.</summary>
    public bool Succeeded { get; set; }

    public bool Imported { get; set; }

    /// <summary>Set once this run has been committed to the configured git sync remote, if any.</summary>
    public string? GitCommitSha { get; set; }

    public ICollection<ConfigSnapshotSection> Sections { get; set; } = new List<ConfigSnapshotSection>();
}

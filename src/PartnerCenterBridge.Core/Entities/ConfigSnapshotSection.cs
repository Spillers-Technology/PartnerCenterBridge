namespace PartnerCenterBridge.Core.Entities;

/// <summary>One <see cref="Abstractions.IConfigSection"/>'s captured content within a <see cref="ConfigSnapshotRun"/>.</summary>
public class ConfigSnapshotSection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RunId { get; set; }
    public ConfigSnapshotRun? Run { get; set; }

    public string SectionId { get; set; } = "";
    public string SectionName { get; set; } = "";

    public int ItemCount { get; set; }

    /// <summary>Normalized JSON array, each element keyed by a top-level "id" -- see <see cref="Abstractions.IConfigSection.CaptureAsync"/>.</summary>
    public string ContentJson { get; set; } = "[]";

    /// <summary>Set if this section failed to capture; <see cref="ContentJson"/> stays at its default in that case.</summary>
    public string? Error { get; set; }
}

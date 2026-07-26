using PartnerCenterBridge.Core.ConfigSnapshots;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Api.Contracts;

public record ConfigSectionDto(string Id, string Name, string Category);

public record ConfigSnapshotSectionSummaryDto(string SectionId, string SectionName, int ItemCount, bool Failed, string? Error)
{
    public static ConfigSnapshotSectionSummaryDto From(ConfigSnapshotSection s) =>
        new(s.SectionId, s.SectionName, s.ItemCount, s.Error is not null, s.Error);
}

public record ConfigSnapshotRunDto(
    Guid Id, Guid TenantId, string Operator, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    bool Succeeded, bool Imported, string? GitCommitSha, IReadOnlyList<ConfigSnapshotSectionSummaryDto> Sections)
{
    public static ConfigSnapshotRunDto From(ConfigSnapshotRun r) => new(
        r.Id, r.TenantId, r.Operator, r.StartedAt, r.CompletedAt, r.Succeeded, r.Imported, r.GitCommitSha,
        r.Sections.Select(ConfigSnapshotSectionSummaryDto.From).ToList());
}

// --- Diff ---------------------------------------------------------------------------------
public record ConfigFieldChangeDto(string Field, string? Before, string? After);
public record ConfigItemChangeDto(string Kind, string ItemId, string? Label, IReadOnlyList<ConfigFieldChangeDto> FieldChanges);
public record SectionDiffDto(string SectionId, string SectionName, IReadOnlyList<ConfigItemChangeDto> Changes)
{
    public static SectionDiffDto From(SectionDiff d) => new(
        d.SectionId, d.SectionName,
        d.Changes.Select(c => new ConfigItemChangeDto(
            c.Kind.ToString(), c.ItemId, c.Label,
            c.FieldChanges.Select(f => new ConfigFieldChangeDto(f.Field, f.Before, f.After)).ToList())).ToList());
}

// --- Export / import "workbook" ------------------------------------------------------------
// A workbook is just the data a snapshot already holds, made portable: exportable as a file and
// re-importable elsewhere (e.g. onto a different instance, or offline) for comparison. It never
// writes anything back to a live tenant -- see ConfigSnapshotsController remarks.
public record ConfigWorkbookSectionDto(string SectionId, string SectionName, string ContentJson);
public record ConfigWorkbookDto(string TenantDisplayName, DateTimeOffset CapturedAt, string Operator, IReadOnlyList<ConfigWorkbookSectionDto> Sections);
public record ImportWorkbookRequest(IReadOnlyList<ConfigWorkbookSectionDto> Sections);

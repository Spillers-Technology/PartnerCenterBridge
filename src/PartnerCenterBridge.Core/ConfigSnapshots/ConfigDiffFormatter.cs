using System.Text;

namespace PartnerCenterBridge.Core.ConfigSnapshots;

/// <summary>Renders a set of <see cref="SectionDiff"/>s as readable patch-style text -- the exportable "workbook" of what changed, for pasting into a ticket or reviewing outside the app. Not a real unified diff and not machine-appliable; see <c>ConfigSnapshotsController</c> remarks on why there's deliberately no apply-to-tenant path.</summary>
public static class ConfigDiffFormatter
{
    public static string ToPatchText(IEnumerable<SectionDiff> diffs)
    {
        var sb = new StringBuilder();
        foreach (var section in diffs.Where(d => d.HasChanges))
        {
            sb.AppendLine($"=== {section.SectionName} ({section.SectionId}) ===");
            foreach (var change in section.Changes)
            {
                var label = change.Label is not null ? $"{change.Label} ({change.ItemId})" : change.ItemId;
                if (change.Kind == ConfigChangeKind.Added)
                    sb.AppendLine($"+ added: {label}");
                else if (change.Kind == ConfigChangeKind.Removed)
                    sb.AppendLine($"- removed: {label}");
                else
                {
                    sb.AppendLine($"~ modified: {label}");
                    foreach (var f in change.FieldChanges)
                    {
                        sb.AppendLine($"  - {f.Field}: {f.Before ?? "(none)"}");
                        sb.AppendLine($"  + {f.Field}: {f.After ?? "(none)"}");
                    }
                }
            }
            sb.AppendLine();
        }
        return sb.Length == 0 ? "No changes.\n" : sb.ToString();
    }
}

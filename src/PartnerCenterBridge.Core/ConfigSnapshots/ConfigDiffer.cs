using System.Text.Json.Nodes;

namespace PartnerCenterBridge.Core.ConfigSnapshots;

public enum ConfigChangeKind { Added, Removed, Modified }

/// <summary>One top-level field that differs between the before/after copy of an item. Nested objects/arrays are compared as whole blocks, not recursively field-by-field -- enough to show "grantControls changed" without the complexity of a full recursive JSON diff.</summary>
public record ConfigFieldChange(string Field, string? Before, string? After);

public record ConfigItemChange(ConfigChangeKind Kind, string ItemId, string? Label, IReadOnlyList<ConfigFieldChange> FieldChanges);

public record SectionDiff(string SectionId, string SectionName, IReadOnlyList<ConfigItemChange> Changes)
{
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// Generic, section-agnostic diff: keys each side's JSON array by its "id" property, then does a
/// top-level property comparison on items present in both. Works the same way regardless of which
/// Graph resource a section captures, so a new <see cref="Abstractions.IConfigSection"/> never
/// needs its own diff logic.
/// </summary>
public static class ConfigDiffer
{
    public static SectionDiff Diff(string sectionId, string sectionName, string beforeJson, string afterJson)
    {
        var before = ParseById(beforeJson);
        var after = ParseById(afterJson);
        var changes = new List<ConfigItemChange>();

        foreach (var (id, node) in before)
            if (!after.ContainsKey(id))
                changes.Add(new ConfigItemChange(ConfigChangeKind.Removed, id, LabelOf(node), []));

        foreach (var (id, node) in after)
        {
            if (!before.TryGetValue(id, out var prev))
            {
                changes.Add(new ConfigItemChange(ConfigChangeKind.Added, id, LabelOf(node), []));
                continue;
            }
            var fieldChanges = DiffFields(prev.AsObject(), node.AsObject());
            if (fieldChanges.Count > 0)
                changes.Add(new ConfigItemChange(ConfigChangeKind.Modified, id, LabelOf(node), fieldChanges));
        }

        return new SectionDiff(sectionId, sectionName, changes);
    }

    private static Dictionary<string, JsonNode> ParseById(string json)
    {
        var dict = new Dictionary<string, JsonNode>();
        if (string.IsNullOrWhiteSpace(json)) return dict;
        var array = JsonNode.Parse(json)?.AsArray() ?? [];
        foreach (var item in array)
            if (item is JsonObject obj && obj.TryGetPropertyValue("id", out var idNode) && idNode is not null)
                dict[idNode.ToString()] = item;
        return dict;
    }

    private static string? LabelOf(JsonNode node) =>
        node is JsonObject obj && obj.TryGetPropertyValue("displayName", out var n) ? n?.ToString() : null;

    private static List<ConfigFieldChange> DiffFields(JsonObject before, JsonObject after)
    {
        var changes = new List<ConfigFieldChange>();
        var keys = before.Select(p => p.Key).Union(after.Select(p => p.Key)).Where(k => k != "id");
        foreach (var key in keys)
        {
            before.TryGetPropertyValue(key, out var b);
            after.TryGetPropertyValue(key, out var a);
            var bStr = b?.ToJsonString();
            var aStr = a?.ToJsonString();
            if (bStr != aStr)
                changes.Add(new ConfigFieldChange(key, bStr, aStr));
        }
        return changes;
    }
}

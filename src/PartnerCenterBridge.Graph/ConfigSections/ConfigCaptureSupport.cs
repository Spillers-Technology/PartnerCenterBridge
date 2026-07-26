using System.Text.Json;
using System.Text.Json.Nodes;

namespace PartnerCenterBridge.Graph.ConfigSections;

/// <summary>
/// Shared normalization for config-section captures: strip volatile timestamp fields (they change
/// on every edit regardless of whether anything meaningful did, which would otherwise show up as
/// permanent noise in every diff) and serialize to the JSON-array-keyed-by-id shape
/// <see cref="Core.Abstractions.IConfigSection"/> promises.
/// </summary>
internal static class ConfigCaptureSupport
{
    private static readonly string[] VolatileFields = ["createdDateTime", "modifiedDateTime", "lastModifiedDateTime"];

    public static string Normalize(IEnumerable<JsonElement> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            var obj = JsonNode.Parse(item.GetRawText())!.AsObject();
            foreach (var field in VolatileFields) obj.Remove(field);
            array.Add(obj);
        }
        return array.ToJsonString();
    }
}

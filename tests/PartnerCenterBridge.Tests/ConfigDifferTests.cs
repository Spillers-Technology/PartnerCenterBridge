using PartnerCenterBridge.Core.ConfigSnapshots;

namespace PartnerCenterBridge.Tests;

public class ConfigDifferTests
{
    [Fact]
    public void Diff_DetectsAddedRemovedAndModified()
    {
        var before = """[{"id":"1","displayName":"Require MFA","state":"enabled"},{"id":"2","displayName":"Block legacy auth","state":"enabled"}]""";
        var after = """[{"id":"1","displayName":"Require MFA","state":"disabled"},{"id":"3","displayName":"New policy","state":"enabled"}]""";

        var diff = ConfigDiffer.Diff("conditional-access-policies", "Conditional Access Policies", before, after);

        Assert.Equal(3, diff.Changes.Count);
        Assert.Contains(diff.Changes, c => c.Kind == ConfigChangeKind.Removed && c.ItemId == "2");
        Assert.Contains(diff.Changes, c => c.Kind == ConfigChangeKind.Added && c.ItemId == "3");

        var modified = Assert.Single(diff.Changes, c => c.Kind == ConfigChangeKind.Modified);
        Assert.Equal("1", modified.ItemId);
        var fieldChange = Assert.Single(modified.FieldChanges);
        Assert.Equal("state", fieldChange.Field);
        Assert.Equal("\"enabled\"", fieldChange.Before);
        Assert.Equal("\"disabled\"", fieldChange.After);
    }

    [Fact]
    public void Diff_IdenticalContent_HasNoChanges()
    {
        var json = """[{"id":"1","displayName":"Require MFA"}]""";
        var diff = ConfigDiffer.Diff("s", "Section", json, json);
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void Diff_EmptyBefore_TreatsEveryItemAsAdded()
    {
        var after = """[{"id":"1","displayName":"A"},{"id":"2","displayName":"B"}]""";
        var diff = ConfigDiffer.Diff("s", "Section", "[]", after);
        Assert.Equal(2, diff.Changes.Count);
        Assert.All(diff.Changes, c => Assert.Equal(ConfigChangeKind.Added, c.Kind));
    }

    [Fact]
    public void ToPatchText_NoChanges_SaysSo()
    {
        var json = """[{"id":"1"}]""";
        var diff = ConfigDiffer.Diff("s", "Section", json, json);
        var text = ConfigDiffFormatter.ToPatchText([diff]);
        Assert.Equal("No changes.\n", text);
    }

    [Fact]
    public void ToPatchText_RendersEachChangeKind()
    {
        var before = """[{"id":"1","displayName":"Old"}]""";
        var after = """[{"id":"1","displayName":"Old","state":"new"},{"id":"2","displayName":"Added"}]""";
        var diff = ConfigDiffer.Diff("sec", "Section Name", before, after);

        var text = ConfigDiffFormatter.ToPatchText([diff]);

        Assert.Contains("=== Section Name (sec) ===", text);
        Assert.Contains("+ added: Added (2)", text);
        Assert.Contains("~ modified: Old (1)", text);
        Assert.Contains("state", text);
    }
}

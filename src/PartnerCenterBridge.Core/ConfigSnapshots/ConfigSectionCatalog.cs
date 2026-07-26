using PartnerCenterBridge.Core.Abstractions;

namespace PartnerCenterBridge.Core.ConfigSnapshots;

/// <summary>Registry over the DI-registered <see cref="IConfigSection"/> implementations -- same pattern as <c>WorkflowCatalog</c>.</summary>
public class ConfigSectionCatalog
{
    public IReadOnlyList<IConfigSection> All { get; }

    public ConfigSectionCatalog(IEnumerable<IConfigSection> sections) =>
        All = sections.OrderBy(s => s.Category).ThenBy(s => s.Name).ToList();

    public IConfigSection? Find(string id) => All.FirstOrDefault(s => s.Id == id);
}

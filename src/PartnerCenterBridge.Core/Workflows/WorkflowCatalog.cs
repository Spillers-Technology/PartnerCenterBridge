namespace PartnerCenterBridge.Core.Workflows;

/// <summary>Registry of the available workflows, populated from DI. Lets the API list and dispatch them.</summary>
public class WorkflowCatalog
{
    private readonly IReadOnlyDictionary<string, IWorkflow> _byId;

    public WorkflowCatalog(IEnumerable<IWorkflow> workflows)
    {
        _byId = workflows.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IWorkflow> All => _byId.Values.ToList();

    public IWorkflow? Find(string id) => _byId.TryGetValue(id, out var w) ? w : null;
}

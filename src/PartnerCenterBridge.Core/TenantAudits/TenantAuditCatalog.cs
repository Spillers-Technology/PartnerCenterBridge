namespace PartnerCenterBridge.Core.TenantAudits;

/// <summary>A named selection of checks offered in the run picker.</summary>
public sealed record AuditPreset(string Id, string Name, string Description, IReadOnlyList<string> Categories);

/// <summary>A validated run request: which checks, in run order, with resolved parameters.</summary>
public sealed record AuditSelection(string AuditName, IReadOnlyList<ITenantAuditCheck> Checks, SortedDictionary<string, int> Parameters);

/// <summary>Raised for an invalid run request (unknown check, bad parameter); maps to HTTP 400.</summary>
public sealed class AuditRequestException(string message) : Exception(message);

/// <summary>
/// Registry over the DI-registered <see cref="ITenantAuditCheck"/> implementations -- the same
/// shape as <c>ConfigSectionCatalog</c> and <c>WorkflowCatalog</c>. Owns run-request validation
/// so the API, a future scheduler and MCP all resolve selections identically.
/// </summary>
public sealed class TenantAuditCatalog
{
    public const string FullPresetId = "full";

    public TenantAuditCatalog(IEnumerable<ITenantAuditCheck> checks)
    {
        var list = checks.ToList();
        var duplicate = list.GroupBy(c => c.Descriptor.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Two tenant audit checks share the id '{duplicate.Key}'.");

        All = list
            .OrderBy(c => AuditCategories.Order(c.Descriptor.Category))
            .ThenBy(c => c.Descriptor.Id, StringComparer.Ordinal)
            .ToList();

        // A parameter key must mean one thing everywhere: two checks declaring the same key with
        // different bounds or defaults is a programming error, caught at startup.
        var parameters = new Dictionary<string, AuditParameterDefinition>(StringComparer.Ordinal);
        foreach (var p in All.SelectMany(c => c.Descriptor.Parameters))
        {
            if (parameters.TryGetValue(p.Key, out var existing) && existing != p)
                throw new InvalidOperationException($"Tenant audit parameter '{p.Key}' is declared inconsistently by two checks.");
            parameters[p.Key] = p;
        }
        Parameters = parameters.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every check, in canonical run/report order (category order, then id).</summary>
    public IReadOnlyList<ITenantAuditCheck> All { get; }

    public IReadOnlyList<AuditParameterDefinition> Parameters { get; }

    public ITenantAuditCheck? Find(string id) => All.FirstOrDefault(c => c.Descriptor.Id == id);

    public IReadOnlyList<AuditPreset> Presets
    {
        get
        {
            var presets = new List<AuditPreset>
            {
                new(FullPresetId, "Full tenant health check", "Every available check in every category.", AuditCategories.All)
            };
            foreach (var category in AuditCategories.All.Where(c => All.Any(x => x.Descriptor.Category == c)))
                presets.Add(new(category.ToLowerInvariant(), AuditCategories.Label(category),
                    $"{All.Count(x => x.Descriptor.Category == category)} checks.", [category]));
            return presets;
        }
    }

    /// <summary>
    /// Resolves a run request. Checks can be named directly, by category, or both (union); with
    /// neither, every check runs. Parameter values outside a definition's bounds are rejected, not
    /// silently clamped, so a stored run never claims a threshold it did not use.
    /// </summary>
    public AuditSelection Resolve(IEnumerable<string>? checkIds, IEnumerable<string>? categories, IReadOnlyDictionary<string, int>? parameters)
    {
        var ids = (checkIds ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        var cats = (categories ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();

        foreach (var id in ids)
            if (Find(id) is null) throw new AuditRequestException($"Unknown audit check '{id}'.");
        foreach (var c in cats)
            if (!AuditCategories.All.Contains(c, StringComparer.OrdinalIgnoreCase))
                throw new AuditRequestException($"Unknown audit category '{c}'. Use one of: {string.Join(", ", AuditCategories.All)}.");

        var everything = ids.Count == 0 && cats.Count == 0;
        var selected = All.Where(c => everything
            || ids.Contains(c.Descriptor.Id, StringComparer.Ordinal)
            || cats.Contains(c.Descriptor.Category, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0) throw new AuditRequestException("The selection contains no audit checks.");

        var resolved = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var given = parameters ?? new Dictionary<string, int>();
        foreach (var key in given.Keys)
            if (Parameters.All(p => p.Key != key))
                throw new AuditRequestException($"Unknown audit parameter '{key}'.");
        foreach (var p in selected.SelectMany(c => c.Descriptor.Parameters).DistinctBy(p => p.Key))
        {
            var value = given.TryGetValue(p.Key, out var v) ? v : p.Default;
            if (value < p.Min || value > p.Max)
                throw new AuditRequestException($"{p.Label} must be between {p.Min} and {p.Max}.");
            resolved[p.Key] = value;
        }

        return new AuditSelection(NameFor(selected), selected, resolved);
    }

    private string NameFor(IReadOnlyList<ITenantAuditCheck> selected)
    {
        if (selected.Count == All.Count) return "Full tenant health check";
        var cats = selected.Select(c => c.Descriptor.Category).Distinct().OrderBy(AuditCategories.Order).ToList();
        // Whole categories read as their names; a hand-picked subset says so.
        var whole = cats.All(cat => All.Where(c => c.Descriptor.Category == cat).All(selected.Contains));
        var label = string.Join(", ", cats.Select(AuditCategories.Label));
        return whole ? label : selected.Count == 1 ? selected[0].Descriptor.Name : $"Custom audit ({label})";
    }
}

/// <summary>Small helpers so checks build findings consistently and tersely.</summary>
public static class AuditFindingBuilder
{
    /// <summary>
    /// A finding for <paramref name="d"/>. Non-passing findings inherit the descriptor's business
    /// impact and recommendation unless overridden; Pass findings carry neither.
    /// </summary>
    public static AuditFinding Create(
        AuditCheckDescriptor d, string key, AuditSeverity severity, string title, string summary,
        IEnumerable<AuditColumn>? columns = null, IEnumerable<AuditSubject>? subjects = null,
        string? businessImpact = null, string? recommendation = null)
    {
        var subjectList = subjects?.ToList() ?? new List<AuditSubject>();
        var passing = severity == AuditSeverity.Pass;
        return new AuditFinding
        {
            Id = $"{d.Id}:{key}",
            CheckId = d.Id,
            Severity = severity,
            Title = title,
            Summary = summary,
            BusinessImpact = passing ? null : businessImpact ?? d.BusinessImpact,
            Recommendation = passing ? null : recommendation ?? d.Recommendation,
            Columns = columns?.ToList() ?? new List<AuditColumn>(),
            Subjects = subjectList,
            SubjectCount = subjectList.Count
        };
    }

    public static AuditFinding Pass(AuditCheckDescriptor d, string summary, string? title = null) =>
        Create(d, "pass", AuditSeverity.Pass, title ?? d.Name, summary);

    /// <summary>"1 user" / "3 users".</summary>
    public static string Count(int n, string singular, string? plural = null) =>
        $"{n} {(n == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>Invariant ISO-8601 UTC date (yyyy-MM-dd) for subject properties; empty when null.</summary>
    public static string? Date(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static string YesNo(bool value) => value ? "Yes" : "No";
}

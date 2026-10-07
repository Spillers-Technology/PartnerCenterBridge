using System.Collections.Concurrent;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Core.TenantAudits;

/// <summary>
/// One read-only tenant health check. Implementations are discovered by type (see
/// <c>TenantAuditRegistration</c>) and listed by <see cref="TenantAuditCatalog"/>, so adding a check
/// is adding a class: no controller, UI or export change.
/// <para/>
/// A check reads tenant state only through the audit data providers (<see cref="IAuditDirectoryData"/>
/// and friends), which fetch each dataset once per tenant per run and translate "PCB cannot read
/// this here" into <see cref="AuditUnavailableException"/>. A check never writes to a tenant.
/// </summary>
public interface ITenantAuditCheck
{
    AuditCheckDescriptor Descriptor { get; }

    /// <summary>
    /// Evaluate the tenant and return the findings (at least one -- emit a Pass finding when
    /// nothing is wrong, so a clean result is visible and exportable). Throw
    /// <see cref="AuditUnavailableException"/> (or let a provider throw it) when the check cannot
    /// run here; any other exception is recorded as an Error for this check only.
    /// </summary>
    Task<AuditCheckOutput> RunAsync(AuditCheckContext context);
}

/// <summary>What a check returns: findings plus notes on scope and caveats.</summary>
public sealed class AuditCheckOutput
{
    public List<AuditFinding> Findings { get; } = new();
    public List<string> Notes { get; } = new();

    public AuditCheckOutput Add(AuditFinding finding)
    {
        Findings.Add(finding);
        return this;
    }

    public AuditCheckOutput Note(string note)
    {
        Notes.Add(note);
        return this;
    }
}

/// <summary>
/// Per-tenant, per-run state handed to every check: the tenant, resolved parameters, a fixed
/// "now" (so every check in a run measures inactivity from the same instant), and the dataset
/// cache. One context per tenant, so a multi-tenant run is just several contexts.
/// </summary>
public sealed class AuditCheckContext
{
    public AuditCheckContext(Tenant tenant, AuditParameters parameters, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Tenant = tenant;
        Parameters = parameters;
        Now = now;
        CancellationToken = cancellationToken;
    }

    public Tenant Tenant { get; }
    public AuditParameters Parameters { get; }
    public DateTimeOffset Now { get; }
    public CancellationToken CancellationToken { get; }
    public AuditDataCache Cache { get; } = new();
}

/// <summary>
/// Memoizes datasets for one tenant within one run: the user list is read once and shared by
/// every identity and licensing check. A failed load (e.g. 403) is cached too, so eight checks
/// that need the same data report the same reason without eight identical Graph calls.
/// </summary>
public sealed class AuditDataCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _items = new(StringComparer.Ordinal);

    public async Task<T> GetAsync<T>(string key, Func<Task<T>> load)
    {
        var lazy = _items.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => (await load())!));
        return (T)await lazy.Value;
    }
}

/// <summary>Resolved run parameters. Unknown keys and out-of-range values are rejected up front by the catalog.</summary>
public sealed class AuditParameters
{
    private readonly IReadOnlyDictionary<string, int> _values;

    public AuditParameters(IReadOnlyDictionary<string, int> values) => _values = values;

    public static AuditParameters Defaults { get; } = new(new Dictionary<string, int>());

    public int Get(AuditParameterDefinition definition) =>
        _values.TryGetValue(definition.Key, out var v) ? Math.Clamp(v, definition.Min, definition.Max) : definition.Default;
}

/// <summary>
/// "PCB cannot evaluate this here" -- a missing Graph permission or GDAP role, a license the
/// tenant does not have, a product it does not use, or a PCB dependency that is not configured.
/// The engine records the check as <see cref="AuditCheckStatus.Unavailable"/> with this reason; it
/// never fails the whole audit.
/// </summary>
public sealed class AuditUnavailableException : Exception
{
    public AuditUnavailableException(string reason, IEnumerable<string>? missing = null) : base(reason) =>
        Missing = missing?.ToList() ?? new List<string>();

    /// <summary>Requirement(s) believed missing, as display strings, e.g. "Graph permission AuditLog.Read.All".</summary>
    public IReadOnlyList<string> Missing { get; }
}

/// <summary>The fixed category names, in display order.</summary>
public static class AuditCategories
{
    public const string Identity = "Identity";
    public const string Licensing = "Licensing";
    public const string Exchange = "Exchange";
    public const string Devices = "Devices";
    public const string Security = "Security";

    public static IReadOnlyList<string> All { get; } = [Identity, Licensing, Exchange, Devices, Security];

    /// <summary>Human label for the run picker and reports.</summary>
    public static string Label(string category) => category switch
    {
        Identity => "Identity hygiene",
        Licensing => "Licensing",
        Exchange => "Exchange / Mail",
        Devices => "Endpoint / Intune",
        Security => "Tenant security",
        _ => category
    };

    public static int Order(string category)
    {
        for (var i = 0; i < All.Count; i++)
            if (All[i] == category) return i;
        return int.MaxValue;
    }
}

/// <summary>Shared run parameters. Checks reference these instances so the same key means the same thing everywhere.</summary>
public static class AuditParameterKeys
{
    public static readonly AuditParameterDefinition InactiveDays = new(
        "inactiveDays", "Inactive for (days)", 90, 7, 730, [30, 60, 90, 180],
        "Accounts with no qualifying sign-in for at least this many days count as inactive.");

    public static readonly AuditParameterDefinition DeviceStaleDays = new(
        "deviceStaleDays", "Device not synced for (days)", 30, 7, 365, [14, 30, 60, 90],
        "Intune-managed devices that have not checked in for at least this many days count as stale.");
}

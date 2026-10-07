using PartnerCenterBridge.Core.Operations;

namespace PartnerCenterBridge.Core.TenantAudits;

// Tenant Audits (health checks): read-only checks against a tenant's Microsoft 365 state that
// produce structured findings. Not to be confused with AuditEvent, which is PCB's own security
// log of who did what in the workbench. See docs/tenant-audits.html.

/// <summary>
/// How a finding reads. Ordered by weight so callers can compare (Fail is the worst).
/// <see cref="Unknown"/> means the data PCB could read does not prove either way.
/// </summary>
public enum AuditSeverity
{
    Pass = 0,
    Info = 1,
    Unknown = 2,
    Warn = 3,
    Fail = 4
}

/// <summary>Whether a check ran. A check that did not complete still appears in every result and export.</summary>
public enum AuditCheckStatus
{
    /// <summary>The check read what it needed and produced findings (at least one, even if just Pass).</summary>
    Completed,
    /// <summary>The check could not run here: a missing permission, license, product or dependency. Not a tenant problem.</summary>
    Unavailable,
    /// <summary>The check was attempted and failed unexpectedly (throttling, outage, a bug).</summary>
    Error
}

/// <summary>One-word health of a tenant's audit run, used for estate summaries.</summary>
public enum AuditHealth
{
    /// <summary>Every completed check passed or produced only Info/Unknown findings.</summary>
    Healthy,
    /// <summary>At least one Warn finding and no Fail findings.</summary>
    AttentionNeeded,
    /// <summary>At least one Fail finding.</summary>
    HighRisk,
    /// <summary>No requested check completed (usually missing permissions or a lost connection).</summary>
    InsufficientAccess
}

/// <summary>What kind of dependency a check needs.</summary>
public enum AuditRequirementKind
{
    /// <summary>A Microsoft Graph permission (delegated scope or GDAP role coverage), e.g. User.Read.All.</summary>
    GraphPermission,
    /// <summary>A license the tenant must hold for the data to exist, e.g. Microsoft Entra ID P1.</summary>
    License,
    /// <summary>A product that must be in use in the tenant, e.g. Microsoft Intune.</summary>
    Product,
    /// <summary>A PCB-side dependency, e.g. Exchange Online app-only certificate setup.</summary>
    Dependency
}

public sealed record AuditRequirement(AuditRequirementKind Kind, string Name, string? Note = null)
{
    public static AuditRequirement Graph(string permission, string? note = null) => new(AuditRequirementKind.GraphPermission, permission, note);
    public static AuditRequirement License(string license, string? note = null) => new(AuditRequirementKind.License, license, note);
    public static AuditRequirement Product(string product, string? note = null) => new(AuditRequirementKind.Product, product, note);
    public static AuditRequirement Dependency(string dependency, string? note = null) => new(AuditRequirementKind.Dependency, dependency, note);

    public override string ToString() => Kind switch
    {
        AuditRequirementKind.GraphPermission => $"Graph permission {Name}",
        AuditRequirementKind.License => $"License: {Name}",
        AuditRequirementKind.Product => $"Product: {Name}",
        _ => Name
    };
}

/// <summary>
/// A run-level numeric parameter a check reads (e.g. the inactivity threshold). Parameters are
/// shared by key across checks, so one "inactive days" value drives every inactivity check in a run.
/// </summary>
public sealed record AuditParameterDefinition(
    string Key, string Label, int Default, int Min, int Max, IReadOnlyList<int> Suggested, string Description);

/// <summary>Everything static about a check: what it is, why it matters, what it needs, how it grades.</summary>
public sealed record AuditCheckDescriptor
{
    /// <summary>Stable kebab-case id. Never reuse or rename: exports and history key on it.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>One of <see cref="AuditCategories"/>.</summary>
    public required string Category { get; init; }
    /// <summary>What the check looks at, in one sentence.</summary>
    public required string Description { get; init; }
    /// <summary>One or two short sentences of business context. Copied onto findings unless a finding overrides it.</summary>
    public required string BusinessImpact { get; init; }
    /// <summary>Default recommendation for non-passing findings.</summary>
    public required string Recommendation { get; init; }
    /// <summary>Short lines describing how findings are graded, e.g. "Fail: a Global Administrator has not signed in for N+ days".</summary>
    public IReadOnlyList<string> SeverityRules { get; init; } = [];
    public IReadOnlyList<AuditRequirement> Requirements { get; init; } = [];
    public IReadOnlyList<AuditParameterDefinition> Parameters { get; init; } = [];
    /// <summary>What the check cannot see, stated up front (copied into every result's notes).</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
    /// <summary>Bump when grading or output shape changes, so stored results can be told apart.</summary>
    public int Version { get; init; } = 1;
}

/// <summary>A column of a finding's subject table: the key into <see cref="AuditSubject.Properties"/> and its display label.</summary>
public sealed record AuditColumn(string Key, string Label);

/// <summary>
/// A pointer from a finding (or one subject) into PCB's existing planned-operation or known-fix
/// workflows. The audit never acts on it; the UI offers it as a link that opens the normal
/// plan -> review -> apply -> verify flow.
/// </summary>
/// <param name="Kind">"offboarding" (Target = user id) or "workflow" (Target = workflow id).</param>
public sealed record AuditRemediation(string Kind, string Target, string Label, string? Identity = null);

/// <summary>The object a finding is about: a user, device, mailbox, policy, app, SKU...</summary>
public sealed class AuditSubject
{
    /// <summary>e.g. "user", "device", "mailbox", "policy", "servicePrincipal", "subscription", "role".</summary>
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Upn { get; set; }
    /// <summary>The evidence for this subject in one line (what PCB read that put it in the finding).</summary>
    public string? Evidence { get; set; }
    /// <summary>Values for the finding's <see cref="AuditFinding.Columns"/>. Missing keys render empty.</summary>
    public Dictionary<string, string?> Properties { get; set; } = new();
    public AuditRemediation? Remediation { get; set; }
}

/// <summary>
/// One observation from a check. Subjects in a finding share its severity: a check that sees two
/// grades of the same problem emits two findings rather than per-row severities.
/// </summary>
public sealed class AuditFinding
{
    /// <summary>Stable within a check: "{checkId}:{key}".</summary>
    public string Id { get; set; } = "";
    public string CheckId { get; set; } = "";
    public AuditSeverity Severity { get; set; }
    /// <summary>Short title, e.g. "Dormant licensed users".</summary>
    public string Title { get; set; } = "";
    /// <summary>What PCB found, as a fact with counts, e.g. "17 licensed users have no successful sign-in in 90+ days."</summary>
    public string Summary { get; set; } = "";
    public string? BusinessImpact { get; set; }
    public string? Recommendation { get; set; }
    public List<AuditColumn> Columns { get; set; } = new();
    public List<AuditSubject> Subjects { get; set; } = new();
    /// <summary>Subjects found in total; larger than Subjects.Count only when the list was capped.</summary>
    public int SubjectCount { get; set; }
}

/// <summary>A check's outcome within one run.</summary>
public sealed class AuditCheckResult
{
    public string CheckId { get; set; } = "";
    public string CheckName { get; set; } = "";
    public string Category { get; set; } = "";
    public int CheckVersion { get; set; }
    public AuditCheckStatus Status { get; set; }
    /// <summary>Why the check is Unavailable or in Error. Null when Completed.</summary>
    public string? StatusReason { get; set; }
    /// <summary>The requirement(s) PCB believes are missing, as display strings. Empty when not known.</summary>
    public List<string> MissingRequirements { get; set; } = new();
    public List<AuditFinding> Findings { get; set; } = new();
    /// <summary>Scope and caveats for this result (what was evaluated, what could not be seen).</summary>
    public List<string> Notes { get; set; } = new();
    public long DurationMs { get; set; }

    /// <summary>Highest severity among the findings; Unknown for a check that did not complete.</summary>
    public AuditSeverity WorstSeverity => Status != AuditCheckStatus.Completed
        ? AuditSeverity.Unknown
        : Findings.Count == 0 ? AuditSeverity.Pass : Findings.Max(f => f.Severity);
}

/// <summary>Counts over a report, for list views, the summary bar and estate rollups.</summary>
public sealed class AuditSummaryCounts
{
    public int ChecksRequested { get; set; }
    public int ChecksCompleted { get; set; }
    public int ChecksUnavailable { get; set; }
    public int ChecksErrored { get; set; }
    public int Pass { get; set; }
    public int Info { get; set; }
    public int Unknown { get; set; }
    public int Warn { get; set; }
    public int Fail { get; set; }
    /// <summary>Subjects across non-passing findings (e.g. users, devices) -- a rough "items to review" count.</summary>
    public int AffectedSubjects { get; set; }
    public AuditHealth Health { get; set; }
}

/// <summary>
/// The full, self-describing result of one audit run against one tenant: what ran, with which
/// parameters, what each check found or why it could not run. Stored as JSON and exported as-is.
/// Never contains tokens or secrets.
/// </summary>
public sealed class TenantAuditReport
{
    /// <summary>Shape version of this document. Bump on breaking changes to the stored/exported JSON.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string RunId { get; set; } = "";
    /// <summary>Groups runs started together across several tenants. Null for a single-tenant run.</summary>
    public string? BatchId { get; set; }
    /// <summary>e.g. "Full tenant health check" or "Identity hygiene, Licensing".</summary>
    public string AuditName { get; set; } = "";
    public OperationTenantRef Tenant { get; set; } = new();
    public string Operator { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    /// <summary>PCB version that produced the run.</summary>
    public string EngineVersion { get; set; } = "";
    /// <summary>Resolved parameter values (defaults filled in), keyed by parameter key.</summary>
    public SortedDictionary<string, int> Parameters { get; set; } = new(StringComparer.Ordinal);
    public List<string> RequestedCheckIds { get; set; } = new();
    public List<AuditCheckResult> Checks { get; set; } = new();
    public AuditSummaryCounts Summary { get; set; } = new();
}

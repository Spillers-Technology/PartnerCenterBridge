using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PartnerCenterBridge.Core.TenantAudits;

/// <summary>
/// Normalized CSV: one row per finding subject (a finding with no subjects is one row, and a check
/// that did not complete is one row saying why), so the file loads straight into Excel, Power BI or
/// a script and nothing is silently missing. Output is deterministic for a given report.
/// <list type="bullet">
/// <item>RFC 4180 quoting, CRLF row endings, UTF-8 with BOM (so Excel reads non-ASCII names correctly).</item>
/// <item>Every value is a single line: CR, LF and TAB inside a value become spaces.</item>
/// <item>Spreadsheet formula injection: a text value starting with = + - @ (or a control character)
/// is prefixed with an apostrophe, so a hostile display name cannot run as a formula.</item>
/// <item>Columns are fixed; check-specific values that have no dedicated column go in Details as
/// "Label=value" pairs in the finding's column order.</item>
/// </list>
/// </summary>
public static class TenantAuditCsv
{
    public static readonly string[] Header =
    [
        "AuditRunId", "AuditName", "TenantId", "TenantName", "Timestamp", "Category", "CheckId", "CheckName",
        "Status", "Severity", "FindingId", "Finding", "Summary", "SubjectType", "SubjectId", "SubjectName", "UPN",
        "Evidence", "DaysInactive", "LastSuccessfulSignIn", "LicenseSku", "BusinessImpact", "Recommendation", "Details"
    ];

    /// <summary>Subject properties promoted to their own columns (and so left out of Details).</summary>
    private static readonly string[] PromotedKeys = ["daysInactive", "lastSuccessfulSignIn", "licenses"];

    public static string Write(TenantAuditReport report)
    {
        var sb = new StringBuilder();
        AppendRow(sb, Header);
        foreach (var row in Rows(report)) AppendRow(sb, row);
        return sb.ToString();
    }

    /// <summary>UTF-8 bytes with a byte-order mark, ready to save as a .csv file.</summary>
    public static byte[] WriteBytes(TenantAuditReport report) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(Write(report))).ToArray();

    /// <summary>The data rows (without the header), each aligned to <see cref="Header"/>.</summary>
    public static IEnumerable<string?[]> Rows(TenantAuditReport report)
    {
        var timestamp = report.StartedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        foreach (var check in report.Checks)
        {
            string?[] Prefix() =>
            [
                report.RunId, report.AuditName, report.Tenant.TenantId, report.Tenant.DisplayName, timestamp,
                check.Category, check.CheckId, check.CheckName, check.Status.ToString()
            ];

            if (check.Status != AuditCheckStatus.Completed)
            {
                yield return
                [
                    .. Prefix(), AuditSeverity.Unknown.ToString(), $"{check.CheckId}:{check.Status.ToString().ToLowerInvariant()}",
                    check.Status == AuditCheckStatus.Unavailable ? "Check unavailable" : "Check failed",
                    check.StatusReason, null, null, null, null, null, null, null, null, null, null,
                    check.MissingRequirements.Count == 0 ? null : "Missing=" + string.Join(" | ", check.MissingRequirements)
                ];
                continue;
            }

            foreach (var f in check.Findings)
            {
                string?[] FindingCells() => [.. Prefix(), f.Severity.ToString(), f.Id, f.Title, f.Summary];
                if (f.Subjects.Count == 0)
                {
                    yield return [.. FindingCells(), null, null, null, null, null, null, null, null, f.BusinessImpact, f.Recommendation, null];
                    continue;
                }
                foreach (var s in f.Subjects)
                {
                    yield return
                    [
                        .. FindingCells(), s.Type, s.Id, s.Name, s.Upn, s.Evidence,
                        s.Properties.GetValueOrDefault("daysInactive"),
                        s.Properties.GetValueOrDefault("lastSuccessfulSignIn"),
                        s.Properties.GetValueOrDefault("licenses"),
                        f.BusinessImpact, f.Recommendation, Details(f, s)
                    ];
                }
            }
        }
    }

    /// <summary>Remaining subject properties as "Label=value; ..." -- declared columns first, then any extras by key.</summary>
    private static string? Details(AuditFinding f, AuditSubject s)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(PromotedKeys, StringComparer.Ordinal);
        foreach (var c in f.Columns)
        {
            if (!seen.Add(c.Key)) continue;
            if (s.Properties.TryGetValue(c.Key, out var v) && !string.IsNullOrEmpty(v)) parts.Add($"{c.Label}={v}");
        }
        foreach (var kv in s.Properties.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (seen.Add(kv.Key) && !string.IsNullOrEmpty(kv.Value)) parts.Add($"{kv.Key}={kv.Value}");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static void AppendRow(StringBuilder sb, IEnumerable<string?> cells)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Escape(cell));
        }
        sb.Append("\r\n");
    }

    /// <summary>One CSV cell: single-lined, formula-neutralized, and quoted when needed.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var v = value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        if (IsFormulaLike(v)) v = "'" + v;
        var quote = v.IndexOfAny([',', '"']) >= 0 || v.StartsWith(' ') || v.EndsWith(' ');
        return quote ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>
    /// True when a spreadsheet would treat the value as a formula. Plain numbers (including negative
    /// ones) are left alone so numeric columns stay numeric.
    /// </summary>
    private static bool IsFormulaLike(string v)
    {
        if (v.Length == 0) return false;
        var c = v[0];
        if (c is '=' or '+' or '@' || char.IsControl(c)) return true;
        if (c == '-') return !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        return false;
    }
}

/// <summary>Full-fidelity JSON: the stored report shape (camelCase, enums as names, indented).</summary>
public static class TenantAuditJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Compact form for storage.</summary>
    public static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Write(TenantAuditReport report) => JsonSerializer.Serialize(report, Options);
    public static string Serialize(TenantAuditReport report) => JsonSerializer.Serialize(report, StorageOptions);
    public static TenantAuditReport? Deserialize(string json) => JsonSerializer.Deserialize<TenantAuditReport>(json, StorageOptions);
}

/// <summary>Ticket-ready Markdown: summary first, then each check worst-first with its findings and affected items.</summary>
public static class TenantAuditMarkdown
{
    /// <summary>Rows shown per finding table; the CSV/JSON exports always carry every row.</summary>
    public const int MaxRowsPerFinding = 50;

    public static string Write(TenantAuditReport r)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        var s = r.Summary;
        sb.AppendLine($"# {Text(r.AuditName)}: {Text(r.Tenant.DisplayName)}");
        sb.AppendLine();
        sb.AppendLine($"- **Tenant:** {Text(r.Tenant.DisplayName)} (`{r.Tenant.TenantId}`)");
        sb.AppendLine($"- **Run:** {r.StartedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", inv)} by {Text(r.Operator)} (run `{r.RunId}`)");
        sb.AppendLine($"- **Overall:** {Health(s.Health)}");
        sb.AppendLine($"- **Findings:** {s.Fail} fail, {s.Warn} warn, {s.Unknown} unknown, {s.Info} info, {s.Pass} pass");
        sb.AppendLine($"- **Checks:** {s.ChecksCompleted} of {s.ChecksRequested} completed" +
                      (s.ChecksUnavailable + s.ChecksErrored > 0 ? $" ({s.ChecksUnavailable} unavailable, {s.ChecksErrored} failed)" : ""));
        if (r.Parameters.Count > 0)
            sb.AppendLine($"- **Parameters:** {string.Join(", ", r.Parameters.Select(p => $"{p.Key}={p.Value}"))}");
        sb.AppendLine();
        sb.AppendLine("_A read-only health check: PCB read tenant configuration and changed nothing. It is not a compliance certification._");
        sb.AppendLine();

        var ordered = r.Checks
            .OrderByDescending(c => c.WorstSeverity)
            .ThenBy(c => AuditCategories.Order(c.Category))
            .ThenBy(c => c.CheckId, StringComparer.Ordinal);
        foreach (var c in ordered)
        {
            sb.AppendLine($"## {Text(c.CheckName)} ({AuditCategories.Label(c.Category)})");
            sb.AppendLine();
            if (c.Status != AuditCheckStatus.Completed)
            {
                sb.AppendLine($"**{(c.Status == AuditCheckStatus.Unavailable ? "Unavailable" : "Failed")}:** {Text(c.StatusReason)}");
                if (c.MissingRequirements.Count > 0) sb.AppendLine($"Missing: {Text(string.Join("; ", c.MissingRequirements))}");
                sb.AppendLine();
                continue;
            }
            foreach (var f in c.Findings)
            {
                sb.AppendLine($"**{f.Severity.ToString().ToUpperInvariant()}: {Text(f.Title)}** - {Text(f.Summary)}");
                sb.AppendLine();
                if (f.BusinessImpact is not null) sb.AppendLine($"- Business impact: {Text(f.BusinessImpact)}");
                if (f.Recommendation is not null) sb.AppendLine($"- Recommendation: {Text(f.Recommendation)}");
                if (f.BusinessImpact is not null || f.Recommendation is not null) sb.AppendLine();
                if (f.Subjects.Count > 0)
                {
                    var columns = f.Columns.Take(5).ToList();
                    sb.AppendLine("| Name | " + string.Join(" | ", columns.Select(x => Cell(x.Label))) + " |");
                    sb.AppendLine("|---|" + string.Concat(columns.Select(_ => "---|")));
                    foreach (var subj in f.Subjects.Take(MaxRowsPerFinding))
                    {
                        var name = subj.Upn is null || subj.Upn == subj.Name ? subj.Name : $"{subj.Name} ({subj.Upn})";
                        sb.AppendLine($"| {Cell(name)} | " + string.Join(" | ", columns.Select(x => Cell(subj.Properties.GetValueOrDefault(x.Key)))) + " |");
                    }
                    if (f.SubjectCount > MaxRowsPerFinding)
                        sb.AppendLine().AppendLine($"_{f.SubjectCount - MaxRowsPerFinding} more not shown; see the CSV export._");
                    sb.AppendLine();
                }
            }
            if (c.Notes.Count > 0)
            {
                sb.AppendLine("<details><summary>Scope and notes</summary>");
                sb.AppendLine();
                foreach (var n in c.Notes) sb.AppendLine($"- {Text(n)}");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    public static string Health(AuditHealth h) => h switch
    {
        AuditHealth.Healthy => "Healthy",
        AuditHealth.AttentionNeeded => "Attention needed",
        AuditHealth.HighRisk => "High-risk findings",
        AuditHealth.InsufficientAccess => "Insufficient access (no check could run)",
        _ => h.ToString()
    };

    private static string Text(string? s) => (s ?? "").Replace("\r", " ").Replace("\n", " ");
    private static string Cell(string? s) => Text(s).Replace("|", "\\|");
}

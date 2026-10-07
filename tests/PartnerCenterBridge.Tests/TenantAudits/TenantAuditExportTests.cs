using System.Text;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Core.TenantAudits;

namespace PartnerCenterBridge.Tests.TenantAudits;

public class TenantAuditExportTests
{
    internal static TenantAuditReport Report()
    {
        var report = new TenantAuditReport
        {
            RunId = "8d3c1d7e-0000-4000-8000-000000000001",
            AuditName = "Full tenant health check",
            Tenant = new OperationTenantRef { Id = "pcb-1", DisplayName = "Contoso, Ltd", TenantId = "11111111-2222-3333-4444-555555555555" },
            Operator = "tech@contoso.com",
            StartedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 5, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 10, 1, 12, 1, 0, TimeSpan.Zero),
            EngineVersion = "0.10.0",
            Parameters = new(StringComparer.Ordinal) { ["inactiveDays"] = 90 },
            Checks =
            [
                new AuditCheckResult
                {
                    CheckId = "dormant-licensed-users", CheckName = "Dormant licensed users", Category = "Licensing", Status = AuditCheckStatus.Completed,
                    Findings =
                    [
                        new AuditFinding
                        {
                            Id = "dormant-licensed-users:dormant", CheckId = "dormant-licensed-users", Severity = AuditSeverity.Warn,
                            Title = "Dormant licensed users", Summary = "2 licensed users have no successful sign-in recorded in the last 90 days.",
                            BusinessImpact = "Potential unnecessary spend.", Recommendation = "Review these users.",
                            Columns = [new("enabled", "Enabled"), new("licenses", "License / SKU(s)"), new("daysInactive", "Days inactive"), new("adminRoles", "Admin roles")],
                            Subjects =
                            [
                                new AuditSubject
                                {
                                    Type = "user", Id = "u1", Name = "=HYPERLINK(\"http://evil\")", Upn = "evil@contoso.com",
                                    Evidence = "Last successful sign-in 2026-06-03 (120 days ago).",
                                    Properties = { ["enabled"] = "Yes", ["licenses"] = "SPE_E3; EMS", ["daysInactive"] = "120", ["lastSuccessfulSignIn"] = "2026-06-03", ["adminRoles"] = null }
                                },
                                new AuditSubject
                                {
                                    Type = "user", Id = "u2", Name = "Zoë \"Z\" Müller, CFO", Upn = "zoe@contoso.com",
                                    Evidence = "line one\r\nline two",
                                    Properties = { ["enabled"] = "No", ["licenses"] = "SPE_E3", ["daysInactive"] = "300", ["adminRoles"] = "Global Administrator", ["extra"] = "x" }
                                }
                            ],
                            SubjectCount = 2
                        },
                        new AuditFinding { Id = "dormant-licensed-users:new-accounts", Severity = AuditSeverity.Info, Title = "New", Summary = "-1 is not a formula but -cmd is" }
                    ]
                },
                new AuditCheckResult
                {
                    CheckId = "mailbox-forwarding", CheckName = "External mailbox forwarding", Category = "Exchange", Status = AuditCheckStatus.Unavailable,
                    StatusReason = "Exchange Online is not configured.", MissingRequirements = ["Exchange Online app-only access"]
                }
            ]
        };
        report.Summary = AuditSummary.Compute(report);
        return report;
    }

    /// <summary>A minimal RFC 4180 reader, to prove the output parses back into the same cells.</summary>
    internal static List<List<string>> Parse(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new(); i++; }
            else cell.Append(c);
        }
        return rows;
    }

    private static Dictionary<string, string> Row(List<List<string>> rows, int index) =>
        rows[0].Zip(rows[index]).ToDictionary(p => p.First, p => p.Second);

    [Fact]
    public void One_row_per_subject_plus_finding_and_unavailable_rows_all_aligned_to_the_header()
    {
        var rows = Parse(TenantAuditCsv.Write(Report()));

        Assert.Equal(TenantAuditCsv.Header, rows[0]);
        Assert.Equal(1 + 2 + 1 + 1, rows.Count); // header, two subjects, one subject-less finding, one unavailable check
        Assert.All(rows, r => Assert.Equal(TenantAuditCsv.Header.Length, r.Count));
    }

    [Fact]
    public void Core_columns_carry_the_normalized_values()
    {
        var row = Row(Parse(TenantAuditCsv.Write(Report())), 2);

        Assert.Equal("8d3c1d7e-0000-4000-8000-000000000001", row["AuditRunId"]);
        Assert.Equal("11111111-2222-3333-4444-555555555555", row["TenantId"]);
        Assert.Equal("Contoso, Ltd", row["TenantName"]);
        Assert.Equal("2026-10-01T12:00:05Z", row["Timestamp"]);
        Assert.Equal("Completed", row["Status"]);
        Assert.Equal("Warn", row["Severity"]);
        Assert.Equal("user", row["SubjectType"]);
        Assert.Equal("zoe@contoso.com", row["UPN"]);
        Assert.Equal("300", row["DaysInactive"]);
        Assert.Equal("SPE_E3", row["LicenseSku"]);
        Assert.Equal("Potential unnecessary spend.", row["BusinessImpact"]);
        // Promoted columns are not repeated in Details; the rest follow the finding's column order, then extras.
        Assert.Equal("Enabled=No; Admin roles=Global Administrator; extra=x", row["Details"]);
    }

    [Fact]
    public void Unavailable_checks_are_a_row_with_the_reason_not_an_omission()
    {
        var row = Row(Parse(TenantAuditCsv.Write(Report())), 4);

        Assert.Equal("mailbox-forwarding", row["CheckId"]);
        Assert.Equal("Unavailable", row["Status"]);
        Assert.Equal("Unknown", row["Severity"]);
        Assert.Equal("Check unavailable", row["Finding"]);
        Assert.Equal("Exchange Online is not configured.", row["Summary"]);
        Assert.Equal("Missing=Exchange Online app-only access", row["Details"]);
    }

    [Fact]
    public void Commas_quotes_newlines_and_unicode_survive_and_every_value_is_one_line()
    {
        var csv = TenantAuditCsv.Write(Report());
        var row = Row(Parse(csv), 2);

        Assert.Equal("Zoë \"Z\" Müller, CFO", row["SubjectName"]);
        Assert.Equal("line one line two", row["Evidence"]);
        Assert.Equal(5, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1-555", "'+1-555")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("-cmd|' /C calc'!A0", "'-cmd|' /C calc'!A0")]
    [InlineData("\tlead", " lead")] // a leading tab becomes a space, which spreadsheets treat as text
    [InlineData("-5", "-5")]
    [InlineData("120", "120")]
    [InlineData("plain text", "plain text")]
    public void Formula_injection_is_neutralized_but_numbers_stay_numbers(string value, string expectedCell)
    {
        var escaped = TenantAuditCsv.Escape(value);
        var parsed = Parse(escaped + "\r\n")[0][0];
        Assert.Equal(expectedCell, parsed);
    }

    [Fact]
    public void A_hostile_display_name_is_exported_inert()
    {
        var row = Row(Parse(TenantAuditCsv.Write(Report())), 1);
        Assert.StartsWith("'=HYPERLINK", row["SubjectName"]);
    }

    [Fact]
    public void Bytes_start_with_a_utf8_bom_for_excel_and_output_is_deterministic()
    {
        var bytes = TenantAuditCsv.WriteBytes(Report());

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(bytes, TenantAuditCsv.WriteBytes(Report()));
        Assert.Contains("Müller", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Json_round_trips_the_full_report()
    {
        var report = Report();
        var back = TenantAuditJson.Deserialize(TenantAuditJson.Serialize(report))!;

        Assert.Equal(TenantAuditJson.Serialize(report), TenantAuditJson.Serialize(back));
        Assert.Contains("\"severity\": \"Warn\"", TenantAuditJson.Write(report));
        Assert.Contains("\"schemaVersion\": 1", TenantAuditJson.Write(report));
    }

    [Fact]
    public void Markdown_is_ticket_ready_and_does_not_claim_compliance()
    {
        var md = TenantAuditMarkdown.Write(Report());

        Assert.StartsWith("# Full tenant health check: Contoso, Ltd", md);
        Assert.Contains("not a compliance certification", md);
        Assert.Contains("**WARN: Dormant licensed users**", md);
        Assert.Contains("**Unavailable:** Exchange Online is not configured.", md);
        Assert.Contains("- Business impact: Potential unnecessary spend.", md);
        Assert.DoesNotContain("line one\r\n", md);
        // Worst check first: the unavailable one has Unknown worst-severity, below the Warn check.
        Assert.True(md.IndexOf("## Dormant licensed users", StringComparison.Ordinal) < md.IndexOf("## External mailbox forwarding", StringComparison.Ordinal));
    }
}

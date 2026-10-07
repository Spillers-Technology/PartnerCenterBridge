using PartnerCenterBridge.Core.TenantAudits;
using static PartnerCenterBridge.Tests.TenantAudits.AuditTest;

namespace PartnerCenterBridge.Tests.TenantAudits;

public class TenantAuditEngineTests
{
    /// <summary>A check whose behavior is a delegate.</summary>
    internal sealed class LambdaCheck(string id, Func<AuditCheckContext, Task<AuditCheckOutput>> run, string category = AuditCategories.Identity) : ITenantAuditCheck
    {
        public int Calls { get; private set; }
        public AuditCheckDescriptor Descriptor { get; } = new()
        {
            Id = id, Name = id, Category = category, Description = "d", BusinessImpact = "impact", Recommendation = "rec",
            Requirements = [AuditRequirement.Graph("User.Read.All")], Limitations = ["a limitation"]
        };
        public Task<AuditCheckOutput> RunAsync(AuditCheckContext context) { Calls++; return run(context); }
    }

    private static AuditSelection Select(params ITenantAuditCheck[] checks) =>
        new("Test audit", checks, new SortedDictionary<string, int>(StringComparer.Ordinal) { ["inactiveDays"] = 90 });

    private static Task<TenantAuditReport> RunAsync(params ITenantAuditCheck[] checks) =>
        TenantAuditEngine.RunAsync(Tenant(), Select(checks), "tech@contoso.com", "1.2.3", new FixedClock(Now));

    [Fact]
    public async Task One_unavailable_or_failing_check_never_stops_the_rest()
    {
        var unavailable = new LambdaCheck("a-unavailable", _ => throw new AuditUnavailableException("no permission", ["Graph permission X.Read.All"]));
        var broken = new LambdaCheck("b-broken", _ => throw new InvalidOperationException("Graph 503: try later"));
        var fine = new LambdaCheck("c-fine", _ => Task.FromResult(new AuditCheckOutput()));

        var report = await RunAsync(unavailable, broken, fine);

        Assert.Equal([AuditCheckStatus.Unavailable, AuditCheckStatus.Error, AuditCheckStatus.Completed], report.Checks.Select(c => c.Status));
        Assert.Equal("no permission", report.Checks[0].StatusReason);
        Assert.Equal(["Graph permission X.Read.All"], report.Checks[0].MissingRequirements);
        Assert.Equal("Graph 503: try later", report.Checks[1].StatusReason);
        Assert.Equal(1, fine.Calls);
    }

    [Fact]
    public async Task A_completed_check_with_no_findings_gets_an_explicit_pass_and_its_limitations()
    {
        var report = await RunAsync(new LambdaCheck("quiet", _ => Task.FromResult(new AuditCheckOutput())));

        var check = Assert.Single(report.Checks);
        Assert.Equal(AuditSeverity.Pass, Assert.Single(check.Findings).Severity);
        Assert.Contains("a limitation", check.Notes);
    }

    [Fact]
    public async Task Report_records_who_when_what_and_with_which_parameters()
    {
        var report = await RunAsync(new LambdaCheck("x", _ => Task.FromResult(new AuditCheckOutput())));

        Assert.Equal(TenantAuditReport.CurrentSchemaVersion, report.SchemaVersion);
        Assert.Equal("Contoso Ltd", report.Tenant.DisplayName);
        Assert.Equal("11111111-2222-3333-4444-555555555555", report.Tenant.TenantId);
        Assert.Equal("tech@contoso.com", report.Operator);
        Assert.Equal("1.2.3", report.EngineVersion);
        Assert.Equal(Now, report.StartedAt);
        Assert.Equal(["x"], report.RequestedCheckIds);
        Assert.Equal(90, report.Parameters["inactiveDays"]);
        Assert.True(Guid.TryParse(report.RunId, out _));
    }

    [Fact]
    public async Task Findings_and_subjects_come_out_in_a_deterministic_order()
    {
        var check = new LambdaCheck("order", _ =>
        {
            var o = new AuditCheckOutput();
            o.Add(new AuditFinding { Id = "order:b", Severity = AuditSeverity.Info, Subjects = [new() { Name = "zed", Id = "2" }, new() { Name = "Amy", Id = "1" }] });
            o.Add(new AuditFinding { Id = "order:a", Severity = AuditSeverity.Fail });
            o.Add(new AuditFinding { Id = "order:c", Severity = AuditSeverity.Info });
            return Task.FromResult(o);
        });

        var findings = (await RunAsync(check)).Checks[0].Findings;

        Assert.Equal(["order:a", "order:b", "order:c"], findings.Select(f => f.Id));
        Assert.Equal(["Amy", "zed"], findings[1].Subjects.Select(s => s.Name));
        Assert.All(findings, f => Assert.Equal("order", f.CheckId));
    }

    [Fact]
    public async Task Huge_subject_lists_are_capped_but_the_true_count_is_kept()
    {
        var check = new LambdaCheck("big", _ => Task.FromResult(new AuditCheckOutput().Add(new AuditFinding
        {
            Id = "big:x", Severity = AuditSeverity.Warn,
            Subjects = Enumerable.Range(0, TenantAuditEngine.MaxSubjectsPerFinding + 7).Select(i => new AuditSubject { Id = $"{i}", Name = $"u{i:D6}" }).ToList()
        })));

        var f = (await RunAsync(check)).Checks[0].Findings[0];

        Assert.Equal(TenantAuditEngine.MaxSubjectsPerFinding, f.Subjects.Count);
        Assert.Equal(TenantAuditEngine.MaxSubjectsPerFinding + 7, f.SubjectCount);
    }

    [Fact]
    public async Task Cancellation_stops_the_run_instead_of_being_recorded_as_an_error()
    {
        using var cts = new CancellationTokenSource();
        var check = new LambdaCheck("slow", _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TenantAuditEngine.RunAsync(Tenant(), Select(check), "op", "v", new FixedClock(Now), ct: cts.Token));
    }

    [Fact]
    public async Task Datasets_are_read_once_per_tenant_per_run_and_failures_are_cached_too()
    {
        var calls = 0;
        var ctx = Context();
        Task<int> Load() { calls++; return Task.FromResult(42); }
        Task<int> Fail() { calls++; throw new AuditUnavailableException("denied"); }

        Assert.Equal(42, await ctx.Cache.GetAsync("users", Load));
        Assert.Equal(42, await ctx.Cache.GetAsync("users", Load));
        await Assert.ThrowsAsync<AuditUnavailableException>(() => ctx.Cache.GetAsync("roles", Fail));
        await Assert.ThrowsAsync<AuditUnavailableException>(() => ctx.Cache.GetAsync("roles", Fail));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(0, 0, 0, 3, 0, AuditHealth.Healthy)]
    [InlineData(0, 2, 0, 3, 0, AuditHealth.AttentionNeeded)]
    [InlineData(1, 2, 0, 3, 0, AuditHealth.HighRisk)]
    [InlineData(0, 0, 0, 0, 3, AuditHealth.InsufficientAccess)]
    [InlineData(0, 0, 5, 1, 2, AuditHealth.Healthy)] // Unknown findings and some unavailable checks don't alone make a tenant unhealthy
    public void Health_classification(int fail, int warn, int unknown, int completed, int unavailable, AuditHealth expected)
    {
        var c = new AuditSummaryCounts
        {
            Fail = fail, Warn = warn, Unknown = unknown, ChecksCompleted = completed, ChecksUnavailable = unavailable,
            ChecksRequested = completed + unavailable
        };
        Assert.Equal(expected, AuditSummary.Classify(c));
    }

    [Fact]
    public async Task Summary_counts_findings_of_completed_checks_and_affected_subjects_of_warn_and_fail()
    {
        var check = new LambdaCheck("mix", _ => Task.FromResult(new AuditCheckOutput()
            .Add(new AuditFinding { Id = "mix:f", Severity = AuditSeverity.Fail, SubjectCount = 2, Subjects = [new() { Id = "1" }, new() { Id = "2" }] })
            .Add(new AuditFinding { Id = "mix:w", Severity = AuditSeverity.Warn, SubjectCount = 3, Subjects = [new() { Id = "1" }, new() { Id = "2" }, new() { Id = "3" }] })
            .Add(new AuditFinding { Id = "mix:i", Severity = AuditSeverity.Info, SubjectCount = 10, Subjects = Enumerable.Range(0, 10).Select(i => new AuditSubject { Id = $"{i}" }).ToList() })
            .Add(new AuditFinding { Id = "mix:u", Severity = AuditSeverity.Unknown })));
        var unavailable = new LambdaCheck("gone", _ => throw new AuditUnavailableException("x"));

        var s = (await RunAsync(check, unavailable)).Summary;

        Assert.Equal((1, 1, 1, 1, 0), (s.Fail, s.Warn, s.Info, s.Unknown, s.Pass));
        Assert.Equal(5, s.AffectedSubjects);
        Assert.Equal((2, 1, 1, 0), (s.ChecksRequested, s.ChecksCompleted, s.ChecksUnavailable, s.ChecksErrored));
        Assert.Equal(AuditHealth.HighRisk, s.Health);
    }

    [Fact]
    public void Estate_summary_rolls_up_latest_runs()
    {
        var runs = new[]
        {
            new AuditSummaryCounts { Health = AuditHealth.Healthy },
            new AuditSummaryCounts { Health = AuditHealth.Healthy, ChecksUnavailable = 2 },
            new AuditSummaryCounts { Health = AuditHealth.AttentionNeeded },
            new AuditSummaryCounts { Health = AuditHealth.HighRisk, ChecksErrored = 1 },
            new AuditSummaryCounts { Health = AuditHealth.InsufficientAccess, ChecksUnavailable = 20 }
        };

        var e = AuditEstateSummary.From(runs);

        Assert.Equal((5, 2, 1, 1, 1, 3), (e.TenantsScanned, e.Healthy, e.AttentionNeeded, e.HighRisk, e.InsufficientAccess, e.WithCoverageGaps));
    }
}

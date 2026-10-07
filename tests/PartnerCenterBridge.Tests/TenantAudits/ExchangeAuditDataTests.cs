using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.TenantAudits;
using PartnerCenterBridge.Exchange;
using PartnerCenterBridge.Exchange.TenantAudits;
using static PartnerCenterBridge.Tests.TenantAudits.AuditTest;

namespace PartnerCenterBridge.Tests.TenantAudits;

public class ExchangeAuditDataTests
{
    private sealed class Runner(string stdout) : IPwshRunner
    {
        public int Calls { get; private set; }
        public string? Payload { get; private set; }
        public Task<PwshResult> RunAsync(string scriptPath, string payloadJson, CancellationToken ct = default)
        {
            Calls++;
            Payload = payloadJson;
            return Task.FromResult(new PwshResult(0, stdout, ""));
        }
    }

    private sealed class Org : ITenantExchangeOrganizationProvider
    {
        public Task<string> GetOrganizationAsync(Tenant tenant, CancellationToken ct = default) => Task.FromResult("contoso.onmicrosoft.com");
    }

    private static ExchangeOnlineService Service(IPwshRunner runner) => new(runner, new Org(),
        Options.Create(new ExchangeOptions { AppId = "app", CertificatePath = "/c.pfx" }), NullLogger<ExchangeOnlineService>.Instance);

    private const string ScriptOutput = """
        {"success":true,"steps":[{"name":"Connect","success":true,"detail":"contoso.onmicrosoft.com"},{"name":"Tenant check","success":true,"detail":"x"}],
         "data":{
           "mailboxes":[
             {"objectId":"u1","userPrincipalName":"ada@contoso.com","displayName":"Ada","primarySmtpAddress":"ada@contoso.com","recipientTypeDetails":"UserMailbox",
              "forwardingSmtpAddress":"smtp:ada.home@gmail.com","forwardingAddress":null,"forwardingAddressSmtp":null,"deliverToMailboxAndForward":true,
              "archiveEnabled":false,"archiveStatus":"None","autoExpandingArchiveEnabled":false,"litigationHoldEnabled":false,"grantSendOnBehalfTo":["Grace"]},
             {"objectId":"s1","userPrincipalName":"info@contoso.com","displayName":"Info","primarySmtpAddress":"info@contoso.com","recipientTypeDetails":"SharedMailbox",
              "forwardingSmtpAddress":null,"forwardingAddress":"Sales Team","forwardingAddressSmtp":"sales@contoso.com","deliverToMailboxAndForward":false,
              "archiveEnabled":true,"archiveStatus":"Active","autoExpandingArchiveEnabled":true,"litigationHoldEnabled":true,"grantSendOnBehalfTo":[]}
           ],
           "acceptedDomains":["contoso.com","contoso.onmicrosoft.com"],
           "autoForwardingMode":"On",
           "permissions":[{"mailbox":"Info","trustee":"ada@contoso.com","right":"SendAs"},{"mailbox":"info@contoso.com","trustee":"grace@contoso.com","right":"FullAccess"}],
           "fullAccessEvaluated":2,"fullAccessComplete":true,
           "partialErrors":["Send As permissions: partially denied"]
         },
         "notFound":false,"tenantMismatch":false}
        """;

    [Fact]
    public async Task Parses_the_audit_operation_and_sends_its_bounds()
    {
        var runner = new Runner(ScriptOutput.ReplaceLineEndings(""));

        var report = await Service(runner).GetMailboxAuditReportAsync(Tenant());

        using var payload = JsonDocument.Parse(runner.Payload!);
        Assert.Equal("auditMailboxes", payload.RootElement.GetProperty("operation").GetString());
        Assert.Equal(200, payload.RootElement.GetProperty("params").GetProperty("maxFullAccessMailboxes").GetInt32());
        Assert.Equal(2, report.Mailboxes.Count);
        var ada = report.Mailboxes[0];
        Assert.Equal(("u1", "smtp:ada.home@gmail.com", true, false), (ada.ObjectId, ada.ForwardingSmtpAddress, ada.DeliverToMailboxAndForward, ada.ArchiveEnabled));
        Assert.Equal(["Grace"], ada.GrantSendOnBehalfTo);
        Assert.True(report.Mailboxes[1].IsShared && report.Mailboxes[1].LitigationHoldEnabled);
        Assert.Equal("sales@contoso.com", report.Mailboxes[1].ForwardingAddressSmtp);
        Assert.Equal(["contoso.com", "contoso.onmicrosoft.com"], report.AcceptedDomains);
        Assert.Equal("On", report.AutoForwardingMode);
        Assert.Equal(2, report.Permissions.Count);
        Assert.True(report.FullAccessComplete);
        Assert.Equal(["Send As permissions: partially denied"], report.PartialErrors);
    }

    [Fact]
    public async Task A_failed_read_with_no_data_throws_with_the_script_reason()
    {
        var runner = new Runner("""{"success":false,"steps":[{"name":"Error","success":false,"detail":"The role assigned to application isn't supported"}],"data":null}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(runner).GetMailboxAuditReportAsync(Tenant()));
        Assert.Contains("role assigned to application", ex.Message);
    }

    [Fact]
    public async Task Not_configured_exchange_is_unavailable_with_the_same_reason_as_the_rest_of_the_app_and_never_runs_pwsh()
    {
        var runner = new Runner(ScriptOutput);
        var data = new ExchangeAuditMailboxData(new FixedExchangeCapability(false), Service(runner));

        Assert.Contains("Exchange:AppId", data.UnavailableReason);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() => data.GetMailboxReportAsync(Context()));
        Assert.Contains("Exchange:AppId", ex.Message);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task One_exchange_session_serves_every_exchange_check_in_a_run()
    {
        var runner = new Runner(ScriptOutput.ReplaceLineEndings(""));
        var data = new ExchangeAuditMailboxData(new FixedExchangeCapability(true), Service(runner));
        var ctx = Context();

        await data.GetMailboxReportAsync(ctx);
        await data.GetMailboxReportAsync(ctx);

        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public void The_audit_operation_in_the_script_is_read_only()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PartnerCenterBridge.Exchange", "Scripts", "exo-op.ps1"));
        var start = script.IndexOf("'auditMailboxes' {", StringComparison.Ordinal);
        var end = script.IndexOf("default { throw", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var block = script[start..end];

        var cmdlets = Regex.Matches(block, @"\b([A-Z][a-z]+)-(?:EXO)?[A-Z][A-Za-z]+\b").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("Get", cmdlets);
        Assert.All(cmdlets, verb => Assert.Contains(verb, new[] { "Get", "Resolve", "Select", "Where", "Add", "ForEach" }));
        // "Add-" is allowed only for the script's own Add-Step helper, never an Exchange cmdlet.
        Assert.DoesNotMatch(@"Add-(?!Step\b)", block);
        Assert.DoesNotMatch(@"\b(Set|New|Remove|Enable|Disable|Start|Stop|Update|Clear)-", block);
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PartnerCenterBridge.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

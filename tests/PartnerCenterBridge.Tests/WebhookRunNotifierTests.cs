using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Api.Notifications;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PartnerCenterBridge.Tests;

public class WebhookRunNotifierTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    private WebhookRunNotifier Notifier(string? url = null, string format = "teams", bool onSuccess = false) => new(
        new SingleHttpClientFactory(),
        Options.Create(new NotificationOptions { WebhookUrl = url ?? $"{_server.Url}/hook", Format = format, NotifyOnSuccess = onSuccess }),
        NullLogger<WebhookRunNotifier>.Instance);

    private static WorkflowRun FailedRun() => new()
    {
        WorkflowId = "mfa-reset",
        WorkflowName = "MFA / auth method reset",
        Kind = WorkflowRunKind.Remediate,
        Operator = "op@example.com",
        Tenant = new Tenant { TenantId = "t", DisplayName = "Contoso" },
        Succeeded = false,
        Steps = new List<ProvisioningStep> { new("Revoke sign-in sessions", false, "boom") }
    };

    private void StubHook(int status = 200) =>
        _server.Given(Request.Create().WithPath("/hook").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status));

    [Fact]
    public async Task Failure_posts_teams_card_with_workflow_and_tenant()
    {
        StubHook();

        await Notifier().NotifyAsync(FailedRun());

        var body = Assert.Single(_server.LogEntries).RequestMessage.Body!;
        Assert.Contains("application/vnd.microsoft.card.adaptive", body);
        Assert.Contains("Workflow FAILED: MFA / auth method reset", body);
        Assert.Contains("Contoso", body);
        Assert.Contains("Revoke sign-in sessions: boom", body);
    }

    [Fact]
    public async Task Json_format_posts_plain_summary()
    {
        StubHook();

        await Notifier(format: "json").NotifyAsync(FailedRun());

        var body = Assert.Single(_server.LogEntries).RequestMessage.Body!;
        Assert.DoesNotContain("AdaptiveCard", body);
        Assert.Contains("\"workflowId\":\"mfa-reset\"", body.Replace(" ", ""));
    }

    [Fact]
    public async Task Empty_url_disables_notifications()
    {
        await Notifier(url: "").NotifyAsync(FailedRun());

        Assert.Empty(_server.LogEntries);
    }

    [Fact]
    public async Task Success_is_silent_unless_opted_in()
    {
        StubHook();
        var run = FailedRun();
        run.Succeeded = true;

        await Notifier().NotifyAsync(run);
        Assert.Empty(_server.LogEntries);

        await Notifier(onSuccess: true).NotifyAsync(run);
        Assert.Single(_server.LogEntries);
    }

    [Fact]
    public async Task Webhook_errors_never_throw()
    {
        StubHook(500);

        await Notifier().NotifyAsync(FailedRun());
        // Reaching here without an exception is the assertion.
        Assert.Single(_server.LogEntries);
    }
}

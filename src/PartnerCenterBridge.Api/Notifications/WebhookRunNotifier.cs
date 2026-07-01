using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Api.Notifications;

public class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Incoming-webhook URL (Teams Workflows flow or any JSON receiver). Empty disables notifications.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>"teams" posts an Adaptive Card envelope; "json" posts a plain JSON summary.</summary>
    public string Format { get; set; } = "teams";

    /// <summary>Also notify on successful remediations, not just failures.</summary>
    public bool NotifyOnSuccess { get; set; }
}

/// <summary>
/// Posts workflow run outcomes to a configured webhook. Failures always notify; successes only
/// when <see cref="NotificationOptions.NotifyOnSuccess"/> is set. Diagnose runs notify only when
/// they errored (an unhealthy diagnosis is a normal result, not an incident).
/// </summary>
public class WebhookRunNotifier : IRunNotifier
{
    private readonly IHttpClientFactory _http;
    private readonly NotificationOptions _opts;
    private readonly ILogger<WebhookRunNotifier> _log;

    public WebhookRunNotifier(IHttpClientFactory http, IOptions<NotificationOptions> opts, ILogger<WebhookRunNotifier> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public async Task NotifyAsync(WorkflowRun run, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.WebhookUrl)) return;
        if (run.Succeeded && !(_opts.NotifyOnSuccess && run.Kind == WorkflowRunKind.Remediate)) return;

        try
        {
            var payload = _opts.Format.Equals("teams", StringComparison.OrdinalIgnoreCase)
                ? TeamsCard(run)
                : PlainJson(run);
            using var resp = await _http.CreateClient("notifications").PostAsJsonAsync(_opts.WebhookUrl, payload, ct);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("Run notification webhook returned {Status} for workflow {WorkflowId}",
                    (int)resp.StatusCode, run.WorkflowId);
        }
        catch (Exception ex)
        {
            // Best-effort: a broken webhook must never fail the operator's request.
            _log.LogWarning(ex, "Run notification webhook failed for workflow {WorkflowId}", run.WorkflowId);
        }
    }

    private static object PlainJson(WorkflowRun run) => new
    {
        workflowId = run.WorkflowId,
        workflowName = run.WorkflowName,
        tenant = run.Tenant?.DisplayName ?? run.TenantId.ToString(),
        kind = run.Kind.ToString(),
        @operator = run.Operator,
        succeeded = run.Succeeded,
        healthy = run.Healthy,
        error = run.Error,
        failedSteps = run.Steps.Where(s => !s.Success).Select(s => new { s.Name, s.Detail }).ToList(),
        startedAt = run.StartedAt
    };

    private static object TeamsCard(WorkflowRun run)
    {
        var facts = new List<object>
        {
            new { title = "Tenant", value = run.Tenant?.DisplayName ?? run.TenantId.ToString() },
            new { title = "Kind", value = run.Kind.ToString() },
            new { title = "Operator", value = run.Operator },
            new { title = "Started", value = run.StartedAt.ToString("u") }
        };
        if (!string.IsNullOrEmpty(run.Error))
            facts.Add(new { title = "Error", value = run.Error });
        var failed = run.Steps.Where(s => !s.Success).Select(s => $"{s.Name}: {s.Detail}").ToList();
        if (failed.Count > 0)
            facts.Add(new { title = "Failed steps", value = string.Join("; ", failed) });

        return new
        {
            type = "message",
            attachments = new object[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[]
                        {
                            new
                            {
                                type = "TextBlock",
                                size = "Medium",
                                weight = "Bolder",
                                text = run.Succeeded
                                    ? $"Workflow succeeded: {run.WorkflowName}"
                                    : $"Workflow FAILED: {run.WorkflowName}"
                            },
                            new { type = "FactSet", facts = (object)facts }
                        }
                    }
                }
            }
        };
    }
}

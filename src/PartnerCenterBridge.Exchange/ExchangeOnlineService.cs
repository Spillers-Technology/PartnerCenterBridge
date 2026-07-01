using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Exchange;

/// <summary>
/// Exchange Online operations backed by the EXO PowerShell V3 module, invoked out-of-process via
/// <see cref="IPwshRunner"/>. Each call connects app-only (certificate) scoped to the customer
/// tenant, runs one operation, and returns the parsed per-step result.
/// </summary>
public class ExchangeOnlineService : IExchangeOnlineService
{
    private const string EmbeddedScript = "PartnerCenterBridge.Exchange.Scripts.exo-op.ps1";

    private readonly IPwshRunner _runner;
    private readonly ExchangeOptions _opts;
    private readonly ILogger<ExchangeOnlineService> _log;
    private readonly Lazy<string> _scriptPath;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ExchangeOnlineService(IPwshRunner runner, IOptions<ExchangeOptions> opts, ILogger<ExchangeOnlineService> log)
    {
        _runner = runner;
        _opts = opts.Value;
        _log = log;
        _scriptPath = new Lazy<string>(ExtractScript);
    }

    public async Task<MailboxInfo?> GetMailboxAsync(Tenant tenant, string identity, CancellationToken ct = default)
    {
        var script = await RunAsync(tenant, "getMailbox", new { identity }, ct);
        return script.Data is { ValueKind: JsonValueKind.Object } d ? ToMailbox(d) : null;
    }

    public async Task<ExoResult> ConvertToSharedAsync(
        Tenant tenant, string identity, string? forwardingSmtpAddress, bool deliverToMailboxAndForward, CancellationToken ct = default)
    {
        var script = await RunAsync(tenant, "convertToShared",
            new { identity, forwardingSmtpAddress, deliverToMailboxAndForward }, ct);
        return new ExoResult { Steps = script.Steps };
    }

    public async Task<IReadOnlyList<MailboxInfo>> ListSharedMailboxesAsync(Tenant tenant, CancellationToken ct = default)
    {
        var script = await RunAsync(tenant, "listShared", new { }, ct);
        if (script.Data is not { ValueKind: JsonValueKind.Array } arr) return Array.Empty<MailboxInfo>();
        return arr.EnumerateArray().Select(ToMailbox).ToList();
    }

    public async Task<ArchiveState?> GetArchiveStateAsync(Tenant tenant, string identity, CancellationToken ct = default)
    {
        var script = await RunAsync(tenant, "getArchiveState", new { identity }, ct);
        return script.Data is { ValueKind: JsonValueKind.Object } d ? ToArchiveState(d) : null;
    }

    public async Task<ArchiveRemediationResult> RemediateArchiveAsync(
        Tenant tenant, string identity, ArchiveRemediationOptions options, CancellationToken ct = default)
    {
        var script = await RunAsync(tenant, "remediateArchive", new
        {
            identity,
            enableAutoExpandingArchive = options.EnableAutoExpandingArchive,
            retentionPolicyName = options.RetentionPolicyName,
            clearProcessingBlocks = options.ClearProcessingBlocks,
            triggerProcessing = options.TriggerProcessing
        }, ct);
        return new ArchiveRemediationResult
        {
            Steps = script.Steps,
            State = script.Data is { ValueKind: JsonValueKind.Object } d ? ToArchiveState(d) : null
        };
    }

    public async Task<ArchiveRemediationResult> NudgeArchiveAsync(Tenant tenant, string identity, CancellationToken ct = default)
    {
        var script = await RunAsync(tenant, "nudgeArchive", new { identity }, ct);
        return new ArchiveRemediationResult
        {
            Steps = script.Steps,
            State = script.Data is { ValueKind: JsonValueKind.Object } d ? ToArchiveState(d) : null
        };
    }

    private static ArchiveState ToArchiveState(JsonElement e)
    {
        string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        long Long(string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

        return new ArchiveState(
            Str("userPrincipalName") ?? "",
            Str("primarySize") ?? "",
            Long("primaryItemCount"),
            Str("prohibitSendReceiveQuota") ?? "",
            Bool("archiveEnabled"),
            Str("archiveStatus") ?? "",
            Bool("autoExpandingArchiveEnabled"),
            Str("archiveQuota"),
            Str("archiveWarningQuota"),
            Str("archiveSize"),
            Long("archiveItemCount"),
            Str("retentionPolicy"),
            Bool("retentionHoldEnabled"),
            Bool("elcProcessingDisabled"));
    }

    private async Task<ExoScriptResult> RunAsync(Tenant tenant, string operation, object parameters, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            operation,
            connect = new
            {
                appId = _opts.AppId,
                organization = tenant.DefaultDomain ?? tenant.TenantId,
                certificatePath = _opts.CertificatePath,
                certificatePassword = _opts.CertificatePassword
            },
            @params = parameters
        }, Json);

        var result = await _runner.RunAsync(_scriptPath.Value, payload, ct);
        var json = ExtractJson(result.Stdout);
        if (json is null)
        {
            _log.LogError("EXO script produced no JSON. exit={Exit} stderr={Err}", result.ExitCode, result.Stderr);
            return new ExoScriptResult
            {
                Steps = { new ProvisioningStep("Exchange Online", false,
                    string.IsNullOrWhiteSpace(result.Stderr) ? "No output from EXO script." : result.Stderr.Trim()) }
            };
        }
        return JsonSerializer.Deserialize<ExoScriptResult>(json, Json) ?? new ExoScriptResult();
    }

    /// <summary>Pull the JSON result object out of stdout (the script emits it as the final line).</summary>
    internal static string? ExtractJson(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var trimmed = stdout.Trim();
        if (trimmed.StartsWith('{')) return trimmed;
        // Fall back to the last brace-delimited line in case the module wrote extra output.
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var l = line.Trim();
            if (l.StartsWith('{') && l.EndsWith('}')) return l;
        }
        return null;
    }

    private static MailboxInfo ToMailbox(JsonElement e) => new(
        e.GetProperty("userPrincipalName").GetString() ?? "",
        e.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
        e.TryGetProperty("recipientTypeDetails", out var rt) ? rt.GetString() ?? "" : "",
        e.TryGetProperty("forwardingSmtpAddress", out var fwd) ? fwd.GetString() : null,
        e.TryGetProperty("deliverToMailboxAndForward", out var d) && d.ValueKind == JsonValueKind.True);

    private static string ExtractScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(EmbeddedScript)
            ?? throw new InvalidOperationException($"Embedded script {EmbeddedScript} not found.");
        var path = Path.Combine(Path.GetTempPath(), "pcb-exo-op.ps1");
        using (var file = File.Create(path))
            stream.CopyTo(file);
        return path;
    }

    private sealed class ExoScriptResult
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("steps")] public List<ProvisioningStep> Steps { get; set; } = new();
        [JsonPropertyName("data")] public JsonElement? Data { get; set; }
    }
}

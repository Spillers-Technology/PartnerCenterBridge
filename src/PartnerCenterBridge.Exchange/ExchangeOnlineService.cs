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
/// The organization connected to is the tenant's Graph-verified initial domain
/// (<see cref="ITenantExchangeOrganizationProvider"/>), never <see cref="Tenant.DefaultDomain"/>, and
/// the script refuses to run anything unless the connection reports the tenant's own Entra id.
/// </summary>
public class ExchangeOnlineService : IExchangeOnlineService
{
    private const string EmbeddedScript = "PartnerCenterBridge.Exchange.Scripts.exo-op.ps1";

    private readonly IPwshRunner _runner;
    private readonly ITenantExchangeOrganizationProvider _organizations;
    private readonly ExchangeOptions _opts;
    private readonly ILogger<ExchangeOnlineService> _log;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ExchangeOnlineService(
        IPwshRunner runner, ITenantExchangeOrganizationProvider organizations,
        IOptions<ExchangeOptions> opts, ILogger<ExchangeOnlineService> log)
    {
        _runner = runner;
        _organizations = organizations;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<MailboxInfo?> GetMailboxAsync(Tenant tenant, string identity, CancellationToken ct = default)
    {
        var script = await RunAsync(tenant, "getMailbox", new { identity }, ct);
        if (script.Data is { ValueKind: JsonValueKind.Object } d) return ToMailbox(d);
        // null means "Exchange confirmed there is no such mailbox": only the script's structured
        // marker, set when the Get-EXOMailbox lookup itself reported the mailbox missing, says so.
        // Anything else (connect, auth, throttling, the pwsh dependency, or a result that has
        // neither data nor the marker) is an error, not an absent mailbox.
        if (script.NotFound && script.Steps.All(s => s.Success)) return null;
        var failure = script.Steps.FirstOrDefault(s => !s.Success);
        throw new InvalidOperationException("Exchange Online mailbox lookup failed: " +
            (failure?.Detail ?? "the script returned no mailbox and did not confirm that none exists."));
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
        // The organization comes from Microsoft Graph for this tenant id (persisted, or resolved
        // now); DefaultDomain is a display value and never selects the Exchange organization.
        var organization = await _organizations.GetOrganizationAsync(tenant, ct);
        var thumbprint = string.IsNullOrWhiteSpace(_opts.CertificateThumbprint) ? null : _opts.CertificateThumbprint.Trim();

        var payload = JsonSerializer.Serialize(new
        {
            operation,
            // The script compares the tenant id of the Exchange connection against this and runs
            // nothing when they differ.
            expectedTenantId = tenant.TenantId,
            connect = new
            {
                appId = _opts.AppId,
                organization,
                // Certificate-store auth (Windows) needs no secret at all; otherwise the PFX password
                // travels only inside this payload, which the runner hands to pwsh on stdin.
                certificateThumbprint = thumbprint,
                certificatePath = thumbprint is null ? _opts.CertificatePath : null,
                certificatePassword = thumbprint is null ? _opts.CertificatePassword : null
            },
            @params = parameters
        }, Json);

        PwshResult result;
        using (var script = ExtractedScript.FromEmbeddedResource(EmbeddedScript, "exo-op"))
            result = await _runner.RunAsync(script.Path, payload, ct);
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
        var parsed = JsonSerializer.Deserialize<ExoScriptResult>(json, Json) ?? new ExoScriptResult();
        var tenantCheck = parsed.Steps.FirstOrDefault(s => s.Name == TenantCheckStep && !s.Success);
        if (parsed.TenantMismatch || tenantCheck is not null)
        {
            _log.LogError("EXO tenant check failed for tenant {TenantId} ({Organization}): {Detail}",
                tenant.TenantId, organization, tenantCheck?.Detail);
            throw new ExchangeTenantMismatchException(tenant.DisplayName, tenantCheck?.Detail);
        }
        return parsed;
    }

    /// <summary>Step the script records after connecting, before any operation runs.</summary>
    internal const string TenantCheckStep = "Tenant check";

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

    private sealed class ExoScriptResult
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("steps")] public List<ProvisioningStep> Steps { get; set; } = new();
        [JsonPropertyName("data")] public JsonElement? Data { get; set; }
        /// <summary>Set by getMailbox only when the lookup itself reported that no such mailbox exists.</summary>
        [JsonPropertyName("notFound")] public bool NotFound { get; set; }
        /// <summary>Set when the connected tenant id did not match the expected one; no operation ran.</summary>
        [JsonPropertyName("tenantMismatch")] public bool TenantMismatch { get; set; }
    }
}

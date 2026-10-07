using System.Net;
using System.Text.Json;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.TenantAudits;
using PartnerCenterBridge.Graph.Operations;

namespace PartnerCenterBridge.Graph.TenantAudits;

/// <summary>
/// The only Graph surface the audit providers get: GET and paged GET. Audits are read-only by
/// construction -- there is no Post/Patch/Delete to call by mistake.
/// </summary>
internal sealed class AuditGraphReader
{
    private readonly GraphRestClient _graph;

    private AuditGraphReader(GraphRestClient graph) => _graph = graph;

    public Task<JsonDocument> GetAsync(string url, CancellationToken ct) => _graph.GetAsync(url, ct);
    public Task<List<JsonElement>> GetAllAsync(string url, CancellationToken ct) => _graph.GetAllAsync(url, ct);

    /// <summary>
    /// One reader per tenant per run (cached on the context). Failing to get a token at all --
    /// the tenant's admin account is not connected, needs reconnecting, or the partner credential
    /// is missing -- makes every Graph check unavailable with that reason, rather than an error.
    /// </summary>
    public static Task<AuditGraphReader> ForAsync(TenantGraphRest factory, AuditCheckContext ctx) =>
        ctx.Cache.GetAsync("graph:reader", async () =>
        {
            try
            {
                return new AuditGraphReader(await factory.CreateAsync(ctx.Tenant, ctx.CancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AuditUnavailableException(
                    $"PCB could not get a Microsoft Graph token for {ctx.Tenant.DisplayName}: {ex.Message}",
                    ["Connected Microsoft tenant (direct sign-in or partner GDAP)"]);
            }
        });

    /// <summary>
    /// Runs <paramref name="read"/>, translating "PCB cannot read this here" Graph answers into
    /// <see cref="AuditUnavailableException"/>. Anything else (throttling, outages) propagates and
    /// the engine records it as an error for the checks that needed it.
    /// </summary>
    public static async Task<T> ReadAsync<T>(TenantGraphRest factory, AuditCheckContext ctx, string what, string[] permissions,
        Func<AuditGraphReader, Task<T>> read)
    {
        var reader = await ForAsync(factory, ctx);
        try
        {
            return await read(reader);
        }
        catch (GraphRequestException ex) when (Unavailable(ex, what, permissions) is { } unavailable)
        {
            throw unavailable;
        }
    }

    /// <summary>
    /// Maps a Graph failure to "unavailable" when it means PCB cannot read the data in this tenant:
    /// 401/403 (missing permission or role, or a license the tenant lacks) and Intune's
    /// "not applicable to target tenant". Returns null for everything else.
    /// </summary>
    public static AuditUnavailableException? Unavailable(GraphRequestException ex, string what, IReadOnlyList<string> permissions)
    {
        var message = GraphErrors.Message(ex);
        switch (ex.Status)
        {
            case HttpStatusCode.Forbidden when NeedsLicense(ex):
                return new AuditUnavailableException(
                    $"The tenant does not have the license Microsoft requires to read {what} (Graph 403: {message}).",
                    ["License: Microsoft Entra ID P1 or P2 (or the product license this data needs)"]);
            case HttpStatusCode.Forbidden:
                return new AuditUnavailableException(
                    $"PCB's access to this tenant cannot read {what} (Graph 403: {message}). The connection needs {string.Join(" or ", permissions)}, and the signed-in admin or GDAP role must cover it.",
                    permissions.Select(p => $"Graph permission {p}"));
            case HttpStatusCode.Unauthorized:
                return new AuditUnavailableException(
                    $"Microsoft rejected PCB's token while reading {what} (Graph 401: {message}). Reconnect the tenant.",
                    ["Connected Microsoft tenant (direct sign-in or partner GDAP)"]);
            case HttpStatusCode.BadRequest when ex.Body.Contains("not applicable to target tenant", StringComparison.OrdinalIgnoreCase):
                return new AuditUnavailableException(
                    $"Microsoft Intune is not available in this tenant, so {what} cannot be read ({message}).",
                    ["Product: Microsoft Intune"]);
            default:
                return null;
        }
    }

    /// <summary>
    /// Graph's 403 for "this tenant lacks the license", e.g. Authentication_RequestFromNonPremiumTenantOrB2CTenant
    /// ("Neither tenant is B2C or tenant doesn't have premium license").
    /// </summary>
    public static bool NeedsLicense(GraphRequestException ex) =>
        ex.Body.Contains("premium", StringComparison.OrdinalIgnoreCase)
        || ex.Body.Contains("license", StringComparison.OrdinalIgnoreCase)
        || ex.Body.Contains("licence", StringComparison.OrdinalIgnoreCase);

    // --- JSON helpers ---------------------------------------------------------------------------

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    public static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            // Graph uses 0001-01-01 for "never" in a few places.
            && d.Year > 1601 ? d : null;

    public static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    public static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    /// <summary>"#microsoft.graph.windows10CompliancePolicy" -> "windows10CompliancePolicy".</summary>
    public static string ODataType(JsonElement e) => (Str(e, "@odata.type") ?? "").Replace("#microsoft.graph.", "");

    public static string Escape(string id) => Uri.EscapeDataString(id);
}

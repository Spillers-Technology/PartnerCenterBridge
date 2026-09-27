using System.Net;

namespace PartnerCenterBridge.Api.Hosting;

/// <summary>
/// Guarantees that the Local profile never listens on a non-loopback address unless the operator
/// asked for it with <c>--listen</c>. Kestrel endpoints from configuration are cumulative with the
/// ones bound in code, so an inherited <c>Kestrel__Endpoints__*</c> variable would otherwise add a
/// network listener silently. Two layers: configured endpoints are refused before the host is
/// built, and the effective server addresses are checked once the server has started.
/// </summary>
public static class LocalListeners
{
    public const string EndpointsSection = "Kestrel:Endpoints";

    /// <summary>Throws when configuration defines Kestrel endpoints (they would add listeners).</summary>
    public static void RefuseConfiguredEndpoints(IConfiguration configuration)
    {
        var endpoints = configuration.GetSection(EndpointsSection).GetChildren()
            .Select(child => child.Key).ToList();
        if (endpoints.Count == 0) return;
        throw new InvalidOperationException(
            $"The Local profile refuses configured Kestrel endpoints ({string.Join(", ", endpoints)} under {EndpointsSection}): " +
            "they would add listeners next to the loopback one, possibly reachable from other machines. Remove the " +
            "Kestrel:Endpoints settings (for example Kestrel__Endpoints__* environment variables or appsettings/pcb.local.json " +
            "entries). Use --port to change the port, and --listen <address> only if other machines must reach this instance.");
    }

    /// <summary>
    /// Checks the addresses the server actually bound. Returns null when every one is loopback, or
    /// is the explicit <c>--listen</c> address; otherwise a message saying why the process must stop.
    /// </summary>
    public static string? Validate(IEnumerable<string>? boundAddresses, LocalWorkbenchOptions options)
    {
        var addresses = boundAddresses?.ToList();
        if (addresses is null || addresses.Count == 0)
            return "Could not determine the addresses the server is listening on; stopping rather than risk an unintended network listener.";

        var unexpected = addresses.Where(address => !IsAllowed(address, options)).ToList();
        if (unexpected.Count == 0)
        {
            // Both loopback families must be ours: a free [::1] (or 127.0.0.1) could be taken by another
            // Windows user and serve this user's browser, which resolves "localhost" to either.
            // Kestrel reports "localhost" for a listener on both loopback addresses.
            var bound = addresses.SelectMany(address => IsLocalhost(address)
                    ? new IPAddress?[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
                    : new[] { ParseHost(address) })
                .Where(ip => ip is not null).ToList();
            var missing = options.ListenAddresses
                .Where(expected => IPAddress.IsLoopback(expected) && !bound.Any(ip => expected.Equals(ip)))
                .ToList();
            if (missing.Count == 0) return null;
            return $"The server is not listening on {string.Join(", ", missing)} for port {options.Port}, so another program " +
                   "could answer the browser there. Stopping.";
        }
        return $"The server is listening on {string.Join(", ", unexpected)}, which is not loopback and was not requested with --listen. " +
               "Stopping. Remove any Kestrel endpoint / URL configuration (Kestrel__Endpoints__*, ASPNETCORE_URLS, ASPNETCORE_HTTP_PORTS) " +
               "or pass --listen <address> deliberately.";
    }

    private static bool IsLocalhost(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    private static IPAddress? ParseHost(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) ? ip : null;

    private static bool IsAllowed(string address, LocalWorkbenchOptions options)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false; // "http://*:80", "http://+:80": wildcard
        var host = uri.Host.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        return !options.IsLoopbackOnly && options.ListenAddresses.Any(ip.Equals);
    }
}

using PartnerCenterBridge.Exchange;

namespace PartnerCenterBridge.Api.Diagnostics;

/// <summary>Thrown instead of a raw process error when the Exchange Online path cannot run at all.</summary>
public sealed class ExchangeDependencyException : InvalidOperationException
{
    public ExchangeDependencyException(string reason)
        : base($"Exchange Online dependency not configured: {reason}.") { }
}

/// <summary>
/// Wraps the real <see cref="IPwshRunner"/> so every Exchange Online operation (controllers,
/// workflows, offboarding) fails fast with a clear, actionable reason when pwsh, the
/// ExchangeOnlineManagement module or the app-only certificate is missing, instead of surfacing a
/// "file not found" process error or a script failure halfway through a connect.
/// </summary>
public sealed class GuardedPwshRunner : IPwshRunner
{
    private readonly IPwshRunner _inner;
    private readonly IExchangeDependencyProbe _probe;
    private readonly Func<string, IPwshRunner>? _runnerForPath;

    public GuardedPwshRunner(IPwshRunner inner, IExchangeDependencyProbe probe, Func<string, IPwshRunner>? runnerForPath = null)
    {
        _inner = inner;
        _probe = probe;
        _runnerForPath = runnerForPath;
    }

    public async Task<PwshResult> RunAsync(string scriptPath, string payloadJson, CancellationToken ct = default)
    {
        var state = await _probe.GetAsync(ct);
        if (state.MissingReason is { } reason) throw new ExchangeDependencyException(reason);
        return await (_runnerForPath?.Invoke(state.PwshPath!) ?? _inner).RunAsync(scriptPath, payloadJson, ct);
    }
}

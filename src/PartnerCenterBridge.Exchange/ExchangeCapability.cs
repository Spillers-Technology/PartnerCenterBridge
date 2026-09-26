using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Operations;

namespace PartnerCenterBridge.Exchange;

/// <summary>
/// Cheap, local check of whether Exchange Online operations can run: app id and certificate
/// configured, certificate file present, pwsh resolvable. It does not start pwsh, so a missing
/// ExchangeOnlineManagement module or a bad certificate only surfaces when an operation runs.
/// </summary>
public class ExchangeCapability : IExchangeCapability
{
    private readonly ExchangeOptions _opts;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string?> _pathVariable;

    public ExchangeCapability(IOptions<ExchangeOptions> opts)
        : this(opts, File.Exists, () => Environment.GetEnvironmentVariable("PATH")) { }

    internal ExchangeCapability(IOptions<ExchangeOptions> opts, Func<string, bool> fileExists, Func<string?> pathVariable)
    {
        _opts = opts.Value;
        _fileExists = fileExists;
        _pathVariable = pathVariable;
    }

    public ExchangeCapabilityStatus Check()
    {
        if (string.IsNullOrWhiteSpace(_opts.AppId))
            return new(false, "Exchange Online is not configured: Exchange:AppId (app-only Exchange app registration) is not set.");
        if (string.IsNullOrWhiteSpace(_opts.CertificatePath))
            return new(false, "Exchange Online is not configured: Exchange:CertificatePath (app-only certificate) is not set.");
        if (!_fileExists(_opts.CertificatePath))
            return new(false, $"Exchange Online is not configured: the certificate file '{_opts.CertificatePath}' was not found.");
        if (!PwshAvailable())
            return new(false, $"PowerShell 7 (pwsh) was not found at '{_opts.PwshPath}'; Exchange Online operations need it.");
        return new(true, null);
    }

    private bool PwshAvailable()
    {
        var pwsh = string.IsNullOrWhiteSpace(_opts.PwshPath) ? "pwsh" : _opts.PwshPath;
        if (Path.IsPathRooted(pwsh) || pwsh.Contains(Path.DirectorySeparatorChar) || pwsh.Contains(Path.AltDirectorySeparatorChar))
            return _fileExists(pwsh);

        var names = OperatingSystem.IsWindows() && !pwsh.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { pwsh + ".exe", pwsh }
            : new[] { pwsh };
        foreach (var dir in (_pathVariable() ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                try
                {
                    if (_fileExists(Path.Combine(dir.Trim(), name))) return true;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        return false;
    }
}

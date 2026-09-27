using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Hosting;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Diagnostics;

public enum SystemCheckStatus { Ok, Warning, Error, NotConfigured }

/// <summary>How to fix a non-Ok check: a shell command to run and/or an SPA route to open.</summary>
public sealed record SystemCheckFix(string Label, string? Command, string? Route);

public sealed record SystemCheck(string Id, string Label, SystemCheckStatus Status, string Detail, SystemCheckFix? Fix);

public sealed record SystemCapabilities(bool Graph, bool Exchange, bool PartnerCenter);

public sealed record SystemDiagnosticsReport(IReadOnlyList<SystemCheck> Checks, SystemCapabilities Capabilities);

/// <summary>
/// The checks behind <c>GET /api/system/diagnostics</c> and the <c>doctor</c> command. Check ids:
/// hosting, database, data-protection, auth, sam, tenants, pwsh, exchange-module, exchange-app.
/// </summary>
public interface ISystemDiagnostics
{
    Task<SystemDiagnosticsReport> RunAsync(CancellationToken ct);
}

public sealed class SystemDiagnostics : ISystemDiagnostics
{
    public const string MicrosoftSettingsRoute = "/settings/microsoft";

    private readonly HostingInfo _hosting;
    private readonly PersistenceInfo _persistence;
    private readonly BridgeDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly AuthModeInfo _authMode;
    private readonly ISamStatusService _sam;
    private readonly IExchangeDependencyProbe _exchange;

    public SystemDiagnostics(HostingInfo hosting, PersistenceInfo persistence, BridgeDbContext db, IConfiguration cfg,
        AuthModeInfo authMode, ISamStatusService sam, IExchangeDependencyProbe exchange)
    {
        _hosting = hosting;
        _persistence = persistence;
        _db = db;
        _cfg = cfg;
        _authMode = authMode;
        _sam = sam;
        _exchange = exchange;
    }

    public async Task<SystemDiagnosticsReport> RunAsync(CancellationToken ct)
    {
        var checks = new List<SystemCheck> { Hosting() };
        var database = await DatabaseAsync(ct);
        checks.Add(database);
        checks.Add(DataProtection());
        var databaseUsable = database.Status is SystemCheckStatus.Ok;
        checks.Add(await AuthAsync(databaseUsable, ct));
        var (samCheck, samReady) = await SamAsync(databaseUsable, ct);
        checks.Add(samCheck);
        checks.Add(await TenantsAsync(databaseUsable, ct));

        var exchange = await _exchange.GetAsync(ct);
        checks.AddRange(ExchangeChecks(exchange));

        return new SystemDiagnosticsReport(checks, new SystemCapabilities(
            Graph: samReady, Exchange: exchange.Ready, PartnerCenter: samReady));
    }

    private bool DatabaseExists => _persistence.DatabaseFilePath is null || File.Exists(_persistence.DatabaseFilePath);

    private SystemCheck Hosting()
    {
        if (_hosting.Local is not { } local)
            return new("hosting", "Hosting", SystemCheckStatus.Ok, $"Server profile, version {_hosting.Version}", null);
        var detail = $"Local profile at {local.CanonicalUrl}, data in {local.DataRoot}, version {_hosting.Version}";
        if (local.IsLoopbackOnly) return new("hosting", "Hosting", SystemCheckStatus.Ok, detail, null);
        return new("hosting", "Hosting", SystemCheckStatus.Warning,
            $"{detail}. Listening on {local.ListenAddress}, which other machines can reach.",
            new SystemCheckFix("Restart without --listen to bind to 127.0.0.1 only", HostingInfo.CommandName, null));
    }

    private async Task<SystemCheck> DatabaseAsync(CancellationToken ct)
    {
        const string id = "database", label = "Database";
        var target = _persistence.Target;
        if (!DatabaseExists)
            return new(id, label, SystemCheckStatus.Warning, $"{target} (not created yet; it is created on first start)",
                new SystemCheckFix("Start Partner Center Bridge once", HostingInfo.CommandName, null));
        try
        {
            if (!await _db.Database.CanConnectAsync(ct))
                return new(id, label, SystemCheckStatus.Error, $"{target}: cannot connect",
                    new SystemCheckFix("Check ConnectionStrings:" + _persistence.Provider + " and that the server is running", null, null));
            var applied = (await _db.Database.GetAppliedMigrationsAsync(ct)).Count();
            var pending = (await _db.Database.GetPendingMigrationsAsync(ct)).Count();
            if (pending > 0)
                return new(id, label, SystemCheckStatus.Warning,
                    $"{target}: {applied} migration(s) applied, {pending} pending (applied automatically on the next start)",
                    new SystemCheckFix("Restart Partner Center Bridge", HostingInfo.CommandName, null));
            return new(id, label, SystemCheckStatus.Ok, $"{target}: schema up to date ({applied} migration(s))", null);
        }
        catch (Exception ex)
        {
            return new(id, label, SystemCheckStatus.Error, $"{target}: {ex.GetBaseException().Message}", null);
        }
    }

    private SystemCheck DataProtection()
    {
        const string id = "data-protection", label = "Data protection keys";
        var path = _cfg["DataProtection:KeyRingPath"] ?? _hosting.Local?.KeysPath ?? "/keys";
        if (!Directory.Exists(path))
            return new(id, label, SystemCheckStatus.Error, $"Key ring folder {path} does not exist",
                new SystemCheckFix("Create the folder (or mount the /keys volume) and restart", null, null));
        try
        {
            var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(id, label, SystemCheckStatus.Error, $"Key ring folder {path} is not writable: {ex.Message}",
                new SystemCheckFix("Give the service account write access to the key ring folder", null, null));
        }
        var keys = Directory.GetFiles(path, "key-*.xml").Length;
        return keys == 0
            ? new(id, label, SystemCheckStatus.Warning, $"No keys in {path} yet (created on first start). Losing this folder makes stored secrets unreadable.", null)
            : new(id, label, SystemCheckStatus.Ok, $"{keys} key(s) in {path}", null);
    }

    private async Task<SystemCheck> AuthAsync(bool databaseUsable, CancellationToken ct)
    {
        const string id = "auth", label = "Sign-in";
        switch (_authMode.Mode)
        {
            case AuthModeInfo.Local:
                if (!databaseUsable)
                    return new(id, label, SystemCheckStatus.Ok, "Local accounts (user count unavailable until the database is ready)", null);
                if (_hosting.IsLocal && await Auth.WorkbenchOwnerService.IsAccountlessAsync(_db, ct))
                    return new(id, label, SystemCheckStatus.Ok,
                        "No account (one-time launch links from the exe, this Windows user only). Anyone who can run programs as " + Environment.UserName +
                        " on this computer can use this workbench with full administrator rights.",
                        new SystemCheckFix("Protect with an account", null, "/settings/security"));
                var users = await _db.AppUsers.CountAsync(ct);
                if (users == 0)
                    return new(id, label, SystemCheckStatus.Warning,
                        "Local accounts: none registered yet. The first account created becomes the instance Administrator.",
                        new SystemCheckFix("Create the first account", null, "/register"));
                var admins = await _db.AppUsers.CountAsync(user => user.IsActive
                    && (user.InstanceRoles & InstanceRole.Administrator) != 0, ct);
                return new(id, label, SystemCheckStatus.Ok, $"Local accounts: {users} user(s), {admins} active administrator(s)", null);
            case AuthModeInfo.Dev:
                return new(id, label, SystemCheckStatus.Warning, "Dev mode: every request is treated as a signed-in administrator",
                    new SystemCheckFix("Set Auth:Mode to Local or Oidc for any shared deployment", null, null));
            default:
                return string.IsNullOrWhiteSpace(_cfg["Auth:Authority"])
                    ? new(id, label, SystemCheckStatus.Error, "OIDC mode but Auth:Authority is not set",
                        new SystemCheckFix("Set Auth:Authority and Auth:Audience", null, null))
                    : new(id, label, SystemCheckStatus.Ok, $"OIDC via {_cfg["Auth:Authority"]}", null);
        }
    }

    private async Task<(SystemCheck Check, bool Ready)> SamAsync(bool databaseUsable, CancellationToken ct)
    {
        const string id = "sam", label = "Partner Center / Graph (SAM)";
        var fixRoute = new SystemCheckFix("Configure the Partner app and bootstrap SAM",
            $"{HostingInfo.CommandName} bootstrap-sam", MicrosoftSettingsRoute);
        if (!databaseUsable)
            return (new(id, label, SystemCheckStatus.NotConfigured, "Database not ready (see the database check)", fixRoute), false);
        SamStatus status;
        try
        {
            status = await _sam.GetAsync(ct);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (new(id, label, SystemCheckStatus.Error,
                "A SAM refresh token is stored but cannot be decrypted (the Data Protection key ring changed)",
                fixRoute), false);
        }
        if (!status.AppConfigured)
            return (new(id, label, SystemCheckStatus.NotConfigured,
                "Partner:ClientId, Partner:ClientSecret and Partner:PartnerTenantId are not all set",
                new SystemCheckFix("Configure the Partner (SAM) app registration", null, MicrosoftSettingsRoute)), false);
        if (!status.Ready)
            return (new(id, label, SystemCheckStatus.NotConfigured, "The Partner app is configured but SAM has not been bootstrapped",
                fixRoute), false);
        return (new(id, label, SystemCheckStatus.Ok,
            status.Bootstrapped ? "Bootstrapped; refresh token stored encrypted" : "Using Partner:SeedRefreshToken (not bootstrapped yet)",
            null), true);
    }

    private async Task<SystemCheck> TenantsAsync(bool databaseUsable, CancellationToken ct)
    {
        const string id = "tenants", label = "Tenants";
        if (!databaseUsable)
            return new(id, label, SystemCheckStatus.NotConfigured, "Database not ready (see the database check)", null);
        var active = await _db.Tenants.CountAsync(tenant => tenant.Status == TenantStatus.Active, ct);
        return active == 0
            ? new(id, label, SystemCheckStatus.NotConfigured, "No active tenants onboarded",
                new SystemCheckFix("Onboard a customer tenant", null, "/tenants"))
            : new(id, label, SystemCheckStatus.Ok, $"{active} active tenant(s)", null);
    }

    private static IEnumerable<SystemCheck> ExchangeChecks(ExchangeDependencyState state)
    {
        yield return state.PwshAvailable
            ? new("pwsh", "PowerShell 7", SystemCheckStatus.Ok, $"PowerShell {state.PwshVersion} at {state.PwshPath}", null)
            : new("pwsh", "PowerShell 7", SystemCheckStatus.NotConfigured,
                (state.PwshError ?? "pwsh not found") + " (only needed for Exchange Online operations)",
                new SystemCheckFix("Install PowerShell 7",
                    OperatingSystem.IsWindows() ? "winget install --id Microsoft.PowerShell --source winget" : null, null));

        yield return !state.PwshAvailable
            ? new("exchange-module", "Exchange Online module", SystemCheckStatus.NotConfigured, "Needs PowerShell 7 first",
                new SystemCheckFix("Install PowerShell 7, then the module", InstallModuleCommand, null))
            : state.ModuleAvailable
                ? new("exchange-module", "Exchange Online module", SystemCheckStatus.Ok, $"ExchangeOnlineManagement {state.ModuleVersion}", null)
                : new("exchange-module", "Exchange Online module", SystemCheckStatus.NotConfigured,
                    "pwsh found, " + (state.ModuleError ?? "ExchangeOnlineManagement not installed"),
                    new SystemCheckFix("Install module", InstallModuleCommand, null));

        var usesCertificateThumbprint = state.CertificatePath.StartsWith("Cert:", StringComparison.OrdinalIgnoreCase);
        yield return state.AppConfigured
            ? new("exchange-app", "Exchange Online app", SystemCheckStatus.Ok, $"App-only certificate at {state.CertificatePath}", null)
            : new("exchange-app", "Exchange Online app", SystemCheckStatus.NotConfigured,
                !state.AppIdConfigured
                    ? "Exchange:AppId is not set"
                    : $"Certificate not found at '{state.CertificatePath}' ({(usesCertificateThumbprint ? "Exchange:CertificateThumbprint" : "Exchange:CertificatePath")})",
                new SystemCheckFix("Configure the Exchange app registration and certificate", null, MicrosoftSettingsRoute));
    }

    private const string InstallModuleCommand = "pwsh -c \"Install-Module ExchangeOnlineManagement -Scope CurrentUser\"";
}

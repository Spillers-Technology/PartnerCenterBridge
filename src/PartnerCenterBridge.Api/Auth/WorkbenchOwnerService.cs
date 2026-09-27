using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Hosting;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Auth;

public enum LaunchCheck { Accepted, Rejected, Throttled }

/// <summary>
/// "Use without an account" for the Local Workbench: whether first run may offer it, the launch
/// secret that signs the built-in workbench owner in, and the backoff on wrong secrets. Never
/// available under the Server profile (<see cref="Local"/> is null there) or with a non-loopback
/// <c>--listen</c> address.
/// </summary>
public sealed class WorkbenchOwnerService
{
    /// <summary>The owner's placeholder email: .local is reserved (RFC 6762), so it can never receive mail.</summary>
    public const string OwnerEmail = "owner@workbench.local";

    /// <summary>Wrong secrets allowed before each further attempt has to wait (doubling, capped).</summary>
    public const int FreeFailures = 3;
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock;
    private readonly ILogger<WorkbenchOwnerService> _log;
    private readonly object _gate = new();
    private int _failures;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    public WorkbenchOwnerService(HostingInfo hosting, TimeProvider clock, ILogger<WorkbenchOwnerService> log)
    {
        Local = hosting.Local;
        _clock = clock;
        _log = log;
    }

    /// <summary>The Local Workbench options, or null under the Server profile.</summary>
    public LocalWorkbenchOptions? Local { get; }

    /// <summary>Why this instance cannot run without an account, or null when it can.</summary>
    public string? UnavailableReason =>
        Local is null
            ? "Using Partner Center Bridge without an account is only possible in the Local Workbench."
            : !Local.IsLoopbackOnly
                ? $"This workbench listens on {Local.ListenAddress} (--listen), so it is reachable from other machines and must use an account."
                : null;

    /// <summary>The owner's display name: the Windows user this workbench runs as.</summary>
    public static string OwnerDisplayName() => Environment.UserName + " (this computer)";

    /// <summary>Whether this database is in no-account mode (an active workbench owner exists).</summary>
    public static Task<bool> IsAccountlessAsync(BridgeDbContext db, CancellationToken ct) =>
        db.AppUsers.AsNoTracking().AnyAsync(user => user.IsWorkbenchOwner && user.IsActive, ct);

    /// <summary>Creates the launch secret if it does not exist yet; returns it.</summary>
    public string EnsureSecret() =>
        LocalLaunchSecretStore.GetOrCreate(Local ?? throw new InvalidOperationException(UnavailableReason));

    /// <summary>Deletes the launch secret (every launch link stops working). No-op under the Server profile.</summary>
    public void RevokeSecret()
    {
        if (Local is null) return;
        LocalLaunchSecretStore.Delete(Local);
        lock (_gate)
        {
            _failures = 0;
            _blockedUntil = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Compares <paramref name="presented"/> with the saved secret in constant time. While a backoff
    /// is running every attempt is refused without comparing (so guesses cannot be sped up), and
    /// each wrong secret past <see cref="FreeFailures"/> doubles the wait, up to
    /// <see cref="MaxBackoff"/>. Success resets the counter.
    /// </summary>
    public LaunchCheck Check(string? presented, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (now < _blockedUntil)
            {
                retryAfter = _blockedUntil - now;
                return LaunchCheck.Throttled;
            }

            var saved = Local is null || !Local.IsLoopbackOnly ? null : LocalLaunchSecretStore.TryRead(Local);
            if (saved is not null && !string.IsNullOrEmpty(presented)
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(saved)))
            {
                _failures = 0;
                return LaunchCheck.Accepted;
            }

            _failures++;
            if (_failures >= FreeFailures)
            {
                var seconds = Math.Min(Math.Pow(2, _failures - FreeFailures), MaxBackoff.TotalSeconds);
                _blockedUntil = now + TimeSpan.FromSeconds(seconds);
                _log.LogWarning("{Failures} wrong launch secrets in a row; launch sign-in paused for {Seconds}s.", _failures, seconds);
            }
            return LaunchCheck.Rejected;
        }
    }
}

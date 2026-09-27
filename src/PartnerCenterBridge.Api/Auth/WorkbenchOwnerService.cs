using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Hosting;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>What a launch ticket may be exchanged for.</summary>
public enum TicketPurpose
{
    /// <summary>First run of a Local Workbench: creating the first account, or choosing no account.</summary>
    Setup,
    /// <summary>Signing the no-account workbench owner in (<c>POST /api/auth/launch</c>).</summary>
    SignIn
}

public enum TicketCheck { Accepted, Rejected, Throttled }

/// <summary>What <see cref="WorkbenchOwnerService.CreateBrowserLinkAsync"/> produced.</summary>
public enum BrowserLinkKind { Plain, Setup, SignIn }

/// <summary>The URL a browser should open for this workbench, and whether it carries a one-time ticket.</summary>
public sealed record BrowserLink(string Url, BrowserLinkKind Kind)
{
    public bool HasTicket => Kind != BrowserLinkKind.Plain;
}

/// <summary>
/// "Use without an account" for the Local Workbench, and the one-time launch tickets that prove a
/// browser was opened by this workbench's own process (the exe's browser launch, its console, or a
/// second launch that reached it over the current-user-only hand-off pipe).
/// <para>
/// Tickets are minted in memory by the running process only: 256 random bits, single use, kept as
/// SHA-256 hashes, and short-lived (<see cref="SignInLifetime"/>; <see cref="SetupLifetime"/> for a
/// first-run ticket, which has to outlive filling in the first-account form). Nothing is persisted,
/// so a restart invalidates every outstanding ticket, and a bookmarked or copied ticket URL never
/// works twice. A ticket is compared before any backoff applies: a valid ticket is always accepted;
/// only wrong ones are counted, and while they keep coming they are refused without an audit row.
/// </para>
/// Never available under the Server profile (<see cref="Local"/> is null there). Signing in without
/// an account is also refused with a non-loopback <c>--listen</c> address.
/// </summary>
public sealed class WorkbenchOwnerService
{
    /// <summary>The owner's placeholder email: .local is reserved (RFC 6762), so it can never receive mail.</summary>
    public const string OwnerEmail = "owner@workbench.local";

    public static readonly TimeSpan SignInLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SetupLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Outstanding tickets kept at most; minting more drops the oldest.</summary>
    public const int MaxOutstanding = 16;

    /// <summary>32 bytes, base64url without padding.</summary>
    public const int TicketLength = 43;

    /// <summary>Wrong tickets allowed before further wrong ones are refused without being recorded (doubling, capped).</summary>
    public const int FreeFailures = 3;
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private sealed record Entry(byte[] Hash, TicketPurpose Purpose, DateTimeOffset ExpiresAt);

    private readonly TimeProvider _clock;
    private readonly ILogger<WorkbenchOwnerService> _log;
    private readonly object _gate = new();
    private readonly List<Entry> _tickets = new();
    private int _failures;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFailure = DateTimeOffset.MinValue;

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

    /// <summary>
    /// The URL a browser opened by this process should use: a first-run setup ticket while the
    /// workbench has no user, a sign-in ticket while it is used without an account (loopback only),
    /// otherwise the plain canonical URL. Under the Server profile there is no browser link.
    /// </summary>
    public async Task<BrowserLink> CreateBrowserLinkAsync(BridgeDbContext db, CancellationToken ct)
    {
        var local = Local ?? throw new InvalidOperationException(UnavailableReason);
        if (!await db.AppUsers.AsNoTracking().AnyAsync(ct))
            return new BrowserLink(local.TicketUrl(Mint(TicketPurpose.Setup)), BrowserLinkKind.Setup);
        if (UnavailableReason is null && await IsAccountlessAsync(db, ct))
            return new BrowserLink(local.TicketUrl(Mint(TicketPurpose.SignIn)), BrowserLinkKind.SignIn);
        return new BrowserLink(local.CanonicalUrl, BrowserLinkKind.Plain);
    }

    /// <summary>Mints a one-time ticket. Only this process can: nothing about it is stored outside memory.</summary>
    public string Mint(TicketPurpose purpose)
    {
        if (Local is null) throw new InvalidOperationException(UnavailableReason);
        var bytes = RandomNumberGenerator.GetBytes(32);
        var ticket = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            _tickets.RemoveAll(entry => entry.ExpiresAt <= now);
            while (_tickets.Count >= MaxOutstanding) _tickets.RemoveAt(0);
            _tickets.Add(new Entry(Hash(ticket), purpose,
                now + (purpose == TicketPurpose.Setup ? SetupLifetime : SignInLifetime)));
        }
        return ticket;
    }

    /// <summary>
    /// Consumes <paramref name="presented"/> if it is an outstanding, unexpired ticket for
    /// <paramref name="purpose"/>. The ticket is checked first, so a valid one is accepted even
    /// while wrong ones are being refused; a malformed value is rejected without hashing. Wrong
    /// tickets past <see cref="FreeFailures"/> start a doubling backoff (capped at
    /// <see cref="MaxBackoff"/>) during which further wrong tickets report
    /// <see cref="TicketCheck.Throttled"/>, so callers can skip recording them.
    /// </summary>
    public TicketCheck Consume(string? presented, TicketPurpose purpose, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var hash = Local is not null && IsWellFormed(presented) ? Hash(presented!) : null;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            _tickets.RemoveAll(entry => entry.ExpiresAt <= now);
            if (hash is not null)
            {
                // Every entry is compared (no early exit) in constant time.
                Entry? match = null;
                foreach (var entry in _tickets)
                    if (CryptographicOperations.FixedTimeEquals(entry.Hash, hash) && entry.Purpose == purpose)
                        match = entry;
                if (match is not null)
                {
                    _tickets.Remove(match);
                    return TicketCheck.Accepted;
                }
            }

            if (now < _blockedUntil)
            {
                retryAfter = _blockedUntil - now;
                return TicketCheck.Throttled;
            }
            // A quiet period (no wrong ticket for MaxBackoff) starts the count again.
            if (_failures > 0 && now - _lastFailure >= MaxBackoff) _failures = 0;
            _lastFailure = now;
            _failures++;
            if (_failures >= FreeFailures)
            {
                var seconds = Math.Min(Math.Pow(2, _failures - FreeFailures), MaxBackoff.TotalSeconds);
                _blockedUntil = now + TimeSpan.FromSeconds(seconds);
                _log.LogWarning("{Failures} wrong launch tickets; further wrong tickets are refused without being recorded for {Seconds}s.",
                    _failures, seconds);
            }
            return TicketCheck.Rejected;
        }
    }

    /// <summary>Drops every outstanding ticket for <paramref name="purpose"/> (all of them when null).</summary>
    public void RevokeTickets(TicketPurpose? purpose = null)
    {
        lock (_gate)
        {
            _tickets.RemoveAll(entry => purpose is null || entry.Purpose == purpose);
        }
    }

    /// <summary>Outstanding (unexpired) tickets; for tests and diagnostics.</summary>
    public int OutstandingTickets
    {
        get
        {
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                return _tickets.Count(entry => entry.ExpiresAt > now);
            }
        }
    }

    private static bool IsWellFormed(string? value)
    {
        if (value is null || value.Length != TicketLength) return false;
        foreach (var c in value)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                return false;
        return true;
    }

    private static byte[] Hash(string ticket) => SHA256.HashData(Encoding.ASCII.GetBytes(ticket));
}

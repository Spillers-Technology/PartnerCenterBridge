using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Win32.SafeHandles;
using PartnerCenterBridge.Api.Auth;

namespace PartnerCenterBridge.Api.Hosting;

/// <summary>
/// The authenticated channel between a second launch of the exe and the Local Workbench already
/// running on the port. The HTTP port proves nothing about who answers it (another Windows user can
/// bind it while PCB is stopped), so the second launch asks the running process for a fresh one-time
/// link over a named pipe instead:
/// <list type="bullet">
/// <item>The pipe is named <c>PartnerCenterBridge-&lt;port&gt;-&lt;user SID&gt;</c> and its DACL grants only
/// the current user (network logons are denied explicitly), so only this Windows user can connect.</item>
/// <item>The pipe namespace is shared, so another user could create a pipe with that name first. The
/// second launch therefore checks who serves the pipe: the server process's token must belong to the
/// current user. It connects with an anonymous impersonation level, so an impostor server could not
/// borrow its token either.</item>
/// <item>The running instance, in turn, answers only a client process of the same user.</item>
/// </list>
/// Only when the pipe is verified does the second launch open (or print) the link. Otherwise it
/// reveals nothing and reports the port as used by another program. Windows only; elsewhere a second
/// launch never receives a credential.
/// </summary>
public static class LaunchHandOff
{
    public const string RequestLine = "PCB-HANDOFF/1";
    public const string Product = "PartnerCenterBridge";
    private const int MaxLineBytes = 4096;

    public static string PipeName(int port, string userSid) => $"{Product}-{port}-{userSid}";

    /// <summary>The pipe name for this workbench and the current Windows user; null where pipes are not used.</summary>
    public static string? PipeName(LocalWorkbenchOptions options) =>
        OperatingSystem.IsWindows() && PipePeer.CurrentUserSid() is { } sid ? PipeName(options.Port, sid) : null;

    /// <summary>The reply the running instance sends: what kind of link, and the URL.</summary>
    public sealed record Reply(string Product, string Kind, string Url);

    public enum Outcome
    {
        /// <summary>A verified Partner Center Bridge of this Windows user answered; <see cref="Result.Link"/> is set.</summary>
        Verified,
        /// <summary>Nothing serves the pipe (not PCB, an older PCB, or not Windows).</summary>
        NoServer,
        /// <summary>Something serves the pipe, but not a process of this Windows user, or it answered nonsense.</summary>
        Unverified
    }

    public sealed record Result(Outcome Outcome, BrowserLink? Link = null);

    /// <summary>
    /// Asks the running instance for a fresh link. <paramref name="serverIsCurrentUser"/> is the
    /// identity check on the pipe's server end (replaceable in tests, where both ends are this user).
    /// </summary>
    public static async Task<Result> RequestLinkAsync(LocalWorkbenchOptions options, TimeSpan timeout,
        Func<NamedPipeClientStream, bool>? serverIsCurrentUser = null, string? pipeName = null, CancellationToken ct = default)
    {
        pipeName ??= PipeName(options);
        if (pipeName is null || !OperatingSystem.IsWindows()) return new Result(Outcome.NoServer);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        // Anonymous: whoever serves this pipe cannot impersonate this process.
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Anonymous);
        try
        {
            await client.ConnectAsync((int)Math.Min(timeout.TotalMilliseconds, 2000), cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            return new Result(Outcome.NoServer);
        }

        try
        {
            if (!(serverIsCurrentUser ?? PipePeer.ServerIsCurrentUser)(client)) return new Result(Outcome.Unverified);
            await WriteLineAsync(client, RequestLine, cts.Token);
            var line = await ReadLineAsync(client, cts.Token);
            var reply = line is null ? null : JsonSerializer.Deserialize<Reply>(line, JsonOptions);
            if (reply is null || reply.Product != Product || !Enum.TryParse<BrowserLinkKind>(reply.Kind, out var kind)
                || !IsOwnUrl(reply.Url, options))
                return new Result(Outcome.Unverified);
            return new Result(Outcome.Verified, new BrowserLink(reply.Url, kind));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException)
        {
            return new Result(Outcome.Unverified);
        }
    }

    /// <summary>Only a link to this workbench's canonical origin is ever opened.</summary>
    private static bool IsOwnUrl(string? url, LocalWorkbenchOptions options) =>
        url is not null && (url == options.CanonicalUrl || url.StartsWith(options.CanonicalUrl + "/", StringComparison.Ordinal));

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Reads one line (without the newline), at most <see cref="MaxLineBytes"/>; null at end of stream or when too long.</summary>
    internal static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxLineBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), ct);
            if (read == 0) return null;
            if (buffer[length] == (byte)'\n') return Encoding.UTF8.GetString(buffer, 0, length);
            length++;
        }
        return null;
    }
}

/// <summary>
/// Serves <see cref="LaunchHandOff"/> in the running Local Workbench (Windows, Kestrel only): each
/// verified request gets a freshly minted one-time link (see
/// <see cref="WorkbenchOwnerService.CreateBrowserLinkAsync"/>). The link is written to the pipe only,
/// never logged.
/// </summary>
public sealed class LaunchHandOffServer : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly LocalWorkbenchOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly IServer _server;
    private readonly ILogger<LaunchHandOffServer> _log;
    private readonly string? _pipeName;
    private readonly bool _requireKestrel;
    private readonly Func<NamedPipeServerStream, bool> _clientIsCurrentUser;

    public LaunchHandOffServer(IHostApplicationLifetime lifetime, LocalWorkbenchOptions options, IServiceScopeFactory scopes,
        IServer server, ILogger<LaunchHandOffServer> log)
        : this(lifetime, options, scopes, server, log, pipeName: null, requireKestrel: true, clientIsCurrentUser: null)
    {
    }

    /// <summary>For tests: a specific pipe name, any server type, and a replaceable client check.</summary>
    public LaunchHandOffServer(IHostApplicationLifetime lifetime, LocalWorkbenchOptions options, IServiceScopeFactory scopes,
        IServer server, ILogger<LaunchHandOffServer> log, string? pipeName, bool requireKestrel,
        Func<NamedPipeServerStream, bool>? clientIsCurrentUser)
    {
        _lifetime = lifetime;
        _options = options;
        _scopes = scopes;
        _server = server;
        _log = log;
        _pipeName = pipeName ?? LaunchHandOff.PipeName(options);
        _requireKestrel = requireKestrel;
        _clientIsCurrentUser = clientIsCurrentUser ?? PipePeer.ClientIsCurrentUser;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows() || _pipeName is null) return;
        // The integration test host binds no port, so there is nothing to hand over to.
        if (_requireKestrel && _server.GetType().Assembly.GetName().Name != "Microsoft.AspNetCore.Server.Kestrel.Core") return;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (_lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        await using (stoppingToken.Register(() => started.TrySetCanceled(stoppingToken)))
        {
            try { await started.Task; }
            catch (OperationCanceledException) { return; }
        }

        await ServeAsync(stoppingToken);
    }

    [SupportedOSPlatform("windows")]
    private async Task ServeAsync(CancellationToken ct)
    {
        NamedPipeServerStream current;
        try
        {
            // FirstPipeInstance: if someone already created a pipe with this name, do not join it.
            current = PipePeer.CreateServer(_pipeName!, firstInstance: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not create the second-launch pipe ({ExceptionType}); running {Command} again will not open a signed-in window.",
                ex.GetType().Name, HostingInfo.CommandName);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await current.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await current.DisposeAsync();
                return;
            }
            catch (IOException)
            {
                // The client went away before we saw it; serve the next one on a fresh instance.
                await current.DisposeAsync();
                if (!TryCreateNext(out current)) return;
                continue;
            }

            // The next instance exists before this one is handed off, so the name never lapses.
            var connected = current;
            if (!TryCreateNext(out current))
            {
                await HandleAsync(connected, ct);
                return;
            }
            _ = Task.Run(() => HandleAsync(connected, ct), CancellationToken.None);
        }
        await current.DisposeAsync();
    }

    [SupportedOSPlatform("windows")]
    private bool TryCreateNext(out NamedPipeServerStream next)
    {
        try
        {
            next = PipePeer.CreateServer(_pipeName!, firstInstance: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("The second-launch pipe stopped ({ExceptionType}).", ex.GetType().Name);
            next = null!;
            return false;
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (!_clientIsCurrentUser(pipe))
            {
                _log.LogWarning("Refused a second-launch request from a process of another Windows user.");
                return;
            }
            if (await LaunchHandOff.ReadLineAsync(pipe, cts.Token) != LaunchHandOff.RequestLine) return;

            using var scope = _scopes.CreateScope();
            var link = await scope.ServiceProvider.GetRequiredService<WorkbenchOwnerService>()
                .CreateBrowserLinkAsync(scope.ServiceProvider.GetRequiredService<PartnerCenterBridge.Data.BridgeDbContext>(), cts.Token);
            var reply = new LaunchHandOff.Reply(LaunchHandOff.Product, link.Kind.ToString(), link.Url);
            await LaunchHandOff.WriteLineAsync(pipe, JsonSerializer.Serialize(reply, LaunchHandOff.JsonOptions), cts.Token);
            if (OperatingSystem.IsWindows()) pipe.WaitForPipeDrain();
            _log.LogInformation("Handed a {Kind} link to a second launch.", link.Kind);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            _log.LogDebug("Second-launch request ended early ({ExceptionType}).", ex.GetType().Name);
        }
    }
}

/// <summary>Who is on the other end of a pipe: Windows process-token checks.</summary>
public static class PipePeer
{
    public static string? CurrentUserSid() =>
        OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value : null;

    /// <summary>A pipe instance only the current user can open (network logons denied explicitly).</summary>
    [SupportedOSPlatform("windows")]
    public static NamedPipeServerStream CreateServer(string name, bool firstInstance)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No current user SID.");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 4096, 4096, security);
    }

    /// <summary>The process serving <paramref name="pipe"/> runs as the current Windows user.</summary>
    public static bool ServerIsCurrentUser(NamedPipeClientStream pipe) =>
        OperatingSystem.IsWindows() && GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) && IsCurrentUser(pid);

    /// <summary>The process connected to <paramref name="pipe"/> runs as the current Windows user.</summary>
    public static bool ClientIsCurrentUser(NamedPipeServerStream pipe) =>
        OperatingSystem.IsWindows() && GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) && IsCurrentUser(pid);

    [SupportedOSPlatform("windows")]
    private static bool IsCurrentUser(uint processId)
    {
        var current = WindowsIdentity.GetCurrent().User;
        if (current is null) return false;
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid) return false;
        if (!OpenProcessToken(process, TokenQuery, out var token)) return false;
        using (token)
        {
            try
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                return identity.User is { } owner && current.Equals(owner);
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or ArgumentException or SecurityException)
            {
                return false;
            }
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}

/// <summary>
/// What the exe does when its port is taken (Program.cs): ask the running instance over the verified
/// pipe for a fresh link and open or print it, or refuse without revealing anything.
/// </summary>
public static class SecondLaunch
{
    public static async Task<int> RunAsync(LocalWorkbenchOptions options, TextWriter output, TextWriter error,
        Action<string> openBrowser, Func<NamedPipeClientStream, bool>? serverIsCurrentUser = null, string? pipeName = null,
        CancellationToken ct = default)
    {
        var result = await LaunchHandOff.RequestLinkAsync(options, TimeSpan.FromSeconds(5), serverIsCurrentUser, pipeName, ct);
        if (result is { Outcome: LaunchHandOff.Outcome.Verified, Link: { } link })
        {
            output.WriteLine($"Partner Center Bridge is already running at {options.CanonicalUrl}.");
            if (options.OpenBrowser) openBrowser(link.Url);
            else if (link.HasTicket) foreach (var line in LocalWorkbenchLifetime.DescribeLink(link, "")) output.WriteLine(line);
            return 0;
        }

        error.WriteLine($"Port {options.Port} is in use by another program; use --port <N> to pick a different one.");
        if (!OperatingSystem.IsWindows())
            error.WriteLine($"If that is Partner Center Bridge, open {options.CanonicalUrl} in a browser.");
        return 1;
    }
}

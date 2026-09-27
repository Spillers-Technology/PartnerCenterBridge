using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Hosting;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// Finding 1: a second launch gets a credential only from a hand-off pipe served by this Windows
/// user's own running Partner Center Bridge -- never from whatever answers on the HTTP port.
/// </summary>
public sealed class LaunchHandOffTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "pcb-handoff-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string UniquePipe() => "PartnerCenterBridge-test-" + Guid.NewGuid().ToString("N");

    private static LocalWorkbenchOptions Options(int port, bool openBrowser = false) =>
        new() { DataRoot = Path.GetTempPath(), Port = port, OpenBrowser = openBrowser };

    /// <summary>What another Windows user could run on the port while PCB is stopped: it claims to be PCB.</summary>
    private TcpListener SpoofedHttpServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    await stream.ReadAsync(new byte[4096]);
                    const string body = "{\"profile\":\"Local\",\"accountless\":true}";
                    var response = "HTTP/1.1 200 OK\r\nX-PCB-Instance: PartnerCenterBridge\r\nContent-Type: application/json\r\n" +
                                   $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                }
            }
            catch (Exception) { /* stopped */ }
        });
        _disposables.Add(new Stopper(listener.Stop));
        return listener;
    }

    private sealed class Stopper(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }

    private WebApplicationFactory<Program> AccountlessHost(out string pipeName, Func<NamedPipeServerStream, bool>? clientCheck = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(HostingKeys.Profile, HostingProfile.Local);
            builder.UseSetting(HostingKeys.DataDir, _dataDir);
            builder.UseSetting(HostingKeys.Port, "5199");
            builder.UseSetting(HostingKeys.OpenBrowser, "false");
        });
        _disposables.Add(factory);
        factory.CreateClient();
        pipeName = UniquePipe();
        var server = new LaunchHandOffServer(
            factory.Services.GetRequiredService<IHostApplicationLifetime>(),
            factory.Services.GetRequiredService<LocalWorkbenchOptions>(),
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<IServer>(),
            NullLogger<LaunchHandOffServer>.Instance, pipeName, requireKestrel: false, clientCheck);
        server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _disposables.Add(new Stopper(() => server.StopAsync(CancellationToken.None).GetAwaiter().GetResult()));
        return factory;
    }

    [Fact]
    public async Task Spoofed_http_server_without_a_verified_pipe_gets_no_credential()
    {
        var spoof = SpoofedHttpServer();
        var options = Options(((IPEndPoint)spoof.LocalEndpoint).Port, openBrowser: true);
        Assert.Equal(PortState.InUse, await PortPreflight.CheckAsync(options));

        var opened = new List<string>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SecondLaunch.RunAsync(options, output, error, opened.Add, pipeName: UniquePipe());

        Assert.Equal(1, exit);
        Assert.Empty(opened); // the browser is not sent anywhere, let alone with a ticket
        Assert.Contains($"Port {options.Port} is in use by another program", error.ToString());
        Assert.DoesNotContain("ticket", output + error.ToString());
    }

    [Fact]
    public async Task Pipe_served_by_another_user_is_not_trusted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var factory = AccountlessHost(out var pipeName);
        (await factory.CreateClient().PostAsJsonAsync("/api/auth/setup/no-account",
            new { confirm = true, ticket = factory.Services.GetRequiredService<WorkbenchOwnerService>().Mint(TicketPurpose.Setup) }))
            .EnsureSuccessStatusCode();

        // The server-identity check fails, as it would for an impostor pipe created by another user.
        var opened = new List<string>();
        var output = new StringWriter();
        var exit = await SecondLaunch.RunAsync(Options(5199, openBrowser: true), output, new StringWriter(), opened.Add,
            serverIsCurrentUser: _ => false, pipeName: pipeName);
        Assert.Equal(1, exit);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task Running_instance_refuses_a_client_of_another_user()
    {
        if (!OperatingSystem.IsWindows()) return;
        var checkedClient = false;
        AccountlessHost(out var pipeName, clientCheck: _ => { checkedClient = true; return false; });
        var result = await LaunchHandOff.RequestLinkAsync(Options(5199), TimeSpan.FromSeconds(5), pipeName: pipeName);
        Assert.NotEqual(LaunchHandOff.Outcome.Verified, result.Outcome);
        Assert.Null(result.Link);
        Assert.True(checkedClient); // refused before any ticket is minted
    }

    [Fact]
    public async Task Verified_pipe_hands_a_fresh_single_use_ticket_to_a_second_launch()
    {
        if (!OperatingSystem.IsWindows()) return;
        var factory = AccountlessHost(out var pipeName);
        var client = factory.CreateClient();

        // First run: the hand-off gives a setup link.
        var setup = await LaunchHandOff.RequestLinkAsync(Options(5199), TimeSpan.FromSeconds(5), pipeName: pipeName);
        Assert.Equal(LaunchHandOff.Outcome.Verified, setup.Outcome);
        Assert.Equal(BrowserLinkKind.Setup, setup.Link!.Kind);
        (await client.PostAsJsonAsync("/api/auth/setup/no-account",
            new { confirm = true, ticket = setup.Link.Url.Split("#ticket=")[1] })).EnsureSuccessStatusCode();

        // Accountless: a second launch (real identity checks on both ends) opens a signed-in window.
        var opened = new List<string>();
        var exit = await SecondLaunch.RunAsync(Options(5199, openBrowser: true), new StringWriter(), new StringWriter(), opened.Add,
            pipeName: pipeName);
        Assert.Equal(0, exit);
        var url = Assert.Single(opened);
        Assert.StartsWith("http://localhost:5199/#ticket=", url);
        var ticket = url.Split("#ticket=")[1];
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/launch", new { ticket })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/launch", new { ticket })).StatusCode);

        // With --no-browser the fresh link is printed instead, and differs every time.
        var output = new StringWriter();
        Assert.Equal(0, await SecondLaunch.RunAsync(Options(5199), output, new StringWriter(), _ => throw new InvalidOperationException(),
            pipeName: pipeName));
        Assert.Contains("Sign in:   http://localhost:5199/#ticket=", output.ToString());
        Assert.DoesNotContain(ticket, output.ToString());
    }

    [Fact]
    public void Pipe_is_named_for_the_port_and_the_current_user()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid = PipePeer.CurrentUserSid()!;
        Assert.StartsWith("S-1-5-", sid);
        Assert.Equal($"PartnerCenterBridge-5199-{sid}", LaunchHandOff.PipeName(Options(5199)));
    }

    [Fact]
    public async Task Pipe_acl_grants_only_the_current_user_and_denies_network_logons()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var server = PipePeer.CreateServer(UniquePipe(), firstInstance: true);
        var rules = server.GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.IO.Pipes.PipeAccessRule>().ToList();
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var network = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.NetworkSid, null);
        Assert.All(rules, rule => Assert.True(
            rule.IdentityReference.Equals(user) && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
            || rule.IdentityReference.Equals(network) && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Deny,
            $"unexpected rule {rule.IdentityReference} {rule.AccessControlType}"));
        Assert.Contains(rules, rule => rule.IdentityReference.Equals(network));
        await Task.CompletedTask;
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Hosting;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// "Skip -- use without an account" in the Local Workbench and the one-time launch tickets behind
/// it: the first-run choice (which, like the first account, needs a setup ticket), ticket sign-in
/// (single use, expiry, wrong tickets never blocking a valid one), conversion to a password account,
/// and the guardrails (never in the Server profile, never with a non-loopback --listen).
/// </summary>
public sealed class WorkbenchOwnerTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "pcb-owner-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _factories = new();
    private readonly ManualClock _clock = new();

    private const string Password = "correct horse battery staple";

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private WebApplicationFactory<Program> Host(params (string Key, string Value)[] settings)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(HostingKeys.Profile, HostingProfile.Local);
            builder.UseSetting(HostingKeys.DataDir, _dataDir);
            builder.UseSetting(HostingKeys.Port, "5199");
            builder.UseSetting(HostingKeys.OpenBrowser, "false");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(_clock));
        });
        _factories.Add(factory);
        return factory;
    }

    private static WorkbenchOwnerService Owner(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<WorkbenchOwnerService>();

    /// <summary>A ticket as the running process hands it out (its browser launch, console or the hand-off pipe).</summary>
    private static string Ticket(WebApplicationFactory<Program> factory, TicketPurpose purpose = TicketPurpose.SignIn) =>
        Owner(factory).Mint(purpose);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> SkipAsync(HttpClient client, string? ticket) =>
        client.PostAsJsonAsync("/api/auth/setup/no-account", new { confirm = true, ticket });

    private static Task<HttpResponseMessage> SkipAsync(WebApplicationFactory<Program> factory, HttpClient client) =>
        SkipAsync(client, Ticket(factory, TicketPurpose.Setup));

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string? ticket = null) =>
        client.PostAsJsonAsync("/api/auth/register", new { email, password = Password, displayName = email.Split('@')[0], ticket });

    private static Task<HttpResponseMessage> LaunchAsync(HttpClient client, string? ticket) =>
        client.PostAsJsonAsync("/api/auth/launch", new { ticket });

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task First_run_offers_skip_and_creates_the_owner_with_admin_roles_and_no_tenants()
    {
        var factory = Host();
        var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.True(status.GetProperty("needsFirstUser").GetBoolean());
        Assert.True(status.GetProperty("canSkipAccount").GetBoolean());
        Assert.True(status.GetProperty("setupTicketRequired").GetBoolean());
        Assert.False(status.GetProperty("accountless").GetBoolean());
        Assert.Equal(Environment.UserName, status.GetProperty("windowsUser").GetString());

        var auth = await JsonAsync(await SkipAsync(factory, client));
        var user = auth.GetProperty("user");
        Assert.Equal(Environment.UserName + " (this computer)", user.GetProperty("displayName").GetString());
        Assert.Equal(WorkbenchOwnerService.OwnerEmail, user.GetProperty("email").GetString());
        Assert.True(user.GetProperty("isWorkbenchOwner").GetBoolean());
        Assert.True(user.GetProperty("isSystemAdmin").GetBoolean());
        Assert.Equal(new[] { "Administrator" }, user.GetProperty("instanceRoles").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.Empty(user.GetProperty("tenantAccess").EnumerateArray());

        status = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.False(status.GetProperty("needsFirstUser").GetBoolean());
        Assert.False(status.GetProperty("canSkipAccount").GetBoolean());
        Assert.False(status.GetProperty("setupTicketRequired").GetBoolean());
        Assert.True(status.GetProperty("accountless").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EventType == AuditEventType.WorkbenchOwnerCreated));
        // Nothing reusable is written to disk: sign-in uses tickets the running process mints.
        Assert.False(File.Exists(Path.Combine(_dataDir, "launch-secret.protected")));
    }

    // --- Finding 2: first-run setup must prove it came from this workbench's own process ---

    [Fact]
    public async Task First_run_setup_needs_the_setup_ticket_in_the_local_profile()
    {
        var factory = Host();
        var client = factory.CreateClient();

        // Another Windows user on the same machine can reach loopback, but has no ticket.
        var skip = await SkipAsync(client, null);
        Assert.Equal(HttpStatusCode.Forbidden, skip.StatusCode);
        Assert.Contains("PartnerCenterBridge.exe", await skip.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await RegisterAsync(client, "intruder@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RegisterAsync(client, "intruder@example.com", new string('A', 43))).StatusCode);
        // A sign-in ticket is not a setup ticket.
        Assert.Equal(HttpStatusCode.Forbidden, (await SkipAsync(client, Ticket(factory, TicketPurpose.SignIn))).StatusCode);
        using (var scope = factory.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<BridgeDbContext>().AppUsers.AnyAsync());

        // The ticket this process handed out works, once.
        var ticket = Ticket(factory, TicketPurpose.Setup);
        var admin = await JsonAsync(await RegisterAsync(client, "admin@example.com", ticket));
        Assert.True(admin.GetProperty("user").GetProperty("isSystemAdmin").GetBoolean());
        // Setup is over: later accounts need no ticket (open registration, no instance role) ...
        var second = await JsonAsync(await RegisterAsync(client, "second@example.com"));
        Assert.False(second.GetProperty("user").GetProperty("isSystemAdmin").GetBoolean());
        // ... and the used ticket is gone.
        Assert.NotEqual(TicketCheck.Accepted, Owner(factory).Consume(ticket, TicketPurpose.Setup, out _));
    }

    [Fact]
    public async Task Setup_ticket_is_single_use_and_expires()
    {
        var factory = Host();
        var client = factory.CreateClient();
        var owner = Owner(factory);

        var ticket = owner.Mint(TicketPurpose.Setup);
        Assert.Equal(TicketCheck.Accepted, owner.Consume(ticket, TicketPurpose.Setup, out _));
        Assert.Equal(TicketCheck.Rejected, owner.Consume(ticket, TicketPurpose.Setup, out _));

        var expired = owner.Mint(TicketPurpose.Setup);
        _clock.Now += WorkbenchOwnerService.SetupLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(HttpStatusCode.Forbidden, (await SkipAsync(client, expired)).StatusCode);
        (await SkipAsync(factory, client)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Skip_is_refused_once_any_user_exists()
    {
        var factory = Host();
        var client = factory.CreateClient();
        (await RegisterAsync(client, "admin@example.com", Ticket(factory, TicketPurpose.Setup))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await SkipAsync(factory, client)).StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.False(status.GetProperty("canSkipAccount").GetBoolean());
        Assert.False(status.GetProperty("accountless").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("windowsUser").ValueKind);
    }

    [Fact]
    public async Task Skip_twice_and_registering_while_accountless_are_refused()
    {
        var factory = Host();
        var client = factory.CreateClient();
        (await SkipAsync(factory, client)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await SkipAsync(factory, client)).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await RegisterAsync(client, "second@example.com")).StatusCode);
        // No password login for the owner, whatever is typed.
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = WorkbenchOwnerService.OwnerEmail, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Skip_needs_an_explicit_json_confirmation()
    {
        var factory = Host();
        var client = factory.CreateClient();
        var ticket = Ticket(factory, TicketPurpose.Setup);

        // A cross-site form/text POST (no CORS preflight) cannot make the choice.
        var text = await client.PostAsync("/api/auth/setup/no-account",
            new StringContent($"{{\"confirm\":true,\"ticket\":\"{ticket}\"}}", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        var empty = await client.PostAsync("/api/auth/setup/no-account", null);
        Assert.False(empty.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/setup/no-account", new { confirm = false, ticket })).StatusCode);

        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<BridgeDbContext>().AppUsers.AnyAsync());
    }

    [Fact]
    public async Task Skip_is_refused_with_a_non_loopback_listen_address()
    {
        var factory = Host((HostingKeys.Listen, "192.168.1.10"));
        var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.True(status.GetProperty("needsFirstUser").GetBoolean());
        Assert.False(status.GetProperty("canSkipAccount").GetBoolean());
        var response = await SkipAsync(factory, client);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("--listen", await response.Content.ReadAsStringAsync());
        // The first account still needs the setup ticket with --listen.
        Assert.Equal(HttpStatusCode.Forbidden, (await RegisterAsync(client, "admin@example.com")).StatusCode);
        (await RegisterAsync(client, "admin@example.com", Ticket(factory, TicketPurpose.Setup))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Accountless_workbench_refuses_to_start_with_a_non_loopback_listen_address()
    {
        var first = Host();
        (await SkipAsync(first, first.CreateClient())).EnsureSuccessStatusCode();
        foreach (var factory in _factories) factory.Dispose();
        _factories.Clear();
        SqliteConnection.ClearAllPools();

        var exposed = Host((HostingKeys.Listen, "192.168.1.10"));
        var error = Assert.ThrowsAny<Exception>(() => exposed.CreateClient());
        Assert.Contains("used without an account", error.GetBaseException().Message);
    }

    [Fact]
    public async Task Server_profile_never_exposes_the_no_account_mode_and_keeps_open_bootstrap()
    {
        var sqlite = Path.Combine(_dataDir, "server.db");
        Directory.CreateDirectory(_dataDir);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(HostingKeys.Profile, HostingProfile.Server);
            builder.UseSetting("Persistence:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={sqlite}");
            builder.UseSetting("Auth:Mode", AuthModeInfo.Local);
            builder.UseSetting("Auth:Local:SigningKey", "server-profile-test-signing-key-with-32-bytes!");
            builder.UseSetting("DataProtection:KeyRingPath", Path.Combine(_dataDir, "keys"));
        });
        _factories.Add(factory);
        var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.Equal("Server", status.GetProperty("profile").GetString());
        Assert.True(status.GetProperty("needsFirstUser").GetBoolean());
        Assert.False(status.GetProperty("canSkipAccount").GetBoolean());
        Assert.False(status.GetProperty("setupTicketRequired").GetBoolean());
        Assert.False(status.GetProperty("accountless").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("windowsUser").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await SkipAsync(client, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await LaunchAsync(client, "anything")).StatusCode);

        var service = new WorkbenchOwnerService(new HostingInfo(HostingProfile.Server, null), TimeProvider.System,
            NullLogger<WorkbenchOwnerService>.Instance);
        Assert.NotNull(service.UnavailableReason);
        Assert.Throws<InvalidOperationException>(() => service.Mint(TicketPurpose.SignIn));
        Assert.Equal(TicketCheck.Rejected, service.Consume(new string('A', 43), TicketPurpose.SignIn, out _));

        // The container deployment's first run is unchanged: bootstrap relies on network controls.
        var admin = await JsonAsync(await RegisterAsync(client, "admin@example.com"));
        Assert.True(admin.GetProperty("user").GetProperty("isSystemAdmin").GetBoolean());
    }

    // --- Findings 1 and 5: one-time sign-in tickets ---

    [Fact]
    public async Task Sign_in_ticket_works_once_and_expires()
    {
        var factory = Host();
        var client = factory.CreateClient();
        (await SkipAsync(factory, client)).EnsureSuccessStatusCode();

        var ticket = Ticket(factory);
        var ok = await JsonAsync(await LaunchAsync(client, ticket));
        Assert.True(ok.GetProperty("user").GetProperty("isWorkbenchOwner").GetBoolean());
        var token = ok.GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", token))).StatusCode);
        // A copied, bookmarked or replayed link does nothing the second time.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, ticket)).StatusCode);

        var late = Ticket(factory);
        _clock.Now += WorkbenchOwnerService.SignInLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, late)).StatusCode);
        // A setup ticket does not sign in.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, Ticket(factory, TicketPurpose.Setup))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LaunchAsync(client, Ticket(factory))).StatusCode);
    }

    [Fact]
    public async Task Wrong_tickets_never_block_a_valid_one()
    {
        var factory = Host();
        var client = factory.CreateClient();
        (await SkipAsync(factory, client)).EnsureSuccessStatusCode();
        var ticket = Ticket(factory);

        var codes = new List<HttpStatusCode>();
        foreach (var wrong in new[] { "", null, "short", new string('A', 43), new string('x', 10_000), "not/base64url+chars/aaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ticket + "x" })
            codes.Add((await LaunchAsync(client, wrong)).StatusCode);
        Assert.All(codes, code => Assert.Contains(code, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests }));
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);

        // In the middle of the backoff, the real ticket still signs in.
        Assert.Equal(HttpStatusCode.OK, (await LaunchAsync(client, ticket)).StatusCode);

        // Only the failures before the backoff were recorded, so floods do not grow the audit log.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        Assert.Equal(WorkbenchOwnerService.FreeFailures,
            await db.AuditEvents.CountAsync(e => e.EventType == AuditEventType.LoginFailed && e.ActorName == "launch link"));
    }

    [Fact]
    public async Task Oversized_launch_bodies_are_refused_before_model_binding()
    {
        var factory = Host();
        var client = factory.CreateClient();
        var huge = await client.PostAsync("/api/auth/launch",
            new StringContent("{\"ticket\":\"" + new string('A', 64 * 1024) + "\"}", Encoding.UTF8, "application/json"));
        Assert.False(huge.IsSuccessStatusCode);
        Assert.NotEqual(HttpStatusCode.OK, huge.StatusCode);
    }

    [Fact]
    public async Task Launch_backoff_doubles_while_wrong_tickets_continue()
    {
        var factory = Host();
        var client = factory.CreateClient();
        (await SkipAsync(factory, client)).EnsureSuccessStatusCode();

        for (var i = 0; i < WorkbenchOwnerService.FreeFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LaunchAsync(client, "wrong")).StatusCode);
        _clock.Now += TimeSpan.FromSeconds(1.5);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, "wrong")).StatusCode); // 4th failure: 2s
        _clock.Now += TimeSpan.FromSeconds(1.5);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LaunchAsync(client, "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LaunchAsync(client, Ticket(factory))).StatusCode);
    }

    [Fact]
    public async Task Launch_token_has_instance_roles_and_tenant_access_only_for_tenants_it_adds()
    {
        var factory = Host();
        var client = factory.CreateClient();
        (await SkipAsync(factory, client)).EnsureSuccessStatusCode();
        var token = (await JsonAsync(await LaunchAsync(client, Ticket(factory)))).GetProperty("accessToken").GetString()!;

        var tenants = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Get, "/api/tenants", token)));
        Assert.Empty(tenants.EnumerateArray());

        // An existing tenant (registered directly, as a sync by someone else would) grants nothing.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
            db.Tenants.Add(new PartnerCenterBridge.Core.Entities.Tenant { TenantId = "other-tenant", DisplayName = "Other" });
            await db.SaveChangesAsync();
        }
        Assert.Empty((await JsonAsync(await client.SendAsync(Authed(HttpMethod.Get, "/api/tenants", token)))).EnumerateArray());

        var created = await client.SendAsync(Authed(HttpMethod.Post, "/api/tenants", token,
            new { tenantId = "11111111-2222-3333-4444-555555555555", displayName = "Contoso" }));
        created.EnsureSuccessStatusCode();
        var me = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", token)));
        var access = me.GetProperty("tenantAccess").EnumerateArray().ToList();
        Assert.Single(access);
        Assert.Equal("Contoso", access[0].GetProperty("tenantName").GetString());
        Assert.Equal("Owner", access[0].GetProperty("role").GetString());

        // Audit attribution uses the owner's (Windows) display name.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
            var created2 = await db.AuditEvents.Where(e => e.EventType == AuditEventType.EntityCreated).Select(e => e.ActorName).ToListAsync();
            Assert.Contains(WorkbenchOwnerService.OwnerDisplayName(), created2);
        }
    }

    [Fact]
    public async Task Owner_can_use_mcp_tokens_but_not_passkeys_or_totp_until_protected()
    {
        var factory = Host();
        var client = factory.CreateClient();
        var token = (await JsonAsync(await SkipAsync(factory, client))).GetProperty("accessToken").GetString()!;

        var pat = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Post, "/api/mcp-tokens", token, new { name = "Claude" })));
        var patJwt = pat.GetProperty("jwt").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", patJwt))).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Authed(HttpMethod.Post, "/api/auth/totp/enroll", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Authed(HttpMethod.Post, "/api/auth/passkey/register/options", token))).StatusCode);
    }

    [Fact]
    public async Task Protecting_with_an_account_disables_launch_login_and_enables_password_login()
    {
        var factory = Host();
        var client = factory.CreateClient();
        var token = (await JsonAsync(await SkipAsync(factory, client))).GetProperty("accessToken").GetString()!;
        var pending = Ticket(factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Authed(HttpMethod.Post, "/api/auth/owner/protect", token,
            new { email = "me@example.com", password = "short" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Authed(HttpMethod.Post, "/api/auth/owner/protect", token,
            new { email = WorkbenchOwnerService.OwnerEmail, password = Password }))).StatusCode);

        var protectedUser = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Post, "/api/auth/owner/protect", token,
            new { email = "Me@Example.com", password = Password, displayName = "Maya" })));
        Assert.False(protectedUser.GetProperty("user").GetProperty("isWorkbenchOwner").GetBoolean());
        Assert.Equal("me@example.com", protectedUser.GetProperty("user").GetProperty("email").GetString());
        Assert.Equal("Maya", protectedUser.GetProperty("user").GetProperty("displayName").GetString());
        Assert.True(protectedUser.GetProperty("user").GetProperty("isSystemAdmin").GetBoolean());
        var fresh = protectedUser.GetProperty("accessToken").GetString()!;

        // Outstanding tickets are dropped, and any ticket minted later is refused: there is no owner.
        Assert.Equal(0, Owner(factory).OutstandingTickets);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, pending)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, Ticket(factory))).StatusCode);
        var login = await JsonAsync(await client.PostAsJsonAsync("/api/auth/login", new { email = "me@example.com", password = Password }));
        Assert.Equal("Maya", login.GetProperty("user").GetProperty("displayName").GetString());

        var status = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.False(status.GetProperty("accountless").GetBoolean());
        // Only once: the account is an ordinary one now.
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Authed(HttpMethod.Post, "/api/auth/owner/protect", fresh,
            new { email = "other@example.com", password = Password }))).StatusCode);
        // And the workbench is multi-user again.
        (await RegisterAsync(client, "second@example.com")).EnsureSuccessStatusCode();
        var link = await LocalWorkbenchLifetime.CreateBrowserLinkAsync(
            factory.Services.GetRequiredService<IServiceScopeFactory>(), CancellationToken.None);
        Assert.Equal(new BrowserLink("http://localhost:5199", BrowserLinkKind.Plain), link);

        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<BridgeDbContext>().AuditEvents
            .AnyAsync(e => e.EventType == AuditEventType.WorkbenchOwnerProtected));
    }

    // --- Finding 3: conversion revokes everything issued before it ---

    [Fact]
    public async Task Protecting_revokes_every_earlier_session_and_mcp_token()
    {
        var factory = Host();
        var client = factory.CreateClient();
        var setupToken = (await JsonAsync(await SkipAsync(factory, client))).GetProperty("accessToken").GetString()!;
        var launchToken = (await JsonAsync(await LaunchAsync(client, Ticket(factory)))).GetProperty("accessToken").GetString()!;
        var pat = (await JsonAsync(await client.SendAsync(Authed(HttpMethod.Post, "/api/mcp-tokens", launchToken, new { name = "agent" }))))
            .GetProperty("jwt").GetString()!;
        // A PAT authenticates (and is then restricted to /mcp): 403, not 401.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", pat))).StatusCode);

        var converted = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Post, "/api/auth/owner/protect", setupToken,
            new { email = "me@example.com", password = Password })));
        var fresh = converted.GetProperty("accessToken").GetString()!;

        foreach (var stale in new[] { setupToken, launchToken, pat })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", stale))).StatusCode);
        // An old session can no longer enroll a passkey or mint a PAT without the new password.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Authed(HttpMethod.Post, "/api/auth/passkey/register/options", launchToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Authed(HttpMethod.Post, "/api/mcp-tokens", launchToken, new { name = "late" }))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", fresh))).StatusCode);
        var newPat = (await JsonAsync(await client.SendAsync(Authed(HttpMethod.Post, "/api/mcp-tokens", fresh, new { name = "new" }))))
            .GetProperty("jwt").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", newPat))).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        Assert.Equal(1, await db.AppUsers.Select(u => u.SessionEpoch).SingleAsync());
        Assert.NotNull((await db.McpTokens.SingleAsync(t => t.Name == "agent")).RevokedAt);
        Assert.Null((await db.McpTokens.SingleAsync(t => t.Name == "new")).RevokedAt);
    }

    [Fact]
    public async Task A_launch_racing_the_conversion_never_yields_a_working_session()
    {
        var factory = Host();
        var client = factory.CreateClient();
        var token = (await JsonAsync(await SkipAsync(factory, client))).GetProperty("accessToken").GetString()!;
        var tickets = Enumerable.Range(0, 8).Select(_ => Ticket(factory)).ToList();

        var launches = tickets.Select(ticket => Task.Run(() => LaunchAsync(client, ticket))).ToList();
        var protect = Task.Run(() => client.SendAsync(Authed(HttpMethod.Post, "/api/auth/owner/protect", token,
            new { email = "me@example.com", password = Password })));
        await Task.WhenAll(launches.Cast<Task>().Append(protect));
        (await protect).EnsureSuccessStatusCode();

        // Whatever the interleaving, a launch either lost (401) or got a token from before the epoch bump.
        foreach (var launch in launches)
        {
            var response = await launch;
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                continue;
            }
            var launched = (await JsonAsync(response)).GetProperty("accessToken").GetString()!;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", launched))).StatusCode);
        }
        // And every leftover ticket is dead.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, Ticket(factory))).StatusCode);
    }

    [Fact]
    public async Task Browser_link_carries_a_fresh_ticket_in_the_fragment_and_tickets_die_with_the_process()
    {
        string oldTicket;
        {
            var first = Host();
            var firstLink = await LocalWorkbenchLifetime.CreateBrowserLinkAsync(
                first.Services.GetRequiredService<IServiceScopeFactory>(), CancellationToken.None);
            Assert.Equal(BrowserLinkKind.Setup, firstLink.Kind);
            Assert.StartsWith("http://localhost:5199/#ticket=", firstLink.Url);
            (await SkipAsync(first.CreateClient(), firstLink.Url.Split("#ticket=")[1])).EnsureSuccessStatusCode();
            oldTicket = Ticket(first);
        }

        var second = Host();
        var client = second.CreateClient();
        // Tickets live only in the process that minted them.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, oldTicket)).StatusCode);

        var link = await LocalWorkbenchLifetime.CreateBrowserLinkAsync(
            second.Services.GetRequiredService<IServiceScopeFactory>(), CancellationToken.None);
        Assert.Equal(BrowserLinkKind.SignIn, link.Kind);
        Assert.StartsWith("http://localhost:5199/#ticket=", link.Url);
        var ticket = link.Url.Split("#ticket=")[1];
        Assert.Equal(WorkbenchOwnerService.TicketLength, ticket.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", ticket);
        var token = (await JsonAsync(await LaunchAsync(client, ticket))).GetProperty("accessToken").GetString()!;
        Assert.NotEqual(link, await LocalWorkbenchLifetime.CreateBrowserLinkAsync(
            second.Services.GetRequiredService<IServiceScopeFactory>(), CancellationToken.None));

        // Tickets are never written anywhere under the data root.
        foreach (var file in Directory.GetFiles(_dataDir, "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            Assert.DoesNotContain(ticket, await reader.ReadToEndAsync());
        }

        // The diagnostics auth check says what this mode means.
        var diagnostics = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Get, "/api/system/diagnostics", token)));
        var auth = diagnostics.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "auth");
        Assert.Equal("Ok", auth.GetProperty("status").GetString());
        Assert.StartsWith("No account (one-time launch links", auth.GetProperty("detail").GetString());
    }

    [Fact]
    public void Tickets_are_256_bit_base64url_and_bounded_in_number()
    {
        var clock = new ManualClock();
        var options = new LocalWorkbenchOptions { DataRoot = _dataDir, Port = 5199 };
        var service = new WorkbenchOwnerService(new HostingInfo(HostingProfile.Local, options), clock,
            NullLogger<WorkbenchOwnerService>.Instance);

        var tickets = Enumerable.Range(0, WorkbenchOwnerService.MaxOutstanding + 4).Select(_ => service.Mint(TicketPurpose.SignIn)).ToList();
        Assert.All(tickets, t => Assert.Equal(43, t.Length));
        Assert.Equal(tickets.Count, tickets.Distinct().Count());
        Assert.Equal(WorkbenchOwnerService.MaxOutstanding, service.OutstandingTickets);
        // The oldest were dropped to make room; the newest still work.
        Assert.Equal(TicketCheck.Rejected, service.Consume(tickets[0], TicketPurpose.SignIn, out _));
        Assert.Equal(TicketCheck.Accepted, service.Consume(tickets[^1], TicketPurpose.SignIn, out _));

        service.RevokeTickets();
        Assert.Equal(0, service.OutstandingTickets);
    }

    [Fact]
    public void Legacy_launch_secret_file_is_deleted_at_startup()
    {
        var options = new LocalWorkbenchOptions { DataRoot = _dataDir, Port = 5199 };
        LocalDataDirectory.Ensure(options);
        File.WriteAllText(options.LegacyLaunchSecretPath, "old");
        LocalDataDirectory.Ensure(options);
        Assert.False(File.Exists(options.LegacyLaunchSecretPath));
    }

    [Fact]
    public void Second_launch_reads_the_accountless_flag_from_the_running_instance()
    {
        Assert.Equal(new PortPreflightResult(PortState.ThisApp, true),
            PortPreflight.ParseStatus("{\"profile\":\"Local\",\"accountless\":true}"));
        Assert.Equal(new PortPreflightResult(PortState.ThisApp, false),
            PortPreflight.ParseStatus("{\"profile\":\"Local\",\"accountless\":false}"));
        // An instance that predates the flag.
        Assert.Equal(new PortPreflightResult(PortState.ThisApp, false), PortPreflight.ParseStatus("{\"profile\":\"Local\"}"));
        Assert.Null(PortPreflight.ParseStatus("{\"status\":\"ok\"}"));
    }
}

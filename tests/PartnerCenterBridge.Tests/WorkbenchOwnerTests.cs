using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Hosting;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// "Skip -- use without an account" in the Local Workbench: the first-run choice, launch-secret
/// sign-in with backoff, protected storage, conversion to a password account, and the guardrails
/// (never in the Server profile, never with a non-loopback --listen).
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

    private LocalWorkbenchOptions LocalOptions(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<LocalWorkbenchOptions>();

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> SkipAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/auth/setup/no-account", new { confirm = true });

    private static Task<HttpResponseMessage> LaunchAsync(HttpClient client, string? secret) =>
        client.PostAsJsonAsync("/api/auth/launch", new { secret });

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
        Assert.False(status.GetProperty("accountless").GetBoolean());
        Assert.Equal(Environment.UserName, status.GetProperty("windowsUser").GetString());

        var auth = await JsonAsync(await SkipAsync(client));
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
        Assert.True(status.GetProperty("accountless").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EventType == AuditEventType.WorkbenchOwnerCreated));
        Assert.True(File.Exists(LocalOptions(factory).LaunchSecretPath));
    }

    [Fact]
    public async Task Skip_is_refused_once_any_user_exists()
    {
        var client = Host().CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register",
            new { email = "admin@example.com", password = Password, displayName = "Admin" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await SkipAsync(client)).StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.False(status.GetProperty("canSkipAccount").GetBoolean());
        Assert.False(status.GetProperty("accountless").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("windowsUser").ValueKind);
    }

    [Fact]
    public async Task Skip_twice_and_registering_while_accountless_are_refused()
    {
        var client = Host().CreateClient();
        (await SkipAsync(client)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await SkipAsync(client)).StatusCode);

        var register = await client.PostAsJsonAsync("/api/auth/register",
            new { email = "second@example.com", password = Password, displayName = "Second" });
        Assert.Equal(HttpStatusCode.Conflict, register.StatusCode);
        // No password login for the owner, whatever is typed.
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = WorkbenchOwnerService.OwnerEmail, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Skip_needs_an_explicit_json_confirmation()
    {
        var factory = Host();
        var client = factory.CreateClient();

        // A cross-site form/text POST (no CORS preflight) cannot make the choice.
        var text = await client.PostAsync("/api/auth/setup/no-account", new StringContent("{\"confirm\":true}", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        var empty = await client.PostAsync("/api/auth/setup/no-account", null);
        Assert.False(empty.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/setup/no-account", new { confirm = false })).StatusCode);

        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<BridgeDbContext>().AppUsers.AnyAsync());
    }

    [Fact]
    public async Task Skip_is_refused_with_a_non_loopback_listen_address()
    {
        var client = Host((HostingKeys.Listen, "192.168.1.10")).CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.True(status.GetProperty("needsFirstUser").GetBoolean());
        Assert.False(status.GetProperty("canSkipAccount").GetBoolean());
        var response = await SkipAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("--listen", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Accountless_workbench_refuses_to_start_with_a_non_loopback_listen_address()
    {
        (await SkipAsync(Host().CreateClient())).EnsureSuccessStatusCode();
        foreach (var factory in _factories) factory.Dispose();
        _factories.Clear();
        SqliteConnection.ClearAllPools();

        var exposed = Host((HostingKeys.Listen, "192.168.1.10"));
        var error = Assert.ThrowsAny<Exception>(() => exposed.CreateClient());
        Assert.Contains("used without an account", error.GetBaseException().Message);
    }

    [Fact]
    public async Task Server_profile_never_exposes_the_no_account_mode()
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
        Assert.False(status.GetProperty("accountless").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("windowsUser").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await SkipAsync(client)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await LaunchAsync(client, "anything")).StatusCode);

        var service = new WorkbenchOwnerService(new HostingInfo(HostingProfile.Server, null), TimeProvider.System,
            NullLogger<WorkbenchOwnerService>.Instance);
        Assert.NotNull(service.UnavailableReason);
        Assert.Equal(LaunchCheck.Rejected, service.Check("anything", out _));
    }

    [Fact]
    public async Task Launch_accepts_the_persisted_secret_and_backs_off_after_wrong_ones()
    {
        var factory = Host();
        var client = factory.CreateClient();
        (await SkipAsync(client)).EnsureSuccessStatusCode();
        var secret = LocalLaunchSecretStore.TryRead(LocalOptions(factory));
        Assert.NotNull(secret);

        var ok = await JsonAsync(await LaunchAsync(client, secret));
        Assert.True(ok.GetProperty("user").GetProperty("isWorkbenchOwner").GetBoolean());
        var token = ok.GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", token))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, secret + "x")).StatusCode);
        // The third wrong secret started a backoff: even the right one waits, without being compared.
        var throttled = await LaunchAsync(client, secret);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);

        _clock.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(HttpStatusCode.OK, (await LaunchAsync(client, secret)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        Assert.Equal(3, await db.AuditEvents.CountAsync(e => e.EventType == AuditEventType.LoginFailed && e.ActorName == "launch link"));
    }

    [Fact]
    public async Task Launch_backoff_doubles_while_wrong_secrets_continue()
    {
        var factory = Host();
        var client = factory.CreateClient();
        (await SkipAsync(client)).EnsureSuccessStatusCode();

        for (var i = 0; i < WorkbenchOwnerService.FreeFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LaunchAsync(client, "wrong")).StatusCode);
        _clock.Now += TimeSpan.FromSeconds(1.5);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, "wrong")).StatusCode); // 4th failure: 2s
        _clock.Now += TimeSpan.FromSeconds(1.5);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LaunchAsync(client, "wrong")).StatusCode);
    }

    [Fact]
    public async Task Launch_token_has_instance_roles_and_tenant_access_only_for_tenants_it_adds()
    {
        var factory = Host();
        var client = factory.CreateClient();
        (await SkipAsync(client)).EnsureSuccessStatusCode();
        var token = (await JsonAsync(await LaunchAsync(client, LocalLaunchSecretStore.TryRead(LocalOptions(factory)))))
            .GetProperty("accessToken").GetString()!;

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
        var token = (await JsonAsync(await SkipAsync(client))).GetProperty("accessToken").GetString()!;

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
        var token = (await JsonAsync(await SkipAsync(client))).GetProperty("accessToken").GetString()!;
        var options = LocalOptions(factory);
        var secret = LocalLaunchSecretStore.TryRead(options)!;

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

        Assert.False(File.Exists(options.LaunchSecretPath));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LaunchAsync(client, secret)).StatusCode);
        var login = await JsonAsync(await client.PostAsJsonAsync("/api/auth/login", new { email = "me@example.com", password = Password }));
        Assert.Equal("Maya", login.GetProperty("user").GetProperty("displayName").GetString());

        var status = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.False(status.GetProperty("accountless").GetBoolean());
        // Only once: the account is an ordinary one now.
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Authed(HttpMethod.Post, "/api/auth/owner/protect", token,
            new { email = "other@example.com", password = Password }))).StatusCode);
        // And the workbench is multi-user again.
        (await client.PostAsJsonAsync("/api/auth/register",
            new { email = "second@example.com", password = Password, displayName = "Second" })).EnsureSuccessStatusCode();
        Assert.Null(await LocalWorkbenchLifetime.ResolveLaunchUrlAsync(
            factory.Services.GetRequiredService<IServiceScopeFactory>(), options, CancellationToken.None));

        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<BridgeDbContext>().AuditEvents
            .AnyAsync(e => e.EventType == AuditEventType.WorkbenchOwnerProtected));
    }

    [Fact]
    public async Task Launch_secret_survives_a_restart_and_the_browser_url_carries_it_in_the_fragment()
    {
        string secret;
        {
            var first = Host();
            (await SkipAsync(first.CreateClient())).EnsureSuccessStatusCode();
            secret = LocalLaunchSecretStore.TryRead(LocalOptions(first))!;
        }

        var second = Host();
        var client = second.CreateClient();
        var options = LocalOptions(second);
        var url = await LocalWorkbenchLifetime.ResolveLaunchUrlAsync(
            second.Services.GetRequiredService<IServiceScopeFactory>(), options, CancellationToken.None);
        Assert.Equal($"http://localhost:5199/#launch={secret}", url);
        Assert.Equal(url, options.LaunchUrl(secret));
        Assert.Equal(HttpStatusCode.OK, (await LaunchAsync(client, secret)).StatusCode);

        // The diagnostics auth check says what this mode means.
        var token = (await JsonAsync(await LaunchAsync(client, secret))).GetProperty("accessToken").GetString()!;
        var diagnostics = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Get, "/api/system/diagnostics", token)));
        var auth = diagnostics.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "auth");
        Assert.Equal("Ok", auth.GetProperty("status").GetString());
        Assert.StartsWith("No account (launch secret, this Windows user only)", auth.GetProperty("detail").GetString());
    }

    [Fact]
    public void Launch_secret_is_256_bits_protected_at_rest_and_stable()
    {
        var options = LocalWorkbenchOptions.FromConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [HostingKeys.DataDir] = _dataDir, [HostingKeys.Port] = "5199" }).Build());
        LocalDataDirectory.Ensure(options);
        Assert.Null(LocalLaunchSecretStore.TryRead(options));

        var secret = LocalLaunchSecretStore.GetOrCreate(options);
        Assert.Equal(43, secret.Length); // 32 bytes, base64url without padding
        Assert.DoesNotContain('+', secret);
        Assert.DoesNotContain('/', secret);
        Assert.Equal(secret, LocalLaunchSecretStore.GetOrCreate(options));
        Assert.Equal(secret, LocalLaunchSecretStore.TryRead(options));
        Assert.DoesNotContain(secret, File.ReadAllText(options.LaunchSecretPath));
        if (OperatingSystem.IsWindows())
        {
            // The key ring that protects it is itself encrypted with DPAPI for this Windows user.
            var keyXml = Directory.GetFiles(options.KeysPath, "*.xml").Select(File.ReadAllText).ToList();
            Assert.NotEmpty(keyXml);
            Assert.All(keyXml, xml => Assert.Contains("DpapiXmlDecryptor", xml));
        }

        LocalLaunchSecretStore.Delete(options);
        Assert.Null(LocalLaunchSecretStore.TryRead(options));
        Assert.NotEqual(secret, LocalLaunchSecretStore.GetOrCreate(options)); // rotated
        Assert.Empty(Directory.GetFiles(_dataDir, "*.tmp"));
    }

    [Fact]
    public void Launch_secret_shared_explicitly_is_refused_like_the_signing_key()
    {
        if (!OperatingSystem.IsWindows()) return;
        var options = LocalWorkbenchOptions.FromConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [HostingKeys.DataDir] = _dataDir, [HostingKeys.Port] = "5199" }).Build());
        LocalDataDirectory.Ensure(options);
        LocalLaunchSecretStore.GetOrCreate(options);
        Assert.Empty(LocalDataDirectory.Ensure(options));

        var file = new FileInfo(options.LaunchSecretPath);
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(security);

        var error = Assert.Throws<InvalidOperationException>(() => LocalDataDirectory.Ensure(options));
        Assert.Contains("launch-secret.protected", error.Message);
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

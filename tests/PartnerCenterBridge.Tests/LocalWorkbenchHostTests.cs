using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Diagnostics;
using PartnerCenterBridge.Api.Hosting;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// The real app (Program.cs) hosted in-process under the Local profile: SQLite in a temp data
/// directory, generated signing key, Local auth. No Postgres, Node or network needed.
/// </summary>
public sealed class LocalWorkbenchHostTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "pcb-local-" + Guid.NewGuid().ToString("N"));
    private readonly string _spaDir = Path.Combine(Path.GetTempPath(), "pcb-spa-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _factories = new();

    private const string Password = "correct horse battery staple";

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _dataDir, _spaDir })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
        });
        _factories.Add(factory);
        return factory;
    }

    /// <summary>A first-run setup ticket, as the exe's browser launch or console would hand out.</summary>
    private static string SetupTicket(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<WorkbenchOwnerService>().Mint(TicketPurpose.Setup);

    private static async Task<string> RegisterAsync(HttpClient client, string email, string? ticket = null)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = Password, displayName = email.Split('@')[0], ticket });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static HttpRequestMessage Get(string path, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Status_is_anonymous_and_reports_first_run()
    {
        var factory = Host();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/system/status");
        response.EnsureSuccessStatusCode();
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var root = json.RootElement;
            Assert.Equal(new[] { "profile", "version", "authMode", "needsFirstUser", "accountless", "canSkipAccount", "windowsUser", "setupTicketRequired" },
                root.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal("Local", root.GetProperty("profile").GetString());
            Assert.Equal(HostingInfo.ProductVersion, root.GetProperty("version").GetString());
            Assert.Equal("Local", root.GetProperty("authMode").GetString());
            Assert.True(root.GetProperty("needsFirstUser").GetBoolean());
        }

        await RegisterAsync(client, "admin@example.com", SetupTicket(factory));
        var after = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.False(after.GetProperty("needsFirstUser").GetBoolean());
        Assert.True(File.Exists(Path.Combine(_dataDir, "pcb.db")));
    }

    [Fact]
    public async Task Diagnostics_requires_auth_and_hides_details_from_non_admins()
    {
        var factory = Host();
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/system/diagnostics")).StatusCode);

        var adminToken = await RegisterAsync(client, "admin@example.com", SetupTicket(factory));   // first account: Administrator
        var userToken = await RegisterAsync(client, "user@example.com");     // later accounts: no instance role

        var admin = await client.SendAsync(Get("/api/system/diagnostics", adminToken));
        admin.EnsureSuccessStatusCode();
        using (var json = JsonDocument.Parse(await admin.Content.ReadAsStringAsync()))
        {
            var ids = json.RootElement.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToArray();
            Assert.Equal(new[] { "hosting", "database", "data-protection", "auth", "sam", "tenants", "pwsh", "exchange-module", "exchange-app" }, ids);
            var database = json.RootElement.GetProperty("checks")[1];
            Assert.Equal("Ok", database.GetProperty("status").GetString());
            Assert.Contains("SQLite", database.GetProperty("detail").GetString());
            var sam = json.RootElement.GetProperty("checks")[4];
            Assert.Equal("NotConfigured", sam.GetProperty("status").GetString());
            Assert.Equal(SystemDiagnostics.MicrosoftSettingsRoute, sam.GetProperty("fix").GetProperty("route").GetString());
            Assert.False(json.RootElement.GetProperty("capabilities").GetProperty("graph").GetBoolean());
        }

        var user = await client.SendAsync(Get("/api/system/diagnostics", userToken));
        user.EnsureSuccessStatusCode();
        using (var json = JsonDocument.Parse(await user.Content.ReadAsStringAsync()))
        {
            Assert.Empty(json.RootElement.GetProperty("checks").EnumerateArray());
            Assert.True(json.RootElement.TryGetProperty("capabilities", out var capabilities));
            Assert.True(capabilities.TryGetProperty("exchange", out _));
        }
    }

    [Fact]
    public async Task Loopback_ip_requests_are_redirected_to_the_canonical_origin()
    {
        var client = Host().CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://127.0.0.1:5199"),
            AllowAutoRedirect = false
        });

        var get = await client.GetAsync("/people/abc/def?tab=access");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, get.StatusCode);
        Assert.Equal("http://localhost:5199/people/abc/def?tab=access", get.Headers.Location!.ToString());

        var post = await client.PostAsJsonAsync("/api/auth/login", new { email = "a@b.c", password = "x" });
        Assert.Equal(HttpStatusCode.MisdirectedRequest, post.StatusCode);

        // The canonical host itself is served normally.
        var canonical = Host().CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:5199") });
        Assert.Equal(HttpStatusCode.OK, (await canonical.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Spa_fallback_serves_index_for_deep_links_but_not_for_server_paths()
    {
        Directory.CreateDirectory(Path.Combine(_spaDir, "assets"));
        await File.WriteAllTextAsync(Path.Combine(_spaDir, "index.html"), "<!doctype html><title>pcb-spa</title>");
        await File.WriteAllTextAsync(Path.Combine(_spaDir, "assets", "app-abc123.js"), "console.log(1)");
        var client = Host(("Spa:Path", _spaDir)).CreateClient();

        var deepLink = await client.GetAsync("/people/x/y");
        Assert.Equal(HttpStatusCode.OK, deepLink.StatusCode);
        Assert.Contains("pcb-spa", await deepLink.Content.ReadAsStringAsync());
        Assert.Equal("no-cache", deepLink.Headers.CacheControl!.ToString());

        var upn = await client.GetAsync("/people/tenant/jane.doe@contoso.com");
        Assert.Equal(HttpStatusCode.OK, upn.StatusCode);

        var asset = await client.GetAsync("/assets/app-abc123.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("immutable", asset.Headers.CacheControl!.ToString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/assets/missing.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/people/x/y", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Signing_key_is_generated_once_persisted_protected_and_reused()
    {
        string firstKey, token;
        {
            var first = Host();
            var client = first.CreateClient();
            token = await RegisterAsync(client, "admin@example.com", SetupTicket(first));
            firstKey = first.Services.GetRequiredService<IOptions<LocalAuthOptions>>().Value.SigningKey;
        }

        Assert.Equal(32, Convert.FromBase64String(firstKey).Length);
        var keyFile = Path.Combine(_dataDir, "auth-signing-key.protected");
        Assert.True(File.Exists(keyFile));
        Assert.DoesNotContain(firstKey, await File.ReadAllTextAsync(keyFile));

        // A second host over the same data directory reuses the key: the first host's token still works.
        var second = Host();
        Assert.Equal(firstKey, second.Services.GetRequiredService<IOptions<LocalAuthOptions>>().Value.SigningKey);
        var me = await second.CreateClient().SendAsync(Get("/api/auth/me", token));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public void Local_profile_uses_sqlite_local_auth_and_localhost_passkeys()
    {
        var factory = Host();
        factory.CreateClient();
        var services = factory.Services;
        var configuration = services.GetRequiredService<IConfiguration>();

        Assert.Equal("Sqlite", configuration["Persistence:Provider"]);
        Assert.Equal(AuthModeInfo.Local, services.GetRequiredService<AuthModeInfo>().Mode);
        var passkeys = services.GetRequiredService<IOptions<PasskeyOptions>>().Value;
        Assert.Equal("localhost", passkeys.RelyingPartyId);
        Assert.Equal(new[] { "http://localhost:5199" }, passkeys.Origins);
        var local = services.GetRequiredService<LocalWorkbenchOptions>();
        Assert.True(local.IsLoopbackOnly);
        Assert.Equal(IPAddress.Loopback, local.ListenAddress);
    }

    [Fact]
    public void Local_profile_refuses_dev_auth()
    {
        var factory = Host(("Auth:Mode", AuthModeInfo.Dev));
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("refuses Auth:Mode=Dev", error.GetBaseException().Message);
    }
}

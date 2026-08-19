using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Fido2NetLib;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.GitSync;
using PartnerCenterBridge.Api.Notifications;
using PartnerCenterBridge.Api.Orchestration;
using PartnerCenterBridge.Core.ConfigSnapshots;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Data;
using PartnerCenterBridge.Exchange;
using PartnerCenterBridge.Exchange.Workflows;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.Graph.ConfigSections;
using PartnerCenterBridge.Graph.Workflows;
using PartnerCenterBridge.PartnerCenter;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// --- Persistence -----------------------------------------------------------
// The audit interceptor needs the acting user, which needs HttpContext -- registered before the
// DbContext so the (sp, o) overload below can resolve it.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<PartnerCenterBridge.Core.Abstractions.ICurrentActor, HttpContextCurrentActor>();
builder.Services.AddScoped<AuditSaveChangesInterceptor>();

builder.Services.AddDbContext<BridgeDbContext>((sp, o) =>
    o.UseNpgsql(cfg.GetConnectionString("Postgres"))
     .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));

// Data Protection keys must be persisted so the encrypted SAM token survives restarts.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(cfg["DataProtection:KeyRingPath"] ?? "/keys"))
    .SetApplicationName("PartnerCenterBridge");

// --- Microsoft plane (SAM + GDAP + Graph + Intune) -------------------------
builder.Services.Configure<PartnerOptions>(cfg.GetSection(PartnerOptions.SectionName));
builder.Services.Configure<IntuneOptions>(cfg.GetSection(IntuneOptions.SectionName));
builder.Services.AddScoped<ISamTokenStore, ProtectedSamTokenStore>();
builder.Services.AddScoped<ITokenProvider, SamTokenService>();
builder.Services.AddScoped<SamBootstrapService>();
builder.Services.AddScoped<IGraphTenantClientFactory, GraphTenantClientFactory>();
builder.Services.AddScoped<IGraphUserService, GraphUserService>();
builder.Services.AddSingleton<IIntuneWinPackageReader, IntuneWinPackageReader>();

// Exchange Online (out-of-process EXO PowerShell V3, app-only certificate).
builder.Services.Configure<ExchangeOptions>(cfg.GetSection(ExchangeOptions.SectionName));
builder.Services.AddSingleton<IPwshRunner>(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ExchangeOptions>>().Value;
    return new PwshRunner(o.PwshPath, o.TimeoutSeconds);
});
builder.Services.AddScoped<IExchangeOnlineService, ExchangeOnlineService>();

// Known-fix workflow library (catalog + Graph-backed workflows). Runs are persisted and
// failures pushed to the configured webhook (Notifications section; empty URL disables).
builder.Services.AddScoped<PartnerCenterBridge.Core.Workflows.WorkflowCatalog>();
builder.Services.AddGraphWorkflows();
builder.Services.AddExchangeWorkflows();
builder.Services.Configure<NotificationOptions>(cfg.GetSection(NotificationOptions.SectionName));
builder.Services.AddScoped<IRunNotifier, WebhookRunNotifier>();
builder.Services.AddHttpClient("notifications");
builder.Services.AddScoped<IIntuneWin32Service, IntuneWin32Service>();
builder.Services.AddScoped<DeploymentOrchestrator>();
builder.Services.AddSingleton<IPackageStore, FilePackageStore>();
builder.Services.AddHttpClient("graph");
builder.Services.AddHttpClient<PartnerCenterClient>();

// Config snapshots (backup + diff): catalog + Graph-backed sections, same pattern as workflows.
// Git sync is opt-in -- GitSyncOptions.Enabled is false unless GitSync:RepoUrl is configured.
builder.Services.AddScoped<ConfigSectionCatalog>();
builder.Services.AddGraphConfigSections();
builder.Services.Configure<GitSyncOptions>(cfg.GetSection(GitSyncOptions.SectionName));
builder.Services.AddSingleton<GitSyncService>();
builder.Services.AddScoped<ConfigSnapshotService>();
builder.Services.AddScoped<PartnerCenterBridge.Api.Services.PendingActionService>();
builder.Services.AddScoped<PartnerCenterBridge.Api.Services.IPendingActionExecutor, PartnerCenterBridge.Api.Mcp.WorkflowRemediateExecutor>();

// --- Operator plane: OIDC (Authentik), local self-registered accounts, or dev bypass ----------
// Auth:Mode is the current knob (Oidc | Local | Dev). Auth:Enabled (true/false) is kept as a
// fallback for existing config that predates Auth:Mode, mapping to Oidc/Dev as before.
var authMode = cfg["Auth:Mode"] ?? (cfg.GetValue("Auth:Enabled", true) ? AuthModeInfo.Oidc : AuthModeInfo.Dev);
builder.Services.AddSingleton(new AuthModeInfo(authMode));
builder.Services.Configure<LocalAuthOptions>(cfg.GetSection(LocalAuthOptions.SectionName));
builder.Services.AddSingleton<LocalTokenService>();
builder.Services.AddScoped<ITenantAccessService, TenantAccessService>();
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();
builder.Services.AddScoped<AuthResponseFactory>();

// TOTP and passkeys are Local-mode features, but registered unconditionally like the above --
// AuthController/TotpController/PasskeyController are always present, so their constructors must
// always resolve even under Oidc/Dev (where their endpoints just 400 via AuthModeInfo checks).
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ChallengeCache>();
builder.Services.AddSingleton<TotpService>();
builder.Services.Configure<PasskeyOptions>(cfg.GetSection(PasskeyOptions.SectionName));
builder.Services.AddSingleton<IFido2>(sp =>
{
    var po = sp.GetRequiredService<IOptions<PasskeyOptions>>().Value;
    var config = new Fido2Configuration
    {
        ServerDomain = po.RelyingPartyId,
        ServerName = po.RelyingPartyName,
        Origins = new HashSet<string>(po.Origins)
    };
    // No IMetadataService: this app doesn't verify authenticator attestation against the FIDO
    // Metadata Service (AttestationPreference is None in PasskeyController), so it's never invoked.
    return new Fido2(config, null!);
});

switch (authMode)
{
    case AuthModeInfo.Local:
        var signingKey = cfg[$"{LocalAuthOptions.SectionName}:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
            throw new InvalidOperationException(
                "Auth:Local:SigningKey must be set (e.g. `openssl rand -base64 32`) when Auth:Mode=Local.");
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = LocalTokenService.Issuer,
                    ValidateAudience = true,
                    ValidAudience = LocalTokenService.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ValidateLifetime = true,
                    NameClaimType = ClaimTypes.Name
                };
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var db = context.HttpContext.RequestServices.GetRequiredService<BridgeDbContext>();
                        if (!await McpTokenValidator.ValidateAsync(context.Principal, db, context.HttpContext.RequestAborted))
                        {
                            context.Fail("MCP token has been revoked.");
                        }
                    }
                };
            });
        break;
    case AuthModeInfo.Dev:
        builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
        break;
    default: // Oidc
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.Authority = cfg["Auth:Authority"];
                o.Audience = cfg["Auth:Audience"];
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidateIssuer = true,
                    NameClaimType = cfg["Auth:NameClaim"] ?? "preferred_username"
                };
            });
        break;
}
builder.Services.AddAuthorization();

var origins = cfg.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// Enums cross the wire as their names ("Active", "Ok"), matching the SPA's string unions --
// except Fido2NetLib's, which need their own WebAuthn-spec wire values (see AppJsonStringEnumConverter).
builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.Converters.Add(new PartnerCenterBridge.Api.AppJsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Apply schema at startup so a fresh Postgres is usable immediately.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
    db.Database.Migrate();
}

// CLI mode: `dotnet run -- bootstrap-sam` runs the interactive device-code flow and exits.
if (args.Contains("bootstrap-sam"))
{
    using var scope = app.Services.CreateScope();
    var boot = scope.ServiceProvider.GetRequiredService<SamBootstrapService>();
    Console.WriteLine("Starting Secure Application Model bootstrap (device code)...");
    var user = await boot.BootstrapAsync(msg => { Console.WriteLine(msg); return Task.CompletedTask; });
    Console.WriteLine($"SAM bootstrap complete for {user}. Encrypted refresh token stored.");
    return;
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseMiddleware<McpPatEndpointRestrictionMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapMcp("/mcp").RequireAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can reference the entry point.</summary>
public partial class Program;

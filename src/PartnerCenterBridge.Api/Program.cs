using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Orchestration;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Data;
using PartnerCenterBridge.Exchange;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.Graph.Workflows;
using PartnerCenterBridge.PartnerCenter;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// --- Persistence -----------------------------------------------------------
builder.Services.AddDbContext<BridgeDbContext>(o =>
    o.UseNpgsql(cfg.GetConnectionString("Postgres")));

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

// Known-fix workflow library (catalog + Graph-backed workflows).
builder.Services.AddScoped<PartnerCenterBridge.Core.Workflows.WorkflowCatalog>();
builder.Services.AddGraphWorkflows();
builder.Services.AddScoped<IIntuneWin32Service, IntuneWin32Service>();
builder.Services.AddScoped<DeploymentOrchestrator>();
builder.Services.AddSingleton<IPackageStore, FilePackageStore>();
builder.Services.AddHttpClient("graph");
builder.Services.AddHttpClient<PartnerCenterClient>();

// --- Operator plane (OIDC via Authentik, or dev bypass) --------------------
var authEnabled = cfg.GetValue("Auth:Enabled", true);
if (authEnabled)
{
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
}
else
{
    builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
}
builder.Services.AddAuthorization();

var origins = cfg.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddControllers();
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
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can reference the entry point.</summary>
public partial class Program;

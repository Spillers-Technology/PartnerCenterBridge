using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PartnerCenterBridge.Api.TenantAudits;
using PartnerCenterBridge.Core.ConfigSnapshots;
using PartnerCenterBridge.Core.TenantAudits;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Tests.TenantAudits;

/// <summary>Guards the contract every check must meet, so adding one can't quietly break exports, the UI or history.</summary>
public class TenantAuditCatalogTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    /// <summary>Every real check, built the same way the app builds them (type discovery), over fake data.</summary>
    internal static TenantAuditCatalog RealCatalog(BridgeDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuditDirectoryData>(new FakeDirectoryData());
        services.AddSingleton<IAuditDeviceData>(new FakeDeviceData());
        services.AddSingleton<IAuditSecurityData>(new FakeSecurityData());
        services.AddSingleton<IAuditMailboxData>(new FakeMailboxData());
        services.AddSingleton(db);
        services.AddSingleton(new ConfigSectionCatalog([]));
        foreach (var t in TenantAuditRegistration.CheckTypes()) services.AddScoped(typeof(ITenantAuditCheck), t);
        services.AddScoped<TenantAuditCatalog>();
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<TenantAuditCatalog>();
    }

    [Fact]
    public void Ships_the_initial_catalog_across_every_category()
    {
        var catalog = RealCatalog(_db.Context);

        Assert.True(catalog.All.Count >= 20, $"expected the initial catalog, found {catalog.All.Count} checks");
        Assert.All(AuditCategories.All, c => Assert.Contains(catalog.All, x => x.Descriptor.Category == c));
        Assert.Contains(catalog.All, c => c.Descriptor.Id == "dormant-licensed-users");
        Assert.Contains(catalog.All, c => c.Descriptor.Id == "config-drift");
    }

    [Fact]
    public void Every_check_has_complete_metadata()
    {
        foreach (var d in RealCatalog(_db.Context).All.Select(c => c.Descriptor))
        {
            Assert.Matches(new Regex("^[a-z0-9]+(-[a-z0-9]+)*$"), d.Id);
            Assert.False(string.IsNullOrWhiteSpace(d.Name), d.Id);
            Assert.False(string.IsNullOrWhiteSpace(d.Description), d.Id);
            Assert.False(string.IsNullOrWhiteSpace(d.BusinessImpact), d.Id);
            Assert.False(string.IsNullOrWhiteSpace(d.Recommendation), d.Id);
            Assert.NotEmpty(d.SeverityRules);
            Assert.NotEmpty(d.Requirements);
            Assert.Contains(d.Category, AuditCategories.All);
            // Business context stays short: one or two sentences, not consultancy prose.
            Assert.True(d.BusinessImpact.Length <= 200, $"{d.Id} business impact is {d.BusinessImpact.Length} chars");
        }
    }

    [Fact]
    public void Check_text_is_ascii_only()
    {
        // Project rule: non-ASCII in compiled string literals has shipped as mojibake before.
        foreach (var d in RealCatalog(_db.Context).All.Select(c => c.Descriptor))
        {
            var text = string.Join("\n", new[] { d.Name, d.Description, d.BusinessImpact, d.Recommendation }
                .Concat(d.SeverityRules).Concat(d.Limitations).Concat(d.Requirements.Select(r => r.Name + r.Note)));
            Assert.True(text.All(ch => ch < 128), $"{d.Id} has non-ASCII text");
        }
    }

    [Fact]
    public void Duplicate_ids_are_rejected_at_startup()
    {
        var a = new TenantAuditEngineTests.LambdaCheck("same", _ => Task.FromResult(new AuditCheckOutput()));
        var b = new TenantAuditEngineTests.LambdaCheck("same", _ => Task.FromResult(new AuditCheckOutput()));

        Assert.Throws<InvalidOperationException>(() => new TenantAuditCatalog([a, b]));
    }

    [Fact]
    public void Resolve_by_category_names_the_audit_and_fills_default_parameters()
    {
        var catalog = RealCatalog(_db.Context);

        var identity = catalog.Resolve(null, ["Identity"], null);
        var full = catalog.Resolve(null, null, null);

        Assert.All(identity.Checks, c => Assert.Equal(AuditCategories.Identity, c.Descriptor.Category));
        Assert.Equal("Identity hygiene", identity.AuditName);
        Assert.Equal(90, identity.Parameters["inactiveDays"]);
        Assert.False(identity.Parameters.ContainsKey("deviceStaleDays")); // only parameters the selection uses
        Assert.Equal("Full tenant health check", full.AuditName);
        Assert.Equal(catalog.All.Count, full.Checks.Count);
        Assert.Equal(30, full.Parameters["deviceStaleDays"]);
    }

    [Fact]
    public void Resolve_accepts_a_custom_threshold_and_a_single_check()
    {
        var s = RealCatalog(_db.Context).Resolve(["dormant-licensed-users"], null, new Dictionary<string, int> { ["inactiveDays"] = 45 });

        Assert.Equal("Dormant licensed users", s.AuditName);
        Assert.Equal(45, s.Parameters["inactiveDays"]);
    }

    [Theory]
    [InlineData("no-such-check", null, null, 0, "Unknown audit check")]
    [InlineData(null, "Finance", null, 0, "Unknown audit category")]
    [InlineData(null, null, "inactiveDays", 3, "between 7 and 730")]
    [InlineData(null, null, "inactiveDays", 5000, "between 7 and 730")]
    [InlineData(null, null, "bogus", 1, "Unknown audit parameter")]
    public void Resolve_rejects_bad_requests_instead_of_guessing(string? check, string? category, string? param, int value, string message)
    {
        var catalog = RealCatalog(_db.Context);
        var ex = Assert.Throws<AuditRequestException>(() => catalog.Resolve(
            check is null ? null : [check], category is null ? null : [category],
            param is null ? null : new Dictionary<string, int> { [param] = value }));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Presets_offer_full_and_each_category()
    {
        var presets = RealCatalog(_db.Context).Presets;

        Assert.Equal("full", presets[0].Id);
        Assert.Equal(["full", "identity", "licensing", "exchange", "devices", "security"], presets.Select(p => p.Id));
    }
}

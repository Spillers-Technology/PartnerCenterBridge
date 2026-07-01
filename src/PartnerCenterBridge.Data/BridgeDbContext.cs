using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Data;

public class BridgeDbContext : DbContext
{
    public BridgeDbContext(DbContextOptions<BridgeDbContext> options) : base(options) { }

    // Shared JSON (de)serialisation + change-tracking comparer for List&lt;string&gt; columns.
    private static readonly ValueConverter<List<string>, string> StringListConverter = new(
        v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
        v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());

    private static readonly ValueComparer<List<string>> StringListComparer = new(
        (a, b) => (a ?? new()).SequenceEqual(b ?? new()),
        v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode())),
        v => v.ToList());

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<AppTemplate> AppTemplates => Set<AppTemplate>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<ProvisioningTemplate> ProvisioningTemplates => Set<ProvisioningTemplate>();
    public DbSet<SecretRecord> Secrets => Set<SecretRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.HasIndex(t => t.TenantId).IsUnique();
            e.Property(t => t.TenantId).IsRequired();
            e.HasOne(t => t.Contract).WithMany(c => c.Tenants)
                .HasForeignKey(t => t.ContractId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Contract>(e =>
        {
            e.Property(c => c.Name).IsRequired();
        });

        b.Entity<AppTemplate>(e =>
        {
            e.Property(a => a.DisplayName).IsRequired();
            // Complex value objects are stored as JSON columns — simple, queryable enough for our needs.
            e.OwnsMany(a => a.DetectionRules, o => o.ToJson());
            e.OwnsMany(a => a.Assignments, o => o.ToJson());
            e.OwnsOne(a => a.Content, o => o.ToJson());
            e.HasOne(a => a.Contract).WithMany(c => c.DesiredApps)
                .HasForeignKey(a => a.ContractId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Deployment>(e =>
        {
            e.HasIndex(d => new { d.TenantId, d.AppTemplateId });
            e.Property(d => d.AssignmentIds).HasColumnType("jsonb")
                .HasConversion(StringListConverter, StringListComparer);
            e.HasOne(d => d.AppTemplate).WithMany()
                .HasForeignKey(d => d.AppTemplateId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.Tenant).WithMany(t => t.Deployments)
                .HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProvisioningTemplate>(e =>
        {
            // One provisioning template per contract.
            e.HasOne(p => p.Contract).WithOne(c => c.ProvisioningTemplate)
                .HasForeignKey<ProvisioningTemplate>(p => p.ContractId).OnDelete(DeleteBehavior.Cascade);
            e.Property(p => p.LicenseSkuIds).HasColumnType("jsonb")
                .HasConversion(StringListConverter, StringListComparer);
            e.Property(p => p.GroupIds).HasColumnType("jsonb")
                .HasConversion(StringListConverter, StringListComparer);
        });

        b.Entity<SecretRecord>(e =>
        {
            e.HasKey(s => s.Name);
        });
    }
}

/// <summary>
/// A single named secret stored as ciphertext (protected via ASP.NET Data Protection). Used for
/// the SAM refresh token and any other at-rest secret the bridge manages.
/// </summary>
public class SecretRecord
{
    public required string Name { get; set; }
    public required string ProtectedValue { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

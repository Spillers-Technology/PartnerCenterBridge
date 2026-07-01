using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Core.Abstractions;

namespace PartnerCenterBridge.Data;

/// <summary>
/// <see cref="ISamTokenStore"/> backed by the <see cref="SecretRecord"/> table, with the refresh
/// token encrypted at rest via ASP.NET Data Protection. The protector purpose isolates this
/// secret from any other protected payloads.
/// </summary>
public class ProtectedSamTokenStore : ISamTokenStore
{
    internal const string SecretName = "sam-refresh-token";
    private const string ProtectorPurpose = "PartnerCenterBridge.SamRefreshToken.v1";

    private readonly BridgeDbContext _db;
    private readonly IDataProtector _protector;

    public ProtectedSamTokenStore(BridgeDbContext db, IDataProtectionProvider dp)
    {
        _db = db;
        _protector = dp.CreateProtector(ProtectorPurpose);
    }

    public async Task<string?> GetRefreshTokenAsync(CancellationToken ct = default)
    {
        var rec = await _db.Secrets.FirstOrDefaultAsync(s => s.Name == SecretName, ct);
        return rec is null ? null : _protector.Unprotect(rec.ProtectedValue);
    }

    public async Task SaveRefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var protectedValue = _protector.Protect(refreshToken);
        var rec = await _db.Secrets.FirstOrDefaultAsync(s => s.Name == SecretName, ct);
        if (rec is null)
        {
            _db.Secrets.Add(new SecretRecord { Name = SecretName, ProtectedValue = protectedValue });
        }
        else
        {
            rec.ProtectedValue = protectedValue;
            rec.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }
}

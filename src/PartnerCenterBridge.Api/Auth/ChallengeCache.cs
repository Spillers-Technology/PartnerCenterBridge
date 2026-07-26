using Microsoft.Extensions.Caching.Memory;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>
/// Short-lived server-side state for multi-step ceremonies that can't be verified in a single
/// stateless request: the TOTP login challenge (password verified, code not yet entered) and
/// WebAuthn attestation/assertion (options generated, response not yet returned). Backed by
/// <see cref="IMemoryCache"/> -- correct for this app's single-replica deployment
/// (`deploy/base/api-deployment.yaml` pins <c>replicas: 1</c>); scaling the API out would need a
/// distributed cache instead.
/// </summary>
public class ChallengeCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly IMemoryCache _cache;

    public ChallengeCache(IMemoryCache cache) => _cache = cache;

    public string Store<T>(T value) where T : notnull
    {
        var key = Guid.NewGuid().ToString("N");
        _cache.Set(key, value, Ttl);
        return key;
    }

    /// <summary>Consumes (removes) the entry if present and of the expected type. For ceremonies that are genuinely one-shot (WebAuthn: the browser only ever calls verify once per fetched options).</summary>
    public bool TryTake<T>(string key, out T? value)
    {
        if (_cache.TryGetValue(key, out var boxed) && boxed is T typed)
        {
            _cache.Remove(key);
            value = typed;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Reads without removing -- for challenges that should survive a wrong attempt (a mistyped TOTP code shouldn't force a fresh password login). Caller decides when to <see cref="Remove"/>.</summary>
    public bool TryPeek<T>(string key, out T? value)
    {
        if (_cache.TryGetValue(key, out var boxed) && boxed is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }

    public void Remove(string key) => _cache.Remove(key);
}

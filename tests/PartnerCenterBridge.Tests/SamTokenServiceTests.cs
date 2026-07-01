using PartnerCenterBridge.PartnerCenter;

namespace PartnerCenterBridge.Tests;

public class SamTokenServiceTests
{
    [Fact]
    public void ExtractRefreshToken_returns_secret_from_msal_cache()
    {
        const string cache =
            """
            {
              "RefreshToken": {
                "key1": { "secret": "rt-new", "last_modification_time": "1700000100" }
              }
            }
            """;

        Assert.Equal("rt-new", SamTokenService.ExtractRefreshToken(cache));
    }

    [Fact]
    public void ExtractRefreshToken_prefers_most_recent_entry()
    {
        const string cache =
            """
            {
              "RefreshToken": {
                "old": { "secret": "rt-old", "last_modification_time": "1700000000" },
                "new": { "secret": "rt-new", "last_modification_time": "1700009999" }
              }
            }
            """;

        Assert.Equal("rt-new", SamTokenService.ExtractRefreshToken(cache));
    }

    [Fact]
    public void ExtractRefreshToken_returns_null_when_absent()
    {
        Assert.Null(SamTokenService.ExtractRefreshToken("{}"));
        Assert.Null(SamTokenService.ExtractRefreshToken(null));
        Assert.Null(SamTokenService.ExtractRefreshToken(""));
    }
}

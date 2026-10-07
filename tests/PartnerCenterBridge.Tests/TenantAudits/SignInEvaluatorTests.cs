using PartnerCenterBridge.Core.TenantAudits;
using PartnerCenterBridge.Core.TenantAudits.Checks;
using static PartnerCenterBridge.Tests.TenantAudits.AuditTest;

namespace PartnerCenterBridge.Tests.TenantAudits;

public class SignInEvaluatorTests
{
    [Theory]
    [InlineData(89, SignInStanding.Active)]
    [InlineData(90, SignInStanding.InactiveSinceSuccess)] // the threshold itself counts as inactive ("90+ days")
    [InlineData(400, SignInStanding.InactiveSinceSuccess)]
    public void A_recorded_successful_sign_in_decides(int successDaysAgo, SignInStanding expected)
    {
        var a = SignInEvaluator.Assess(User("Ada", successDaysAgo: successDaysAgo, attemptDaysAgo: 1), Now, 90);

        Assert.Equal(expected, a.Standing);
        Assert.Equal(successDaysAgo, a.DaysInactive);
        Assert.Equal("last successful sign-in", a.Basis);
    }

    [Fact]
    public void A_recent_failed_attempt_never_makes_an_account_look_used_or_unused()
    {
        var a = SignInEvaluator.Assess(User("Ada", attemptDaysAgo: 3), Now, 90);

        Assert.Equal(SignInStanding.AttemptsWithoutSuccess, a.Standing);
        Assert.False(a.IsInactive);
        Assert.Null(a.DaysInactive);
    }

    [Fact]
    public void Old_attempts_with_no_success_mean_no_activity_of_any_kind_for_the_period()
    {
        var a = SignInEvaluator.Assess(User("Ada", attemptDaysAgo: 200), Now, 90);

        Assert.Equal(SignInStanding.InactiveSinceAttempt, a.Standing);
        Assert.Equal(200, a.DaysInactive);
        Assert.Contains("no success recorded", a.Basis);
    }

    [Fact]
    public void No_record_at_all_is_never_signed_in_unless_the_account_is_new()
    {
        Assert.Equal(SignInStanding.NeverSignedIn, SignInEvaluator.Assess(User("Old", createdDaysAgo: 365), Now, 90).Standing);
        Assert.Equal(SignInStanding.NewAccount, SignInEvaluator.Assess(User("New", createdDaysAgo: 10), Now, 90).Standing);
        // Created exactly at the threshold is old enough to call.
        Assert.Equal(SignInStanding.NeverSignedIn, SignInEvaluator.Assess(User("Edge", createdDaysAgo: 90), Now, 90).Standing);
    }

    [Fact]
    public void Unknown_creation_date_with_no_record_reports_never_signed_in()
    {
        var u = User("Ada") with { CreatedAt = null, SignIn = null };
        var a = SignInEvaluator.Assess(u, Now, 90);

        Assert.Equal(SignInStanding.NeverSignedIn, a.Standing);
        Assert.Null(a.DaysInactive);
    }

    [Fact]
    public void A_future_timestamp_from_clock_skew_is_zero_days_not_negative()
    {
        Assert.Equal(0, SignInEvaluator.DaysBetween(Now.AddHours(2), Now));
    }
}

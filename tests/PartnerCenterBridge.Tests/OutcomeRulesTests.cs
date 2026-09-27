using PartnerCenterBridge.Core.Operations;

namespace PartnerCenterBridge.Tests;

public class OutcomeRulesTests
{
    [Fact]
    public void Outcome_is_derived_from_verification_not_reported_success()
    {
        Assert.Equal(Outcome.NoChangeNeeded, OutcomeRules.Derive([]));
        Assert.Equal(Outcome.Succeeded, OutcomeRules.Derive([new(true, true, true), new(true, true, true)]));
        Assert.Equal(Outcome.PartiallySucceeded, OutcomeRules.Derive([new(true, true, true), new(true, false, false)]));
        Assert.Equal(Outcome.Failed, OutcomeRules.Derive([new(true, false, false)]));
        Assert.Equal(Outcome.VerificationFailed, OutcomeRules.Derive([new(true, true, true), new(true, true, false)]));
        // A requested action that could not run counts against success even when everything else verified.
        Assert.Equal(Outcome.PartiallySucceeded, OutcomeRules.Derive([new(true, true, true), new(false, false, false, RequiredButNotDone: true)]));
        Assert.Equal(Outcome.Failed, OutcomeRules.Derive([new(false, false, false, RequiredButNotDone: true)]));
        // Plain skips (e.g. already a member) are not failures.
        Assert.Equal(Outcome.NoChangeNeeded, OutcomeRules.Derive([new(false, true, false)]));
    }
}

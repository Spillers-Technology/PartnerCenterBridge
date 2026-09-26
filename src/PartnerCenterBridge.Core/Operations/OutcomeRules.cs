namespace PartnerCenterBridge.Core.Operations;

/// <summary>How one requested item ended up, as input to <see cref="OutcomeRules.Derive"/>.</summary>
/// <param name="Attempted">A change was actually sent.</param>
/// <param name="ReportedOk">The service reported the change as done (or already in place).</param>
/// <param name="Verified">A post-change re-read confirmed the desired state.</param>
/// <param name="RequiredButNotDone">
/// Not attempted, but the operator asked for it and it did not happen (blocked, gated, unsupported).
/// Counts against success; plain "nothing to do" skips must pass false.
/// </param>
public readonly record struct ItemOutcome(bool Attempted, bool ReportedOk, bool Verified, bool RequiredButNotDone = false);

/// <summary>
/// The single place an <see cref="Outcome"/> is decided. Success is only ever claimed for changes a
/// verification re-read confirmed:
/// <list type="bullet">
/// <item>any change reported ok that verification does not confirm -> <see cref="Outcome.VerificationFailed"/></item>
/// <item>nothing attempted and nothing required left undone -> <see cref="Outcome.NoChangeNeeded"/></item>
/// <item>every attempted change verified and nothing required left undone -> <see cref="Outcome.Succeeded"/></item>
/// <item>some verified -> <see cref="Outcome.PartiallySucceeded"/>; none -> <see cref="Outcome.Failed"/></item>
/// </list>
/// </summary>
public static class OutcomeRules
{
    public static Outcome Derive(IEnumerable<ItemOutcome> items)
    {
        var list = items.ToList();
        var attempted = list.Where(i => i.Attempted).ToList();
        var notDone = list.Count(i => !i.Attempted && i.RequiredButNotDone);

        if (attempted.Any(i => i.ReportedOk && !i.Verified)) return Outcome.VerificationFailed;
        if (attempted.Count == 0 && notDone == 0) return Outcome.NoChangeNeeded;

        var verified = attempted.Count(i => i.Verified);
        var failed = attempted.Count(i => !i.Verified) + notDone;
        if (failed == 0) return Outcome.Succeeded;
        return verified > 0 ? Outcome.PartiallySucceeded : Outcome.Failed;
    }

    /// <summary>True for outcomes that mean "the requested end state holds".</summary>
    public static bool IsSuccess(Outcome outcome) => outcome is Outcome.Succeeded or Outcome.NoChangeNeeded;
}

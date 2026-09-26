namespace PartnerCenterBridge.Core.Operations;

/// <summary>How one requested item ended up, as input to <see cref="OutcomeRules.Derive"/>.</summary>
/// <param name="Attempted">A change was actually sent.</param>
/// <param name="ReportedOk">The service reported the change as done (or already in place).</param>
/// <param name="Verified">A post-change re-read confirmed the desired state.</param>
/// <param name="RequiredButNotDone">
/// Not attempted, but the operator asked for it and it did not happen (blocked, gated, unsupported).
/// Counts against success; plain "nothing to do" skips must pass false.
/// </param>
/// <param name="Unverifiable">
/// The change was reported ok but nothing can confirm it by re-reading (not a verification
/// failure, not a verified success). Only meaningful with <paramref name="Verified"/> false.
/// </param>
public readonly record struct ItemOutcome(
    bool Attempted, bool ReportedOk, bool Verified, bool RequiredButNotDone = false, bool Unverifiable = false);

/// <summary>
/// The single place an <see cref="Outcome"/> is decided. Success is only ever claimed for changes a
/// verification re-read confirmed:
/// <list type="bullet">
/// <item>any change reported ok that verification contradicts or could not confirm (and that is not
/// unverifiable by nature) -> <see cref="Outcome.VerificationFailed"/></item>
/// <item>nothing attempted and nothing required left undone -> <see cref="Outcome.NoChangeNeeded"/></item>
/// <item>every attempted change verified and nothing required left undone -> <see cref="Outcome.Succeeded"/></item>
/// <item>as above, but at least one change reported ok is unverifiable -> <see cref="Outcome.CompletedUnverified"/></item>
/// <item>some verified (or reported ok but unverifiable) -> <see cref="Outcome.PartiallySucceeded"/>; none -> <see cref="Outcome.Failed"/></item>
/// </list>
/// </summary>
public static class OutcomeRules
{
    public static Outcome Derive(IEnumerable<ItemOutcome> items)
    {
        var list = items.ToList();
        var attempted = list.Where(i => i.Attempted).ToList();
        var notDone = list.Count(i => !i.Attempted && i.RequiredButNotDone);

        if (attempted.Any(i => i.ReportedOk && !i.Verified && !i.Unverifiable)) return Outcome.VerificationFailed;
        if (attempted.Count == 0 && notDone == 0) return Outcome.NoChangeNeeded;

        var verified = attempted.Count(i => i.Verified);
        var unverifiable = attempted.Count(i => i.ReportedOk && !i.Verified && i.Unverifiable);
        var failed = attempted.Count(i => !i.ReportedOk) + notDone;
        if (failed == 0) return unverifiable > 0 ? Outcome.CompletedUnverified : Outcome.Succeeded;
        return verified + unverifiable > 0 ? Outcome.PartiallySucceeded : Outcome.Failed;
    }

    /// <summary>
    /// True for outcomes that mean "the requested end state holds" (confirmed). A
    /// <see cref="Outcome.CompletedUnverified"/> run is deliberately not counted: nothing confirmed it.
    /// </summary>
    public static bool IsSuccess(Outcome outcome) => outcome is Outcome.Succeeded or Outcome.NoChangeNeeded;
}

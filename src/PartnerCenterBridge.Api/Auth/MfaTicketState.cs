namespace PartnerCenterBridge.Api.Auth;

/// <summary>
/// Pending TOTP challenge state (password already verified, second factor not yet entered).
/// Mutable and stored by reference in <see cref="ChallengeCache"/> so incrementing
/// <see cref="Attempts"/> in <see cref="Controllers.TotpController.Challenge"/> updates the same
/// cache entry in place -- no separate re-store call needed.
/// </summary>
public class MfaTicketState
{
    public const int MaxAttempts = 5;

    public required Guid UserId { get; init; }
    public int Attempts { get; set; }
}

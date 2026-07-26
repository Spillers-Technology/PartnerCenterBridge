namespace PartnerCenterBridge.Api.Auth;

/// <summary>Which auth plane this running instance was started with -- resolved once in Program.cs and injected wherever a controller needs to gate on it (e.g. <see cref="Controllers.AuthController"/> refusing to issue local tokens outside <see cref="Local"/> mode).</summary>
public record AuthModeInfo(string Mode)
{
    public const string Oidc = "Oidc";
    public const string Local = "Local";
    public const string Dev = "Dev";

    public bool IsLocal => Mode == Local;
}

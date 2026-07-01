namespace PartnerCenterBridge.Graph;

/// <summary>Tunables for the Intune Win32 flow. Bound from the <c>Intune</c> config section.</summary>
public class IntuneOptions
{
    public const string SectionName = "Intune";

    /// <summary>
    /// Base URL for Graph beta calls. Overridable so integration tests can point the whole upload
    /// state machine at a mock server; defaults to the real Graph beta endpoint.
    /// </summary>
    public string GraphBetaBaseUrl { get; set; } = GraphRestClient.DefaultBeta;
}

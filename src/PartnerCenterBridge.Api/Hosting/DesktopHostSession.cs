namespace PartnerCenterBridge.Api.Hosting;

/// <summary>Desktop startup hand-off inside the process; no second API or WebView RPC layer.</summary>
public static class DesktopHostSession
{
    private static TaskCompletionSource<DesktopHostReady>? pending;

    public static Task<DesktopHostReady> Prepare()
    {
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return pending.Task;
    }

    internal static void Started(IServiceProvider services, LocalWorkbenchOptions local) =>
        pending?.TrySetResult(new(services, local.CanonicalUrl));
}

public sealed record DesktopHostReady(IServiceProvider Services, string Url);

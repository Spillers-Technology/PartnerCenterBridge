using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.PartnerCenter;

namespace PartnerCenterBridge.Graph;

/// <summary>
/// Drives the full Graph beta Win32 LOB upload + assignment flow for a single (tenant, template):
/// create app -> content version -> file -> poll for SAS -> block-blob upload -> commit -> poll ->
/// set committed version -> assign. Pass an existing <see cref="Deployment"/> to push a new
/// content version to an app that already exists in the tenant.
///
/// <para><b>Updating an existing app</b> (docs/specs/win32-package-lifecycle.md §8.1). The step-8
/// PATCH that switches the committed content version also carries the fields the template owns
/// (<see cref="OwnedFieldPatch"/>), so a changed install command or detection rule ships with the
/// content it belongs to. Everything else on the app -- install experience, architectures, return
/// codes, requirement rules -- is left as the tenant has it. Known limitation: if the app has
/// <c>activeInstallScript</c>/<c>activeUninstallScript</c> set, those override the command lines
/// and patching the command lines has no effect; this is not detected.</para>
///
/// <para><b>Assignments</b> are applied only until the deployment first succeeds.
/// <c>/assign</c> replaces the app's entire assignment set, so calling it on every redeploy would
/// silently revert assignment changes made in the Intune portal. Changing assignments on an
/// existing app is a separate operation, not a side effect of a content push.</para>
/// </summary>
public class IntuneWin32Service : IIntuneWin32Service
{
    private readonly ITokenProvider _tokens;
    private readonly IIntuneWinPackageReader _reader;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<IntuneWin32Service> _log;
    private readonly string _graphBetaBaseUrl;

    public IntuneWin32Service(
        ITokenProvider tokens,
        IIntuneWinPackageReader reader,
        IHttpClientFactory httpFactory,
        IOptions<IntuneOptions> options,
        ILogger<IntuneWin32Service> log)
    {
        _tokens = tokens;
        _reader = reader;
        _httpFactory = httpFactory;
        _graphBetaBaseUrl = options.Value.GraphBetaBaseUrl;
        _log = log;
    }

    public async Task<Deployment> DeployAsync(
        Tenant tenant,
        AppTemplate template,
        Stream intuneWinPackage,
        Deployment? existing = null,
        IDeploymentProgress? progress = null,
        Func<Deployment, CancellationToken, Task>? checkpoint = null,
        CancellationToken ct = default)
    {
        var deployment = existing ?? new Deployment { AppTemplateId = template.Id, TenantId = tenant.Id };
        // LastSyncedAt is only set once a deploy has completed, assignment included, so a deployment
        // without it has never been assigned -- including one whose app was created before a crash.
        var initialAssignmentPending = deployment.LastSyncedAt is null;

        async Task Stage(DeploymentStatus status, string detail)
        {
            deployment.Status = status;
            progress?.Report(status, detail);
            if (checkpoint is not null)
                await checkpoint(deployment, ct);
        }

        try
        {
            var token = await _tokens.GetAccessTokenAsync(tenant.TenantId, Resources.Graph, ct);
            var http = _httpFactory.CreateClient("graph");
            var graph = new GraphRestClient(http, token, _graphBetaBaseUrl);

            var content = await _reader.ReadMetadataAsync(intuneWinPackage, ct);

            // 1. Create (or reuse) the app.
            string appId;
            var isUpdate = deployment.IntuneAppId is not null;
            if (deployment.IntuneAppId is { } id)
            {
                appId = id;
            }
            else
            {
                await Stage(DeploymentStatus.Pending, "Creating app");
                using var created = await graph.PostAsync("/deviceAppManagement/mobileApps", BuildWin32App(template, content), ct);
                appId = created.RootElement.GetProperty("id").GetString()!;
                deployment.IntuneAppId = appId;
                // Persist the id now, so a retry after a later failure reuses this app instead of
                // creating a duplicate.
                if (checkpoint is not null)
                    await checkpoint(deployment, ct);
            }

            var basePath = $"/deviceAppManagement/mobileApps/{appId}/microsoft.graph.win32LobApp";

            // 2. Content version + 3. file entry.
            await Stage(DeploymentStatus.Uploading, "Creating content version");
            using var cv = await graph.PostAsync($"{basePath}/contentVersions", new { }, ct);
            var contentVersionId = cv.RootElement.GetProperty("id").GetString()!;

            using var fileResp = await graph.PostAsync(
                $"{basePath}/contentVersions/{contentVersionId}/files",
                new
                {
                    odataType = "#microsoft.graph.mobileAppContentFile",
                    name = content.FileName,
                    size = content.Size,
                    sizeEncrypted = content.SizeEncrypted,
                    isDependency = false
                }.ToGraph(),
                ct);
            var fileId = fileResp.RootElement.GetProperty("id").GetString()!;
            var filePath = $"{basePath}/contentVersions/{contentVersionId}/files/{fileId}";

            // 4. Wait for the Azure Blob SAS URI.
            var sasUri = await WaitForStateAsync(graph, filePath, "azureStorageUriRequestSuccess", "azureStorageUri", ct);

            // 5. Upload the encrypted payload.
            progress?.Report(DeploymentStatus.Uploading, "Uploading package");
            var uploader = new AzureBlobUploader(http);
            await using (var payload = await _reader.OpenEncryptedPayloadAsync(intuneWinPackage, ct))
            {
                await uploader.UploadAsync(payload, sasUri,
                    async c =>
                    {
                        using var _ = await graph.PostAsync($"{filePath}/renewUpload", new { }, c);
                        return await WaitForStateAsync(graph, filePath, "azureStorageUriRenewalSuccess", "azureStorageUri", c);
                    },
                    ct);
            }

            // 6. Commit with the file encryption info + 7. wait for success.
            await Stage(DeploymentStatus.Committing, "Committing");
            using (var _ = await graph.PostAsync($"{filePath}/commit", new { fileEncryptionInfo = BuildEncryptionInfo(content) }, ct))
            { }
            await WaitForStateAsync(graph, filePath, "commitFileSuccess", null, ct);

            // 8. Point the app at the committed content version. On an update the same PATCH
            //    carries the template-owned fields, so new content never runs under an old
            //    command line or an old detection rule that still matches the previous version.
            var patch = isUpdate
                ? OwnedFieldPatch(template, content)
                : new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.win32LobApp" };
            patch["committedContentVersion"] = contentVersionId;
            using (var _ = await graph.PatchAsync($"/deviceAppManagement/mobileApps/{appId}", patch, ct))
            { }
            deployment.CommittedContentVersionId = contentVersionId;

            // 9. Assign -- only until the deployment first succeeds (see the class remarks).
            if (initialAssignmentPending)
            {
                await Stage(DeploymentStatus.Assigning, "Assigning");
                using (var _ = await graph.PostAsync(
                    $"/deviceAppManagement/mobileApps/{appId}/assign",
                    new { mobileAppAssignments = template.Assignments.Select(BuildAssignment).ToArray() },
                    ct))
                { }
            }

            deployment.DeployedTemplateVersion = template.ContentVersion;
            deployment.Status = DeploymentStatus.Succeeded;
            deployment.LastError = null;
            deployment.LastSyncedAt = DateTimeOffset.UtcNow;
            progress?.Report(DeploymentStatus.Succeeded);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Win32 deploy failed for tenant {Tenant} template {Template}", tenant.TenantId, template.DisplayName);
            deployment.Status = DeploymentStatus.Failed;
            deployment.LastError = ex.Message;
            progress?.Report(DeploymentStatus.Failed, ex.Message);
        }
        return deployment;
    }

    /// <summary>Poll a file resource until it reaches <paramref name="targetState"/>; optionally return a property value.</summary>
    private static async Task<string> WaitForStateAsync(
        GraphRestClient graph, string filePath, string targetState, string? returnProperty, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var doc = await graph.GetAsync(filePath, ct);
            var root = doc.RootElement;
            var state = root.TryGetProperty("uploadState", out var s) ? s.GetString() : null;

            if (state == targetState)
                return returnProperty is not null && root.TryGetProperty(returnProperty, out var v)
                    ? v.GetString() ?? string.Empty
                    : string.Empty;

            if (state is not null && state.EndsWith("Failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Intune reported upload state '{state}' while waiting for '{targetState}'.");

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException($"Timed out waiting for upload state '{targetState}'.");
    }

    private static object BuildWin32App(AppTemplate t, Win32ContentInfo c) => new Dictionary<string, object?>
    {
        ["@odata.type"] = "#microsoft.graph.win32LobApp",
        ["displayName"] = t.DisplayName,
        ["description"] = t.Description ?? t.DisplayName,
        ["publisher"] = t.Publisher ?? "Unknown",
        ["installCommandLine"] = t.InstallCommandLine,
        ["uninstallCommandLine"] = t.UninstallCommandLine,
        ["applicableArchitectures"] = "x64",
        ["setupFilePath"] = c.FileName,
        ["fileName"] = c.FileName,
        ["installExperience"] = new { runAsAccount = "system", deviceRestartBehavior = "suppress" },
        ["detectionRules"] = t.DetectionRules.Select(BuildDetectionRule).ToArray(),
        ["returnCodes"] = DefaultReturnCodes()
    };

    /// <summary>
    /// The fields <see cref="AppTemplate"/> actually models, and nothing else. Deliberately not
    /// <see cref="BuildWin32App"/>: the create body also hardcodes run-as account, restart
    /// behavior, architecture, and return codes, and re-sending those on update would overwrite
    /// values an admin adjusted in the portal (spec §8.1).
    /// </summary>
    internal static Dictionary<string, object?> OwnedFieldPatch(AppTemplate t, Win32ContentInfo c) => new()
    {
        ["@odata.type"] = "#microsoft.graph.win32LobApp",
        ["displayName"] = t.DisplayName,
        ["description"] = t.Description ?? t.DisplayName,
        ["publisher"] = t.Publisher ?? "Unknown",
        ["installCommandLine"] = t.InstallCommandLine,
        ["uninstallCommandLine"] = t.UninstallCommandLine,
        ["setupFilePath"] = c.FileName,
        ["detectionRules"] = t.DetectionRules.Select(BuildDetectionRule).ToArray(),
    };

    private static object BuildDetectionRule(DetectionRule r) => r.Type switch
    {
        DetectionRuleType.MsiProductCode => new Dictionary<string, object?>
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppProductCodeDetection",
            ["productCode"] = r.ProductCode,
            ["productVersionOperator"] = r.ProductVersion is null ? "notConfigured" : "greaterThanOrEqual",
            ["productVersion"] = r.ProductVersion
        },
        DetectionRuleType.File => new Dictionary<string, object?>
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppFileSystemDetection",
            ["path"] = r.Path,
            ["fileOrFolderName"] = r.FileOrKeyName,
            ["check32BitOn64System"] = r.Check32BitOn64System,
            ["detectionType"] = "exists"
        },
        DetectionRuleType.Registry => new Dictionary<string, object?>
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppRegistryDetection",
            ["keyPath"] = r.Path,
            ["valueName"] = r.FileOrKeyName,
            ["check32BitOn64System"] = r.Check32BitOn64System,
            ["detectionType"] = "exists"
        },
        DetectionRuleType.PowerShellScript => new Dictionary<string, object?>
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppPowerShellScriptDetection",
            ["scriptContent"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(r.ScriptContent ?? string.Empty)),
            ["enforceSignatureCheck"] = r.EnforceSignatureCheck,
            ["runAs32Bit"] = r.RunAs32Bit
        },
        _ => throw new NotSupportedException($"Detection rule type {r.Type} is not supported.")
    };

    private static object[] DefaultReturnCodes() => new object[]
    {
        new { returnCode = 0, type = "success" },
        new { returnCode = 1707, type = "success" },
        new { returnCode = 3010, type = "softReboot" },
        new { returnCode = 1641, type = "hardReboot" },
        new { returnCode = 1618, type = "retry" }
    };

    private static object BuildEncryptionInfo(Win32ContentInfo c) => new
    {
        encryptionKey = c.EncryptionKey,
        macKey = c.MacKey,
        initializationVector = c.InitializationVector,
        mac = c.Mac,
        profileIdentifier = c.ProfileIdentifier,
        fileDigest = c.FileDigest,
        fileDigestAlgorithm = c.FileDigestAlgorithm
    };

    private static object BuildAssignment(AssignmentSpec a)
    {
        object target = a.TargetType switch
        {
            AssignmentTargetType.AllDevices => new { odataType = "#microsoft.graph.allDevicesAssignmentTarget" }.ToGraph(),
            AssignmentTargetType.AllLicensedUsers => new { odataType = "#microsoft.graph.allLicensedUsersAssignmentTarget" }.ToGraph(),
            AssignmentTargetType.Group => new Dictionary<string, object?>
            {
                ["@odata.type"] = "#microsoft.graph.groupAssignmentTarget",
                ["groupId"] = a.GroupId
            },
            _ => throw new NotSupportedException()
        };
        return new Dictionary<string, object?>
        {
            ["@odata.type"] = "#microsoft.graph.mobileAppAssignment",
            ["intent"] = a.Intent.ToString().ToLowerInvariant(),
            ["target"] = target
        };
    }
}

/// <summary>Helper to rename an anonymous type's <c>odataType</c> to the Graph <c>@odata.type</c> key.</summary>
internal static class GraphBodyExtensions
{
    public static Dictionary<string, object?> ToGraph(this object anonymous)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var p in anonymous.GetType().GetProperties())
        {
            var key = p.Name == "odataType" ? "@odata.type" : p.Name;
            dict[key] = p.GetValue(anonymous);
        }
        return dict;
    }
}

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.PartnerCenter;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// Drives the entire Graph beta Win32 upload state machine against a WireMock server standing in
/// for Graph + Azure Blob, asserting the orchestration walks the documented steps and records a
/// successful deployment. This is the "assert the exact call sequence" coverage for the upload flow.
/// </summary>
public class IntuneWin32ServiceTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    private const string DetectionXml =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <ApplicationInfo xmlns="http://schemas.microsoft.com/Metadata">
          <Name>7-Zip</Name>
          <UnencryptedContentSize>32</UnencryptedContentSize>
          <FileName>IntunePackage.intunewin</FileName>
          <SetupFile>7z.msi</SetupFile>
          <EncryptionInfo>
            <EncryptionKey>QUJD</EncryptionKey><MacKey>REVG</MacKey>
            <InitializationVector>R0hJ</InitializationVector><Mac>SktM</Mac>
            <ProfileIdentifier>ProfileVersion1</ProfileIdentifier>
            <FileDigest>TU5P</FileDigest><FileDigestAlgorithm>SHA256</FileDigestAlgorithm>
          </EncryptionInfo>
        </ApplicationInfo>
        """;

    private static MemoryStream BuildPackage()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("IntuneWinPackage/Metadata/Detection.xml").Open())
                s.Write(Encoding.UTF8.GetBytes(DetectionXml));
            using (var s = zip.CreateEntry("IntuneWinPackage/Contents/IntunePackage.intunewin").Open())
                s.Write(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));
        }
        ms.Position = 0;
        return ms;
    }

    private const string AppPath = "/deviceAppManagement/mobileApps";
    private const string Win32 = "/microsoft.graph.win32LobApp";

    private void StubGraphFlow()
    {
        const string filePath = AppPath + "/app1" + Win32 + "/contentVersions/cv1/files/file1";
        var blobUri = _server.Url + "/blob/container/pkg?sig=abc";

        // 1. create app (skipped entirely when updating an existing app)
        _server.Given(Request.Create().WithPath(AppPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id = "app1" }));

        // 2. content version -- every deploy makes exactly one, so it initialises the scenario
        //    (state s0) for both create and update -- + 3. file (state-independent)
        _server.Given(Request.Create().WithPath(AppPath + "/app1" + Win32 + "/contentVersions").UsingPost())
            .InScenario("upload").WillSetStateTo("s0")
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id = "cv1" }));
        _server.Given(Request.Create().WithPath(AppPath + "/app1" + Win32 + "/contentVersions/cv1/files").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id = "file1" }));

        // 4. first file poll -> SAS ready. Advances state so the post-commit poll differs.
        _server.Given(Request.Create().WithPath(filePath).UsingGet())
            .InScenario("upload").WhenStateIs("s0").WillSetStateTo("s1")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { uploadState = "azureStorageUriRequestSuccess", azureStorageUri = blobUri }));

        // 5. blob block + blocklist (state-independent)
        _server.Given(Request.Create().WithPath("/blob/container/pkg").WithParam("comp", "block").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(201));
        _server.Given(Request.Create().WithPath("/blob/container/pkg").WithParam("comp", "blocklist").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(201));

        // 6. commit (state-independent)
        _server.Given(Request.Create().WithPath(filePath + "/commit").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        // 7. second file poll (after commit) -> commitFileSuccess
        _server.Given(Request.Create().WithPath(filePath).UsingGet())
            .InScenario("upload").WhenStateIs("s1")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { uploadState = "commitFileSuccess" }));

        // 8. set committed content version + 9. assign
        _server.Given(Request.Create().WithPath(AppPath + "/app1").UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(204));
        _server.Given(Request.Create().WithPath(AppPath + "/app1/assign").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
    }

    private IntuneWin32Service CreateService(IHttpClientFactory? http = null)
    {
        var options = Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! });
        return new IntuneWin32Service(
            new FakeTokenProvider(),
            new IntuneWinPackageReader(),
            http ?? new SingleHttpClientFactory(),
            options,
            NullLogger<IntuneWin32Service>.Instance);
    }

    /// <summary>Counts outgoing requests, in order, without touching the mock server mid-flow.</summary>
    private sealed class CountingHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public List<string> Requests { get; } = new();

        public CountingHttpClientFactory() => _client = new HttpClient(new Counter(Requests));
        public HttpClient CreateClient(string name) => _client;

        private sealed class Counter(List<string> requests) : DelegatingHandler(new HttpClientHandler())
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
                return base.SendAsync(request, ct);
            }
        }
    }

    [Fact]
    public async Task Deploy_walks_full_upload_sequence_and_succeeds()
    {
        StubGraphFlow();
        var service = CreateService();
        var tenant = new Tenant { TenantId = "contoso.onmicrosoft.com", DisplayName = "Contoso" };
        var template = new AppTemplate
        {
            DisplayName = "7-Zip", InstallCommandLine = "i", UninstallCommandLine = "u", ContentVersion = 4,
            Assignments = { new AssignmentSpec { TargetType = AssignmentTargetType.AllDevices, Intent = InstallIntent.Required } }
        };

        using var package = BuildPackage();
        var deployment = await service.DeployAsync(tenant, template, package);

        Assert.True(deployment.Status == DeploymentStatus.Succeeded, $"Deploy failed: {deployment.LastError}");
        Assert.Equal("app1", deployment.IntuneAppId);
        Assert.Equal("cv1", deployment.CommittedContentVersionId);
        Assert.Equal(4, deployment.DeployedTemplateVersion);
        Assert.Null(deployment.LastError);

        // The whole documented sequence was exercised.
        var log = _server.LogEntries.Select(e =>
            $"{e.RequestMessage.Method} {e.RequestMessage.Path}").ToList();
        Assert.Contains($"POST {AppPath}", log);
        Assert.Contains($"POST {AppPath}/app1{Win32}/contentVersions/cv1/files/file1/commit", log);
        Assert.Contains($"PATCH {AppPath}/app1", log);
        Assert.Contains($"POST {AppPath}/app1/assign", log);
    }

    private static AppTemplate UpdatedTemplate() => new()
    {
        DisplayName = "7-Zip", Publisher = "Igor Pavlov",
        InstallCommandLine = "msiexec /i 7z-v2.msi /qn", UninstallCommandLine = "msiexec /x 7z-v2.msi /qn",
        ContentVersion = 5,
        DetectionRules = { new DetectionRule { Type = DetectionRuleType.MsiProductCode, ProductCode = "{V2}", ProductVersion = "24.08" } },
        Assignments = { new AssignmentSpec { TargetType = AssignmentTargetType.AllDevices, Intent = InstallIntent.Required } },
    };

    private List<string> CallLog() => _server.LogEntries
        .Select(e => $"{e.RequestMessage.Method} {e.RequestMessage.Path}").ToList();

    private JsonElement PatchBody() => JsonDocument.Parse(_server.LogEntries
        .Single(e => e.RequestMessage.Method == "PATCH" && e.RequestMessage.Path == AppPath + "/app1")
        .RequestMessage.Body!).RootElement;

    [Fact]
    public async Task Update_patches_owned_fields_with_the_new_content_and_does_not_reassign()
    {
        StubGraphFlow();
        var existing = new Deployment
        {
            IntuneAppId = "app1", CommittedContentVersionId = "cv0", DeployedTemplateVersion = 4,
            Status = DeploymentStatus.Succeeded, LastSyncedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };

        using var package = BuildPackage();
        var deployment = await CreateService().DeployAsync(
            new Tenant { TenantId = "contoso.onmicrosoft.com", DisplayName = "Contoso" }, UpdatedTemplate(), package, existing);

        Assert.True(deployment.Status == DeploymentStatus.Succeeded, $"Deploy failed: {deployment.LastError}");
        Assert.Equal("cv1", deployment.CommittedContentVersionId);
        Assert.Equal(5, deployment.DeployedTemplateVersion);

        var log = CallLog();
        Assert.DoesNotContain($"POST {AppPath}", log);          // no duplicate app
        Assert.DoesNotContain($"POST {AppPath}/app1/assign", log); // D3: portal assignment changes survive

        // D1: one PATCH carries the new content version and every template-owned field...
        var body = PatchBody();
        Assert.Equal("#microsoft.graph.win32LobApp", body.GetProperty("@odata.type").GetString());
        Assert.Equal("cv1", body.GetProperty("committedContentVersion").GetString());
        Assert.Equal("7-Zip", body.GetProperty("displayName").GetString());
        Assert.Equal("7-Zip", body.GetProperty("description").GetString());
        Assert.Equal("Igor Pavlov", body.GetProperty("publisher").GetString());
        Assert.Equal("msiexec /i 7z-v2.msi /qn", body.GetProperty("installCommandLine").GetString());
        Assert.Equal("msiexec /x 7z-v2.msi /qn", body.GetProperty("uninstallCommandLine").GetString());
        Assert.Equal("7z.msi", body.GetProperty("setupFilePath").GetString()); // the reader's FileName is the setup file
        var rule = Assert.Single(body.GetProperty("detectionRules").EnumerateArray());
        Assert.Equal("{V2}", rule.GetProperty("productCode").GetString());
        Assert.Equal("24.08", rule.GetProperty("productVersion").GetString());

        // ...and nothing the template does not model, so portal-adjusted values are preserved.
        var sent = body.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[]
        {
            "@odata.type", "committedContentVersion", "description", "detectionRules", "displayName",
            "installCommandLine", "publisher", "setupFilePath", "uninstallCommandLine",
        }, sent);
    }

    [Fact]
    public async Task Retry_after_a_crash_between_create_and_assign_reuses_the_app_and_still_assigns()
    {
        StubGraphFlow();
        // What a checkpoint leaves behind when the process dies after create: an app id, a
        // non-terminal status, and no completed sync.
        var existing = new Deployment { IntuneAppId = "app1", Status = DeploymentStatus.Uploading };

        using var package = BuildPackage();
        var deployment = await CreateService().DeployAsync(
            new Tenant { TenantId = "t", DisplayName = "T" }, UpdatedTemplate(), package, existing);

        Assert.True(deployment.Status == DeploymentStatus.Succeeded, $"Deploy failed: {deployment.LastError}");
        var log = CallLog();
        Assert.DoesNotContain($"POST {AppPath}", log);
        Assert.Contains($"POST {AppPath}/app1/assign", log);
    }

    [Fact]
    public async Task Create_sends_the_full_app_once_and_patches_only_the_content_version()
    {
        StubGraphFlow();
        using var package = BuildPackage();
        await CreateService().DeployAsync(new Tenant { TenantId = "t", DisplayName = "T" }, UpdatedTemplate(), package);

        var body = PatchBody();
        Assert.Equal(new[] { "@odata.type", "committedContentVersion" }, body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Contains($"POST {AppPath}/app1/assign", CallLog());
    }

    [Fact]
    public async Task Checkpoints_record_each_stage_and_the_app_id_before_the_next_remote_write()
    {
        StubGraphFlow();
        var http = new CountingHttpClientFactory();
        var seen = new List<(string Stage, int CallsSoFar)>();

        using var package = BuildPackage();
        var deployment = await CreateService(http).DeployAsync(
            new Tenant { TenantId = "t", DisplayName = "T" }, UpdatedTemplate(), package,
            checkpoint: (d, _) =>
            {
                seen.Add(($"{d.Status}:{d.IntuneAppId ?? "-"}", http.Requests.Count));
                return Task.CompletedTask;
            });

        Assert.True(deployment.Status == DeploymentStatus.Succeeded,
            $"Deploy failed: {deployment.LastError} after {string.Join(" | ", http.Requests)} / {string.Join(" | ", seen)}");
        Assert.Equal(new[] { "Pending:-", "Pending:app1", "Uploading:app1", "Committing:app1", "Assigning:app1" },
            seen.Select(x => x.Stage));

        // Each checkpoint lands before the first call of the stage it announces.
        int IndexOf(string call) => http.Requests.IndexOf(call);
        var calls = seen.Select(x => x.CallsSoFar).ToArray();
        Assert.Equal(IndexOf($"POST {AppPath}"), calls[0]);
        Assert.Equal(IndexOf($"POST {AppPath}/app1{Win32}/contentVersions"), calls[1]); // id saved right after create
        Assert.Equal(calls[1], calls[2]);
        Assert.Equal(IndexOf($"POST {AppPath}/app1{Win32}/contentVersions/cv1/files/file1/commit"), calls[3]);
        Assert.Equal(IndexOf($"POST {AppPath}/app1/assign"), calls[4]);
    }

    [Fact]
    public async Task Update_checkpoints_skip_the_assign_stage()
    {
        StubGraphFlow();
        var statuses = new List<DeploymentStatus>();
        var existing = new Deployment { IntuneAppId = "app1", Status = DeploymentStatus.Succeeded, LastSyncedAt = DateTimeOffset.UtcNow };

        using var package = BuildPackage();
        await CreateService().DeployAsync(
            new Tenant { TenantId = "t", DisplayName = "T" }, UpdatedTemplate(), package, existing,
            checkpoint: (d, _) => { statuses.Add(d.Status); return Task.CompletedTask; });

        Assert.Equal(new[] { DeploymentStatus.Uploading, DeploymentStatus.Committing }, statuses);
    }

    [Fact]
    public async Task Deploy_marks_failed_when_graph_rejects()
    {
        _server.Given(Request.Create().WithPath(AppPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody("Forbidden"));
        var service = CreateService();

        using var package = BuildPackage();
        var deployment = await service.DeployAsync(
            new Tenant { TenantId = "t", DisplayName = "T" },
            new AppTemplate { DisplayName = "X", InstallCommandLine = "i", UninstallCommandLine = "u" },
            package);

        Assert.Equal(DeploymentStatus.Failed, deployment.Status);
        Assert.NotNull(deployment.LastError);
    }
}

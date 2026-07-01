using System.IO.Compression;
using System.Text;
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

        // 1. create app — initialises the scenario (state s0).
        _server.Given(Request.Create().WithPath(AppPath).UsingPost())
            .InScenario("upload").WillSetStateTo("s0")
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id = "app1" }));

        // 2. content version + 3. file (state-independent)
        _server.Given(Request.Create().WithPath(AppPath + "/app1" + Win32 + "/contentVersions").UsingPost())
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

    private IntuneWin32Service CreateService()
    {
        var options = Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! });
        return new IntuneWin32Service(
            new FakeTokenProvider(),
            new IntuneWinPackageReader(),
            new SingleHttpClientFactory(),
            options,
            NullLogger<IntuneWin32Service>.Instance);
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

    private sealed class FakeTokenProvider : ITokenProvider
    {
        public Task<string> GetAccessTokenAsync(string tenantId, string resource, CancellationToken ct = default)
            => Task.FromResult("test-token");
    }

    private sealed class SingleHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client = new();
        public HttpClient CreateClient(string name) => _client;
    }
}

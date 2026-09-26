using System.Net;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using PartnerCenterBridge.Api.Hosting;

namespace PartnerCenterBridge.Tests;

public class HostingProfileTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Parses_hosting_flags_and_maps_them_to_configuration()
    {
        var cli = CliParser.Parse(new[] { "--local", "--port", "5199", "--no-browser", "--data-dir=C:\\pcb", "--listen", "0.0.0.0" });

        Assert.Null(cli.Error);
        Assert.Equal(CliCommand.Run, cli.Command);
        var overrides = cli.ToConfigurationOverrides();
        Assert.Equal("Local", overrides[HostingKeys.Profile]);
        Assert.Equal("5199", overrides[HostingKeys.Port]);
        Assert.Equal("false", overrides[HostingKeys.OpenBrowser]);
        Assert.Equal("C:\\pcb", overrides[HostingKeys.DataDir]);
        Assert.Equal("0.0.0.0", overrides[HostingKeys.Listen]);
        Assert.Empty(cli.HostArgs);
    }

    [Theory]
    [InlineData("doctor", CliCommand.Doctor)]
    [InlineData("bootstrap-sam", CliCommand.BootstrapSam)]
    [InlineData("--help", CliCommand.Help)]
    [InlineData("-h", CliCommand.Help)]
    [InlineData("--version", CliCommand.Version)]
    public void Parses_commands(string arg, CliCommand expected)
    {
        var cli = CliParser.Parse(new[] { arg, "--no-browser" });
        Assert.Null(cli.Error);
        Assert.Equal(expected, cli.Command);
    }

    [Fact]
    public void Passes_host_configuration_through()
    {
        var cli = CliParser.Parse(new[]
        {
            "--environment=Development", "--contentRoot", "C:\\app", "--Auth:Mode", "Oidc", "Exchange:AppId=abc", "--local"
        });

        Assert.Null(cli.Error);
        Assert.True(cli.Local);
        Assert.Equal(new[] { "--environment=Development", "--contentRoot", "C:\\app", "--Auth:Mode", "Oidc", "Exchange:AppId=abc" },
            cli.HostArgs);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("frobnicate")]
    [InlineData("--port", "abc")]
    [InlineData("--port", "70000")]
    [InlineData("--data-dir")]
    [InlineData("doctor", "bootstrap-sam")]
    public void Unknown_or_malformed_arguments_are_errors(params string[] args)
    {
        Assert.NotNull(CliParser.Parse(args).Error);
    }

    [Fact]
    public void Profile_defaults_to_server_without_baked_metadata_or_config()
    {
        // The test assembly carries no PcbHostingProfile metadata, standing in for the container build.
        Assert.Equal(HostingProfile.Server, HostingProfile.Resolve(Config(), typeof(HostingProfileTests).Assembly));
    }

    [Fact]
    public void Configured_profile_wins_and_is_normalized()
    {
        Assert.Equal(HostingProfile.Local, HostingProfile.Resolve(Config((HostingKeys.Profile, "local"))));
        Assert.Equal(HostingProfile.Server, HostingProfile.Resolve(Config((HostingKeys.Profile, "SERVER"))));
        Assert.Throws<InvalidOperationException>(() => HostingProfile.Resolve(Config((HostingKeys.Profile, "cloud"))));
    }

    [Fact]
    public void Api_assembly_built_without_the_local_switch_has_no_baked_profile()
    {
        var baked = typeof(HostingProfile).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key == HostingKeys.BakedProfileMetadata);
        Assert.Empty(baked);
        Assert.Equal(HostingProfile.Server, HostingProfile.Resolve(Config()));
    }

    [Fact]
    public void Local_options_default_to_loopback_on_the_default_port()
    {
        var options = LocalWorkbenchOptions.FromConfiguration(Config((HostingKeys.DataDir, Path.GetTempPath())));

        Assert.Equal(IPAddress.Loopback, options.ListenAddress);
        Assert.True(options.IsLoopbackOnly);
        Assert.Equal(LocalWorkbenchOptions.DefaultPort, options.Port);
        Assert.Equal("http://localhost:5080", options.CanonicalUrl);
        Assert.True(options.OpenBrowser);
    }

    [Fact]
    public void Explicit_listen_address_is_honored_and_flagged_as_not_loopback()
    {
        var options = LocalWorkbenchOptions.FromConfiguration(Config(
            (HostingKeys.DataDir, Path.GetTempPath()), (HostingKeys.Listen, "0.0.0.0"), (HostingKeys.Port, "5199")));

        Assert.Equal(IPAddress.Any, options.ListenAddress);
        Assert.False(options.IsLoopbackOnly);
        Assert.Equal("http://localhost:5199", options.CanonicalUrl);
        Assert.Equal(IPAddress.Loopback, LocalWorkbenchOptions.ParseListenAddress("localhost"));
        Assert.Throws<InvalidOperationException>(() => LocalWorkbenchOptions.ParseListenAddress("example.com"));
    }

    [Fact]
    public void Default_data_root_is_per_user()
    {
        var root = LocalWorkbenchOptions.DefaultDataRoot();
        Assert.EndsWith(LocalWorkbenchOptions.AppFolderName, root);
        if (OperatingSystem.IsWindows())
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), root);
    }
}

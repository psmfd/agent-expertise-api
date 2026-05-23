using ExpertiseApi.Auth;
using Microsoft.Extensions.Hosting;

namespace ExpertiseApi.Tests.Unit;

public class AuthModeStartupGuardTests
{
    [Theory]
    [InlineData("Production", AuthMode.ApiKey)]
    [InlineData("Production", AuthMode.LocalDev)]
    [InlineData("Production", AuthMode.Hybrid)]
    [InlineData("Staging", AuthMode.ApiKey)]
    [InlineData("Staging", AuthMode.Hybrid)]
    public void EnforceModeGuard_NonOidcOutsideDevelopment_Throws(string env, AuthMode mode)
    {
        var environment = new HostingEnvironment { EnvironmentName = env };

        var act = () => AuthExtensions.EnforceModeGuard(mode, environment);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*Auth:Mode '{mode}'*Development*");
    }

    [Theory]
    [InlineData(AuthMode.Oidc)]
    [InlineData(AuthMode.LocalDev)]
    [InlineData(AuthMode.ApiKey)]
    [InlineData(AuthMode.Hybrid)]
    public void EnforceModeGuard_AnyMode_IsPermittedInDevelopment(AuthMode mode)
    {
        var environment = new HostingEnvironment { EnvironmentName = Environments.Development };

        var act = () => AuthExtensions.EnforceModeGuard(mode, environment);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void EnforceModeGuard_OidcMode_BootsInAnyEnvironment(string env)
    {
        var environment = new HostingEnvironment { EnvironmentName = env };

        var act = () => AuthExtensions.EnforceModeGuard(AuthMode.Oidc, environment);

        act.Should().NotThrow();
    }

    [Fact]
    public void ParseAuthMode_DefaultsToHybridInDevelopment()
    {
        var environment = new HostingEnvironment { EnvironmentName = Environments.Development };

        var mode = AuthExtensions.ParseAuthMode(null, environment);

        mode.Should().Be(AuthMode.Hybrid);
    }

    [Fact]
    public void ParseAuthMode_DefaultsToOidcOutsideDevelopment()
    {
        var environment = new HostingEnvironment { EnvironmentName = "Production" };

        var mode = AuthExtensions.ParseAuthMode(null, environment);

        mode.Should().Be(AuthMode.Oidc);
    }

    [Theory]
    [InlineData("oidc", AuthMode.Oidc)]
    [InlineData("OIDC", AuthMode.Oidc)]
    [InlineData("Hybrid", AuthMode.Hybrid)]
    [InlineData("apikey", AuthMode.ApiKey)]
    [InlineData("LocalDev", AuthMode.LocalDev)]
    public void ParseAuthMode_IsCaseInsensitive(string input, AuthMode expected)
    {
        var environment = new HostingEnvironment { EnvironmentName = Environments.Development };

        var mode = AuthExtensions.ParseAuthMode(input, environment);

        mode.Should().Be(expected);
    }

    [Fact]
    public void ParseAuthMode_RejectsUnknownValues()
    {
        var environment = new HostingEnvironment { EnvironmentName = Environments.Development };

        var act = () => AuthExtensions.ParseAuthMode("garbage", environment);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*not a recognized mode*");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    public void EnforceOidcIssuersGuard_OidcWithZeroIssuers_ThrowsInAnyEnvironment(string env)
    {
        // Argument env is documentation-only — the guard does not consult IHostEnvironment.
        // We assert the failure mode is environment-agnostic so the misconfiguration
        // surfaces loudly even in Development where a developer might assume the
        // missing issuers will be ignored.
        _ = env;

        var act = () => AuthExtensions.EnforceOidcIssuersGuard(
            AuthMode.Oidc,
            Array.Empty<OidcIssuerOptions>());

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*requires at least one valid Auth:Oidc:Issuers entry*");
    }

    [Fact]
    public void EnforceOidcIssuersGuard_OidcWithIssuers_DoesNotThrow()
    {
        var issuers = new[]
        {
            new OidcIssuerOptions
            {
                Name = "Test",
                Issuer = "https://example.com",
                Audience = "test-aud"
            }
        };

        var act = () => AuthExtensions.EnforceOidcIssuersGuard(AuthMode.Oidc, issuers);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(AuthMode.LocalDev)]
    [InlineData(AuthMode.ApiKey)]
    [InlineData(AuthMode.Hybrid)]
    public void EnforceOidcIssuersGuard_NonOidcMode_DoesNotThrow_EvenWithZeroIssuers(AuthMode mode)
    {
        // Hybrid in particular: zero issuers is a legitimate Development configuration
        // where ApiKeyAuthHandler is the fallback scheme.
        var act = () => AuthExtensions.EnforceOidcIssuersGuard(
            mode,
            Array.Empty<OidcIssuerOptions>());

        act.Should().NotThrow();
    }

    private class HostingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

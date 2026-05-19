using System.Text.Encodings.Web;
using ExpertiseApi.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ExpertiseApi.Tests.Unit;

public class ApiKeyAuthHandlerLoggingTests
{
    private static (ApiKeyAuthHandler handler, FakeLogCollector collector) BuildHandler(
        string? configuredKey = "test-key",
        Action<HttpContext>? configureContext = null)
    {
        var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        options.CurrentValue.Returns(new AuthenticationSchemeOptions());

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddFakeLogging());
        var sp = services.BuildServiceProvider();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var collector = sp.GetRequiredService<FakeLogCollector>();

        var configEntries = configuredKey is not null
            ? new Dictionary<string, string?> { ["Auth:ApiKey"] = configuredKey }
            : new Dictionary<string, string?>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configEntries)
            .Build();

        var handler = new ApiKeyAuthHandler(
            options,
            loggerFactory,
            UrlEncoder.Default,
            config,
            new StaticAgentUaOptionsMonitor());

        var context = new DefaultHttpContext();
        configureContext?.Invoke(context);

        var scheme = new AuthenticationScheme(
            ApiKeyAuthHandler.SchemeName,
            displayName: null,
            typeof(ApiKeyAuthHandler));

        handler.InitializeAsync(scheme, context).GetAwaiter().GetResult();

        return (handler, collector);
    }

    [Fact]
    public async Task HandleAuthenticate_WhenApiKeyNotConfigured_LogsWarning()
    {
        var (handler, collector) = BuildHandler(configuredKey: null);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        var logs = collector.GetSnapshot();
        logs.Should().ContainSingle(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains("API key not configured on server"));
    }

    [Fact]
    public async Task HandleAuthenticate_WhenMissingAuthHeader_LogsWarning()
    {
        var (handler, collector) = BuildHandler();

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        var logs = collector.GetSnapshot();
        logs.Should().ContainSingle(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains("missing Authorization header"));
    }

    [Fact]
    public async Task HandleAuthenticate_WhenInvalidScheme_LogsWarning()
    {
        var (handler, collector) = BuildHandler(configureContext: ctx =>
            ctx.Request.Headers.Authorization = "Basic dGVzdDp0ZXN0");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        var logs = collector.GetSnapshot();
        logs.Should().ContainSingle(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains("invalid Authorization scheme"));
    }

    [Fact]
    public async Task HandleAuthenticate_WhenInvalidKey_LogsWarning()
    {
        var (handler, collector) = BuildHandler(configureContext: ctx =>
            ctx.Request.Headers.Authorization = "Bearer wrong-key");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        var logs = collector.GetSnapshot();
        logs.Should().ContainSingle(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains("invalid API key"));
    }

    [Fact]
    public async Task HandleAuthenticate_WhenValidKey_DoesNotLogWarning()
    {
        var (handler, collector) = BuildHandler(configureContext: ctx =>
            ctx.Request.Headers.Authorization = "Bearer test-key");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        var logs = collector.GetSnapshot();
        logs.Should().NotContain(r => r.Level == LogLevel.Warning);
    }
}

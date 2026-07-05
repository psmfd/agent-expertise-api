using System.Net;
using System.Text;
using ExpertiseApi.Services.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ExpertiseApi.Tests.Unit;

public class HubTokenClientTests
{
    private sealed class CountingHandler(Func<int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls;
        public HttpRequestMessage? LastRequest;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(Calls);
        }
    }

    private static HubTokenClient BuildClient(CountingHandler handler, SyncOptions options)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        var monitor = Substitute.For<IOptionsMonitor<SyncOptions>>();
        monitor.CurrentValue.Returns(options);

        return new HubTokenClient(factory, monitor, NullLogger<HubTokenClient>.Instance);
    }

    private static HttpResponseMessage TokenResponse(string token, int expiresIn) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"access_token":"{{token}}","expires_in":{{expiresIn}}}""",
            Encoding.UTF8, "application/json"),
    };

    private static SyncOptions Options(string? scope = null) => new()
    {
        Enabled = true,
        HubUrl = "https://hub.example",
        TokenEndpoint = "https://idp.example/token",
        ClientId = "spoke-1",
        ClientSecret = "s3cret",
        TokenScope = scope,
    };

    [Fact]
    public async Task CachesToken_UntilNearExpiry()
    {
        var handler = new CountingHandler(n => TokenResponse($"tok-{n}", expiresIn: 3600));
        using var client = BuildClient(handler, Options());

        var first = await client.GetAccessTokenAsync(CancellationToken.None);
        var second = await client.GetAccessTokenAsync(CancellationToken.None);

        first.Should().Be("tok-1");
        second.Should().Be("tok-1", "a token with 1h of life must be served from cache");
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Refreshes_WhenTokenIsInsideTheExpirySkew()
    {
        // expires_in=30 < the 60s skew — every call must refresh.
        var handler = new CountingHandler(n => TokenResponse($"tok-{n}", expiresIn: 30));
        using var client = BuildClient(handler, Options());

        (await client.GetAccessTokenAsync(CancellationToken.None)).Should().Be("tok-1");
        (await client.GetAccessTokenAsync(CancellationToken.None)).Should().Be("tok-2");
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task SendsClientCredentialsForm_WithOptionalScope()
    {
        var handler = new CountingHandler(_ => TokenResponse("tok", 3600));
        using var client = BuildClient(handler, Options(scope: "expertise.write.draft"));

        await client.GetAccessTokenAsync(CancellationToken.None);

        handler.LastRequest!.RequestUri.Should().Be(new Uri("https://idp.example/token"));
        handler.LastBody.Should().Contain("grant_type=client_credentials")
            .And.Contain("client_id=spoke-1")
            .And.Contain("client_secret=s3cret")
            .And.Contain("scope=expertise.write.draft");
    }

    [Fact]
    public async Task NonSuccessTokenResponse_Throws()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = BuildClient(handler, Options());

        var act = () => client.GetAccessTokenAsync(CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>();
    }
}

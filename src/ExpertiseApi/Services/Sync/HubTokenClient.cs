using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ExpertiseApi.Services.Sync;

/// <summary>
/// Minimal OAuth2 client_credentials token client for spoke→hub sync (ADR-013).
/// Caches the access token until 60 seconds before expiry; single-flight refresh via
/// a semaphore so concurrent ticks (not expected, but cheap to guard) don't stampede
/// the IdP. Deliberately not a general OIDC library: one grant, one endpoint, no
/// discovery — the shared-IdP endpoint is operator-configured (`Sync:TokenEndpoint`).
/// </summary>
internal sealed class HubTokenClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<SyncOptions> options,
    ILogger<HubTokenClient> logger) : IDisposable
{
    internal const string HttpClientName = "hub-token";
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt - ExpirySkew)
            return _cachedToken;

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the lock — another caller may have refreshed.
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt - ExpirySkew)
                return _cachedToken;

            var opts = options.CurrentValue;
            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "client_credentials"),
                new("client_id", opts.ClientId ?? ""),
                new("client_secret", opts.ClientSecret ?? ""),
            };
            if (!string.IsNullOrWhiteSpace(opts.TokenScope))
                form.Add(new("scope", opts.TokenScope));

            using var content = new FormUrlEncodedContent(form);
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(new Uri(opts.TokenEndpoint!), content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var token = JsonSerializer.Deserialize(payload, SyncJsonContext.Default.HubTokenResponse)
                ?? throw new InvalidOperationException("Token endpoint returned an empty body.");

            _cachedToken = token.AccessToken;
            // Defensive floor: an IdP omitting expires_in yields 0 — treat as 5 minutes
            // so the cache still functions instead of refreshing every call.
            var lifetime = token.ExpiresIn > 0 ? TimeSpan.FromSeconds(token.ExpiresIn) : TimeSpan.FromMinutes(5);
            _expiresAt = DateTimeOffset.UtcNow + lifetime;

            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Hub token refreshed; expires {ExpiresAt:O}", _expiresAt);
            return _cachedToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose() => _refreshLock.Dispose();
}

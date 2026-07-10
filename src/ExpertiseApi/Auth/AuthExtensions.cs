using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ExpertiseApi.Auth;

internal static class AuthExtensions
{
    public const string BearerScheme = "Bearer";

    public static IServiceCollection AddExpertiseAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var mode = ParseAuthMode(configuration["Auth:Mode"], environment);
        EnforceModeGuard(mode, environment);

        var issuers = LoadIssuers(configuration);
        EnforceOidcIssuersGuard(mode, issuers);

        services.AddHttpContextAccessor();
        services.AddSingleton<IAuthorizationHandler, ScopeAuthorizationHandler>();

        // Part D C6: bind the User-Agent allowlist for ActorClassResolver corroboration.
        // IOptionsMonitor lets ops adjust the list at runtime without restart.
        services.AddOptions<AgentUserAgentOptions>()
            .Bind(configuration.GetSection(AgentUserAgentOptions.SectionName));

        var authBuilder = services.AddAuthentication(BearerScheme);

        // Always register the policy scheme first so the default scheme exists even if we
        // only end up registering one inner scheme — keeps endpoint configuration uniform.
        authBuilder.AddPolicyScheme(BearerScheme, displayName: null, options =>
        {
            options.ForwardDefaultSelector = ctx => SelectScheme(ctx, mode, issuers);
        });

        if (mode is AuthMode.Oidc or AuthMode.Hybrid)
        {
            foreach (var issuer in issuers)
            {
                // Eagerly load embedded static keys (ADR-015) here, at service-configuration
                // time, so a missing/malformed JwksPath fails startup closed rather than 500ing
                // on the first authenticated request. Discovery-path issuers (JwksPath unset)
                // pass null and fetch lazily via Authority as before.
                var staticKeys = string.IsNullOrWhiteSpace(issuer.JwksPath)
                    ? null
                    : LoadStaticSigningKeys(issuer);
                RegisterJwtBearer(authBuilder, issuer, staticKeys);
            }
        }

        if (mode is AuthMode.LocalDev or AuthMode.Hybrid)
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, LocalDevAuthHandler>(
                LocalDevAuthHandler.SchemeName, null);
        }

        if (mode is AuthMode.ApiKey or AuthMode.Hybrid)
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(
                ApiKeyAuthHandler.SchemeName, null);
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthConstants.Policies.ReadAccess, p =>
                p.AddRequirements(new ScopeRequirement(AuthConstants.ReadScope)))
            .AddPolicy(AuthConstants.Policies.WriteAccess, p =>
                p.AddRequirements(new ScopeRequirement(AuthConstants.WriteDraftScope)))
            .AddPolicy(AuthConstants.Policies.WriteApproveAccess, p =>
                p.AddRequirements(new ScopeRequirement(AuthConstants.WriteApproveScope)))
            .AddPolicy(AuthConstants.Policies.AdminAccess, p =>
                p.AddRequirements(new ScopeRequirement(AuthConstants.AdminScope)));

        return services;
    }

    internal static AuthMode ParseAuthMode(string? value, IHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(value))
            return environment.IsDevelopment() ? AuthMode.Hybrid : AuthMode.Oidc;

        return Enum.TryParse<AuthMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Auth:Mode '{value}' is not a recognized mode. Valid values: " +
                $"{string.Join(", ", Enum.GetNames<AuthMode>())}.");
    }

    internal static void EnforceModeGuard(AuthMode mode, IHostEnvironment environment)
    {
        if (mode == AuthMode.Oidc) return;
        if (environment.IsDevelopment()) return;

        throw new InvalidOperationException(
            $"Auth:Mode '{mode}' is only permitted when ASPNETCORE_ENVIRONMENT='Development'. " +
            $"Current environment: '{environment.EnvironmentName}'. " +
            "Set Auth:Mode to 'Oidc' for non-Development deployments.");
    }

    /// <summary>
    /// Fails startup loudly when <c>Auth:Mode=Oidc</c> is configured but no valid
    /// <c>Auth:Oidc:Issuers</c> entries are loaded. Without this guard the API boots
    /// successfully and 500s on the first authenticated request — a deployment-day
    /// footgun where <c>/health</c> and <c>/metrics</c> look green while every
    /// protected endpoint is broken. Fires in any environment because explicit
    /// <c>Auth:Mode=Oidc</c> with zero issuers is misconfiguration regardless of where.
    ///
    /// Build-time exception: when invoked under the <c>dotnet-getdocument</c> tool
    /// (Microsoft.Extensions.ApiDescription.Server, Part D C8), the host runs against
    /// source-controlled placeholder configuration that intentionally has no real OIDC
    /// issuers. The guard's purpose — catching production misconfigurations — cannot
    /// manifest in that context because nothing is actually being deployed, so it is
    /// bypassed via the entry-assembly check. The runtime behaviour is unchanged.
    /// </summary>
    internal static void EnforceOidcIssuersGuard(AuthMode mode, IReadOnlyList<OidcIssuerOptions> issuers)
    {
        if (mode != AuthMode.Oidc) return;
        if (issuers.Count > 0) return;
        if (IsBuildTimeOpenApiContext()) return;

        throw new InvalidOperationException(
            "Auth:Mode='Oidc' requires at least one valid Auth:Oidc:Issuers entry. " +
            "Found zero — either the configured list is empty, or every entry's Issuer is " +
            "blank or starts with '<TODO' (placeholder values are filtered at load time). " +
            "Either populate Auth:Oidc:Issuers with real issuer URLs, or change Auth:Mode " +
            "(LocalDev/ApiKey/Hybrid permitted only in Development).");
    }

    /// <summary>
    /// Detects whether the current host is being constructed by the
    /// <c>Microsoft.Extensions.ApiDescription.Server</c> build-time OpenAPI document
    /// generator (Part D C8). The tool loads the API assembly in a subprocess whose
    /// entry assembly is <c>dotnet-getdocument</c> / <c>GetDocument.Insider</c>;
    /// production deployments — Kestrel, systemd, IIS, Windows Service — all have
    /// our own assembly as the entry. Used to bypass production-only startup guards
    /// that would otherwise reject the source-controlled placeholder configuration.
    /// </summary>
    internal static bool IsBuildTimeOpenApiContext()
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        return entry is "dotnet-getdocument" or "GetDocument.Insider";
    }

    internal static IReadOnlyList<OidcIssuerOptions> LoadIssuers(IConfiguration configuration)
    {
        var issuers = configuration.GetSection("Auth:Oidc:Issuers")
            .Get<List<OidcIssuerOptions>>() ?? [];

        // Strip placeholder entries (TODO markers in source-controlled configs) — they would
        // otherwise fail JwtBearer setup with confusing discovery errors.
        return issuers
            .Where(i => !string.IsNullOrWhiteSpace(i.Issuer)
                        && !i.Issuer.StartsWith("<TODO", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static void RegisterJwtBearer(
        AuthenticationBuilder builder,
        OidcIssuerOptions issuer,
        IList<SecurityKey>? staticSigningKeys)
    {
        builder.AddJwtBearer(issuer.Name, options =>
        {
            options.Audience = issuer.Audience;
            options.MapInboundClaims = false; // keep `sub`, `scp`, etc. as-is for parsing

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = issuer.Issuer,
                ValidAudiences = [issuer.Audience, .. issuer.AdditionalAudiences],
                NameClaimType = "sub"
            };

            if (staticSigningKeys is not null)
            {
                // ADR-015 (Option D): embedded static JWKS. Preload the configuration with the
                // issuer + signing keys. Because Authority/MetadataAddress are unset, the
                // framework's JwtBearerPostConfigureOptions wraps this Configuration in a
                // StaticConfigurationManager (nulling ConfigurationManager here just prevents a
                // discovery-backed one) — GetConfigurationAsync then performs no I/O, so there
                // is no `.well-known`/`jwks_uri` fetch, no HTTPS metadata endpoint to stand up,
                // and no internal-CA root to trust on the API host. `Authority` is deliberately unset.
                var configuration = new OpenIdConnectConfiguration { Issuer = issuer.Issuer };
                foreach (var key in staticSigningKeys)
                    configuration.SigningKeys.Add(key);
                options.Configuration = configuration;
                options.ConfigurationManager = null;
                options.TokenValidationParameters.IssuerSigningKeys = staticSigningKeys;
            }
            else
            {
                // Discovery path (cloud issuers): Authority drives lazy `.well-known` + JWKS
                // fetch. RequireHttpsMetadata stays at its default (true).
                options.Authority = issuer.Issuer;
            }

            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = ctx => JwtTenantContextEvents.BuildTenantContext(ctx, issuer)
            };
        });
    }

    /// <summary>
    /// Loads and validates an embedded-key issuer's JWKS file (ADR-015, Option D). Runs at
    /// service-configuration time so any problem fails startup closed — a deliberate parallel
    /// to <see cref="EnforceOidcIssuersGuard"/>: a networked instance must never boot green
    /// (<c>/health</c>, <c>/metrics</c>) while its only issuer's keys are missing or malformed
    /// and every protected request would 500.
    /// </summary>
    internal static IList<SecurityKey> LoadStaticSigningKeys(OidcIssuerOptions issuer)
    {
        string json;
        try
        {
            json = File.ReadAllText(issuer.JwksPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Auth:Oidc issuer '{issuer.Name}' JwksPath '{issuer.JwksPath}' could not be read: {ex.Message} " +
                "Provide a readable jwks.json for an embedded-key (ADR-015) issuer, or remove JwksPath to use " +
                "HTTPS discovery via Authority.", ex);
        }

        JsonWebKeySet keySet;
        try
        {
            keySet = new JsonWebKeySet(json);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException(
                $"Auth:Oidc issuer '{issuer.Name}' JwksPath '{issuer.JwksPath}' is not a valid JWKS document: " +
                $"{ex.Message}", ex);
        }

        // Reject private-key material. The API loads a JWKS only to *validate* signatures, so it
        // must be public-only. This fails closed on the operator footgun of pointing JwksPath at
        // mint_token.py's `<client>.priv.json` (or its key dir) instead of the `build-jwks` output
        // — which would otherwise load a token-forging private key into the network-facing process.
        // `d` covers RSA and EC private keys; `p`/`q` are RSA CRT components.
        foreach (var jwk in keySet.Keys)
        {
            if (!string.IsNullOrEmpty(jwk.D) || !string.IsNullOrEmpty(jwk.P) || !string.IsNullOrEmpty(jwk.Q))
            {
                throw new InvalidOperationException(
                    $"Auth:Oidc issuer '{issuer.Name}' JwksPath '{issuer.JwksPath}' contains PRIVATE key material " +
                    $"(kid '{jwk.Kid}'). The API must load a PUBLIC-only JWKS — run 'mint_token.py build-jwks' to " +
                    "produce one; never point JwksPath at a *.priv.json file or the private key directory.");
            }
        }

        var keys = keySet.GetSigningKeys();

        if (keys.Count == 0)
        {
            throw new InvalidOperationException(
                $"Auth:Oidc issuer '{issuer.Name}' JwksPath '{issuer.JwksPath}' contains no signing keys. " +
                "A JWKS with at least one RSA/EC signing key is required for an embedded-key issuer.");
        }

        return keys;
    }

    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Token-shape detection is best-effort. Any parse failure (malformed token, " +
                        "unrecognized format, unexpected exception in JsonWebTokenHandler) must fall through " +
                        "to the first issuer's scheme so JwtBearer surfaces a clean 401. Throwing here would " +
                        "be a worse user experience than the JwtBearer 401.")]
    private static string SelectScheme(HttpContext ctx, AuthMode mode, IReadOnlyList<OidcIssuerOptions> issuers)
    {
        var header = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return FallbackSchemeForMode(mode, issuers);

        var token = header["Bearer ".Length..].Trim();

        if (token.StartsWith(LocalDevAuthHandler.TokenPrefix, StringComparison.OrdinalIgnoreCase)
            && (mode is AuthMode.LocalDev or AuthMode.Hybrid))
        {
            return LocalDevAuthHandler.SchemeName;
        }

        // Three dot-delimited base64url segments → JWT. Forward to the matching issuer's
        // scheme; if no issuer matches the token's `iss`, forward to the first registered
        // issuer scheme so JwtBearer surfaces a clean 401.
        if (LooksLikeJwt(token) && (mode is AuthMode.Oidc or AuthMode.Hybrid) && issuers.Count > 0)
        {
            try
            {
                var handler = new JsonWebTokenHandler();
                if (handler.CanReadToken(token))
                {
                    var parsed = handler.ReadJsonWebToken(token);
                    var match = issuers.FirstOrDefault(i =>
                        string.Equals(i.Issuer, parsed.Issuer, StringComparison.Ordinal));
                    if (match is not null)
                        return match.Name;
                }
            }
            catch (Exception ex) when (ex is SecurityTokenException
                                          or ArgumentException
                                          or FormatException)
            {
                // JWT could not be parsed (malformed, bad base64, missing claims, etc.).
                // Fall through to first scheme; JwtBearer will reject cleanly.
                //
                // Narrowed from `catch (Exception)` to satisfy CodeQL
                // cs/catch-of-all-exceptions. Process-fatal exceptions
                // (OutOfMemoryException, AccessViolationException) propagate
                // by exclusion — do not widen without re-triage. Covered surface:
                //   * SecurityTokenException — base of all
                //     Microsoft.IdentityModel.Tokens.SecurityToken*Exception
                //     (Malformed, InvalidSignature, Expired, etc.).
                //   * ArgumentException     — ReadJsonWebToken null/empty input.
                //   * FormatException       — base64url decode failure on a
                //     malformed segment that slipped past LooksLikeJwt.
            }
            return issuers[0].Name;
        }

        if (mode is AuthMode.ApiKey or AuthMode.Hybrid)
            return ApiKeyAuthHandler.SchemeName;

        return FallbackSchemeForMode(mode, issuers);
    }

    /// <summary>
    /// Picks a registered scheme to forward to when the request has no recognizable bearer
    /// shape. Must never return <see cref="BearerScheme"/> — that's the policy scheme name
    /// itself, which would create an infinite forward loop.
    /// </summary>
    private static string FallbackSchemeForMode(AuthMode mode, IReadOnlyList<OidcIssuerOptions> issuers) => mode switch
    {
        AuthMode.ApiKey => ApiKeyAuthHandler.SchemeName,
        AuthMode.LocalDev => LocalDevAuthHandler.SchemeName,
        AuthMode.Oidc => issuers.Count > 0
            ? issuers[0].Name
            : throw new InvalidOperationException(
                "Auth:Mode=Oidc with zero issuers reached request-time fallback — " +
                "this should be unreachable because EnforceOidcIssuersGuard fails at startup."),
        AuthMode.Hybrid => issuers.Count > 0 ? issuers[0].Name : ApiKeyAuthHandler.SchemeName,
        _ => throw new InvalidOperationException($"Unknown Auth:Mode '{mode}'.")
    };

    private static bool LooksLikeJwt(string token)
    {
        var dots = 0;
        foreach (var c in token)
        {
            if (c == '.') dots++;
            if (dots > 2) return false;
        }
        return dots == 2;
    }
}

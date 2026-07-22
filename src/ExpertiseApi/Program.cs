#pragma warning disable SKEXP0070

using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ExpertiseApi.Auth;
using ExpertiseApi.Cli;
using ExpertiseApi.Data;
using ExpertiseApi.Diagnostics;
using ExpertiseApi.Endpoints;
using ExpertiseApi.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using Prometheus;
using Serilog;
using System.Net;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

// NOTE (#404): the content root — and with it appsettings*.json and wwwroot/ —
// follows the process CWD for a framework-dependent launch. The A2 service
// templates and launch wrapper therefore pin WorkingDirectory / cd to bin/.
// Pinning ContentRootPath = AppContext.BaseDirectory here instead was tried
// and rejected: it defeats WebApplicationFactory's content-root override and
// breaks the integration suite's wwwroot resolution.
var builder = WebApplication.CreateBuilder(args);

// Service-host integration. Both calls are no-ops when the corresponding host
// environment is absent, so Docker / Helm / `dotnet run` paths remain unchanged.
// Enables the Archetype A2 install scripts under scripts/ (systemd `--user`
// unit on Linux, launchd LaunchAgent on macOS, Windows Service on Windows) to
// integrate with the .NET host's lifecycle (Type=notify on systemd, SCM
// signals on Windows). See README § "Archetype A2: native OS service".
builder.Host.UseSystemd();
builder.Host.UseWindowsService(options => options.ServiceName = "ExpertiseApi");

builder.Host.UseSerilog((context, services, config) =>
    config.ReadFrom.Configuration(context.Configuration)
          .ReadFrom.Services(services));

// Default HostOptions.ShutdownTimeout is 5s — insufficient under load to drain
// in-flight HTTP requests, close the Npgsql connection pool, and dispose the
// ONNX inference session before systemd / launchd / SCM escalates to SIGKILL.
// 30s matches the explicit TimeoutStopSec=45 / ExitTimeOut=45 budgets in the
// service templates under scripts/service-templates/ (issue #142).
//
// MUST be registered AFTER UseSystemd() / UseWindowsService() above: the
// Windows lifetime internally registers an IConfigureOptions<HostOptions>
// that overwrites ShutdownTimeout with the SCM-reported stop timeout (~20s),
// and DI options-pattern resolution honours last-writer-wins. Moving this
// block above those lines will silently regress the Windows service path.
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

// Output caching for the OpenAPI document. AddOpenApi does NOT cache by default
// (each request walks the schema graph and runs every IOpenApiDocumentTransformer),
// and the endpoint is anonymous + un-rate-limited per the discovery contract — a
// 5-minute output cache stops anonymous spec-fetch loops from becoming a CPU
// amplifier without compromising downstream tooling that polls infrequently.
// Reference: https://learn.microsoft.com/aspnet/core/performance/caching/output
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("openapi-discovery", policy => policy
        .Cache()
        .Expire(TimeSpan.FromMinutes(5))
        .SetVaryByHost(true));

    // Issue #158 mitigation (2/2): bound the DoS amplification of
    // /health/ready by capping concurrent probes to one underlying check
    // execution per 2-second window. The endpoint is AllowAnonymous (k8s
    // readinessProbe contract); without this cache an attacker can fan-in
    // thousands of probes and multiply each by AddDbContextCheck's
    // CanConnectAsync round-trip plus any other tagged check.
    //
    // Tenant-agnostic + request-header-agnostic cache key so a per-client
    // header (User-Agent, X-Forwarded-For) cannot explode the cache into
    // per-client entries. SetVaryByHost(false) is the explicit default;
    // pinned here for documentation. SetVaryByQuery([]) drops the
    // (defensive) default of varying by query so /health/ready?cb=<rand>
    // cannot defeat the cache.
    //
    // CACHING SEMANTICS NOTE: OutputCachePolicyBuilder.Cache() stores only
    // SUCCESSFUL (200) responses by default. 503 responses (DB outage,
    // pending migrations) are NOT cached. Outage-path amplification is
    // therefore bounded by OutputCache's per-key single-flight (default
    // LockingPolicy.Enabled) — one in-flight check pipeline at a time per
    // cache key, with subsequent requests sharing the result — NOT by the
    // 2s window. Recovery is immediate (no cached 503 to expire).
    //
    // FUTURE-MAINTAINER GUARD: if /health/ready ever gains
    // .RequireAuthorization() (replacing .AllowAnonymous() in
    // HealthEndpoints.cs), the cache policy below MUST either be removed
    // or extended with SetVaryByValue keyed on the principal. The current
    // ASP.NET Core pipeline (UseAuthorization -> ... -> UseOutputCache)
    // protects against unauthenticated cache serving today, but
    // cross-principal sharing of the cached body would still be incorrect.
    // See https://learn.microsoft.com/en-us/aspnet/core/performance/caching/output#authorization
    //
    // 2s window:
    //   * shorter than the Helm readinessProbe period (10s default), so a
    //     steady-state prober never sees a stale response in flight.
    //   * longer than typical inter-probe arrival within a DoS burst (sub-ms),
    //     reducing steady-state amplification to 1 DB round-trip per pod per 2s
    //     regardless of incoming RPS — satisfies the issue's "≤ 10× baseline".
    //   * tolerable recovery latency on a real outage: 200-cache window
    //     applies pre-outage; the outage itself is not cached.
    options.AddPolicy("health-ready", policy => policy
        .Cache()
        .Expire(TimeSpan.FromSeconds(2))
        .SetVaryByHost(false)
        .SetVaryByQuery(Array.Empty<string>()));
});

// AddOpenApi registers the document(s) consumed by both runtime MapOpenApi() and the
// build-time _GenerateOpenApiDocuments target (Part D C8). The build-time output
// (artifacts/openapi/ExpertiseApi.json) is OpenAPI 3.1.1 by the .NET 10 default — the
// `OpenApiOptions.OpenApiVersion` knob in Microsoft.AspNetCore.OpenApi 10.0.7 does NOT
// currently propagate to the build-time emitter (verified 2026-05-18). Downstream
// consumers in the integration backlog (#147 skill = plain curl/JSON; #148 pi extension
// = TypeScript codegen) are 3.1-compatible. If a 3.0-only consumer surfaces later, pin
// here and verify the emitter honours it on the then-current SDK.
//
// BearerSecuritySchemeTransformer advertises the JWT Bearer scheme on every secured
// operation so Scalar UI, codegen, and LLM agents know how to authenticate (#146).
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<ExpertiseApi.OpenApi.BearerSecuritySchemeTransformer>();
    options.AddDocumentTransformer<ExpertiseApi.OpenApi.IdempotencyKeyDocumentTransformer>();
});

// ProblemDetails sanitization (Part D C4). Always emit a traceId extension so
// client-side error reports can be cross-referenced to server logs; outside
// Development, scrub Detail/Instance to prevent exception-message / connection-
// string / stack-frame leakage in 5xx responses (LLM02). See Part D C4 in
// docs/security/integration-threat-model.md and ADR / threat-model rationale.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? ctx.HttpContext.TraceIdentifier;
        ctx.ProblemDetails.Extensions["traceId"] = traceId;
        ctx.ProblemDetails.Instance ??= ctx.HttpContext.Request.Path.Value;

        var env = ctx.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
        if (!env.IsDevelopment())
        {
            ctx.ProblemDetails.Detail = null;
            ctx.ProblemDetails.Instance = null;
            ctx.ProblemDetails.Extensions.Remove("exception");
        }

        // Part D C7 (extension via ADR-008): hygienize the validation 'errors' extension
        // on ProblemDetails responses. This is where minimal-API model binding emits
        // user-shaped messages that an attacker can influence (e.g. by sending crafted
        // input that the binder echoes verbatim). Title and Detail are NOT hygienized
        // here — they are server-authored strings ("Too Many Requests", "Concurrent
        // modification", "Domain, Title, Body, and Source are required.") whose exact
        // text is part of the API contract. Wrapping them would (1) break consumers
        // that match on the literal title for rejection-class detection and (2) hide
        // the value behind a nonce-bearing wrapper when the value is deterministic and
        // safe by construction. The C4 scrub above already nulls Detail in non-Dev.
        var hygiene = ctx.HttpContext.RequestServices.GetService<ExpertiseApi.Hygiene.IResponseHygiene>();
        if (hygiene is null)
            return;

        if (ctx.ProblemDetails.Extensions.TryGetValue("errors", out var errorsObj)
            && errorsObj is IDictionary<string, string[]> errors)
        {
            var nonce = hygiene.MintNonce();
            var sanitized = new Dictionary<string, string[]>(errors.Count, StringComparer.Ordinal);
            foreach (var (field, messages) in errors)
            {
                sanitized[field] = messages
                    .Select(m => hygiene.Hygienize(
                            m, ExpertiseApi.Hygiene.ContentClass.UserSuppliedFreeText, nonce)
                        .Value ?? string.Empty)
                    .ToArray();
            }
            ctx.ProblemDetails.Extensions["errors"] = sanitized;
            ctx.ProblemDetails.Extensions["_hygiene"] = hygiene.GetManifest(nonce);
        }
    };
});

// Typed IExceptionHandler (.NET 8+ preferred shape). Logs full exception
// server-side; returns false so the default IProblemDetailsService writer
// produces the response body (and the customizer above fires).
builder.Services.AddExceptionHandler<UnhandledExceptionLogger>();

// Rate limiting (Part D C5). Three policies, per-principal partitioning with IP
// fallback for unauthenticated paths. 429 responses route through the
// IProblemDetailsService so the C4 customizer fires (correlation traceId, etc.)
// and Retry-After is populated from the lease metadata.
//   - expertise-read     fixed window 60/min  GET /expertise/* (non-semantic), GET /audit/*
//   - expertise-write    fixed window 10/min  POST/PATCH/DELETE on writes
//   - semantic-search    token bucket 10/min  /expertise/search/semantic (expensive: ONNX)
// Health endpoints (/health/*) opt out via DisableRateLimiting in HealthEndpoints.
builder.Services.AddRateLimiter(rateOptions =>
{
    rateOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateOptions.OnRejected = async (ctx, ct) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            ctx.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        var problems = ctx.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = ctx.HttpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests",
                Type = "https://tools.ietf.org/html/rfc6585#section-4",
            },
        }).ConfigureAwait(false);
    };

    // Partition key resolution. Order matters: with MapInboundClaims=false on our JWT
    // bearer scheme (Auth/AuthExtensions.cs) the OIDC `sub` claim is NOT mapped to
    // .NET-style ClaimTypes.NameIdentifier, so the first branch is dead under our
    // current config and the `sub` branch is the load-bearing one. Order kept this way
    // as a defensive default: if a future maintainer toggles MapInboundClaims or wires
    // a non-OIDC scheme that populates NameIdentifier, partitioning remains correct
    // without code changes.
    //
    // IP fallback is dormant under the current endpoint map (every RequireRateLimiting
    // route sits behind UseAuthentication/UseAuthorization so `sub` is always present;
    // /health/* are DisableRateLimiting'd; /metrics and /query carry no rate-limit
    // policy). If a future endpoint is given RequireRateLimiting with AllowAnonymous,
    // the IP fallback becomes load-bearing and partition isolation depends on the
    // ForwardedHeaders:KnownNetworks CIDR list being narrowly scoped — a misconfigured
    // 0.0.0.0/0 would let off-network callers rotate partitions via X-Forwarded-For.
    static string PartitionKey(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? http.User.FindFirstValue("sub")
        ?? http.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";

    rateOptions.AddPolicy("expertise-read", http =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: PartitionKey(http),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

    rateOptions.AddPolicy("expertise-write", http =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: PartitionKey(http),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

    rateOptions.AddPolicy("semantic-search", http =>
        RateLimitPartition.GetTokenBucketLimiter(
            partitionKey: PartitionKey(http),
            factory: _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 10,
                TokensPerPeriod = 10,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddDbContext<ExpertiseDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        o => o.UseVector()));

// Health checks (issue #143). All readiness signals carry the "ready" tag so
// HealthEndpoints.MapHealthEndpoints can filter /health/ready to dependency
// checks while keeping /health/live as a tag-free liveness signal.
//   - AddDbContextCheck: pings the configured Npgsql connection. Cheaper than
//     AddNpgSql and avoids an extra DI service registration; failure mode is
//     ~connection-timeout-bounded (Npgsql default 15s, suitable for readiness).
//   - OnnxModelHealthCheck: DI resolvability of IEmbeddingGenerator (proxy for
//     "model/vocab files were present at startup"; the SemanticKernel
//     registration is conditional on File.Exists and loads the model eagerly,
//     so DI resolution is the meaningful observable at probe time).
//   - PendingMigrationHealthCheck: HealthStatus.Degraded when EF Core reports
//     unapplied migrations. The framework default maps Degraded → 200 OK; the
//     readyOptions registration in HealthEndpoints.cs explicitly overrides
//     ResultStatusCodes so Degraded surfaces as 503.
//
//     Per-probe cost is O(1): the actual EF query runs in
//     MigrationStateRefresher (IHostedService, 5-minute cadence + one-shot
//     at startup), and the health check is a thin volatile read of the
//     singleton MigrationState snapshot. Issue #158 (decouples /health/ready
//     from per-probe DB round-trips).
string[] readyTag = ["ready"];
builder.Services.AddSingleton<ExpertiseApi.Services.Health.MigrationState>();
builder.Services.AddSingleton<ExpertiseApi.Services.Health.IMigrationState>(sp =>
    sp.GetRequiredService<ExpertiseApi.Services.Health.MigrationState>());
builder.Services.AddHostedService<ExpertiseApi.Services.Health.MigrationStateRefresher>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ExpertiseDbContext>(
        name: "db",
        tags: readyTag)
    .AddCheck<ExpertiseApi.Services.Health.OnnxModelHealthCheck>(
        name: "onnx",
        tags: readyTag)
    .AddCheck<ExpertiseApi.Services.Health.PendingMigrationHealthCheck>(
        name: "migrations",
        tags: readyTag);

builder.Services.AddScoped<IExpertiseRepository, ExpertiseRepository>();
builder.Services.AddScoped<ITenantContextAccessor, HttpTenantContextAccessor>();
builder.Services.AddExpertiseAuth(builder.Configuration, builder.Environment);

// Part D C7 — response hygiene. Always-on for /expertise/* read responses per ADR-008.
// Singleton: compiled regexes in PiiDetector/InjectionHeuristic + thread-safe nonce
// provider (RandomNumberGenerator). No per-request state on the orchestrator.
builder.Services.AddSingleton<ExpertiseApi.Hygiene.INonceProvider, ExpertiseApi.Hygiene.NonceProvider>();
builder.Services.AddSingleton<ExpertiseApi.Hygiene.IResponseHygiene, ExpertiseApi.Hygiene.ResponseHygiene>();

// Part D C3 — Idempotency-Key handling (ADR-010).
//
// Dedicated NpgsqlDataSource singleton — owns its own connection pool, sized
// small (the idempotency path is low-volume vs the request workload). Kept
// distinct from the EF Core DbContext's internal data source so the raw-SQL
// store cannot accidentally enlist into an EF change-tracker scope and so a
// DbContext misconfiguration does not regress the idempotency reservation
// transaction. PgBouncer-safe: NpgsqlIdempotencyStore opens one connection +
// one transaction per method call.
builder.Services.AddSingleton<Npgsql.NpgsqlDataSource>(sp =>
{
    var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required for the idempotency store.");
    var dsBuilder = new Npgsql.NpgsqlDataSourceBuilder(cs);
    // Keep the pool narrow — three POSTs, one txn each, low qps.
    return dsBuilder.Build();
});
builder.Services.Configure<ExpertiseApi.Services.Idempotency.IdempotencyOptions>(
    builder.Configuration.GetSection("Idempotency"));
builder.Services.AddSingleton<ExpertiseApi.Services.Idempotency.IIdempotencyStore,
                              ExpertiseApi.Services.Idempotency.NpgsqlIdempotencyStore>();
// Endpoint filter is resolved per-request from DI but the dependencies are
// singletons; using AddSingleton here makes the architecture-test assertion
// (no scoped state in the filter) trivially true.
builder.Services.AddSingleton<ExpertiseApi.Endpoints.Filters.IdempotencyEndpointFilter>();
builder.Services.AddHostedService<ExpertiseApi.Services.Idempotency.IdempotencyGcService>();

// ---------------------------------------------------------------------------
// Aggregator up-sync (ADR-013). The Sync section is ALWAYS bound: the hub role
// only needs Sync:KnownInstances (consumed by the create endpoints for
// OriginInstanceId attribution) with Enabled=false. The spoke machinery —
// resilient HttpClients, token client, worker — registers only when enabled.
// Validation is a manual startup guard (repo convention, like the auth guards):
// a misconfigured spoke must fail at boot, not at first tick.
// ---------------------------------------------------------------------------
builder.Services.Configure<ExpertiseApi.Services.Sync.SyncOptions>(
    builder.Configuration.GetSection("Sync"));
if (builder.Configuration.GetValue<bool>("Sync:Enabled", false))
{
    string[] requiredSyncKeys = ["Sync:HubUrl", "Sync:TokenEndpoint", "Sync:ClientId", "Sync:ClientSecret"];
    var missingSyncKeys = requiredSyncKeys
        .Where(k => string.IsNullOrWhiteSpace(builder.Configuration[k]))
        .ToList();
    if (missingSyncKeys.Count > 0)
    {
        throw new InvalidOperationException(
            $"Sync:Enabled=true requires {string.Join(", ", missingSyncKeys)} to be set. " +
            "Supply Sync__ClientSecret via the environment (secrets.env / k8s secret), never appsettings.json.");
    }

    foreach (var urlKey in new[] { "Sync:HubUrl", "Sync:TokenEndpoint" })
    {
        if (!Uri.TryCreate(builder.Configuration[urlKey], UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{urlKey} must be an absolute http(s) URL.");
        }
    }

    builder.Services.AddHttpClient(ExpertiseApi.Services.Sync.ExpertiseSyncWorker.HttpClientName)
        .AddStandardResilienceHandler();
    builder.Services.AddHttpClient(ExpertiseApi.Services.Sync.HubTokenClient.HttpClientName)
        .AddStandardResilienceHandler();
    builder.Services.AddSingleton<ExpertiseApi.Services.Sync.HubTokenClient>();
    builder.Services.AddHostedService<ExpertiseApi.Services.Sync.ExpertiseSyncWorker>();
}

var baseDir = AppContext.BaseDirectory;
var modelPath = builder.Configuration["Onnx:ModelPath"] ?? Path.Combine(baseDir, "models", "model.onnx");
var vocabPath = builder.Configuration["Onnx:VocabPath"] ?? Path.Combine(baseDir, "models", "vocab.txt");

if (File.Exists(modelPath) && File.Exists(vocabPath))
{
    builder.Services.AddBertOnnxEmbeddingGenerator(modelPath, vocabPath);
}
builder.Services.AddScoped<EmbeddingService>();

// Build-time OpenAPI document generation (Microsoft.Extensions.ApiDescription.Server,
// Part D C8) constructs a stripped host with ValidateOnBuild=true. Without ONNX model
// files staged at build time, IEmbeddingGenerator isn't registered and the validation
// rejects EmbeddingService's transitive dependency before any endpoint metadata is read.
// At runtime this validation correctly catches misconfigurations; in the build-time
// host nothing is actually served, so it is safe (and necessary) to disable. See
// ExpertiseApi.Auth.AuthExtensions.IsBuildTimeOpenApiContext.
if (ExpertiseApi.Auth.AuthExtensions.IsBuildTimeOpenApiContext())
{
    builder.Host.UseDefaultServiceProvider(opts =>
    {
        opts.ValidateOnBuild = false;
        opts.ValidateScopes = false;
    });
}

builder.Services.Configure<DeduplicationOptions>(
    builder.Configuration.GetSection("Deduplication"));
builder.Services.AddScoped<DeduplicationService>();

// X-Forwarded-For support for accurate audit IpAddress capture behind ingress / reverse proxy.
// KnownNetworks must be configured via ForwardedHeaders:KnownNetworks (CIDR list) in
// production — without explicit allowlist the middleware trusts only loopback, which means
// audit IpAddress will record the ingress pod IP rather than the real client.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    var configuredCidrs = builder.Configuration
        .GetSection("ForwardedHeaders:KnownNetworks")
        .Get<string[]>()?
        .Where(static cidr => !string.IsNullOrWhiteSpace(cidr))
        .ToArray();

    // Preserve the framework defaults when no allowlist is configured so only loopback is trusted.
    if (configuredCidrs is null || configuredCidrs.Length == 0)
        return;

    var parsedNetworks = new List<System.Net.IPNetwork>(configuredCidrs.Length);
    foreach (var cidr in configuredCidrs)
    {
        if (!System.Net.IPNetwork.TryParse(cidr, out var network))
            throw new InvalidOperationException(
                $"Invalid ForwardedHeaders:KnownNetworks CIDR entry '{cidr}'.");

        parsedNetworks.Add(network);
    }

    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();

    foreach (var network in parsedNetworks)
        options.KnownIPNetworks.Add(network);
});

var app = builder.Build();

// Top-level Serilog flush guard — wraps ALL post-Build() paths (one-shot CLI
// verbs AND the long-running web host) so the Console sink's async buffer is
// always drained before process exit. Without this outer try/finally the CLI
// verb branches (`return;` below) bypass the web-host `finally` at the bottom
// of this file, causing Serilog to swallow every buffered log entry when
// stdout is not a TTY (i.e. piped output, CI, journalctl). Documented pattern:
// https://github.com/serilog/serilog-aspnetcore#two-stage-initialization
// Fixes issue #263.
try
{

if (MigrateCommand.IsMigrateRequested(args))
{
    // Surfaces a non-zero exit code so scripts/install.{sh,ps1} and
    // scripts/migrate.{sh,ps1} can detect failure and abort before restarting
    // the service. Environment.ExitCode is the only viable mechanism because
    // top-level statements use `return;` (void) and switching to `int Main`
    // would conflict with the existing Reembed/Rehash dispatch shape.
    Environment.ExitCode = await MigrateCommand.RunAsync(app);
    return;
}

if (ReembedCommand.IsReembedRequested(args))
{
    await ReembedCommand.RunAsync(app, args);
    return;
}

if (RehashCommand.IsRehashRequested(args))
{
    await RehashCommand.RunAsync(app, args);
    return;
}

if (BackupCommand.IsBackupRequested(args))
{
    // Exit code matters: scripts/expertise-apictl backup aborts (and never signs or
    // encrypts a partial payload) when the export verb fails. Same mechanism as migrate.
    Environment.ExitCode = await BackupCommand.RunAsync(app, args);
    return;
}

if (RestoreCommand.IsRestoreRequested(args))
{
    Environment.ExitCode = await RestoreCommand.RunAsync(app, args);
    return;
}

// ForwardedHeaders must run before authentication so HttpContext.Connection.RemoteIpAddress
// reflects the real client IP when the audit pipeline reads it.
app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseStatusCodePages();
var metricsEnabled = app.Configuration.GetValue<bool>("Metrics:Enabled", true);
if (metricsEnabled)
    app.UseHttpMetrics();
app.UseSerilogRequestLogging();

// OpenAPI document discovery: exposed in ALL environments (#146). The spec is also
// published as a GitHub Release asset (openapi.json + .sha256) via release.yml so
// downstream agents/codegen can pin a version offline; the runtime endpoint serves
// the live deployment's surface for ad-hoc discovery. Anonymous + not rate-limited
// because (1) the spec is non-sensitive (already public via Release assets) and
// (2) downstream tools must discover the API before holding a bearer token. The
// 5-minute OutputCache (policy "openapi-discovery") backstops DoS by amortising
// the per-request schema walk + transformer execution across spec-fetch loops.
app.MapOpenApi()
    .AllowAnonymous()
    .DisableRateLimiting()
    .CacheOutput("openapi-discovery");

if (app.Environment.IsDevelopment())
{
    // Scalar interactive UI stays Development-only — it's a browser-rendered HTML
    // page that, like the /query debug UI below, would invite token-handling
    // anti-patterns in a deployed environment. Downstream consumers should fetch
    // the JSON document from /openapi/v1.json or the Release asset.
    app.MapScalarApiReference();

    // Static-file serving and the /query debug UI are Development-only because
    // wwwroot/query.html stores the bearer token in localStorage, making it
    // exfiltratable via any same-origin XSS (issue #124). wwwroot/ currently
    // contains only query.html; if other static assets are added later they
    // should live outside wwwroot/ or this gate must be revisited.
    app.UseStaticFiles();

    app.MapGet("/query", (IWebHostEnvironment env) =>
            Results.File(Path.Combine(env.WebRootPath, "query.html"), "text/html"))
        .AllowAnonymous()
        .ExcludeFromDescription();
}

// Part D C3 — opt attributed POSTs into request-body buffering BEFORE
// authentication / authorization so the body remains seekable by the time
// the endpoint filter computes the request hash (model binding consumes
// Request.Body otherwise). Must run after the framework's implicit
// UseRouting() so HttpContext.GetEndpoint() returns the matched endpoint;
// MapHealthEndpoints / MapExpertiseEndpoints etc. trigger UseRouting
// internally if it has not been called yet — this UseMiddleware call sits
// in the pipeline after that internal call.
app.UseMiddleware<ExpertiseApi.Endpoints.Filters.IdempotencyRequestBufferingMiddleware>();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseOutputCache();

app.MapHealthEndpoints();
app.MapExpertiseEndpoints();
app.MapSearchEndpoints();
app.MapSemanticSearchEndpoints();
app.MapHybridSearchEndpoints();
app.MapAuditEndpoints();
if (metricsEnabled)
    app.MapMetrics().AllowAnonymous();

try
{
    // Diagnostic log of the effective Kestrel bind addresses (Part D C1).
    // Kestrel already logs "Now listening on: ..." at Info; this prefixed line is
    // greppable in container logs to confirm the reachability boundary at startup.
    // See docs/security/integration-threat-model.md Part D C1 for the shape-per-shape model.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is not null && addresses.Count > 0)
            Log.Information("[C1] Kestrel bound to {Addresses}", string.Join(", ", addresses));
    });

    await app.RunAsync();
}
#pragma warning disable CA1031 // Top-level fatal handler — log any unhandled exception then exit. Re-throwing here would suppress the log and produce a less-useful stack trace.
catch (Exception ex)
#pragma warning restore CA1031
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    Environment.ExitCode = 1;
}

} // end outer try — web-host path
finally { await Log.CloseAndFlushAsync(); } // outer finally — drains Serilog for ALL paths

// WebApplicationFactory<Program> requires Program to be visible to the test
// assembly via the C# type system. [InternalsVisibleTo] does not satisfy the
// constraint because WebApplicationFactory<TEntryPoint> lives in
// Microsoft.AspNetCore.Mvc.Testing, a third-party assembly. Keep public.
// See ADR-006.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "Public anchor for WebApplicationFactory<Program> in tests; see ADR-006.")]
public partial class Program;

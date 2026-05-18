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

// AddOpenApi registers the document(s) consumed by both runtime MapOpenApi() and the
// build-time _GenerateOpenApiDocuments target (Part D C8). The build-time output
// (artifacts/openapi/ExpertiseApi.json) is OpenAPI 3.1.1 by the .NET 10 default — the
// `OpenApiOptions.OpenApiVersion` knob in Microsoft.AspNetCore.OpenApi 10.0.7 does NOT
// currently propagate to the build-time emitter (verified 2026-05-18). Downstream
// consumers in the integration backlog (#147 skill = plain curl/JSON; #148 pi extension
// = TypeScript codegen) are 3.1-compatible. If a 3.0-only consumer surfaces later, pin
// here and verify the emitter honours it on the then-current SDK.
builder.Services.AddOpenApi();

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
string[] readyTag = ["ready"];
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

// ForwardedHeaders must run before authentication so HttpContext.Connection.RemoteIpAddress
// reflects the real client IP when the audit pipeline reads it.
app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseStatusCodePages();
var metricsEnabled = app.Configuration.GetValue<bool>("Metrics:Enabled", true);
if (metricsEnabled)
    app.UseHttpMetrics();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
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

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthEndpoints();
app.MapExpertiseEndpoints();
app.MapSearchEndpoints();
app.MapSemanticSearchEndpoints();
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
finally { await Log.CloseAndFlushAsync(); }

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

#pragma warning disable SKEXP0070

using System.Globalization;
using ExpertiseApi.Auth;
using ExpertiseApi.Cli;
using ExpertiseApi.Data;
using ExpertiseApi.Endpoints;
using ExpertiseApi.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpOverrides;
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

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

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

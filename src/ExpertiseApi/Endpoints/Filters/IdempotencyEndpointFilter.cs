using System.Buffers.Binary;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ExpertiseApi.Auth;
using ExpertiseApi.Services.Idempotency;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Options;
using Prometheus;

namespace ExpertiseApi.Endpoints.Filters;

/// <summary>
/// Marker metadata attached by <see cref="IdempotencyEndpointFilterExtensions.RequireIdempotency"/>.
/// Consumed by the upstream
/// <see cref="IdempotencyRequestBufferingMiddleware"/> to decide whether to call
/// <c>EnableBuffering</c> on the request body (the filter runs after model
/// binding, by which time <see cref="HttpRequest.Body"/> is already drained
/// unless buffering was enabled earlier).
/// </summary>
internal sealed class RequireIdempotencyMetadata
{
    public static readonly RequireIdempotencyMetadata Instance = new();
    private RequireIdempotencyMetadata() { }
}

/// <summary>
/// Endpoint-filter implementation of Part D C3 (ADR-010). Sits as the
/// outermost endpoint filter on the three target POSTs
/// (<c>POST /expertise</c>, <c>POST /expertise/{id}/approve</c>,
/// <c>POST /expertise/{id}/reject</c>).
/// <para>
/// Hot-path responsibilities:
/// </para>
/// <list type="number">
/// <item>Honor the <c>Idempotency:RequireKey</c> soft/hard switch.</item>
/// <item>Validate the <c>Idempotency-Key</c> header per IETF §2.2.</item>
/// <item>Hash <c>method ‖ route-template ‖ tenant ‖ principal-sub ‖ raw body bytes</c> (SHA-256).</item>
/// <item>Attempt reservation; on hit-match replay, on hit-mismatch 409.</item>
/// <item>Swap response stream into a <see cref="MemoryStream"/> via
///   <see cref="StreamResponseBodyFeature"/>, await the handler, copy
///   captured bytes back to the real body, register
///   <see cref="HttpResponse.OnCompleted(System.Func{Task})"/> to persist.</item>
/// <item>Return <see cref="Results.Empty"/> so the framework does not
///   re-execute the original result against the restored body.</item>
/// </list>
/// <para>
/// Exception-path discipline (ADR-010 amendment 2026-05-19): the swap is
/// wrapped in <c>try/finally</c>; on unhandled exception the original
/// <see cref="IHttpResponseBodyFeature"/> is restored before
/// <c>UseExceptionHandler</c> writes the 5xx ProblemDetails to the wire.
/// 500-from-thrown-exception responses are NOT captured (the next retry
/// re-executes the handler, which is the intended failure mode).
/// </para>
/// </summary>
internal sealed class IdempotencyEndpointFilter : IEndpointFilter
{
    private const string HeaderName = "Idempotency-Key";
    private const string ReplayHeaderName = "Idempotency-Replay";
    private const string BodyOmittedWarning = "199 - \"Idempotent response truncated; original body not replayable\"";

    private static readonly Counter PersistFailedCounter = Metrics.CreateCounter(
        "expertise_idempotency_persist_failed_total",
        "Total number of idempotency-store PersistAsync failures (next retry will re-execute the handler).");

    private static readonly Counter RequestsCounter = Metrics.CreateCounter(
        "expertise_idempotency_requests_total",
        "Total number of idempotency-attributed POSTs processed, partitioned by outcome.",
        new CounterConfiguration { LabelNames = new[] { "outcome" } });

    private readonly IIdempotencyStore _store;
    private readonly IOptionsMonitor<IdempotencyOptions> _options;
    private readonly ILogger<IdempotencyEndpointFilter> _logger;

    public IdempotencyEndpointFilter(
        IIdempotencyStore store,
        IOptionsMonitor<IdempotencyOptions> options,
        ILogger<IdempotencyEndpointFilter> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var opts = _options.CurrentValue;

        // Header presence + soft/hard switch.
        var hasKey = http.Request.Headers.TryGetValue(HeaderName, out var rawKeyHeader);
        var keyValue = hasKey ? rawKeyHeader.ToString() : null;

        if (!hasKey)
        {
            if (opts.RequireKey)
            {
                RequestsCounter.WithLabels("missing_key_rejected").Inc();
                return Results.Problem(
                    title: "Idempotency-Key required",
                    detail: "This endpoint requires an Idempotency-Key header per IETF draft-ietf-httpapi-idempotency-key-header-06.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Soft-require migration window: behave exactly as the unfiltered
            // handler would. No reservation, no capture.
            RequestsCounter.WithLabels("missing_key_passthrough").Inc();
            return await next(context).ConfigureAwait(false);
        }

        // Validate format.
        var validation = IdempotencyKeyValidator.Validate(keyValue);
        if (!validation.IsValid)
        {
            RequestsCounter.WithLabels("invalid_key").Inc();
            return Results.Problem(
                title: "Invalid Idempotency-Key",
                detail: validation.Reason,
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Tenant + principal-sub for the hash. TenantContext is populated by the
        // auth pipeline (UseAuthentication/UseAuthorization upstream of MapGroup
        // .RequireAuthorization). Shared-tenant requests carry a null Tenant on
        // the context until BuildEntry rewrites it; the hash includes whatever
        // the auth pipeline established, which is the correct boundary.
        //
        // DEFENSIVE: assert Tenant is not null. Today the three target POSTs
        // sit behind RequireAuthorization which sets Tenant; a future
        // refactor that admits anonymous or partial-auth callers would
        // otherwise collapse the partition key into ("", key) for every
        // such request, concentrating idempotency contention into one slot.
        var tenantContext = http.RequireTenantContext();
        if (string.IsNullOrEmpty(tenantContext.Tenant))
        {
            // Conservative: refuse rather than store under empty-tenant.
            // This branch should be unreachable under the current route map.
            RequestsCounter.WithLabels("missing_tenant").Inc();
            return Results.Problem(
                title: "Idempotency-Key requires a tenant",
                detail: "This endpoint emitted an Idempotency-Key but the authenticated principal has no resolved tenant.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var tenant = tenantContext.Tenant;
        var sub = tenantContext.Principal.FindFirst("sub")?.Value
                  ?? tenantContext.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? string.Empty;

        // Route template. RawText includes the path-segment prefix from the
        // MapGroup ("/expertise/{id:guid}/approve") so the hash naturally
        // partitions across endpoints even before tenant/sub are mixed in.
        var endpoint = http.GetEndpoint();
        var routeTemplate = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value ?? string.Empty;

        // Raw request bytes. The upstream IdempotencyRequestBufferingMiddleware
        // ensures Request.Body is seekable (FileBufferingReadStream); if it is
        // not (someone bypassed the middleware), fall back to read-and-replay
        // via Read().
        if (http.Request.Body.CanSeek)
        {
            http.Request.Body.Position = 0;
        }
        byte[] bodyBytes;
        await using (var ms = new MemoryStream())
        {
            await http.Request.Body.CopyToAsync(ms, http.RequestAborted).ConfigureAwait(false);
            bodyBytes = ms.ToArray();
        }
        if (http.Request.Body.CanSeek)
        {
            http.Request.Body.Position = 0;
        }

        var requestHash = ComputeRequestHash(http.Request.Method, routeTemplate, tenant, sub, bodyBytes);

        // Lookup / reserve.
        var (outcome, payload) = await _store.TryReserveAsync(
            tenant, keyValue!, requestHash, opts.Ttl, http.RequestAborted).ConfigureAwait(false);

        switch (outcome)
        {
            case IdempotencyLookupOutcome.HitMismatch:
                RequestsCounter.WithLabels("mismatch").Inc();
                return Results.Problem(
                    title: "Idempotency-Key reuse with different request",
                    detail: "An earlier request with this Idempotency-Key carried a different body; refusing to replay or re-execute.",
                    statusCode: StatusCodes.Status409Conflict);

            case IdempotencyLookupOutcome.HitMatch when payload is null:
                // In-flight concurrent request with same key+hash; surface 409
                // rather than block (avoids holding a request thread on a
                // SELECT FOR UPDATE waiting on another pod's writer).
                RequestsCounter.WithLabels("inflight_conflict").Inc();
                return Results.Problem(
                    title: "Concurrent request with same Idempotency-Key",
                    detail: "Another request with the same Idempotency-Key is still being processed; retry after it completes.",
                    statusCode: StatusCodes.Status409Conflict);

            case IdempotencyLookupOutcome.HitMatch:
                RequestsCounter.WithLabels("replay").Inc();
                await WriteReplayAsync(http, payload!).ConfigureAwait(false);
                return Results.Empty;

            case IdempotencyLookupOutcome.Reserved:
            default:
                // Fall through to handler execution below.
                break;
        }

        // Reserved branch: run the handler with response-stream capture.
        // `handlerThrew` tracks unhandled-exception unwind so the finally
        // block can release the placeholder row (otherwise it would block
        // legitimate retries for the full TTL window).
        var origBodyFeature = http.Features.Get<IHttpResponseBodyFeature>();
        await using var buffer = new MemoryStream();
        var bufferFeature = new StreamResponseBodyFeature(buffer);
        http.Features.Set<IHttpResponseBodyFeature>(bufferFeature);

        var handlerThrew = false;
        try
        {
            var handlerResult = await next(context).ConfigureAwait(false);
            if (handlerResult is IResult r)
            {
                await r.ExecuteAsync(http).ConfigureAwait(false);
            }
        }
        catch
        {
            handlerThrew = true;
            throw;
        }
        finally
        {
            // ADR-010 amendment: restore even on exception so UseExceptionHandler
            // writes the 5xx ProblemDetails to the wire rather than the orphaned
            // buffer. The next retry will re-execute the handler.
            if (origBodyFeature is not null)
                http.Features.Set<IHttpResponseBodyFeature>(origBodyFeature);

            if (handlerThrew)
            {
                // Fire-and-forget placeholder release. Cannot await inside
                // finally on the exception path (we are rethrowing), so use
                // OnCompleted; this still runs before the framework completes
                // the response. CancellationToken.None: the request was
                // cancelled but the release operation must complete.
                http.Response.OnCompleted(async () =>
                {
                    try
                    {
                        await _store.ReleaseReservationAsync(tenant, keyValue!, CancellationToken.None).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // fire-and-forget metric/log
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        PersistFailedCounter.Inc();
                        _logger.LogWarning(ex, "Idempotency placeholder release failed after handler exception; GC sweep will reap on next cadence");
                    }
                });
            }
        }

        // Capture status code + content type + selected headers before the body
        // is written. Headers list is intentionally narrow: we replay the
        // hygiene-relevant subset, not the framework-injected Date / Server.
        var capturedStatus = http.Response.StatusCode;
        var capturedContentType = http.Response.ContentType;
        var capturedHeaders = SnapshotReplayableHeaders(http.Response.Headers);

        var bodyArr = buffer.ToArray();
        var shouldCache = ShouldCacheStatus(capturedStatus);
        var bodyTruncated = bodyArr.Length > opts.MaxBodyBytes;

        // Replay-able cache only for the configured status classes. For
        // non-cacheable statuses (5xx, 429) we explicitly release the
        // placeholder row so the caller's retry can re-execute. Without this,
        // a transient 5xx would lock the (tenant, key) slot for the full TTL
        // (default 24h) and every retry would receive 409 inflight-conflict
        // — the exact failure mode idempotency keys are supposed to solve.
        if (shouldCache)
        {
            // Write the captured bytes (no truncation on the wire — the cap
            // applies only to what we persist; the live caller always receives
            // the full handler-emitted body).
            await http.Response.Body.WriteAsync(bodyArr.AsMemory(0, bodyArr.Length), http.RequestAborted).ConfigureAwait(false);

            var bodyForStore = bodyTruncated ? null : bodyArr;
            var bodyHash = ComputeSha256Hex(bodyArr);

            // Fire-and-forget persistence on response completion. Failure mode:
            // log + metric, row stays as placeholder, GC sweep reaps it,
            // retry re-executes. Intentional per ADR-010.
            var tenantCapture = tenant;
            var keyCapture = keyValue!;
            var ctypeCapture = capturedContentType;
            var headersCapture = capturedHeaders;
            http.Response.OnCompleted(async () =>
            {
                try
                {
                    await _store.PersistAsync(
                        tenantCapture,
                        keyCapture,
                        capturedStatus,
                        bodyHash,
                        bodyForStore,
                        ctypeCapture,
                        headersCapture,
                        CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Top-level fire-and-forget handler — every exception class converts to the same metric increment; rethrow would crash the request-completion callback.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    PersistFailedCounter.Inc();
                    _logger.LogWarning(
                        ex,
                        "Idempotency persistence failed for tenant={Tenant} (key length {KeyLength}); next retry will re-execute",
                        tenantCapture,
                        keyCapture.Length);
                }
            });

            RequestsCounter.WithLabels(bodyTruncated ? "executed_truncated" : "executed").Inc();
        }
        else
        {
            // Non-cacheable status: write bytes to wire, release the
            // placeholder so the next retry can re-execute. Fire-and-forget;
            // a failure here means the slot stays held for at most the GC
            // cadence (1h) — still vastly better than the 24h TTL window.
            await http.Response.Body.WriteAsync(bodyArr.AsMemory(0, bodyArr.Length), http.RequestAborted).ConfigureAwait(false);

            var tenantCapture = tenant;
            var keyCapture = keyValue!;
            http.Response.OnCompleted(async () =>
            {
                try
                {
                    await _store.ReleaseReservationAsync(tenantCapture, keyCapture, CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // fire-and-forget metric/log
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    PersistFailedCounter.Inc();
                    _logger.LogWarning(ex, "Idempotency placeholder release failed on non-cacheable status {Status}; GC sweep will reap on next cadence", capturedStatus);
                }
            });
            RequestsCounter.WithLabels($"executed_no_cache_{capturedStatus}").Inc();
        }

        // Sentinel: framework will invoke ExecuteAsync on the returned value;
        // Results.Empty is a no-op so we do not double-write the body.
        return Results.Empty;
    }

    private static async Task WriteReplayAsync(HttpContext http, IdempotencyReplayPayload payload)
    {
        http.Response.StatusCode = payload.StatusCode;
        if (payload.ContentType is not null)
            http.Response.ContentType = payload.ContentType;

        if (payload.Headers is not null)
        {
            foreach (var (k, v) in payload.Headers)
            {
                // Do not clobber framework-managed headers (Content-Length is
                // recomputed from the body write; Date / Server are owned by
                // Kestrel).
                if (string.Equals(k, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;
                http.Response.Headers[k] = v;
            }
        }

        http.Response.Headers[ReplayHeaderName] = "true";
        if (payload.BodyOmittedDueToSize)
            http.Response.Headers["Warning"] = BodyOmittedWarning;

        if (payload.Body is not null && payload.Body.Length > 0)
            await http.Response.Body.WriteAsync(payload.Body).ConfigureAwait(false);
    }

    private static Dictionary<string, string>? SnapshotReplayableHeaders(IHeaderDictionary headers)
    {
        // Replay these. Content-Type is carried separately. Most consumers do
        // not depend on a specific set; we replay the application-meaningful
        // ones and let Kestrel re-derive framework headers.
        string[] replayList = ["Location", "Cache-Control", "ETag", "Vary"];
        Dictionary<string, string>? dict = null;
        foreach (var name in replayList)
        {
            if (headers.TryGetValue(name, out var values) && values.Count > 0)
            {
                (dict ??= new Dictionary<string, string>(StringComparer.Ordinal))[name] = values.ToString();
            }
        }
        return dict;
    }

    private static bool ShouldCacheStatus(int statusCode)
    {
        // ADR-010 amendment (2026-05-19): cache 2xx + deterministic 4xx (400,
        // 409, 422). Do not cache 5xx or 429.
        if (statusCode >= 200 && statusCode < 300) return true;
        return statusCode is 400 or 409 or 422;
    }

    private static string ComputeRequestHash(
        string method,
        string routeTemplate,
        string tenant,
        string sub,
        ReadOnlySpan<byte> bodyBytes)
    {
        // SHA-256 over a length-prefixed concatenation. Length prefixes prevent
        // boundary ambiguity (e.g. "GET/foo" vs "GE" + "T/foo"); each field is
        // delimited by its UTF-8 byte length followed by the bytes.
        //
        // Endianness: explicit little-endian via BinaryPrimitives so the
        // hash is identical across heterogeneous-arch deployments (mixed
        // amd64/arm64 fleets reading each other's stored hashes).
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Mix(hasher, method);
        Mix(hasher, routeTemplate);
        Mix(hasher, tenant);
        Mix(hasher, sub);
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, bodyBytes.Length);
        hasher.AppendData(lenBuf);
        hasher.AppendData(bodyBytes);

        return Convert.ToHexStringLower(hasher.GetHashAndReset());

        static void Mix(IncrementalHash h, string s)
        {
            Span<byte> lb = stackalloc byte[4];
            var bytes = Encoding.UTF8.GetBytes(s);
            BinaryPrimitives.WriteInt32LittleEndian(lb, bytes.Length);
            h.AppendData(lb);
            h.AppendData(bytes);
        }
    }

    private static string ComputeSha256Hex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>
/// Filter-attachment helper. Usage in <see cref="ExpertiseEndpoints"/>:
/// <code>group.MapPost("/", CreateEntry).RequireIdempotency();</code>
/// </summary>
internal static class IdempotencyEndpointFilterExtensions
{
    /// <summary>
    /// Attach the <see cref="IdempotencyEndpointFilter"/> as the outermost
    /// endpoint filter, and tag the route with
    /// <see cref="RequireIdempotencyMetadata"/> so the upstream buffering
    /// middleware enables <c>EnableBuffering</c> on the request body.
    /// </summary>
    public static TBuilder RequireIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(RequireIdempotencyMetadata.Instance);
        if (builder is RouteHandlerBuilder rhb)
        {
            rhb.AddEndpointFilter<IdempotencyEndpointFilter>();
        }
        return builder;
    }
}

/// <summary>
/// Tiny middleware that opts attributed POSTs into request-body buffering
/// (<c>EnableBuffering</c>). Without this the endpoint filter cannot recover
/// the raw request bytes — by filter time, model binding has consumed
/// <see cref="HttpRequest.Body"/>.
/// <para>
/// Must run after <c>UseRouting()</c> (so <see cref="HttpContext.GetEndpoint"/>
/// returns the matched endpoint) and before <c>UseAuthentication()</c>
/// (buffering should be enabled before any consumer reads the body).
/// </para>
/// </summary>
internal sealed class IdempotencyRequestBufferingMiddleware
{
    private readonly RequestDelegate _next;

    public IdempotencyRequestBufferingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<RequireIdempotencyMetadata>() is not null
            && HttpMethods.IsPost(context.Request.Method))
        {
            context.Request.EnableBuffering();
        }
        return _next(context);
    }
}

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;

namespace ExpertiseApi.Diagnostics;

/// <summary>
/// Typed <see cref="IExceptionHandler"/> registered via <c>AddExceptionHandler&lt;T&gt;()</c>
/// — the .NET 8+ preferred shape for global exception logging without claiming the
/// response body (Part D C4).
///
/// Returns <c>false</c> from <see cref="TryHandleAsync"/> for genuinely unhandled
/// exceptions so the framework's default
/// <see cref="Microsoft.AspNetCore.Http.IProblemDetailsService"/> writer produces the
/// response, which means the sanitizer registered in <c>AddProblemDetails(...)</c> in
/// <c>Program.cs</c> fires for both <c>Results.Problem(...)</c> and unhandled-exception
/// paths. The one handled case is <see cref="BadHttpRequestException"/> (minimal-API
/// binding failures), which is written with the exception's own status code instead of
/// the middleware's generic 500 (#329) — still via <c>IProblemDetailsService</c>, so the
/// same customizer fires.
///
/// Critically, the full exception (message, type, stack) is logged server-side with
/// the request path here; the customizer then strips Detail/Instance from the wire
/// response in non-Development environments so the correlation ID (traceId) is the
/// only link between the client-visible response and the server-side log entry.
///
/// <para>
/// <strong>Log-forging defence (CWE-117):</strong> request method and path are
/// attacker-controlled and may carry CR/LF (or other C0 control bytes) that would
/// otherwise inject synthetic log lines into the structured-logging sink. We pass
/// both through <see cref="SanitizeForLog"/> before they reach the message
/// template. The replacement char (<c>?</c>) is deliberately visible so a
/// sanitized log line still surfaces the tampering attempt. Any future field
/// added to this message template (e.g. <c>QueryString</c>, request headers) MUST
/// also be routed through <see cref="SanitizeForLog"/>.
/// </para>
/// </summary>
internal sealed class UnhandledExceptionLogger(
    ILogger<UnhandledExceptionLogger> log,
    IProblemDetailsService problemDetails)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext ctx,
        Exception ex,
        CancellationToken ct)
    {
        // Minimal-API model binding throws BadHttpRequestException (e.g. a missing
        // required [FromQuery] parameter, or a non-numeric value for an int param)
        // BEFORE the handler's own validation guards run. Left to the default path,
        // the exception middleware writes a generic 500 — a misleading "server error"
        // for what is a malformed request (#329). Surface the exception's own status
        // code via IProblemDetailsService so the AddProblemDetails customizer still
        // fires (traceId + non-Development Detail scrub).
        if (ex is BadHttpRequestException badRequest)
        {
            log.LogWarning("Bad request ({StatusCode}) for {Method} {Path}: {Reason}",
                badRequest.StatusCode,
                SanitizeForLog(ctx.Request.Method),
                SanitizeForLog(ctx.Request.Path.Value),
                SanitizeForLog(badRequest.Message));

            ctx.Response.StatusCode = badRequest.StatusCode;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = ctx,
                ProblemDetails =
                {
                    Status = badRequest.StatusCode,
                    Title = ReasonPhrases.GetReasonPhrase(badRequest.StatusCode),
                    Detail = badRequest.Message,
                },
            });
        }

        log.LogError(ex, "Unhandled exception for {Method} {Path}",
            SanitizeForLog(ctx.Request.Method),
            SanitizeForLog(ctx.Request.Path.Value));
        // false = let the default IProblemDetailsService writer handle the response body
        // so the AddProblemDetails customizer in Program.cs runs (correlation ID + sanitization).
        return false;
    }

    /// <summary>
    /// Replace ASCII C0 control characters (including CR, LF, NUL, tab) with <c>?</c>.
    /// Bounded at 2048 chars to cap log-line length when an attacker supplies a very
    /// long synthetic path.
    /// </summary>
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        const int max = 2048;
        var src = value.Length > max ? value.AsSpan(0, max) : value.AsSpan();
        var buf = new char[src.Length];
        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            buf[i] = c < 0x20 || c == 0x7F ? '?' : c;
        }
        return new string(buf);
    }
}

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ExpertiseApi.Services.Health;

/// <summary>
/// Reports unhealthy when the ONNX embedding stack cannot serve a request.
///
/// Observable signal: whether <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>
/// is registered in DI. Program.cs registers it conditionally
/// (<c>if (File.Exists(modelPath) &amp;&amp; File.Exists(vocabPath))</c>) and
/// <c>AddBertOnnxEmbeddingGenerator</c> opens the model file eagerly at
/// registration time. The two consequences are:
///
///   * Files missing at startup ⇒ service never registered ⇒ this check
///     returns <see cref="HealthStatus.Unhealthy"/>.
///   * Files corrupt at startup ⇒ registration throws ⇒ Program.cs fails to
///     boot at all, so this check is never reached. The "files corrupt mid-run"
///     case cannot affect the loaded session (held in the singleton generator),
///     so a probe-time file re-stat would be misleading rather than useful.
///
/// File-path probing is therefore intentionally NOT performed here: DI
/// resolvability is the actual proxy for "embedding stack can serve a request".
/// Filesystem-level diagnostics for an absent install belong in the install
/// scripts under scripts/install.sh, not in a per-request health probe.
/// </summary>
internal sealed class OnnxModelHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _serviceProvider;

    public OnnxModelHealthCheck(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Resolve the generator from DI rather than holding a constructor reference:
        // the service is registered conditionally in Program.cs (only when both
        // files exist at startup), so a constructor-injected dependency would
        // crash the entire check pipeline at activation when the model is absent.
        var generator = _serviceProvider.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
        if (generator is null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "ONNX embedding generator not registered (model/vocab files were absent at startup)."));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "ONNX embedding generator resolved."));
    }
}

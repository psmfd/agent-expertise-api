using ExpertiseApi.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ExpertiseApi.Tests.Unit;

[Collection("SequentialApiFactory")]
public class HostOptionsConfigurationTests
{
    [Fact]
    public void HostOptions_ShutdownTimeout_Is30Seconds()
    {
        // Regression guard for #142. The default 5s is insufficient under load
        // to drain in-flight HTTP, close the Npgsql pool, and dispose the ONNX
        // inference session before systemd / launchd / SCM escalates to SIGKILL.
        //
        // ApiFactory is reused so the IEmbeddingGenerator mock and DbContext
        // override are in place, but no endpoint is invoked — the DbContext is
        // never resolved, so the stub connection string never opens a socket.
        // This avoids the Testcontainers / Docker dependency for a DI-only test.
        using var factory = new ApiFactory(
            "Host=127.0.0.1;Port=1;Database=stub;Username=stub;Password=stub");
        var options = factory.Services.GetRequiredService<IOptions<HostOptions>>().Value;
        options.ShutdownTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }
}

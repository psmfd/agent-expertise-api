using ExpertiseApi.Data;
using ExpertiseApi.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.Connectors.Onnx;
using Xunit.Abstractions;

namespace ExpertiseApi.Tests.Evaluation;

/// <summary>
/// Dedup semantic-threshold gate (#457). Embeds labeled entry pairs with the
/// REAL pinned ONNX model and asserts the shipped
/// <see cref="DeduplicationOptions.SemanticThreshold"/> default separates them:
///
/// <list type="bullet">
///   <item><b>near-dup pairs</b> (same fact resubmitted with light rewording —
///     the class dedup exists to catch) must land BELOW the threshold;</item>
///   <item><b>distinct pairs</b> (different facts in the same topic area — the
///     class a false 409 would wrongly reject) must land ABOVE it;</item>
///   <item><b>moderate-to-full rewordings</b> (same fact, heavier rewrite) are
///     REPORT-ONLY: per #457's risk profile dedup may under-fire (the miss
///     lands as a Draft for curator review) but must not misfire.</item>
/// </list>
///
/// Pair texts are class exemplars modeled on hand-labeled live-corpus
/// neighbor pairs from the 2026-07-24 derivation (issue #457): under
/// jina-v2-small, corpus near-dups clustered at distance ≤ 0.048 and the
/// closest genuinely-distinct pairs began at ≈ 0.051, with the bulk of
/// distinct neighbors at 0.06–0.13. The bge-era default of 0.10 sat inside
/// the distinct mass (45% of corpus entries had a legitimate same-domain
/// neighbor within 0.10) — hence the retune.
///
/// No database: this measures raw embedding geometry only.
/// <code>
/// EXPERTISE_EVAL=1 dotnet test --filter "FullyQualifiedName~DedupThresholdEval"
/// </code>
/// </summary>
public sealed class DedupThresholdEvalTests(ITestOutputHelper output) : IAsyncLifetime
{
    private ServiceProvider? _onnxProvider;
    private EmbeddingService _embedding = null!;

    public Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("EXPERTISE_EVAL") != "1" || ModelFiles.ModelPath is null)
            return Task.CompletedTask;

        var services = new ServiceCollection();
        services.AddBertOnnxEmbeddingGenerator(ModelFiles.ModelPath!, ModelFiles.VocabPath!,
            EmbeddingModelInfo.CreateOnnxOptions());
        _onnxProvider = services.BuildServiceProvider();
        _embedding = new EmbeddingService(
            _onnxProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _onnxProvider?.Dispose();
        return Task.CompletedTask;
    }

    private sealed record Pair(string Label, string TitleA, string BodyA, string TitleB, string BodyB);

    // ---- Near-dup pairs: same fact, light rewording / retitle. ----
    // The agent-resubmission class: an agent re-learns a fact and re-submits it
    // with different phrasing. Modeled on real corpus dup pairs (e.g. the
    // "VS Code Copilot hook parity" retitle at 0.031).
    private static readonly IReadOnlyList<Pair> NearDupPairs =
    [
        new("neardup-pgbouncer",
            "Npgsql with PgBouncer transaction mode needs No Reset On Close",
            "When running Npgsql behind PgBouncer in transaction pooling mode, the connection string must set No Reset On Close=True. Without it Npgsql issues DISCARD ALL on connection return, which breaks under transaction pooling because the session state reset races other clients sharing the server connection.",
            "PgBouncer transaction pooling requires No Reset On Close=True in the Npgsql connection string",
            "Npgsql behind PgBouncer in transaction pool mode must include No Reset On Close=True. Otherwise Npgsql sends DISCARD ALL when a connection is returned, and under transaction pooling that reset conflicts with other clients that share the same server connection."),
        new("neardup-docker-copy",
            "Dockerfile COPY of the models directory fails silently when it is empty",
            "A multi-stage Dockerfile COPY of src/models/ succeeds even when the directory was never populated, producing an image without model files. The failure only surfaces at container start when the API tries to load the ONNX model. CI must download models before docker build.",
            "COPY of ONNX model files in the Dockerfile silently produces a broken image without pre-download",
            "In a multi-stage build, COPY src/models/ does not fail when the directory is empty, so the image builds cleanly but is missing the ONNX model files. The error appears only at runtime when model load fails. Run the model download script before building the image in CI."),
        new("neardup-ssh-socket",
            "Debian 13 ssh.socket activation ignores Port changes in sshd_config on restart",
            "On a fresh Debian 13 install SSH is socket-activated. Restarting ssh.service after editing Port in sshd_config does not change the listener, because the socket unit owns the bind. You must edit the socket unit or restart ssh.socket for a port change to take effect.",
            "SSH port changes need ssh.socket restart on Debian 13 fresh installs",
            "Debian 13 fresh installs use ssh.socket activation, so the listening port is bound by the socket unit. Editing Port in sshd_config and restarting only ssh.service leaves the old listener in place — restart ssh.socket (or override the socket unit) instead."),
    ];

    // ---- Report-only pairs: same fact, moderate-to-full rewording. ----
    // Measured 2026-07-24 (jina-v2-small@6144): moderate rewordings land at
    // 0.05–0.07 — the SAME band where the live corpus's closest genuinely
    // distinct neighbors begin (0.051) — and full paraphrases at 0.13–0.16.
    // No threshold separates these from distinct entries, so per #457's risk
    // profile they are accepted under-fire (the miss lands as a Draft for
    // curator review); this suite reports their drift but does not gate them.
    private static readonly IReadOnlyList<Pair> ReportOnlyPairs =
    [
        new("moderate-set-e-counter",
            "bash set -e aborts on ((counter++)) when the counter starts at zero",
            "Under set -e, the arithmetic expression ((counter++)) returns the pre-increment value, so incrementing from 0 evaluates to 0 which is falsy and terminates the script. Use ((counter++)) || true or counter=$((counter + 1)) instead.",
            "((counter++)) from zero kills scripts running under set -e",
            "With errexit enabled, ((counter++)) yields the value before the increment; starting from 0 that is 0, bash treats it as failure, and the script exits. The safe forms are counter=$((counter + 1)) or appending || true to the arithmetic command."),
        new("moderate-actions-cache",
            "GitHub Actions cache key must hash the lockfile, not the manifest",
            "Keying actions/cache on package.json misses dependency updates that only change package-lock.json, serving stale node_modules. Use hashFiles('**/package-lock.json') in the cache key so any resolved-dependency change busts the cache.",
            "actions/cache keyed on package.json serves stale dependencies",
            "A cache key built from package.json does not change when only package-lock.json moves, so dependency bumps restore an outdated node_modules. Build the key with hashFiles over the lockfile instead of the manifest."),
        new("para-pgbouncer",
            "Npgsql with PgBouncer transaction mode needs No Reset On Close",
            "When running Npgsql behind PgBouncer in transaction pooling mode, the connection string must set No Reset On Close=True. Without it Npgsql issues DISCARD ALL on connection return, which breaks under transaction pooling because the session state reset races other clients sharing the server connection.",
            "Session-reset commands from the .NET Postgres driver conflict with shared pooler connections",
            "Our API pods started throwing prepared-statement errors after we introduced a connection pooler in transaction mode. Root cause: the driver's automatic cleanup command on connection release assumes it owns the session, but the pooler hands the same backend to many clients. The fix is a driver flag that disables the automatic session reset."),
        new("para-ssh-socket",
            "Debian 13 ssh.socket activation ignores Port changes in sshd_config on restart",
            "On a fresh Debian 13 install SSH is socket-activated. Restarting ssh.service after editing Port in sshd_config does not change the listener, because the socket unit owns the bind. You must edit the socket unit or restart ssh.socket for a port change to take effect.",
            "Why changing the SSH listening port appeared to do nothing on a new Trixie VM",
            "Spent an hour debugging why the daemon kept answering on 22 after a config edit and service restart. Turns out systemd owns the listener through socket activation on new installs of this release, so the daemon config's port directive is not consulted for the bind — the systemd socket unit is."),
    ];

    // ---- Distinct pairs: same topic area, different fact (must NOT dedup). ----
    // Modeled on real corpus neighbor pairs hand-labeled distinct (0.06–0.09).
    private static readonly IReadOnlyList<Pair> DistinctPairs =
    [
        new("distinct-ado-networking",
            "Azure DevOps hosted agents cannot reach Azure Private Endpoints",
            "Microsoft-hosted ADO agents run outside your vnet and resolve private-endpoint hostnames to public IPs that refuse connections. Builds needing private resources must use self-hosted agents or vnet-injected pools.",
            "Managed DevOps Pools support vnet injection for private endpoint access",
            "Managed DevOps Pools can be injected into a subnet of your vnet, giving pipeline jobs direct line-of-sight to private endpoints without standing up self-hosted VMs. Configure the pool's network profile with the target subnet resource id."),
        new("distinct-markdownlint",
            "markdownlint MD060 flags compact pipe-table separators",
            "markdownlint v0.40 introduced MD060 which fires on |---|---| style table separator rows, wanting padded | --- | --- | form. Existing docs with compact separators fail lint after the version bump.",
            "markdownlint MD013 line-length ignores tables only when configured",
            "MD013 counts table rows against the line-length limit unless the tables:false option is set for the rule. Wide comparison tables need that override or they fail lint at the default 80-character limit."),
        new("distinct-bash-traps",
            "set -u unbound-variable exits bypass ERR traps",
            "An unbound variable expansion under set -u terminates the shell directly without running the ERR trap, so cleanup handlers keyed on ERR never fire for this failure class. Guard expansions with ${var:-} where cleanup must run.",
            "ERR traps do not inherit into functions without set -E",
            "A trap on ERR set at the top level does not fire for failures inside shell functions unless errtrace (set -E) is enabled, so function-level failures skip the handler even though the script-level command would have triggered it."),
        new("distinct-kitty",
            "kitten ssh is required for a working TTY on kitty terminals",
            "Plain ssh from kitty sends the xterm-kitty TERM value that remote hosts lack a terminfo entry for, breaking full-screen programs. kitten ssh copies the terminfo to the remote host on connect.",
            "kitty file transfer over nested SSH has low throughput",
            "The transfer kitten works across nested SSH hops but its escape-sequence encoding caps throughput well below scp for large files; use it for small config files and fall back to scp or rsync for anything sizable."),
        new("distinct-keda-ado",
            "KEDA azure-pipelines scaler prefers poolName over poolID when both are set",
            "When a ScaledObject sets both poolName and poolID, the scaler resolves the pool by name and silently ignores the id, which surprises configs that rotated pool names but pinned ids.",
            "ADO agent pool ScaledJobs should target pool ID, not display name",
            "Pool display names are mutable and org-unique only per project collection; jobs keyed on the name break when an admin renames the pool. Configure the numeric pool id, which is stable across renames."),
    ];

    [EvalFact]
    public async Task PairDistances_SeparateAtTheShippedThreshold()
    {
        var threshold = new DeduplicationOptions().SemanticThreshold;

        // Sanity pin: identical text must embed to (near-)zero distance.
        var idText = EmbeddingService.BuildInputText(NearDupPairs[0].TitleA, NearDupPairs[0].BodyA);
        var idVecs = await _embedding.GenerateBatchAsync([idText, idText]);
        var selfDistance = ExpertiseRepository.CosineDistance(idVecs[0].ToArray(), idVecs[1].ToArray());
        selfDistance.Should().NotBeNull().And.BeLessThan(0.001);

        async Task<List<(string Label, double Distance)>> MeasureAsync(IReadOnlyList<Pair> pairs)
        {
            var texts = pairs.SelectMany(p => new[]
            {
                EmbeddingService.BuildInputText(p.TitleA, p.BodyA),
                EmbeddingService.BuildInputText(p.TitleB, p.BodyB),
            });
            var vecs = await _embedding.GenerateBatchAsync(texts);
            return pairs.Select((p, i) =>
                (p.Label, ExpertiseRepository.CosineDistance(
                    vecs[2 * i].ToArray(), vecs[2 * i + 1].ToArray())!.Value)).ToList();
        }

        var nearDups = await MeasureAsync(NearDupPairs);
        var reportOnly = await MeasureAsync(ReportOnlyPairs);
        var distincts = await MeasureAsync(DistinctPairs);

        output.WriteLine($"SemanticThreshold under test: {threshold}");
        output.WriteLine("");
        output.WriteLine("class      | pair                     | distance | vs threshold");
        output.WriteLine("-----------|--------------------------|----------|-------------");
        foreach (var (label, d) in nearDups)
            output.WriteLine($"near-dup   | {label,-24} | {d:F4}   | {(d < threshold ? "CAUGHT" : "MISSED")}");
        foreach (var (label, d) in reportOnly)
            output.WriteLine($"report     | {label,-24} | {d:F4}   | {(d < threshold ? "caught" : "missed (accepted under-fire)")}");
        foreach (var (label, d) in distincts)
            output.WriteLine($"distinct   | {label,-24} | {d:F4}   | {(d > threshold ? "PASSED THROUGH" : "FALSE 409")}");

        // Gates. Near-dups must be caught; distincts must never false-409.
        // Moderate/full rewordings are report-only (accepted under-fire, #457).
        nearDups.Should().OnlyContain(p => p.Distance < threshold,
            "light-rewording resubmissions are the class the semantic dedup threshold exists to catch");
        distincts.Should().OnlyContain(p => p.Distance > threshold,
            "a distinct same-topic entry rejected as a duplicate is a false 409 — the costly failure direction");
    }
}

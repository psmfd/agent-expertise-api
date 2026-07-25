using System.Data.Common;
using ExpertiseApi.Data;
using Microsoft.EntityFrameworkCore;

namespace ExpertiseApi.Cli;

/// <summary>
/// One-shot CLI verb running the ADR-020 integrity verification sweep: recompute every
/// entry MAC, re-verify every sealed audit checkpoint, seal a new checkpoint through
/// the latest grace-aged audit row. Scheduling is an OS concern (systemd timer /
/// launchd on A2, CronJob on k8s) — same posture as <c>backup</c> and <c>reembed</c>.
/// Wrapped by <c>scripts/expertise-apictl verify</c>.
/// </summary>
internal static class VerifyCommand
{
    public static bool IsVerifyRequested(string[] args) =>
        args.Length > 0 && args[0].Equals("verify", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs the verification sweep.</summary>
    /// <returns>0 clean; 1 on any integrity mismatch; 2 on precondition failure (bad arguments or database error).</returns>
    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        using var scope = app.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Verify");

        var batchSize = GetIntOption(args, "--batch-size", 500);
        var graceSeconds = GetIntOption(args, "--grace-seconds",
            (int)IntegrityVerificationService.DefaultGraceWindow.TotalSeconds);
        var seal = !Array.Exists(args, a => a.Equals("--no-seal", StringComparison.OrdinalIgnoreCase));

        if (batchSize <= 0 || graceSeconds < 0)
        {
            logger.LogCritical("Verify: --batch-size must be positive and --grace-seconds non-negative.");
            return 2;
        }

        try
        {
            var service = scope.ServiceProvider.GetRequiredService<IntegrityVerificationService>();
            var result = await service.RunAsync(batchSize, TimeSpan.FromSeconds(graceSeconds), seal);

            logger.LogInformation(
                "Verify: {Entries} entries checked ({Legacy} legacy-format, {Unhashed} unhashed), " +
                "{Checkpoints} checkpoints verified, sealed={Sealed} (through seq {SealedSeq}), mismatches={Mismatches}",
                result.EntriesChecked, result.LegacyCount, result.UnhashedCount,
                result.CheckpointsChecked, result.CheckpointSealed,
                result.SealedThroughSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                result.TotalMismatches);

            if (result.Clean)
                return 0;

            logger.LogCritical(
                "Verify: FAILED — {EntryMac} entry MAC mismatch(es), {UnknownKey} unknown key id(s), {Checkpoint} checkpoint mismatch(es). See preceding log entries for row-level detail.",
                result.EntryMacMismatches, result.UnknownKeyIds, result.CheckpointMismatches);
            return 1;
        }
        // Same narrowed-catch posture as BackupCommand (process-fatal exceptions and
        // OperationCanceledException propagate).
        catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException)
        {
            logger.LogCritical(ex, "Verify: precondition failure (full exception detail follows).");
            return 2;
        }
    }

    private static int GetIntOption(string[] args, string name, int fallback)
    {
        var idx = Array.IndexOf(args, name);
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var value))
            return value;
        return fallback;
    }
}

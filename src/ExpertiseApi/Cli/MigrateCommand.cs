using ExpertiseApi.Data;
using Microsoft.EntityFrameworkCore;

namespace ExpertiseApi.Cli;

/// <summary>
/// One-shot CLI command that applies pending EF Core migrations and exits.
///
/// Wired from <see cref="Program"/> as <c>dotnet ExpertiseApi.dll migrate</c>
/// (or the SCD-published equivalent). Invoked by <c>scripts/migrate.sh</c> /
/// <c>scripts/migrate.ps1</c> and by the A2 native-install scripts
/// (<c>scripts/install.sh</c>, <c>scripts/install.ps1</c>) between publish
/// and service start so a deploy carrying a schema change applies it before
/// the service exposes the readiness probe (issue #144).
///
/// Semantics:
///   * Idempotent — no-op when <see cref="DatabaseFacade.GetPendingMigrationsAsync"/>
///     returns an empty set; logs and exits 0.
///   * Failure is fatal — any EF / Npgsql exception is logged and the
///     command returns 1, so the calling install script aborts before
///     restarting the service (acceptance criterion: "prior state intact").
///   * No down-migrations / rollback support (explicitly out of scope per
///     the issue; deferred until A2 grows beyond solo-dev).
/// </summary>
internal static class MigrateCommand
{
    public static bool IsMigrateRequested(string[] args) =>
        args.Length > 0 && args[0].Equals("migrate", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies any pending EF Core migrations to the configured database.
    /// </summary>
    /// <returns>0 on success or no-op; 1 on any database / migration failure.</returns>
    public static async Task<int> RunAsync(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migrate");

        // Scoped DbContext: the EF DI registration is scoped, and Database.MigrateAsync
        // opens a long-running connection for the duration of the apply. await using
        // the scope so the connection is torn down asynchronously when this method
        // returns (important under the SCD-published path where the process exits
        // immediately afterward and a synchronous Dispose would block shutdown
        // logging).
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();

        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count == 0)
            {
                logger.LogInformation("Migrate: no pending migrations (database is up to date).");
                return 0;
            }

            logger.LogInformation(
                "Migrate: applying {Count} pending migration(s): {Migrations}",
                pending.Count,
                string.Join(", ", pending));

            await db.Database.MigrateAsync();

            logger.LogInformation("Migrate: applied {Count} migration(s) successfully.", pending.Count);
            return 0;
        }
#pragma warning disable CA1031 // Do not catch general exception types.
        // Migrate is an install-pipeline entrypoint whose contract is
        // "return 0 on success, return 1 on any failure" — install.sh /
        // install.ps1 inspect the exit code to decide whether to restart the
        // service. Letting an exception escape would crash the .NET host with
        // an unwieldy stack trace and bypass the install-script abort branch.
        // The full exception is logged at Critical for postmortem.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Single LogCritical entry: the exception parameter carries Type +
            // Message + StackTrace, so a separate LogError would just duplicate
            // the headline.
            //
            // Connection-string leakage: Npgsql's NpgsqlConnectionStringBuilder
            // marks Password with [PasswordPropertyText] and redacts it when
            // any NpgsqlException serializes the conn string into its Message.
            // This relies on the Npgsql data-source builder NOT having
            // `IncludeErrorDetail=true` set (which would surface raw parameter
            // values from failing SQL). Program.cs does not set that flag;
            // do not enable it without re-evaluating this log path.
            logger.LogCritical(ex, "Migrate: failed (full exception detail follows).");
            return 1;
        }
    }
}

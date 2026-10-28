using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Sqlite;

/// <summary>
/// Configures jellyfin to use an SQLite database.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-SQLite")]
public sealed class SqliteDatabaseProvider : IJellyfinDatabaseProvider
{
    private const string BackupFolderName = "SQLiteBackups";

    /// <summary>
    /// Rows sampled per index by the scheduled ANALYZE. 0 means a full scan.
    /// </summary>
    private const int ScheduledAnalysisLimit = 1000;

    /// <summary>
    /// VACUUM only when at least this share of the pages is on the free list...
    /// </summary>
    private const long VacuumFreelistPercent = 10;

    /// <summary>
    /// ...and the free list is at least this many pages (about 10 MiB at the default page size), so a
    /// small database never gets rewritten over a handful of free pages.
    /// </summary>
    private const long VacuumMinFreelistPages = 2560;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<SqliteDatabaseProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDatabaseProvider"/> class.
    /// </summary>
    /// <param name="applicationPaths">Service to construct the fallback when the old data path configuration is used.</param>
    /// <param name="logger">A logger.</param>
    public SqliteDatabaseProvider(IApplicationPaths applicationPaths, ILogger<SqliteDatabaseProvider> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        static T? GetOption<T>(ICollection<CustomDatabaseOption>? options, string key, Func<string, T> converter, Func<T>? defaultValue = null)
        {
            if (options is null)
            {
                return defaultValue is not null ? defaultValue() : default;
            }

            var value = options.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (value is null)
            {
                return defaultValue is not null ? defaultValue() : default;
            }

            return converter(value.Value);
        }

        var customOptions = databaseConfiguration.CustomProviderOptions?.Options;

        var sqliteConnectionBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = GetOption(customOptions, "path", e => e, () => Path.Combine(_applicationPaths.DataPath, "jellyfin.db")),
            // Private, not Default: sqlite3_enable_shared_cache is process-global, so a plugin
            // enabling it makes these connections share a cache too. Contention then surfaces as
            // SQLITE_LOCKED ("database table is locked"), which the busy handler does not cover,
            // so busy_timeout is skipped and the command fails at CommandTimeout instead.
            Cache = GetOption(customOptions, "cache", Enum.Parse<SqliteCacheMode>, () => SqliteCacheMode.Private),
            Pooling = GetOption(customOptions, "pooling", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => true),
            DefaultTimeout = GetOption(customOptions, "command-timeout", int.Parse, () => 60)
        };

        var connectionString = sqliteConnectionBuilder.ToString();

        // Log SQLite connection parameters
        _logger.LogInformation("SQLite connection string: {ConnectionString}", connectionString);

        options
            .UseSqlite(
                connectionString,
                sqLiteOptions => sqLiteOptions.MigrationsAssembly(GetType().Assembly))
            // TODO: Remove when https://github.com/dotnet/efcore/pull/35873 is merged & released
            .ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.NonTransactionalMigrationOperationWarning))
            .AddInterceptors(new PragmaConnectionInterceptor(
                _logger,
                // Negative values are KiB. 32 MiB is a ceiling, not an allocation: SQLite only
                // keeps as many pages as it touches, so small databases stay small.
                GetOption(customOptions, "cacheSize", e => int.Parse(e, CultureInfo.InvariantCulture), () => -32_768),
                GetOption(customOptions, "lockingmode", e => e, () => "NORMAL")!,
                GetOption(customOptions, "journalsizelimit", int.Parse, () => 134_217_728),
                GetOption(customOptions, "tempstoremode", int.Parse, () => 2),
                GetOption(customOptions, "syncmode", int.Parse, () => 1),
                // Memory-mapped reads of the database file: 256 MiB of address space, backed by the
                // OS page cache, so hot index pages are not copied through SQLite's own cache.
                GetOption(customOptions, "mmapsize", long.Parse, () => 268_435_456L),
                customOptions?.Where(e => e.Key.StartsWith("#PRAGMA:", StringComparison.OrdinalIgnoreCase)).ToDictionary(e => e.Key["#PRAGMA:".Length..], e => e.Value) ?? []));

        var enableSensitiveDataLogging = GetOption(customOptions, "EnableSensitiveDataLogging", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => false);
        if (enableSensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging(enableSensitiveDataLogging);
            _logger.LogInformation("EnableSensitiveDataLogging is enabled on SQLite connection");
        }

        // The model is compiled at build time (dotnet ef dbcontext optimize, see CompiledModel/), so the
        // first query does not pay for building it by reflection. Regenerate it whenever the entity
        // configuration changes; the tests compare it against the live model.
        options.UseModel(CompiledModel.JellyfinDbContextModel.Instance);
    }

    /// <inheritdoc/>
    public Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        // Approximate statistics (analysis_limit) are what SQLite recommends for periodic runs on large
        // databases; the full scan is reserved for the post-migration run.
        return OptimizeAsync(reclaimSpace: true, analysisLimit: ScheduledAnalysisLimit, cancellationToken);
    }

    /// <inheritdoc/>
    public Task RunPostMigrationOptimisation(CancellationToken cancellationToken)
    {
        // VACUUM rewrites the whole file under an exclusive lock, which is not something to block startup
        // on; the scheduled task reclaims space when the file is actually fragmented.
        return OptimizeAsync(reclaimSpace: false, analysisLimit: 0, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task RefreshStatistics(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return;
        }

        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!await HasLibraryItemsAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                _logger.LogInformation("Analyzing jellyfin.db");
                await AnalyzeAsync(context, ScheduledAnalysisLimit, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.SetDefaultDateTimeKind(DateTimeKind.Utc);
    }

    /// <inheritdoc/>
    public async Task RunShutdownTask(CancellationToken cancellationToken)
    {
        // Run before disposing the application. Only a checkpoint: stopping is on a deadline.

        // Empty the pool first. Anything still parked in it can start reading again between here and the
        // checkpoint, and a reader that holds the write-ahead log open is exactly what makes the truncation
        // fail. Connections handed out already cannot be taken away, but they get disposed on return.
        SqliteConnection.ClearAllPools();

        try
        {
            if (DbContextFactory is not null)
            {
                var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using (context.ConfigureAwait(false))
                {
                    await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // A missed checkpoint only leaves a write-ahead log for the next start to replay, so never fail the
            // shutdown over this.
            _logger.LogError(ex, "Error while checkpointing jellyfin.db");
        }

        // The checkpointing connection went back into the pool, so retire that one as well.
        SqliteConnection.ClearAllPools();
    }

    private async Task OptimizeAsync(bool reclaimSpace, int analysisLimit, CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return;
        }

        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);

                if (reclaimSpace && await IsFragmentedAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    await VacuumAsync(context, cancellationToken).ConfigureAwait(false);
                }

                // Statistics taken while the library is empty make the planner treat every table as one row and
                // pick full scans once it fills up; no statistics at all plan far better until there is data.
                if (await HasLibraryItemsAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    await AnalyzeAsync(context, analysisLimit, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogInformation("Not analyzing jellyfin.db, the library holds no items yet");
                }

                await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("jellyfin.db optimized successfully!");
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    private static Task<bool> HasLibraryItemsAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        // Folders and the seeded placeholder exist before any library has been scanned.
        return context.BaseItems.AnyAsync(e => !e.IsFolder && e.Type != "PLACEHOLDER", cancellationToken);
    }

    private async Task<bool> IsFragmentedAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        var freelistCount = await ReadPragmaValueAsync(context, "freelist_count", cancellationToken).ConfigureAwait(false) ?? 0;
        var pageCount = await ReadPragmaValueAsync(context, "page_count", cancellationToken).ConfigureAwait(false) ?? 0;
        if (pageCount == 0)
        {
            return false;
        }

        var freePercent = freelistCount * 100 / pageCount;
        var fragmented = freelistCount >= VacuumMinFreelistPages && freePercent >= VacuumFreelistPercent;
        _logger.LogInformation(
            "jellyfin.db has {FreePages} free pages of {TotalPages} ({FreePercent}%), {Action}",
            freelistCount,
            pageCount,
            freePercent,
            fragmented ? "vacuuming" : "skipping vacuum");
        return fragmented;
    }

    private async Task VacuumAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        var tempStore = await ReadPragmaValueAsync(context, "temp_store", cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Rebuilding jellyfin.db on disk, scratch space goes to {TempDirectory}",
            Environment.GetEnvironmentVariable("SQLITE_TMPDIR") ?? "SQLite's default temporary directory");
        await context.Database.ExecuteSqlRawAsync("PRAGMA temp_store=1", cancellationToken).ConfigureAwait(false);
        try
        {
            await context.Database.ExecuteSqlRawAsync("VACUUM", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The connection goes back to the pool, so hand it over the way it was handed to us.
            if (tempStore is not null)
            {
                await context.Database.ExecuteSqlRawAsync(
                    FormattableString.Invariant($"PRAGMA temp_store={tempStore.Value}"),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task AnalyzeAsync(JellyfinDbContext context, int analysisLimit, CancellationToken cancellationToken)
    {
        var previousLimit = await ReadPragmaValueAsync(context, "analysis_limit", cancellationToken).ConfigureAwait(false);

        await context.Database.ExecuteSqlRawAsync(FormattableString.Invariant($"PRAGMA analysis_limit={analysisLimit}"), cancellationToken).ConfigureAwait(false);
        try
        {
            await context.Database.ExecuteSqlRawAsync("ANALYZE", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The connection goes back to the pool, so hand it over the way it was handed to us.
            if (previousLimit is not null)
            {
                await context.Database.ExecuteSqlRawAsync(
                    FormattableString.Invariant($"PRAGMA analysis_limit={previousLimit.Value}"),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<long?> ReadPragmaValueAsync(JellyfinDbContext context, string pragma, CancellationToken cancellationToken)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // The pragma name is a compile-time constant.
            command.CommandText = "PRAGMA " + pragma;
#pragma warning restore CA2100
            return await ReadPragmaValueAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long?> ReadPragmaValueAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Conventions.Add(_ => new DoNotUseReturningClauseConvention());
    }

    /// <inheritdoc />
    public async Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        var path = GetDatabasePath();
        var backupFolder = Path.Combine(_applicationPaths.DataPath, BackupFolderName);
        Directory.CreateDirectory(backupFolder);

        if (DbContextFactory is not null && File.Exists(path))
        {
            var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
            }
        }

        var key = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupFile = Path.Combine(backupFolder, $"{key}_jellyfin.db");
        for (var attempt = 1; File.Exists(backupFile); attempt++)
        {
            key = string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{attempt}");
            backupFile = Path.Combine(backupFolder, $"{key}_jellyfin.db");
        }

        File.Copy(path, backupFile);
        return key;
    }

    /// <inheritdoc />
    public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        // ensure there are absolutely no dangling Sqlite connections.
        SqliteConnection.ClearAllPools();
        var path = GetDatabasePath();
        var backupFile = Path.Combine(_applicationPaths.DataPath, BackupFolderName, $"{key}_jellyfin.db");

        if (!File.Exists(backupFile))
        {
            _logger.LogCritical("Tried to restore a backup that does not exist: {Key}", key);
            return Task.CompletedTask;
        }

        if (!TryRetireWriteAheadLog(path))
        {
            _logger.LogCritical(
                "Refusing to restore jellyfin.db: the write-ahead log at {WriteAheadLog} could not be retired, which "
                + "means the database is still open and replacing it now would silently bring back the data this "
                + "rollback is undoing. Stop the server and copy {Backup} over {Path} by hand.",
                path + "-wal",
                backupFile,
                path);
            return Task.CompletedTask;
        }

        File.Copy(backupFile, path, true);

        return Task.CompletedTask;
    }

    private bool TryRetireWriteAheadLog(string path)
    {
        var writeAheadLogPath = path + "-wal";
        if (!File.Exists(path) || !File.Exists(writeAheadLogPath))
        {
            return true;
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            // Either something else holds the database or it is too damaged to open. The check below covers both.
            _logger.LogError(ex, "Could not open jellyfin.db to retire its write-ahead log");
        }

        return !File.Exists(writeAheadLogPath);
    }

    /// <inheritdoc />
    public Task DeleteBackup(string key)
    {
        var backupFile = Path.Combine(_applicationPaths.DataPath, BackupFolderName, $"{key}_jellyfin.db");

        if (!File.Exists(backupFile))
        {
            _logger.LogCritical("Tried to delete a backup that does not exist: {Key}", key);
            return Task.CompletedTask;
        }

        File.Delete(backupFile);
        return Task.CompletedTask;
    }

    private string GetDatabasePath()
    {
        if (DbContextFactory is null)
        {
            throw new InvalidOperationException("The database provider has not been initialized.");
        }

        using var dbContext = DbContextFactory.CreateDbContext();
        return dbContext.Database.GetDbConnection().DataSource;
    }

    /// <inheritdoc/>
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(tableNames);

        var deleteQueries = new List<string>();
        foreach (var tableName in tableNames)
        {
            deleteQueries.Add($"DELETE FROM \"{tableName}\";");
        }

        var deleteAllQuery =
        $"""
        PRAGMA foreign_keys = OFF;
        {string.Join('\n', deleteQueries)}
        PRAGMA foreign_keys = ON;
        """;

        await dbContext.Database.ExecuteSqlRawAsync(deleteAllQuery).ConfigureAwait(false);
    }
}

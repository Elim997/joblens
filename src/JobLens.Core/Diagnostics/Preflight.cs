using System.Globalization;
using System.Net.Sockets;
using JobLens.Core.Configuration;
using JobLens.Core.Resume;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace JobLens.Core.Diagnostics;

public enum PreflightStatus
{
    Success,
    Fatal,
    Degraded,
    Cancelled,
}

public record PreflightResult(
    PreflightStatus Status,
    string Build,
    ReziTokenCacheStatus? ReziTokenCache,
    DateTimeOffset? LatestConfiguredGroupMessage,
    bool? BridgeHealthy,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Failures)
{
    public int ExitCode => Status switch
    {
        PreflightStatus.Success => 0,
        PreflightStatus.Fatal => 1,
        PreflightStatus.Degraded => 3,
        PreflightStatus.Cancelled => 4,
        _ => throw new InvalidOperationException($"Unknown preflight status '{Status}'."),
    };
}

public interface IPostgresReadinessProbe
{
    Task ProbeAsync(CancellationToken cancellationToken);
}

public sealed class NpgsqlPostgresReadinessProbe(IServiceProvider services) : IPostgresReadinessProbe
{
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        var dataSource = services.GetService(typeof(NpgsqlDataSource)) as NpgsqlDataSource
            ?? throw new InvalidOperationException("NpgsqlDataSource is not registered.");
        await using var command = dataSource.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(cancellationToken);
    }
}

public interface ISourceFreshnessProbe
{
    Task<DateTimeOffset?> GetLatestConfiguredGroupMessageAsync(CancellationToken cancellationToken);
}

public sealed class SqliteSourceFreshnessProbe(IOptions<JobLensOptions> options) : ISourceFreshnessProbe
{
    public async Task<DateTimeOffset?> GetLatestConfiguredGroupMessageAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = configured.MessagesDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var placeholders = configured.GroupChatJids.Select((_, index) => $"@chatJid{index}").ToArray();
        command.CommandText = $"SELECT MAX(timestamp) FROM messages WHERE chat_jid IN ({string.Join(", ", placeholders)})";
        for (var i = 0; i < configured.GroupChatJids.Length; i++)
            command.Parameters.AddWithValue($"@chatJid{i}", configured.GroupChatJids[i]);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
            return null;

        return DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
    }
}

public interface IBridgeHealthProbe
{
    Task<bool> IsHealthyAsync(int port, CancellationToken cancellationToken);
}

public interface IPreflightRuntime
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemPreflightRuntime : IPreflightRuntime
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public sealed class TcpBridgeHealthProbe : IBridgeHealthProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    public async Task<bool> IsHealthyAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        try
        {
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

public interface IPreflight
{
    Task<PreflightResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Shared, read-only environment diagnostics. Lock acquisition is intentionally not part of this
/// service: --preflight can run while a pipeline invocation owns RunLock, while F6's --run-once
/// will acquire the lock before invoking this same implementation.
/// </summary>
public sealed class Preflight(
    IConfiguration configuration,
    IOptions<JobLensOptions> options,
    IPostgresReadinessProbe postgres,
    IReziTokenCacheDiagnostics tokenCache,
    ISourceFreshnessProbe source,
    IBridgeHealthProbe bridge,
    IPreflightRuntime runtime,
    ILogger<Preflight> logger) : IPreflight
{
    private const int PostgresAttempts = 5;
    private static readonly TimeSpan PostgresRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QuietFeedWarningThreshold = TimeSpan.FromHours(12);

    public async Task<PreflightResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var failures = new List<string>();
        ReziTokenCacheStatus? tokenStatus = null;
        DateTimeOffset? latestMessage = null;
        bool? bridgeHealthy = null;

        logger.LogInformation("Preflight build {Build}", BuildInfo.Marker);

        try
        {
            RequiredConfigurationValidator.Validate(configuration);
            logger.LogInformation("Preflight configuration validation succeeded.");
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
            logger.LogError(ex, "Preflight configuration validation failed.");
            return CreateResult(PreflightStatus.Fatal);
        }

        try
        {
            await ProbePostgresWithRetryAsync(cancellationToken);
            logger.LogInformation("Preflight PostgreSQL readiness probe succeeded.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Preflight was cancelled during PostgreSQL readiness probing.");
            return CreateResult(PreflightStatus.Cancelled);
        }
        catch (Exception ex)
        {
            failures.Add($"PostgreSQL readiness failed: {ex.Message}");
            logger.LogError(ex, "Preflight PostgreSQL readiness failed after {Attempts} attempts.", PostgresAttempts);
            return CreateResult(PreflightStatus.Fatal);
        }

        try
        {
            var inspection = await tokenCache.InspectAsync(cancellationToken);
            tokenStatus = inspection.Status;
            switch (inspection.Status)
            {
                case ReziTokenCacheStatus.Absent:
                    logger.LogInformation("Preflight Rezi token cache is absent; this is expected before login or after expiration.");
                    break;
                case ReziTokenCacheStatus.Present:
                    logger.LogInformation("Preflight Rezi token cache is present and structurally parseable; live Rezi acceptance was not tested.");
                    break;
                case ReziTokenCacheStatus.Undecryptable:
                    var tokenWarning = "Rezi token cache exists but could not be decrypted or parsed; verify the Task Scheduler user and DPAPI CurrentUser context.";
                    warnings.Add(tokenWarning);
                    logger.LogWarning(inspection.Exception, "{Warning}", tokenWarning);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown Rezi token cache status '{inspection.Status}'.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Preflight was cancelled while inspecting the Rezi token cache.");
            return CreateResult(PreflightStatus.Cancelled);
        }
        catch (Exception ex)
        {
            var tokenWarning = $"Rezi token cache could not be inspected: {ex.Message}";
            warnings.Add(tokenWarning);
            logger.LogWarning(ex, "{Warning}", tokenWarning);
        }

        try
        {
            latestMessage = await source.GetLatestConfiguredGroupMessageAsync(cancellationToken);
            logger.LogInformation(
                "Preflight newest configured-group source message is {LatestMessage}.",
                latestMessage?.ToString("O") ?? "absent");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Preflight was cancelled while reading WhatsApp source freshness.");
            return CreateResult(PreflightStatus.Cancelled);
        }
        catch (Exception ex)
        {
            var sourceFailure = $"WhatsApp source database is unreadable: {ex.Message}";
            failures.Add(sourceFailure);
            logger.LogWarning(ex, "{Failure}", sourceFailure);
        }

        try
        {
            bridgeHealthy = await bridge.IsHealthyAsync(options.Value.BridgeHealthPort, cancellationToken);
            if (bridgeHealthy == true)
            {
                logger.LogInformation(
                    "Preflight WhatsApp bridge TCP probe succeeded on 127.0.0.1:{Port}.",
                    options.Value.BridgeHealthPort);
            }
            else
            {
                var bridgeFailure = $"WhatsApp bridge TCP probe failed on 127.0.0.1:{options.Value.BridgeHealthPort}.";
                failures.Add(bridgeFailure);
                logger.LogWarning("{Failure}", bridgeFailure);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Preflight was cancelled during the WhatsApp bridge TCP probe.");
            return CreateResult(PreflightStatus.Cancelled);
        }
        catch (Exception ex)
        {
            bridgeHealthy = false;
            var bridgeFailure =
                $"WhatsApp bridge TCP probe failed on 127.0.0.1:{options.Value.BridgeHealthPort}: {ex.Message}";
            failures.Add(bridgeFailure);
            logger.LogWarning(ex, "{Failure}", bridgeFailure);
        }

        if (bridgeHealthy == true &&
            latestMessage is DateTimeOffset timestamp &&
            runtime.UtcNow - timestamp > QuietFeedWarningThreshold)
        {
            var staleWarning = $"Newest configured-group WhatsApp message is older than {QuietFeedWarningThreshold.TotalHours:0} hours; a quiet feed may be expected.";
            warnings.Add(staleWarning);
            logger.LogWarning("{Warning}", staleWarning);
        }

        var status = failures.Count == 0 ? PreflightStatus.Success : PreflightStatus.Degraded;
        return CreateResult(status);

        PreflightResult CreateResult(PreflightStatus status) =>
            new(
                status,
                BuildInfo.Marker,
                tokenStatus,
                latestMessage,
                bridgeHealthy,
                warnings.ToList(),
                failures.ToList());
    }

    private async Task ProbePostgresWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= PostgresAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await postgres.ProbeAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < PostgresAttempts)
            {
                logger.LogWarning(
                    ex,
                    "Preflight PostgreSQL readiness attempt {Attempt}/{Attempts} failed; retrying in {DelaySeconds} seconds.",
                    attempt,
                    PostgresAttempts,
                    PostgresRetryDelay.TotalSeconds);
                await runtime.DelayAsync(PostgresRetryDelay, cancellationToken);
            }
        }
    }
}

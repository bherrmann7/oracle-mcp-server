using System;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;

namespace OracleMcpServer;

/// <summary>
/// Provides resilient Oracle database connection handling with:
/// - Connection pooling disabled (prevents stale connection issues in long-lived MCP server)
/// - Connection timeouts to prevent hanging
/// - Retry logic with exponential backoff at the operation level
/// - Proper cancellation token support
/// </summary>
public static class OracleConnectionHelper
{
    private const int ConnectionTimeoutSeconds = 15;
    private const int CommandTimeoutSeconds = 120;
    private const int MaxRetryAttempts = 3;
    private const int InitialRetryDelayMs = 1000;

    /// <summary>
    /// Opens a single connection attempt with timeout. No internal retries —
    /// retries are handled by ExecuteWithResilienceAsync to avoid nested retry loops.
    /// </summary>
    public static async Task<OracleConnection> OpenConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        connectionString = EnsureConnectionSettings(connectionString);
        var connection = new OracleConnection(connectionString);

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectionTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await connection.OpenAsync(linkedCts.Token);
            return connection;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await SafeDisposeAsync(connection);
            throw new TimeoutException($"Connection timed out after {ConnectionTimeoutSeconds} seconds");
        }
        catch
        {
            await SafeDisposeAsync(connection);
            throw;
        }
    }

    /// <summary>
    /// Executes an operation with a single retry loop that covers both connection and command failures.
    /// </summary>
    public static async Task<T> ExecuteWithResilienceAsync<T>(
        string connectionString,
        Func<OracleConnection, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            OracleConnection? connection = null;

            try
            {
                connection = await OpenConnectionAsync(connectionString, cancellationToken);

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(CommandTimeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                return await operation(connection, linkedCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancelled — don't retry, just propagate
                throw;
            }
            catch (OracleException oex) when (!IsRecoverableError(oex))
            {
                // SQL errors like ORA-00904 (invalid identifier) should fail immediately
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                Console.Error.WriteLine($"[OracleMCP] Attempt {attempt}/{MaxRetryAttempts} failed: {ex.Message}");
            }
            finally
            {
                if (connection != null)
                {
                    await SafeDisposeAsync(connection);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (attempt < MaxRetryAttempts)
            {
                var delayMs = InitialRetryDelayMs * (int)Math.Pow(2, attempt - 1);
                Console.Error.WriteLine($"[OracleMCP] Retrying in {delayMs}ms...");
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Failed after {MaxRetryAttempts} attempts. Last error: {lastException?.Message}",
            lastException);
    }

    /// <summary>
    /// Safely disposes a connection without throwing.
    /// </summary>
    private static async Task SafeDisposeAsync(OracleConnection connection)
    {
        try
        {
            if (connection.State != System.Data.ConnectionState.Closed)
            {
                await Task.Run(() => connection.Close());
            }

            connection.Dispose();
        }
        catch
        {
            // Swallow close/dispose errors
        }
    }

    /// <summary>
    /// Configures connection string for MCP server use:
    /// - Pooling disabled to avoid stale connections in long-lived, sporadic-use process
    /// - Connection timeout enforced
    /// </summary>
    private static string EnsureConnectionSettings(string connectionString)
    {
        var builder = new OracleConnectionStringBuilder(connectionString);

        // Disable connection pooling entirely.
        // MCP servers are long-lived but usage is sporadic — pooled connections
        // go stale between uses and cause ORA-50000/50201 errors.
        builder.Pooling = false;

        // Enforce connection timeout
        if (builder.ConnectionTimeout == 0 || builder.ConnectionTimeout > ConnectionTimeoutSeconds)
        {
            builder.ConnectionTimeout = ConnectionTimeoutSeconds;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Checks if an Oracle error is recoverable and worth retrying.
    /// Returns false for SQL/semantic errors that will always fail.
    /// </summary>
    private static bool IsRecoverableError(OracleException oex)
    {
        return oex.Number switch
        {
            // Connection / network errors
            12170 => true, // TNS:Connect timeout occurred
            12541 => true, // TNS:no listener
            12543 => true, // TNS:destination host unreachable
            12545 => true, // TNS:name lookup failure
            12560 => true, // TNS:protocol adapter error
            12571 => true, // TNS:packet writer failure

            // Session/connection state errors
            28 => true,    // Your session has been killed
            1012 => true,  // Not logged on
            1033 => true,  // Oracle initialization or shutdown in progress
            1034 => true,  // Oracle not available
            1089 => true,  // Immediate shutdown in progress
            1090 => true,  // Shutdown in progress
            1092 => true,  // Oracle instance terminated
            3113 => true,  // End-of-file on communication channel
            3114 => true,  // Not connected to Oracle
            3135 => true,  // Connection lost contact

            // Driver-level errors
            17002 => true, // Connection failure
            17008 => true, // Connection closed
            17410 => true, // No more data to read from socket
            24338 => true, // Statement handle not executed

            // ODP.NET managed driver errors (50000+)
            50000 => true, // Connection request timed out
            50201 => true, // Failed to connect / parse connect string

            // Any ODP.NET internal error >= 50000 is worth retrying
            _ => oex.Number >= 50000
        };
    }
}

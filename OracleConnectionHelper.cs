using System;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;

namespace OracleMcpServer;

/// <summary>
/// Provides resilient Oracle database connection handling with:
/// - Connection timeouts to prevent hanging
/// - Retry logic with exponential backoff
/// - Connection pool management for stale connections
/// - Proper cancellation token support
/// </summary>
public static class OracleConnectionHelper
{
    // Configuration constants
    private const int ConnectionTimeoutSeconds = 15;
    private const int CommandTimeoutSeconds = 60;  // Reduced from 120 to fail faster
    private const int MaxRetryAttempts = 3;
    private const int InitialRetryDelayMs = 500;

    /// <summary>
    /// Opens a connection with timeout, retry logic, and stale connection handling.
    /// </summary>
    public static async Task<OracleConnection> OpenConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        // Enhance connection string with timeout settings if not already present
        connectionString = EnsureConnectionTimeouts(connectionString);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            var connection = new OracleConnection(connectionString);

            try
            {
                // Create a timeout cancellation token
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectionTimeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                await connection.OpenAsync(linkedCts.Token);

                // Validate the connection is actually usable
                if (await ValidateConnectionAsync(connection, linkedCts.Token))
                {
                    return connection;
                }

                // Connection validation failed - close and retry
                await SafeCloseConnectionAsync(connection);
                lastException = new InvalidOperationException("Connection validation failed - connection may be stale");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // This was our timeout, not the caller's cancellation
                await SafeCloseConnectionAsync(connection);
                lastException = new TimeoutException($"Connection attempt {attempt} timed out after {ConnectionTimeoutSeconds} seconds");

                // Clear the connection pool to remove potentially stale connections
                ClearPoolForConnectionString(connectionString);
            }
            catch (OracleException oex)
            {
                await SafeCloseConnectionAsync(connection);
                lastException = oex;

                // Check if this is a recoverable error that warrants a retry
                if (!IsRecoverableError(oex))
                {
                    throw; // Non-recoverable errors should fail immediately
                }

                // Clear pool on connection errors to remove stale connections
                if (IsConnectionPoolError(oex))
                {
                    ClearPoolForConnectionString(connectionString);
                }
            }
            catch (Exception ex)
            {
                await SafeCloseConnectionAsync(connection);
                lastException = ex;
            }

            // Check if caller cancelled before retrying
            cancellationToken.ThrowIfCancellationRequested();

            // Wait before retrying with exponential backoff
            if (attempt < MaxRetryAttempts)
            {
                var delayMs = InitialRetryDelayMs * (int)Math.Pow(2, attempt - 1);
                Console.Error.WriteLine($"Connection attempt {attempt} failed, retrying in {delayMs}ms...");
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Failed to connect to database after {MaxRetryAttempts} attempts. Last error: {lastException?.Message}",
            lastException);
    }

    /// <summary>
    /// Executes an operation with proper timeout and error handling.
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

                // Create timeout for the operation itself
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(CommandTimeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                return await operation(connection, linkedCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException($"Operation timed out after {CommandTimeoutSeconds} seconds");

                // Clear pool on timeout
                ClearPoolForConnectionString(connectionString);
            }
            catch (OracleException oex)
            {
                lastException = oex;

                // Check for stale connection errors that warrant retry
                if (IsStaleConnectionError(oex))
                {
                    ClearPoolForConnectionString(connectionString);

                    if (attempt < MaxRetryAttempts)
                    {
                        Console.Error.WriteLine($"Detected stale connection (ORA-{oex.Number}), clearing pool and retrying...");
                        continue; // Retry without delay for stale connections
                    }
                }

                // Non-recoverable error or max retries reached
                throw;
            }
            finally
            {
                if (connection != null)
                {
                    await SafeCloseConnectionAsync(connection);
                }
            }

            // Check if caller cancelled before retrying
            cancellationToken.ThrowIfCancellationRequested();

            // Wait before retrying
            if (attempt < MaxRetryAttempts)
            {
                var delayMs = InitialRetryDelayMs * (int)Math.Pow(2, attempt - 1);
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Operation failed after {MaxRetryAttempts} attempts. Last error: {lastException?.Message}",
            lastException);
    }

    /// <summary>
    /// Validates that a connection is actually usable by running a simple query.
    /// </summary>
    private static async Task<bool> ValidateConnectionAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            using var command = new OracleCommand("SELECT 1 FROM DUAL", connection);
            command.CommandTimeout = 5; // Quick validation timeout
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Safely closes a connection without throwing.
    /// </summary>
    private static async Task SafeCloseConnectionAsync(OracleConnection connection)
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
            // Ignore close errors
        }
    }

    /// <summary>
    /// Clears the connection pool for a given connection string to remove stale connections.
    /// </summary>
    private static void ClearPoolForConnectionString(string connectionString)
    {
        try
        {
            OracleConnection.ClearPool(new OracleConnection(connectionString));
            Console.Error.WriteLine("Connection pool cleared.");
        }
        catch
        {
            // Ignore pool clear errors
        }
    }

    /// <summary>
    /// Ensures the connection string has appropriate timeout settings.
    /// </summary>
    private static string EnsureConnectionTimeouts(string connectionString)
    {
        var builder = new OracleConnectionStringBuilder(connectionString);

        // Set connection timeout if not already set
        if (builder.ConnectionTimeout == 0 || builder.ConnectionTimeout > ConnectionTimeoutSeconds)
        {
            builder.ConnectionTimeout = ConnectionTimeoutSeconds;
        }

        // Enable connection lifetime to help with stale connections
        // Connections older than this will be discarded when returned to pool
        if (!connectionString.Contains("Connection Lifetime", StringComparison.OrdinalIgnoreCase))
        {
            builder["Connection Lifetime"] = 180; // 3 minutes (reduced from 5)
        }

        // Validate connection on borrow from pool
        if (!connectionString.Contains("Validate Connection", StringComparison.OrdinalIgnoreCase))
        {
            builder["Validate Connection"] = true;
        }
        
        // Limit pool size to prevent too many stale connections
        if (!connectionString.Contains("Min Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            builder["Min Pool Size"] = 1;
        }
        if (!connectionString.Contains("Max Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            builder["Max Pool Size"] = 5;
        }
        
        // Reduce how long connections can be idle before being removed
        if (!connectionString.Contains("Incr Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            builder["Incr Pool Size"] = 1;
        }
        if (!connectionString.Contains("Decr Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            builder["Decr Pool Size"] = 1;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Checks if an Oracle error is recoverable and worth retrying.
    /// </summary>
    private static bool IsRecoverableError(OracleException oex)
    {
        // Common recoverable Oracle error codes
        return oex.Number switch
        {
            // Connection errors
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

            // Temporary resource errors
            17002 => true, // Connection failure
            17008 => true, // Connection closed
            17410 => true, // No more data to read from socket
            24338 => true, // Statement handle not executed

            // ODP.NET internal errors (high numbers are internal driver errors)
            50000 => true, // Connection request timed out (ODP.NET internal)
            50201 => true, // Failed to connect/parse connect string (ODP.NET internal)
            
            // Any error >= 50000 is likely an ODP.NET internal error worth retrying
            _ => oex.Number >= 50000
        };
    }

    /// <summary>
    /// Checks if an error indicates a stale/dead connection that should be removed from pool.
    /// </summary>
    private static bool IsStaleConnectionError(OracleException oex)
    {
        return oex.Number switch
        {
            28 => true,    // Your session has been killed
            1012 => true,  // Not logged on
            3113 => true,  // End-of-file on communication channel
            3114 => true,  // Not connected to Oracle
            3135 => true,  // Connection lost contact
            17002 => true, // Connection failure
            17008 => true, // Connection closed
            17410 => true, // No more data to read from socket
            50000 => true, // Connection request timed out (ODP.NET internal)
            50201 => true, // Failed to connect/parse connect string (ODP.NET internal)
            _ => oex.Number >= 50000 // Any ODP.NET internal error
        };
    }

    /// <summary>
    /// Checks if the error indicates a connection pool issue.
    /// </summary>
    private static bool IsConnectionPoolError(OracleException oex)
    {
        return IsStaleConnectionError(oex) || oex.Number switch
        {
            12170 => true, // TNS:Connect timeout
            12560 => true, // TNS:protocol adapter error
            _ => false
        };
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;

namespace OracleMcpServer;

/// <summary>
/// Background service that pings every configured schema's connection every 2 minutes
/// with SELECT 1 FROM DUAL. This keeps at least one connection warm in each pool and
/// prevents firewall/NAT idle timeouts from silently killing sockets overnight.
/// </summary>
public class ConnectionKeepAliveService : BackgroundService
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);

    private readonly IConfiguration _configuration;

    public ConnectionKeepAliveService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.Error.WriteLine("[OracleMCP-KeepAlive] Service started");

        // Wait a bit before the first ping to let the host finish starting up
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await PingAllSchemasAsync(stoppingToken);

            try
            {
                await Task.Delay(PingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Console.Error.WriteLine("[OracleMCP-KeepAlive] Service stopped");
    }

    private async Task PingAllSchemasAsync(CancellationToken stoppingToken)
    {
        var connectionStrings = _configuration.GetSection("ConnectionStrings");

        foreach (var child in connectionStrings.GetChildren())
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            var schema = child.Key;
            var connectionString = child.Value;

            if (string.IsNullOrWhiteSpace(connectionString))
                continue;

            await PingSchemaAsync(schema, connectionString, stoppingToken);
        }
    }

    private static async Task PingSchemaAsync(string schema, string connectionString, CancellationToken stoppingToken)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(PingTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

            using var connection = await OracleConnectionHelper.OpenConnectionAsync(connectionString, linkedCts.Token);
            using var cmd = new OracleCommand("SELECT 1 FROM DUAL", connection);
            await cmd.ExecuteScalarAsync(linkedCts.Token);

            Console.Error.WriteLine($"[OracleMCP-KeepAlive] Ping OK: {schema}");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — stop quietly
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OracleMCP-KeepAlive] Ping FAILED for {schema}: {ex.Message}");
        }
    }
}

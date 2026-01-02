using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace OracleMcpServer;

[McpServerToolType]
public static class OracleDatabaseTools
{
    // Helper method to get connection string by schema name
    private static string GetConnectionString(string schema)
    {
        var configuration = ConfigurationHelper.LoadConfiguration();
        var connectionString = configuration.GetConnectionString(schema);

        if (string.IsNullOrEmpty(connectionString))
            throw new ArgumentException($"No connection string found for schema '{schema}'. Available schemas can be found using ListAvailableSchemas()");

        return connectionString;
    }


    [McpServerTool]
    [Description("Execute a SELECT query against the Oracle database and return results as JSON")]
    public static async Task<string> ExecuteQuery(
        string sql,
        [Description("Schema/user name to use (e.g., 'bherrmann', 'tbherrmann', 'profitshare')")]
        string schema,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var connectionString = GetConnectionString(schema);

            var results = await OracleConnectionHelper.ExecuteWithResilienceAsync(
                connectionString,
                async (connection, ct) =>
                {
                    using var command = new OracleCommand(sql, connection);
                    using var reader = await command.ExecuteReaderAsync(ct);

                    var queryResults = new List<Dictionary<string, object?>>();

                    while (await reader.ReadAsync(ct))
                    {
                        var row = new Dictionary<string, object?>();
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            var columnName = reader.GetName(i);
                            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);

                            // Convert Oracle types to JSON-serializable types
                            if (value is DateTime dt)
                                value = dt.ToString("yyyy-MM-dd HH:mm:ss");
                            else if (value is decimal dec)
                                value = dec;
                            else if (value is OracleDecimal oracleDecimal)
                                value = oracleDecimal.IsNull ? null : oracleDecimal.Value;
                            else if (value is OracleString oracleString)
                                value = oracleString.IsNull ? null : oracleString.Value;
                            else if (value is OracleDate oracleDate)
                                value = oracleDate.IsNull ? null : oracleDate.Value.ToString("yyyy-MM-dd HH:mm:ss");
                            else if (value is OracleTimeStamp oracleTimeStamp)
                                value = oracleTimeStamp.IsNull ? null : oracleTimeStamp.Value.ToString("yyyy-MM-dd HH:mm:ss.fff");

                            row[columnName] = value;
                        }

                        queryResults.Add(row);
                    }

                    return queryResults;
                },
                cancellationToken);

            var response = new
            {
                success = true,
                rowCount = results.Count,
                schema,
                data = results
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex)
        {
            var errorResponse = new
            {
                success = false,
                error = ex.Message,
                schema,
                sqlState = ex is OracleException oex ? oex.Number.ToString() : null
            };

            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
    }

    [McpServerTool]
    [Description("Execute a non-query SQL statement (INSERT, UPDATE, DELETE, CREATE, etc.) against the Oracle database")]
    public static async Task<string> ExecuteNonQuery(
        string sql,
        [Description("Schema/user name to use (e.g., 'bherrmann', 'tbherrmann', 'profitshare')")]
        string schema,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var connectionString = GetConnectionString(schema);

            var rowsAffected = await OracleConnectionHelper.ExecuteWithResilienceAsync(
                connectionString,
                async (connection, ct) =>
                {
                    using var command = new OracleCommand(sql, connection);
                    return await command.ExecuteNonQueryAsync(ct);
                },
                cancellationToken);

            var response = new
            {
                success = true,
                rowsAffected,
                schema,
                message = $"Command executed successfully. {rowsAffected} rows affected."
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex)
        {
            var errorResponse = new
            {
                success = false,
                error = ex.Message,
                schema,
                sqlState = ex is OracleException oex ? oex.Number.ToString() : null
            };

            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
    }

    [McpServerTool]
    [Description("Test the Oracle database connection")]
    public static async Task<string> TestConnection(
        [Description("Schema/user name to test (e.g., 'bherrmann', 'tbherrmann', 'profitshare')")]
        string schema,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var connectionString = GetConnectionString(schema);

            var (serverTime, databaseVersion) = await OracleConnectionHelper.ExecuteWithResilienceAsync(
                connectionString,
                async (connection, ct) =>
                {
                    // Test with a simple query
                    using var command = new OracleCommand("SELECT SYSDATE FROM DUAL", connection);
                    var result = await command.ExecuteScalarAsync(ct);
                    return (result?.ToString(), connection.ServerVersion);
                },
                cancellationToken);

            var response = new
            {
                success = true,
                message = "Connection successful",
                schema,
                serverTime,
                databaseVersion
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex)
        {
            var errorResponse = new
            {
                success = false,
                error = ex.Message,
                schema,
                sqlState = ex is OracleException oex ? oex.Number.ToString() : null
            };

            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
    }

    [McpServerTool]
    [Description("List available schemas/connection strings")]
    public static Task<string> ListAvailableSchemas()
    {
        try
        {
            var configuration = ConfigurationHelper.LoadConfiguration();
            var connectionStrings = configuration.GetSection("ConnectionStrings");
            var schemas = new List<string>();

            foreach (var child in connectionStrings.GetChildren()) schemas.Add(child.Key);

            var credentialsPath = ConfigurationHelper.GetCredentialsPath();

            var response = new
            {
                success = true,
                availableSchemas = schemas,
                credentialsSource = File.Exists(credentialsPath) ? credentialsPath : "appsettings.json"
            };

            return Task.FromResult(JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch (Exception ex)
        {
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };

            return Task.FromResult(JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
    }
}
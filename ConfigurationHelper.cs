using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace OracleMcpServer;

public static class ConfigurationHelper
{
    public static string GetCredentialsPath()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDirectory, ".oracle-mcp-server-creds.json");
    }

    public static IConfiguration LoadConfiguration()
    {
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", false, true);

        var credentialsPath = GetCredentialsPath();

        if (File.Exists(credentialsPath))
            try
            {
                using var fileStream = File.OpenRead(credentialsPath);
                using var reader = new StreamReader(fileStream);
                var credentialsJson = reader.ReadToEnd();

                if (string.IsNullOrWhiteSpace(credentialsJson)) throw new InvalidOperationException("Credentials file is empty");

                var credentialsConfig = JsonSerializer.Deserialize<JsonElement>(credentialsJson);

                if (!credentialsConfig.TryGetProperty("ConnectionStrings", out var credConnectionStrings))
                    throw new InvalidOperationException("Credentials file must contain a 'ConnectionStrings' section");

                if (credConnectionStrings.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("'ConnectionStrings' must be an object");

                var credentialsDict = new Dictionary<string, string?>();
                foreach (var prop in credConnectionStrings.EnumerateObject())
                {
                    var connectionString = prop.Value.GetString();
                    if (string.IsNullOrWhiteSpace(connectionString))
                    {
                        Console.Error.WriteLine($"WARNING: Empty connection string for schema '{prop.Name}' - skipping");
                        continue;
                    }

                    credentialsDict[$"ConnectionStrings:{prop.Name}"] = connectionString;
                }

                if (credentialsDict.Count == 0) throw new InvalidOperationException("No valid connection strings found in credentials file");

                configBuilder.AddInMemoryCollection(credentialsDict);
            }
            catch (Exception)
            {
                // Ignore credentials file loading errors - will fall back to appsettings.json
            }

        return configBuilder.Build();
    }

    public static void LoadCredentialsIntoConfigurationBuilder(IConfigurationBuilder configBuilder)
    {
        var credentialsPath = GetCredentialsPath();

        if (File.Exists(credentialsPath))
        {
            try
            {
                using var fileStream = File.OpenRead(credentialsPath);
                using var reader = new StreamReader(fileStream);
                var credentialsJson = reader.ReadToEnd();

                if (string.IsNullOrWhiteSpace(credentialsJson)) throw new InvalidOperationException("Credentials file is empty");

                var credentialsConfig = JsonSerializer.Deserialize<JsonElement>(credentialsJson);

                if (!credentialsConfig.TryGetProperty("ConnectionStrings", out var credConnectionStrings))
                    throw new InvalidOperationException("Credentials file must contain a 'ConnectionStrings' section");

                if (credConnectionStrings.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("'ConnectionStrings' must be an object");

                var credentialsDict = new Dictionary<string, string?>();
                foreach (var prop in credConnectionStrings.EnumerateObject())
                {
                    var connectionString = prop.Value.GetString();
                    if (string.IsNullOrWhiteSpace(connectionString))
                    {
                        Console.Error.WriteLine($"WARNING: Empty connection string for schema '{prop.Name}' - skipping");
                        continue;
                    }

                    credentialsDict[$"ConnectionStrings:{prop.Name}"] = connectionString;
                }

                if (credentialsDict.Count == 0) throw new InvalidOperationException("No valid connection strings found in credentials file");

                configBuilder.AddInMemoryCollection(credentialsDict);
                Console.Error.WriteLine($"\n\nLoaded credentials from: {credentialsPath}\n\n");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\n\nERROR loading credentials file: {ex.Message}\n\n");
                Console.Error.WriteLine("Continuing without credentials file - tools may not work properly.\n");
            }
        }
        else
        {
            Console.Error.WriteLine($"\n\nWARNING: Credentials file not found at: {credentialsPath}\n\n");
            Console.Error.WriteLine("Please create this file with your Oracle connection strings.\n");
        }
    }
}
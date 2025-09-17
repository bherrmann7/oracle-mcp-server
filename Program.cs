using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OracleMcpServer;

public static class Program
{
    public static void Main(string[] args)
    {
        // Redirect all stdout to stderr for MCP protocol
        Console.SetOut(Console.Error);
        var host = CreateHost(args);

        // Use synchronous Run() to block and keep the process alive
        host.Run();
    }

    public static IHost CreateHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory) // ensures we look next to the exe
            .AddJsonFile("appsettings.json", false, true);

        // Load credentials from user's home directory using the helper
        ConfigurationHelper.LoadCredentialsIntoConfigurationBuilder(builder.Configuration);
        Console.Error.Flush();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        return builder.Build();
    }
}
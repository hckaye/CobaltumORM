using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CobaltumOrm.Tool;

internal sealed class McpCommand
{
    private readonly TextWriter _error;
    private readonly IProjectEvaluator _evaluator;
    private readonly string _currentDirectory;

    public McpCommand(
        TextWriter error,
        IProjectEvaluator evaluator,
        string currentDirectory)
    {
        _error = error;
        _evaluator = evaluator;
        _currentDirectory = currentDirectory;
    }

    public async Task<int> RunAsync(
        ProjectInspectionOptions options,
        CancellationToken cancellationToken)
    {
        var projectPath = ProjectPathResolver.Resolve(options.Project!, _currentDirectory);
        var documentation = McpDocumentation.Load();
        var project = new CobaltumMcpProjectService(
            projectPath,
            options,
            _evaluator,
            _error);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console =>
        {
            console.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var mcp = builder.Services
            .AddMcpServer(server =>
            {
                server.ServerInfo = new Implementation
                {
                    Name = "cobaltum",
                    Version = ServerVersion(),
                };
                server.ServerInstructions =
                    "Read-only CobaltumORM analysis for the project selected at startup. " +
                    "The tools do not connect to a database or write generated files.";
            })
            .WithStdioServerTransport();
        mcp.WithCobaltumMcpSurface(project, documentation);

        using var host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static string ServerVersion()
    {
        var version = typeof(McpCommand).Assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }
}

internal static class CobaltumMcpServerBuilderExtensions
{
    public static IMcpServerBuilder WithCobaltumMcpSurface(
        this IMcpServerBuilder builder,
        CobaltumMcpProjectService project,
        McpDocumentation documentation) => builder
        .WithTools(new CobaltumMcpTools(project, documentation))
        .WithResources(new CobaltumMcpResources(documentation));
}

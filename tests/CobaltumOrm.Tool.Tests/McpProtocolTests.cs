using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CobaltumOrm.Tool.Tests;

public sealed class McpProtocolTests
{
    private static readonly string[] ExpectedToolNames =
    {
        "doctor_project",
        "explain_diagnostic",
        "inspect_project",
        "list_generated_artifacts",
        "read_generated_artifact",
    };

    private static readonly (string Uri, string MimeType)[] ExpectedResources =
    {
        (McpDocumentation.DiagnosticsEnglishUri, McpDocumentation.MarkdownMimeType),
        (McpDocumentation.DiagnosticsJapaneseUri, McpDocumentation.MarkdownMimeType),
        (McpDocumentation.LlmsTextUri, McpDocumentation.PlainTextMimeType),
        (McpDocumentation.QuickReferenceEnglishUri, McpDocumentation.MarkdownMimeType),
        (McpDocumentation.QuickReferenceJapaneseUri, McpDocumentation.MarkdownMimeType),
        (McpDocumentation.RecipesEnglishUri, McpDocumentation.MarkdownMimeType),
        (McpDocumentation.RecipesJapaneseUri, McpDocumentation.MarkdownMimeType),
    };

    [Fact]
    public async Task OfficialClientDiscoversAndCallsTheReadOnlySurfaceAndReadsEveryResource()
    {
        using var fixture = new McpTestFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverInput = clientToServer.Reader.AsStream();
        var serverOutput = serverToClient.Writer.AsStream();
        var clientOutput = clientToServer.Writer.AsStream();
        var clientInput = serverToClient.Reader.AsStream();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var project = new CobaltumMcpProjectService(
            fixture.ProjectPath,
            fixture.Options(),
            fixture.Evaluator,
            TextWriter.Null);
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "cobaltum-test", Version = "1.0.0" };
            })
            .WithStreamServerTransport(serverInput, serverOutput)
            .WithCobaltumMcpSurface(project, McpDocumentation.Load());

        using var host = builder.Build();
        await host.StartAsync(timeout.Token);
        var transport = new StreamClientTransport(clientOutput, clientInput);
        await using (var client = await McpClient.CreateAsync(
                         transport,
                         cancellationToken: timeout.Token))
        {
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            Assert.Equal(
                ExpectedToolNames,
                tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal));
            foreach (var tool in tools)
            {
                Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint is true);
                Assert.True(tool.ProtocolTool.Annotations?.OpenWorldHint is false);
                Assert.NotNull(tool.ProtocolTool.OutputSchema);
                Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            }

            var inspect = await client.CallToolAsync(
                "inspect_project",
                cancellationToken: timeout.Token);
            AssertSuccessWithVersion(inspect);

            var doctor = await client.CallToolAsync(
                "doctor_project",
                cancellationToken: timeout.Token);
            AssertSuccessWithVersion(doctor);
            Assert.Equal("warning", doctor.StructuredContent!.Value.GetProperty("status").GetString());

            var list = await client.CallToolAsync(
                "list_generated_artifacts",
                cancellationToken: timeout.Token);
            AssertSuccessWithVersion(list);
            var artifactName = list.StructuredContent!.Value.GetProperty("artifacts")[0]
                .GetProperty("name")
                .GetString()!;

            var read = await client.CallToolAsync(
                "read_generated_artifact",
                new Dictionary<string, object?> { ["artifactName"] = artifactName },
                cancellationToken: timeout.Token);
            AssertSuccessWithVersion(read);
            Assert.Equal(artifactName, read.StructuredContent!.Value.GetProperty("name").GetString());
            Assert.False(string.IsNullOrEmpty(read.StructuredContent.Value.GetProperty("source").GetString()));

            var explanation = await client.CallToolAsync(
                "explain_diagnostic",
                new Dictionary<string, object?> { ["code"] = "COB104", ["language"] = "ja" },
                cancellationToken: timeout.Token);
            AssertSuccessWithVersion(explanation);
            Assert.Equal("COB104", explanation.StructuredContent!.Value.GetProperty("code").GetString());
            Assert.Equal("ja", explanation.StructuredContent.Value.GetProperty("language").GetString());

            var traversal = await client.CallToolAsync(
                "read_generated_artifact",
                new Dictionary<string, object?> { ["artifactName"] = "../secret.cs" },
                cancellationToken: timeout.Token);
            AssertToolError(traversal, "Paths and '..' are not allowed");

            var unknownCode = await client.CallToolAsync(
                "explain_diagnostic",
                new Dictionary<string, object?> { ["code"] = "COB999", ["language"] = "en" },
                cancellationToken: timeout.Token);
            AssertToolError(unknownCode, "not documented");

            var unknownLanguage = await client.CallToolAsync(
                "explain_diagnostic",
                new Dictionary<string, object?> { ["code"] = "COB001", ["language"] = "fr" },
                cancellationToken: timeout.Token);
            AssertToolError(unknownLanguage, "not supported");

            var resources = await client.ListResourcesAsync(cancellationToken: timeout.Token);
            Assert.Equal(
                ExpectedResources.Select(item => item.Uri),
                resources.Select(resource => resource.Uri).OrderBy(uri => uri, StringComparer.Ordinal));
            Assert.Empty(await client.ListResourceTemplatesAsync(cancellationToken: timeout.Token));
            foreach (var expected in ExpectedResources)
            {
                var resource = Assert.Single(resources, resource => resource.Uri == expected.Uri);
                Assert.Equal(expected.MimeType, resource.MimeType);
                Assert.False(string.IsNullOrWhiteSpace(resource.Description));
                var result = await resource.ReadAsync(cancellationToken: timeout.Token);
                var contents = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
                Assert.Equal(expected.Uri, contents.Uri);
                Assert.Equal(expected.MimeType, contents.MimeType);
                Assert.False(string.IsNullOrWhiteSpace(contents.Text));
            }
        }

        await host.WaitForShutdownAsync(timeout.Token);
        Assert.Equal(0, fixture.ProcessRunner.CallCount);
    }

    [Fact]
    public async Task ActualMcpCommandUsesProtocolOnlyStdoutAndStopsWhenStdinCloses()
    {
        using var fixture = new ActualCommandFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var process = StartToolProcess(fixture.ProjectPath);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);

        await process.StandardInput.WriteLineAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"cobaltum-tests","version":"1.0.0"}}}
            """);
        await process.StandardInput.FlushAsync(timeout.Token);
        var initializeLine = await process.StandardOutput.ReadLineAsync(timeout.Token);
        using (var initialize = JsonDocument.Parse(Assert.IsType<string>(initializeLine)))
        {
            Assert.Equal("2.0", initialize.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal(1, initialize.RootElement.GetProperty("id").GetInt32());
            Assert.Equal("cobaltum", initialize.RootElement.GetProperty("result")
                .GetProperty("serverInfo")
                .GetProperty("name")
                .GetString());
        }

        await process.StandardInput.WriteLineAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await process.StandardInput.WriteLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"inspect_project\",\"arguments\":{}}}");
        await process.StandardInput.FlushAsync(timeout.Token);
        var inspectLine = await process.StandardOutput.ReadLineAsync(timeout.Token);
        using (var inspect = JsonDocument.Parse(Assert.IsType<string>(inspectLine)))
        {
            Assert.Equal("2.0", inspect.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal(2, inspect.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(
                ProjectInspectionOutput.FormatVersion,
                inspect.RootElement.GetProperty("result")
                    .GetProperty("structuredContent")
                    .GetProperty("formatVersion")
                    .GetInt32());
        }

        process.StandardInput.Close();
        var remainingOutput = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var errorText = await standardError;

        foreach (var line in remainingOutput.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var message = JsonDocument.Parse(line);
            Assert.Equal("2.0", message.RootElement.GetProperty("jsonrpc").GetString());
        }

        Assert.Equal(0, process.ExitCode);
        Assert.DoesNotContain("error:", errorText, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSuccessWithVersion(CallToolResult result)
    {
        Assert.False(result.IsError is true);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal(
            ProjectInspectionOutput.FormatVersion,
            result.StructuredContent.Value.GetProperty("formatVersion").GetInt32());
        Assert.NotEmpty(result.Content.OfType<TextContentBlock>());
    }

    private static void AssertToolError(CallToolResult result, string message)
    {
        Assert.True(result.IsError is true);
        Assert.Contains(
            message,
            string.Join("\n", result.Content.OfType<TextContentBlock>().Select(content => content.Text)),
            StringComparison.Ordinal);
    }

    private static Process StartToolProcess(string projectPath)
    {
        var toolAssembly = typeof(ToolApplication).Assembly.Location;
        var testAssembly = typeof(McpProtocolTests).Assembly.Location;
        var testDirectory = Path.GetDirectoryName(testAssembly)!;
        var testName = Path.GetFileNameWithoutExtension(testAssembly);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(Path.Combine(testDirectory, testName + ".runtimeconfig.json"));
        startInfo.ArgumentList.Add("--depsfile");
        startInfo.ArgumentList.Add(Path.Combine(testDirectory, testName + ".deps.json"));
        startInfo.ArgumentList.Add(toolAssembly);
        startInfo.ArgumentList.Add("mcp");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the cobaltum tool process.");
    }

    private sealed class ActualCommandFixture : IDisposable
    {
        public ActualCommandFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "CobaltumOrm.McpCommandTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ProjectPath = Path.Combine(Root, "Actual.csproj");
            File.WriteAllText(ProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <CobaltumOrmAnalysisCache>false</CobaltumOrmAnalysisCache>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(Root, "Input.cs"),
                "public sealed class Input { public int Id { get; set; } }");
        }

        public string Root { get; }

        public string ProjectPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

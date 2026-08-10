using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace CobaltumOrm.Migrations;

/// <summary>Provides the selected environment and .NET configuration to a migration project.</summary>
public sealed class MigrationProjectContext : IDisposable
{
    internal MigrationProjectContext(
        string environmentName,
        string contentRootPath,
        string? settingsPath,
        string connectionString,
        IConfigurationRoot configuration)
    {
        EnvironmentName = environmentName;
        ContentRootPath = contentRootPath;
        SettingsPath = settingsPath;
        ConnectionString = connectionString;
        Configuration = configuration;
    }

    /// <summary>Gets the selected environment name.</summary>
    public string EnvironmentName { get; }

    /// <summary>Gets the directory used to resolve the default settings files.</summary>
    public string ContentRootPath { get; }

    /// <summary>Gets the explicit settings file path, or null when default files are used.</summary>
    public string? SettingsPath { get; }

    /// <summary>Gets the required <c>ConnectionStrings:Cobaltum</c> value.</summary>
    public string ConnectionString { get; }

    /// <summary>Gets the merged .NET configuration.</summary>
    public IConfiguration Configuration { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Configuration is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

internal static class MigrationProjectConfiguration
{
    private const string ConnectionStringName = "Cobaltum";

    internal static MigrationProjectContext Load(
        Assembly migrationAssembly,
        string? requestedEnvironment,
        string? requestedSettingsPath,
        string? contentRootPath = null,
        bool includeEnvironmentVariables = true)
    {
        var contentRoot = Path.GetFullPath(contentRootPath ?? Directory.GetCurrentDirectory());
        var environmentName = ResolveEnvironmentName(requestedEnvironment);
        var builder = new ConfigurationBuilder();
        string? settingsPath = null;

        if (string.IsNullOrWhiteSpace(requestedSettingsPath))
        {
            builder.SetBasePath(contentRoot)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile(
                    $"appsettings.{environmentName}.json",
                    optional: true,
                    reloadOnChange: false);
        }
        else
        {
            settingsPath = Path.GetFullPath(requestedSettingsPath!);
            if (!File.Exists(settingsPath))
            {
                throw new MigrationValidationException($"Settings file '{settingsPath}' does not exist.");
            }

            builder.SetBasePath(Path.GetDirectoryName(settingsPath)!)
                .AddJsonFile(Path.GetFileName(settingsPath), optional: false, reloadOnChange: false);
        }

        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddUserSecrets(migrationAssembly, optional: true);
        }

        if (includeEnvironmentVariables)
        {
            builder.AddEnvironmentVariables();
        }
        var configuration = builder.Build();
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            (configuration as IDisposable)?.Dispose();
            throw new MigrationValidationException(
                "Connection string 'ConnectionStrings:Cobaltum' is not configured. " +
                "Set it in appsettings, .NET user secrets, or ConnectionStrings__Cobaltum.");
        }

        return new MigrationProjectContext(
            environmentName,
            contentRoot,
            settingsPath,
            connectionString!,
            configuration);
    }

    private static string ResolveEnvironmentName(string? requestedEnvironment)
    {
        var value = string.IsNullOrWhiteSpace(requestedEnvironment)
            ? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            : requestedEnvironment;
        value = string.IsNullOrWhiteSpace(value) ? "Production" : value!.Trim();

        if (value == "." || value == ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
            value.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
        {
            throw new MigrationValidationException($"Environment name '{value}' is not valid.");
        }

        return value;
    }
}

using System;
using System.Data.Common;
using System.IO;

namespace CobaltumOrm.Migrations;

/// <summary>
/// Owns a database connection created from a migration project and the configuration
/// context used to create it.
/// </summary>
public sealed class MigrationProjectConnection : IDisposable
{
    private readonly MigrationProjectContext _context;
    private bool _disposed;

    private MigrationProjectConnection(
        MigrationProjectContext context,
        DbConnection connection)
    {
        _context = context;
        Connection = connection;
    }

    /// <summary>Gets the connection created by the migration project.</summary>
    public DbConnection Connection { get; }

    /// <summary>
    /// Creates a connection with the same configuration rules used by the migration CLI.
    /// </summary>
    public static MigrationProjectConnection Create<TProject>()
        where TProject : MigrationProject, new() =>
        Create(new TProject());

    /// <summary>
    /// Creates a connection with the same configuration rules used by the migration CLI.
    /// </summary>
    public static MigrationProjectConnection Create(MigrationProject project)
    {
        if (project is null)
        {
            throw new ArgumentNullException(nameof(project));
        }

        var contentRootPath = GetPublishedConfigurationPath(project);
        return Create(
            project,
            requestedEnvironment: null,
            requestedSettingsPath: null,
            contentRootPath,
            includeEnvironmentVariables: true);
    }

    internal static MigrationProjectConnection Create(
        MigrationProject project,
        string? requestedEnvironment,
        string? requestedSettingsPath,
        string? contentRootPath,
        bool includeEnvironmentVariables)
    {
        if (project is null)
        {
            throw new ArgumentNullException(nameof(project));
        }

        var context = MigrationProjectConfiguration.Load(
            project.GetType().Assembly,
            requestedEnvironment,
            requestedSettingsPath,
            contentRootPath,
            includeEnvironmentVariables);
        try
        {
            var connection = project.CreateConnection(context)
                ?? throw new MigrationValidationException(
                    "The migration project returned a null database connection.");
            return new MigrationProjectConnection(context, connection);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Connection.Dispose();
        }
        finally
        {
            _context.Dispose();
        }
    }

    private static string? GetPublishedConfigurationPath(MigrationProject project)
    {
        var assemblyName = project.GetType().Assembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return null;
        }

        var path = Path.Combine(
            AppContext.BaseDirectory,
            "CobaltumOrm.Migrations",
            assemblyName!);
        return Directory.Exists(path) ? path : null;
    }
}

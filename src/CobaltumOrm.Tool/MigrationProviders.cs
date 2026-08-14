namespace CobaltumOrm.Tool;

internal static class MigrationProviders
{
    public const string Default = "PostgreSql";

    public static IReadOnlyList<string> Names { get; } = new[]
    {
        "PostgreSql",
        "MySql",
        "Sqlite",
        "SqlServer",
        "Oracle",
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RuntimePackage>> RuntimePackageMap =
        new Dictionary<string, IReadOnlyList<RuntimePackage>>(StringComparer.Ordinal)
        {
            ["PostgreSql"] = new[]
            {
                new RuntimePackage("Npgsql", "10.0.3"),
            },
            ["MySql"] = new[]
            {
                new RuntimePackage("MySqlConnector", "2.4.0"),
            },
            ["Sqlite"] = new[]
            {
                new RuntimePackage("Microsoft.Data.Sqlite", "10.0.7"),
                new RuntimePackage("SQLitePCLRaw.bundle_e_sqlite3", "2.1.12"),
                new RuntimePackage("SQLitePCLRaw.core", "2.1.12"),
            },
            ["SqlServer"] = new[]
            {
                new RuntimePackage("Microsoft.Data.SqlClient", "7.0.2"),
            },
            ["Oracle"] = new[]
            {
                new RuntimePackage("Oracle.ManagedDataAccess.Core", "23.26.300"),
            },
        };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        var candidate = value.Trim();
        var canonical = Names.FirstOrDefault(
            name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
        if (canonical is not null)
        {
            return canonical;
        }

        throw new ToolUsageException(
            $"Unsupported provider '{candidate}'. Supported providers: {string.Join(", ", Names)}.");
    }

    public static IReadOnlyList<RuntimePackage> RuntimePackages(string provider) =>
        RuntimePackageMap.TryGetValue(provider, out var packages)
            ? packages
            : throw new ToolUsageException($"Unsupported provider '{provider}'.");

    internal sealed record RuntimePackage(string Id, string Version);
}

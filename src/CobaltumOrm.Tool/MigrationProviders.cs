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
}

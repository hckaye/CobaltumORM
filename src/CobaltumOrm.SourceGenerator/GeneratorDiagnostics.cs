using Microsoft.CodeAnalysis;

namespace CobaltumOrm.SourceGenerator;

internal static class GeneratorDiagnostics
{
    private const string Category = "CobaltumOrm";

    internal static readonly DiagnosticDescriptor InvalidMigration = new DiagnosticDescriptor(
        "COB001",
        "Migration cannot be analyzed safely",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor DynamicMigrationArgument = new DiagnosticDescriptor(
        "COB002",
        "Migration argument must be constant",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor SchemaSql = new DiagnosticDescriptor(
        "COB003",
        "Migration SQL is invalid",
        "{0}: {1}",
        Category,
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor QuerySql = new DiagnosticDescriptor(
        "COB004",
        "Query SQL is invalid",
        "{0}: {1}",
        Category,
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor NameCollision = new DiagnosticDescriptor(
        "COB005",
        "Generated name collides",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor UnsupportedDeclaration = new DiagnosticDescriptor(
        "COB006",
        "Declaration is not supported by generation",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor DynamicRawQuery = new DiagnosticDescriptor(
        "COB007",
        "Raw query cannot be validated at compile time",
        "Query(string) requires compile-time-known SQL; use NoCheckQuery to bypass compile-time SQL validation",
        Category,
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor InvalidConfiguration = new DiagnosticDescriptor(
        "COB008",
        "Generator configuration is invalid",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        true);
}

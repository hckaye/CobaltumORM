using Microsoft.CodeAnalysis;

namespace CobaltumOrm.SourceGenerator;

internal static class GeneratorDiagnostics
{
    private const string Category = "CobaltumOrm";

    /// <summary>
    /// Prefix of the documentation URL used by <see cref="DiagnosticDescriptor.HelpLinkUri"/>.
    /// Each descriptor appends its lowercase identifier as the section anchor.
    /// </summary>
    internal const string HelpLinkPrefix =
        "https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/diagnostics.md#";

    internal static readonly DiagnosticDescriptor InvalidMigration = new DiagnosticDescriptor(
        "COB001",
        "Migration cannot be analyzed safely",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        true,
        helpLinkUri: HelpLink("COB001"));

    internal static readonly DiagnosticDescriptor DynamicMigrationArgument = new DiagnosticDescriptor(
        "COB002",
        "Migration argument must be constant",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        true,
        helpLinkUri: HelpLink("COB002"));

    internal static readonly DiagnosticDescriptor SchemaSql = new DiagnosticDescriptor(
        "COB003",
        "Migration SQL is invalid",
        "{0}: {1}",
        Category,
        DiagnosticSeverity.Error,
        true,
        helpLinkUri: HelpLink("COB003"));

    internal static readonly DiagnosticDescriptor QuerySql = new DiagnosticDescriptor(
        "COB004",
        "Query SQL is invalid",
        "{0}: {1}",
        Category,
        DiagnosticSeverity.Error,
        true,
        helpLinkUri: HelpLink("COB004"));

    internal static readonly DiagnosticDescriptor NameCollision = new DiagnosticDescriptor(
        "COB005",
        "Generated name collides",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        true,
        helpLinkUri: HelpLink("COB005"));

    internal static readonly DiagnosticDescriptor UnsupportedDeclaration = new DiagnosticDescriptor(
        "COB006",
        "Declaration is not supported by generation",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        true,
        helpLinkUri: HelpLink("COB006"));

    internal static readonly DiagnosticDescriptor DynamicRawQuery = new DiagnosticDescriptor(
        "COB007",
        "Raw query cannot be validated at compile time",
        "Query(string) requires compile-time-known SQL; use NoCheckQuery to bypass compile-time SQL validation",
        Category,
        DiagnosticSeverity.Error,
        true,
        helpLinkUri: HelpLink("COB007"));

    internal static readonly DiagnosticDescriptor InvalidConfiguration = new DiagnosticDescriptor(
        "COB008",
        "Generator configuration is invalid",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        true,
        helpLinkUri: HelpLink("COB008"));

    internal static readonly DiagnosticDescriptor ResultMapping = new DiagnosticDescriptor(
        "COB009",
        "Query result type cannot be mapped",
        "Query result cannot be mapped to the specified type: {0}",
        Category,
        DiagnosticSeverity.Error,
        true,
        helpLinkUri: HelpLink("COB009"));

    /// <summary>Returns the documentation URL for one diagnostic identifier.</summary>
    internal static string HelpLink(string id) => HelpLinkPrefix + id.ToLowerInvariant();

    /// <summary>Every descriptor reported by the generator, ordered by identifier.</summary>
    internal static readonly DiagnosticDescriptor[] All =
    {
        InvalidMigration,
        DynamicMigrationArgument,
        SchemaSql,
        QuerySql,
        NameCollision,
        UnsupportedDeclaration,
        DynamicRawQuery,
        InvalidConfiguration,
        ResultMapping,
    };
}

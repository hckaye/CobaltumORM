using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CobaltumOrm.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CobaltumOrm.SourceGenerator;

internal static class CSharpNames
{
    internal static string Pascal(string value, string fallback)
    {
        var builder = new StringBuilder(value.Length + 8);
        var capitalize = true;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                capitalize = true;
                continue;
            }

            if (character == '_')
            {
                capitalize = true;
                continue;
            }

            builder.Append(capitalize ? char.ToUpperInvariant(character) : character);
            capitalize = false;
        }

        if (builder.Length == 0)
        {
            builder.Append(fallback);
        }

        if (!SyntaxFacts.IsIdentifierStartCharacter(builder[0]))
        {
            builder.Insert(0, '_');
        }

        var result = builder.ToString();
        if (SyntaxFacts.GetKeywordKind(result) != SyntaxKind.None ||
            SyntaxFacts.GetContextualKeywordKind(result) != SyntaxKind.None)
        {
            result = "CSharp" + result;
        }

        return result;
    }

    internal static string Camel(string value, string fallback)
    {
        var pascal = Pascal(value, fallback);
        if (pascal.Length == 0 || pascal[0] == '_')
        {
            return pascal;
        }

        var candidate = char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
        return SyntaxFacts.GetKeywordKind(candidate) == SyntaxKind.None ? candidate : "@" + candidate;
    }

    internal static Dictionary<T, string> Allocate<T>(
        IEnumerable<T> values,
        Func<T, string> baseName,
        IEqualityComparer<T>? comparer = null,
        IEnumerable<string>? reserved = null)
        where T : notnull
    {
        var result = new Dictionary<T, string>(comparer ?? EqualityComparer<T>.Default);
        var used = new HashSet<string>(reserved ?? Array.Empty<string>(), StringComparer.Ordinal);
        foreach (var value in values)
        {
            var baseValue = baseName(value);
            var candidate = baseValue;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = baseValue + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            result.Add(value, candidate);
        }

        return result;
    }

    internal static string Literal(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                case '\0': builder.Append("\\0"); break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

}

internal sealed class TypeEnvironment
{
    private readonly bool _hasDateOnly;
    private readonly bool _hasTimeOnly;

    internal TypeEnvironment(Compilation compilation)
    {
        _hasDateOnly = compilation.GetTypeByMetadataName("System.DateOnly") != null;
        _hasTimeOnly = compilation.GetTypeByMetadataName("System.TimeOnly") != null;
    }

    internal string TypeName(string analyzerType)
    {
        var nullable = analyzerType.EndsWith("?", StringComparison.Ordinal);
        var baseName = nullable ? analyzerType.Substring(0, analyzerType.Length - 1) : analyzerType;
        string result;
        switch (baseName)
        {
            case "bool": result = "global::System.Boolean"; break;
            case "short": result = "global::System.Int16"; break;
            case "int": result = "global::System.Int32"; break;
            case "long": result = "global::System.Int64"; break;
            case "float": result = "global::System.Single"; break;
            case "double": result = "global::System.Double"; break;
            case "decimal": result = "global::System.Decimal"; break;
            case "string": result = "global::System.String"; break;
            case "Guid": result = "global::System.Guid"; break;
            case "DateOnly": result = _hasDateOnly ? "global::System.DateOnly" : "global::System.DateTime"; break;
            case "TimeOnly": result = _hasTimeOnly ? "global::System.TimeOnly" : "global::System.TimeSpan"; break;
            case "DateTime": result = "global::System.DateTime"; break;
            case "DateTimeOffset": result = "global::System.DateTimeOffset"; break;
            case "TimeSpan": result = "global::System.TimeSpan"; break;
            case "byte[]": result = "global::System.Byte[]"; break;
            default: result = "global::System.Object"; break;
        }

        return nullable ? result + "?" : result;
    }

    internal string ParameterTypeName(string analyzerType)
    {
        return analyzerType.EndsWith("?", StringComparison.Ordinal)
            ? TypeName(analyzerType)
            : TypeName(analyzerType) + "?";
    }

    internal string DbTypeName(string analyzerType)
    {
        var baseName = analyzerType.EndsWith("?", StringComparison.Ordinal)
            ? analyzerType.Substring(0, analyzerType.Length - 1)
            : analyzerType;
        switch (baseName)
        {
            case "bool": return "Boolean";
            case "short": return "Int16";
            case "int": return "Int32";
            case "long": return "Int64";
            case "float": return "Single";
            case "double": return "Double";
            case "decimal": return "Decimal";
            case "string": return "String";
            case "Guid": return "Guid";
            case "DateOnly": return "Date";
            case "TimeOnly": return "Time";
            case "DateTime": return "DateTime2";
            case "DateTimeOffset": return "DateTimeOffset";
            case "TimeSpan": return "Time";
            case "byte[]": return "Binary";
            default: return "Object";
        }
    }

    internal string ReadExpression(string analyzerType, int ordinal, string context)
    {
        var nullable = analyzerType.EndsWith("?", StringComparison.Ordinal);
        var nonNullableAnalyzerType = nullable
            ? analyzerType.Substring(0, analyzerType.Length - 1)
            : analyzerType;
        var type = TypeName(nonNullableAnalyzerType);
        if (nullable)
        {
            var nullableType = TypeName(analyzerType);
            return "reader.IsDBNull(" + ordinal.ToString(CultureInfo.InvariantCulture) + ") ? (" + nullableType + ")null : " +
                "reader.GetFieldValue<" + type + ">(" + ordinal.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return "reader.IsDBNull(" + ordinal.ToString(CultureInfo.InvariantCulture) + ") ? " +
            "throw new global::System.InvalidOperationException(" + CSharpNames.Literal(context + " returned database null for a non-nullable value.") + ") : " +
            "reader.GetFieldValue<" + type + ">(" + ordinal.ToString(CultureInfo.InvariantCulture) + ")";
    }
}

internal static class GeneratedSourceWriter
{
    internal static string WriteSqlSchema(
        string generatedNamespace,
        DatabaseSchema schema,
        IDatabaseDialect dialect)
    {
        var tables = schema.Tables
            .OrderBy(table => table.Schema ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToList();
        var tableNames = CSharpNames.Allocate(
            tables,
            table => CSharpNames.Pascal(
                dialect.SchemaRules.IsDefaultSchema(table.Schema)
                    ? table.Name
                    : table.Schema + "_" + table.Name,
                "Table"),
            reserved: new[] { "Tables" });
        var schemas = tables
            .Select(table => table.Schema)
            .Where(schemaName => !string.IsNullOrEmpty(schemaName))
            .Select(schemaName => schemaName!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(schemaName => schemaName, StringComparer.Ordinal)
            .ToList();
        var schemaNames = CSharpNames.Allocate(
            schemas,
            schemaName => CSharpNames.Pascal(schemaName!, "Schema"),
            reserved: new[] { "Schemas" });

        var builder = Header(generatedNamespace);
        builder.AppendLine("public static class SqlSchema");
        builder.AppendLine("{");
        builder.AppendLine("    public static class Schemas");
        builder.AppendLine("    {");
        foreach (var schemaName in schemas)
        {
            builder.Append("        public const global::System.String ")
                .Append(schemaNames[schemaName]).Append(" = ")
                .Append(CSharpNames.Literal(dialect.IdentifierQuoter.QuoteIdentifier(schemaName!))).AppendLine(";");
        }

        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static class Tables");
        builder.AppendLine("    {");
        foreach (var table in tables)
        {
            var columnNames = CSharpNames.Allocate(
                table.Columns,
                column => CSharpNames.Pascal(column.Name, "Column"),
                reserved: new[] { "Columns" });
            builder.Append("        public static class ").Append(tableNames[table]).AppendLine();
            builder.AppendLine("        {");
            builder.Append("            public const global::System.String Name = ")
                .Append(CSharpNames.Literal(Qualify(table, dialect))).AppendLine(";");
            builder.Append("            public const global::System.String UnqualifiedName = ")
                .Append(CSharpNames.Literal(dialect.IdentifierQuoter.QuoteIdentifier(table.Name))).AppendLine(";");
            builder.AppendLine();
            builder.AppendLine("            public static class Columns");
            builder.AppendLine("            {");
            foreach (var column in table.Columns)
            {
                builder.Append("                public const global::System.String ")
                    .Append(columnNames[column]).Append(" = ")
                    .Append(CSharpNames.Literal(dialect.IdentifierQuoter.QuoteIdentifier(column.Name))).AppendLine(";");
            }

            builder.AppendLine("            }");
            builder.AppendLine("        }");
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    internal static string WriteModels(
        string generatedNamespace,
        DatabaseSchema schema,
        Compilation compilation,
        IDatabaseDialect dialect)
    {
        var environment = new TypeEnvironment(compilation);
        var tables = schema.Tables
            .OrderBy(table => table.Schema ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToList();
        var tableNames = CSharpNames.Allocate(
            tables,
            table => CSharpNames.Pascal(
                dialect.SchemaRules.IsDefaultSchema(table.Schema)
                    ? table.Name
                    : table.Schema + "_" + table.Name,
                "Table") + "Row");

        var builder = Header(generatedNamespace);
        foreach (var table in tables)
        {
            var recordName = tableNames[table];
            var query = dialect.QueryAnalyzer.Analyze(schema, "SELECT * FROM " + Qualify(table, dialect));
            if (query.HasErrors)
            {
                continue;
            }

            var columnNames = CSharpNames.Allocate(
                table.Columns,
                column => CSharpNames.Pascal(column.Name, "Column"),
                reserved: new[] { "All", "Where", "Query" });
            builder.AppendLine("[global::CobaltumOrm.CobaltumTable(" + CSharpNames.Literal(table.Schema) + ", " + CSharpNames.Literal(table.Name) + ")]");
            builder.Append("public sealed record ").Append(recordName).AppendLine("(");
            for (var index = 0; index < table.Columns.Count; index++)
            {
                var column = table.Columns[index];
                var resultColumn = query.Columns[index];
                builder.Append("    [property: global::CobaltumOrm.CobaltumColumn(")
                    .Append(CSharpNames.Literal(column.Name)).Append(", ")
                    .Append(CSharpNames.Literal(column.SqlType)).Append(", ")
                    .Append(column.IsNullable ? "true" : "false").Append(", ")
                    .Append(column.IsPrimaryKey ? "true" : "false").Append(", ")
                    .Append(CSharpNames.Literal(column.DefaultExpression)).Append(")] ")
                    .Append(environment.TypeName(resultColumn.ClrType)).Append(' ')
                    .Append(columnNames[column]);
                builder.AppendLine(index == table.Columns.Count - 1 ? ");" : ",");
            }

            builder.AppendLine();
        }

        builder.AppendLine("public static class Tables");
        builder.AppendLine("{");
        var tableEntryNames = CSharpNames.Allocate(
            tables,
            table => CSharpNames.Pascal(table.Name, "Table"),
            reserved: new[] { "Tables" });
        foreach (var table in tables)
        {
            builder.Append("    public static ").Append(tableNames[table]).Append("Table ")
                .Append(tableEntryNames[table]).Append(" { get; } = new ")
                .Append(tableNames[table]).AppendLine("Table();");
        }

        builder.AppendLine("}");
        builder.AppendLine();

        foreach (var table in tables)
        {
            var recordName = tableNames[table];
            var query = dialect.QueryAnalyzer.Analyze(schema, "SELECT * FROM " + Qualify(table, dialect));
            if (query.HasErrors)
            {
                continue;
            }

            var columnNames = CSharpNames.Allocate(
                table.Columns,
                column => CSharpNames.Pascal(column.Name, "Column"),
                reserved: new[] { "All", "Where", "Query" });
            builder.Append("public sealed class ").Append(recordName).Append("Table : global::CobaltumOrm.CobaltumTable<")
                .Append(recordName).AppendLine(">");
            builder.AppendLine("{");
            builder.Append("    internal ").Append(recordName).Append("Table() : base(")
                .Append(CSharpNames.Literal("SELECT " + string.Join(", ", table.Columns.Select(column => dialect.IdentifierQuoter.QuoteIdentifier(column.Name))) + " FROM " + Qualify(table, dialect)))
                .AppendLine(", Materialize) { }");
            builder.AppendLine();
            for (var index = 0; index < table.Columns.Count; index++)
            {
                var column = table.Columns[index];
                SqlValueKind columnKind;
                var databaseTypeName = dialect.Provider == DatabaseProvider.PostgreSql &&
                    dialect.TypeMapper.TryMap(column.SqlType, out columnKind)
                    ? dialect.TypeMapper.ToDatabaseTypeName(columnKind)
                    : null;
                builder.Append("    public global::CobaltumOrm.CobaltumColumn<").Append(recordName).Append(", ")
                    .Append(environment.TypeName(query.Columns[index].ClrType)).Append("> ")
                    .Append(columnNames[column]).Append(" { get; } = new global::CobaltumOrm.CobaltumColumn<")
                    .Append(recordName).Append(", ").Append(environment.TypeName(query.Columns[index].ClrType)).Append(">(")
                    .Append(CSharpNames.Literal(dialect.IdentifierQuoter.QuoteIdentifier(column.Name))).Append(", global::System.Data.DbType.")
                    .Append(environment.DbTypeName(query.Columns[index].ClrType));
                if (databaseTypeName != null)
                {
                    builder.Append(", ").Append(CSharpNames.Literal(databaseTypeName))
                        .Append(", static parameter => ((global::Npgsql.NpgsqlParameter)parameter).DataTypeName = ")
                        .Append(CSharpNames.Literal(databaseTypeName));
                }

                builder.AppendLine(");");
            }

            builder.AppendLine();
            builder.Append("    private static ").Append(recordName).AppendLine(" Materialize(global::System.Data.Common.DbDataReader reader)");
            builder.AppendLine("    {");
            builder.Append("        return new ").Append(recordName).AppendLine("(");
            for (var index = 0; index < query.Columns.Count; index++)
            {
                builder.Append("            ").Append(environment.ReadExpression(
                    query.Columns[index].ClrType,
                    index,
                    (table.Schema == null ? table.Name : table.Schema + "." + table.Name) + "." + table.Columns[index].Name));
                builder.AppendLine(index == query.Columns.Count - 1 ? ");" : ",");
            }

            builder.AppendLine("    }");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    internal static string WriteQueries(
        INamedTypeSymbol owner,
        IReadOnlyList<QuerySource> queries,
        IReadOnlyList<AnalysisResult> analyses,
        Compilation compilation,
        IDatabaseDialect dialect)
    {
        var environment = new TypeEnvironment(compilation);
        var ownerNamespace = owner.ContainingNamespace.IsGlobalNamespace
            ? null
            : owner.ContainingNamespace.ToDisplayString();
        var builder = Header(ownerNamespace);
        builder.Append(Accessibility(owner.DeclaredAccessibility))
            .Append(owner.IsStatic ? " static partial class " : " partial class ")
            .Append(EscapeIdentifier(owner.Name)).AppendLine();
        builder.AppendLine("{");
        for (var queryIndex = 0; queryIndex < queries.Count; queryIndex++)
        {
            var source = queries[queryIndex];
            var analysis = analyses[queryIndex];
            var queryName = CSharpNames.Pascal(source.Name, "Query");
            var resultName = queryName + "Result";
            var parametersName = queryName + "Parameters";
            var resultNames = CSharpNames.Allocate(
                analysis.Columns,
                column => CSharpNames.Pascal(column.Name, "Column"));
            var parameterNames = CSharpNames.Allocate(
                analysis.Parameters,
                parameter => CSharpNames.Pascal(parameter.Name, "Parameter"));
            var localParameterNames = CSharpNames.Allocate(
                analysis.Parameters,
                parameter => CSharpNames.Camel(parameter.Name, "parameter"),
                reserved: new[] { "connection", "transaction", "cancellationToken" });

            builder.Append("    public sealed record ").Append(resultName).AppendLine("(");
            for (var index = 0; index < analysis.Columns.Count; index++)
            {
                var column = analysis.Columns[index];
                builder.Append("        ").Append(environment.TypeName(column.ClrType)).Append(' ').Append(resultNames[column]);
                builder.AppendLine(index == analysis.Columns.Count - 1 ? ");" : ",");
            }

            builder.AppendLine();
            if (analysis.Parameters.Count == 0)
            {
                builder.Append("    public sealed record ").Append(parametersName).AppendLine("();");
            }
            else
            {
                builder.Append("    public sealed record ").Append(parametersName).AppendLine("(");
                for (var index = 0; index < analysis.Parameters.Count; index++)
                {
                    var parameter = analysis.Parameters[index];
                    builder.Append("        ").Append(environment.ParameterTypeName(parameter.ClrType)).Append(' ').Append(parameterNames[parameter]);
                    builder.AppendLine(index == analysis.Parameters.Count - 1 ? ");" : ",");
                }
            }

            builder.AppendLine();
            builder.Append("    public static global::CobaltumOrm.CobaltumQueryDefinition<")
                .Append(parametersName).Append(", ").Append(resultName).Append("> ")
                .Append(queryName).AppendLine(" { get; } =");
            builder.Append("        new global::CobaltumOrm.CobaltumQueryDefinition<")
                .Append(parametersName).Append(", ").Append(resultName).AppendLine(">(");
            builder.Append("            ").Append(CSharpNames.Literal(source.Sql)).AppendLine(",");
            builder.AppendLine("            static (command, parameters) =>");
            builder.AppendLine("            {");
            foreach (var parameter in analysis.Parameters)
            {
                var databaseTypeName = dialect.Provider == DatabaseProvider.PostgreSql
                    ? parameter.DatabaseTypeName
                    : null;
                builder.Append("                global::CobaltumOrm.CobaltumParameter.")
                    .Append(databaseTypeName != null ? "AddConfigured" : "Add")
                    .Append("(command, ")
                    .Append(CSharpNames.Literal(parameter.Name)).Append(", parameters.")
                    .Append(parameterNames[parameter]).Append(", global::System.Data.DbType.")
                    .Append(environment.DbTypeName(parameter.ClrType));
                if (databaseTypeName != null)
                {
                    builder.Append(", static parameter => ((global::Npgsql.NpgsqlParameter)parameter).DataTypeName = ")
                        .Append(CSharpNames.Literal(databaseTypeName));
                }

                builder.AppendLine(");");
            }

            builder.AppendLine("            },");
            builder.AppendLine("            static reader =>");
            builder.AppendLine("            {");
            builder.Append("                return new ").Append(resultName).AppendLine("(");
            for (var index = 0; index < analysis.Columns.Count; index++)
            {
                builder.Append("                    ").Append(environment.ReadExpression(
                    analysis.Columns[index].ClrType,
                    index,
                    source.Name + "." + analysis.Columns[index].Name));
                builder.AppendLine(index == analysis.Columns.Count - 1 ? ");" : ",");
            }

            builder.AppendLine("            });");
            builder.AppendLine();

            builder.Append("    public static global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<")
                .Append(resultName).Append(">> ").Append(queryName).AppendLine("Async(");
            builder.AppendLine("        global::System.Data.Common.DbConnection connection,");
            for (var index = 0; index < analysis.Parameters.Count; index++)
            {
                var parameter = analysis.Parameters[index];
                builder.Append("        ").Append(environment.ParameterTypeName(parameter.ClrType)).Append(' ')
                    .Append(localParameterNames[parameter]).AppendLine(",");
            }

            builder.AppendLine("        global::System.Data.Common.DbTransaction? transaction = null,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.Append("        return global::CobaltumOrm.CobaltumQueryExtensions.Query(connection, ")
                .Append(queryName).Append(", new ").Append(parametersName);
            if (analysis.Parameters.Count == 0)
            {
                builder.Append("()");
            }
            else
            {
                builder.Append('(').Append(string.Join(", ", analysis.Parameters.Select(parameter => localParameterNames[parameter]))).Append(')');
            }

            builder.AppendLine(", transaction, cancellationToken);");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    internal static string WriteMigrations(string generatedNamespace, IReadOnlyList<MigrationSource> migrations)
    {
        var builder = Header(generatedNamespace);
        foreach (var migration in migrations.Where(item => item.FlywayFile != null).OrderBy(item => item.Version))
        {
            var className = FlywayClassName(migration);
            builder.Append("[global::CobaltumOrm.Migrations.Migration(")
                .Append(migration.Version.ToString(CultureInfo.InvariantCulture)).Append(", ")
                .Append(CSharpNames.Literal(migration.Description)).AppendLine(")]");
            builder.Append("public sealed class ").Append(className)
                .AppendLine(" : global::CobaltumOrm.Migrations.ForwardOnlyMigration");
            builder.AppendLine("{");
            builder.AppendLine("    public override void Up()");
            builder.AppendLine("    {");
            builder.Append("        Execute.Sql(").Append(CSharpNames.Literal(migration.FlywayFile!.Text)).AppendLine(");");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        builder.AppendLine("public static class CobaltumMigrationCatalog");
        builder.AppendLine("{");
        builder.AppendLine("    public static global::System.Collections.Generic.IReadOnlyList<global::CobaltumOrm.Migrations.MigrationInfo> All { get; } =");
        builder.AppendLine("        global::System.Array.AsReadOnly(new global::CobaltumOrm.Migrations.MigrationInfo[]");
        builder.AppendLine("        {");
        foreach (var migration in migrations.OrderBy(item => item.Version))
        {
            var migrationType = migration.MigrationType != null
                ? migration.MigrationType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                : "global::" + generatedNamespace + "." + FlywayClassName(migration);
            builder.Append("            global::CobaltumOrm.Migrations.MigrationInfo.Create<")
                .Append(migrationType).Append(">(")
                .Append(migration.Version.ToString(CultureInfo.InvariantCulture)).Append(", ")
                .Append(CSharpNames.Literal(migration.Description)).AppendLine("),");
        }

        builder.AppendLine("        });");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static string FlywayClassName(MigrationSource migration) =>
        "FlywayV" + migration.Version.ToString(CultureInfo.InvariantCulture) + "_" +
        CSharpNames.Pascal(migration.Description, "Migration");

    internal static string WriteIsExternalInit()
    {
        return "// <auto-generated/>\n#nullable enable\nnamespace System.Runtime.CompilerServices\n{\n    internal static class IsExternalInit { }\n}\n";
    }

    private static StringBuilder Header(string? namespaceName)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        if (!string.IsNullOrEmpty(namespaceName))
        {
            builder.Append("namespace ").Append(namespaceName).AppendLine(";");
            builder.AppendLine();
        }

        return builder;
    }

    private static string Accessibility(Accessibility accessibility)
    {
        switch (accessibility)
        {
            case Microsoft.CodeAnalysis.Accessibility.Public: return "public";
            case Microsoft.CodeAnalysis.Accessibility.Internal: return "internal";
            default: return "internal";
        }
    }

    private static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None &&
        SyntaxFacts.GetContextualKeywordKind(identifier) == SyntaxKind.None
            ? identifier
            : "@" + identifier;

    private static string Qualify(Table table, IDatabaseDialect dialect)
    {
        return dialect.IdentifierQuoter.QuoteQualifiedName(table.Schema, table.Name);
    }
}

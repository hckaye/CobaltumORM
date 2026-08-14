using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CobaltumOrm.SourceGenerator;

internal static class MigrationSyntaxReader
{
    internal static IReadOnlyList<MigrationStep>? Read(
        IMethodSymbol upMethod,
        Compilation compilation,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report)
    {
        var syntax = upMethod.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as MethodDeclarationSyntax;
        if (syntax is null)
        {
            report(Diagnostic.Create(
                GeneratorDiagnostics.UnsupportedDeclaration,
                upMethod.Locations.FirstOrDefault(),
                $"Migration '{upMethod.ContainingType.ToDisplayString()}' must declare Up in source."));
            return null;
        }

        var semanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);
        var expressions = new List<ExpressionSyntax>();
        if (syntax.ExpressionBody != null)
        {
            expressions.Add(syntax.ExpressionBody.Expression);
        }
        else if (syntax.Body != null)
        {
            foreach (var statement in syntax.Body.Statements)
            {
                if (statement is ExpressionStatementSyntax expressionStatement)
                {
                    expressions.Add(expressionStatement.Expression);
                    continue;
                }

                if (statement is LocalDeclarationStatementSyntax local &&
                    local.Modifiers.Any(modifier => modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ConstKeyword)))
                {
                    continue;
                }

                report(Diagnostic.Create(
                    GeneratorDiagnostics.InvalidMigration,
                    statement.GetLocation(),
                    "Up may contain only supported migration call chains and local const declarations; control flow cannot be evaluated safely."));
                return null;
            }
        }

        var steps = new List<MigrationStep>();
        foreach (var expression in expressions)
        {
            if (!TryEvaluateDatabaseCondition(expression, semanticModel, dialect, report, out var applies))
                return null;
            if (!applies)
            {
                steps.Add(new MigrationStep(
                    "-- Operation skipped by IfDatabase during compile-time analysis.",
                    expression.GetLocation()));
                continue;
            }

            var invocation = expression as InvocationExpressionSyntax;
            if (invocation is null)
            {
                report(Diagnostic.Create(
                    GeneratorDiagnostics.InvalidMigration,
                    expression.GetLocation(),
                    "Up contains an expression that is not a supported migration call chain."));
                return null;
            }

            if (!TryFlatten(invocation, semanticModel, report, out var root, out var calls) ||
                !TryTranslate(root, calls, semanticModel, dialect, report, out var sql))
            {
                return null;
            }

            steps.Add(new MigrationStep(sql, expression.GetLocation()));
        }

        return steps;
    }

    private static bool TryEvaluateDatabaseCondition(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        out bool applies)
    {
        applies = true;
        var invocation = expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(candidate =>
                semanticModel.GetSymbolInfo(candidate).Symbol is IMethodSymbol method &&
                method.Name == "IfDatabase" &&
                method.ContainingType.Name == "Migration" &&
                method.ContainingNamespace.ToDisplayString() == "CobaltumOrm.Migrations");
        if (invocation is null) return true;

        var method = (IMethodSymbol)semanticModel.GetSymbolInfo(invocation).Symbol!;
        if (method.Parameters.Length == 1 && method.Parameters[0].Type.TypeKind == TypeKind.Delegate)
        {
            report(Diagnostic.Create(
                GeneratorDiagnostics.InvalidMigration,
                invocation.GetLocation(),
                "The predicate overload of IfDatabase cannot be evaluated during source generation; use constant database names."));
            return false;
        }

        var requested = new List<string>();
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            var constant = semanticModel.GetConstantValue(argument.Expression);
            if (!constant.HasValue || !(constant.Value is string databaseType))
            {
                report(Diagnostic.Create(
                    GeneratorDiagnostics.DynamicMigrationArgument,
                    argument.GetLocation(),
                    "IfDatabase names must be compile-time string constants during source generation."));
                return false;
            }
            requested.Add(NormalizeDatabaseName(databaseType));
        }

        var providerNames = DatabaseProviderNames(dialect.Provider)
            .Select(NormalizeDatabaseName)
            .ToArray();
        applies = requested.Any(name => providerNames.Contains(name, StringComparer.Ordinal));
        return true;
    }

    private static IEnumerable<string> DatabaseProviderNames(CobaltumOrm.Analysis.DatabaseProvider provider)
    {
        switch (provider)
        {
            case CobaltumOrm.Analysis.DatabaseProvider.PostgreSql:
                return new[] { "PostgreSQL", "Postgres", "PostgreSql", "Npgsql" };
            case CobaltumOrm.Analysis.DatabaseProvider.MySql:
                return new[] { "MySQL", "MySql" };
            case CobaltumOrm.Analysis.DatabaseProvider.SqlServer:
                return new[] { "SqlServer", "SQL Server", "MSSQL" };
            case CobaltumOrm.Analysis.DatabaseProvider.Oracle:
                return new[] { "Oracle" };
            default:
                return new[] { "SQLite", "Sqlite" };
        }
    }

    private static string NormalizeDatabaseName(string value) =>
        new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool TryFlatten(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        Action<Diagnostic> report,
        out string root,
        out List<Call> calls)
    {
        calls = new List<Call>();
        root = string.Empty;
        if (!Flatten(invocation, calls, ref root))
        {
            report(Diagnostic.Create(
                GeneratorDiagnostics.InvalidMigration,
                invocation.GetLocation(),
                "Migration expressions must use the supported Create, Alter, Delete, Rename, or Execute call-chain roots."));
            return false;
        }

        foreach (var call in calls)
        {
            var symbol = semanticModel.GetSymbolInfo(call.Syntax).Symbol as IMethodSymbol;
            if (symbol is null ||
                !IsSupportedDslCall(symbol, call.Name))
            {
                report(Diagnostic.Create(
                    GeneratorDiagnostics.InvalidMigration,
                    call.Syntax.GetLocation(),
                    $"Call '{call.Name}' is not a CobaltumOrm migration DSL operation."));
                return false;
            }
        }

        return true;
    }

    private static bool IsSupportedDslCall(IMethodSymbol symbol, string name)
    {
        if (!string.Equals(symbol.ContainingNamespace?.ToDisplayString(), "CobaltumOrm.Migrations", StringComparison.Ordinal))
        {
            return false;
        }

        var typeName = symbol.ContainingType?.Name;
        switch (typeName)
        {
            case "CreateExpressionRoot":
                return name == "Table" || name == "Schema" || name == "Column" ||
                    name == "ForeignKey" || name == "Index" || name == "Sequence" ||
                    name == "PrimaryKey" || name == "UniqueConstraint";
            case "AlterExpressionRoot":
                return name == "Table" || name == "Column";
            case "DeleteExpressionRoot":
                return name == "Table" || name == "Column" || name == "Schema" ||
                    name == "ForeignKey" || name == "FromTable" || name == "Index" ||
                    name == "Sequence" || name == "PrimaryKey" ||
                    name == "UniqueConstraint" || name == "DefaultConstraint";
            case "RenameExpressionRoot":
                return name == "Table" || name == "Column";
            case "CreateTableExpression":
                return name == "InSchema" || name == "WithColumn" ||
                    name.StartsWith("As", StringComparison.Ordinal) ||
                    name == "Nullable" || name == "NotNullable" || name == "PrimaryKey" ||
                    name == "Identity" || name == "IfNotExists" || name == "WithDescription" ||
                    name == "WithDefault" || name == "WithDefaultValue" ||
                    name == "WithColumnDescription" || name == "WithColumnAdditionalDescription" ||
                    name == "WithColumnAdditionalDescriptions" || name == "Indexed" || name == "Unique" ||
                    name == "Computed" || name == "ForeignKey" || name == "OnDelete" ||
                    name == "ReferencedBy" || name == "OnUpdate" || name == "OnDeleteOrUpdate";
            case "AlterTableExpression":
                return name == "InSchema" || name == "AddColumn" || name == "AlterColumn" ||
                    name.StartsWith("As", StringComparison.Ordinal) ||
                    name == "Nullable" || name == "NotNullable" || name == "PrimaryKey" ||
                    name == "Identity" || name == "IfExists" || name == "ToSchema" ||
                    name == "WithDescription" ||
                    name == "WithDefault" || name == "WithDefaultValue" ||
                    name == "SetExistingRowsTo" || name == "WithColumnDescription" ||
                    name == "WithColumnAdditionalDescription" || name == "WithColumnAdditionalDescriptions" ||
                    name == "Indexed" || name == "Unique" || name == "Computed" ||
                    name == "ForeignKey" || name == "ReferencedBy" || name == "OnDelete" || name == "OnUpdate" ||
                    name == "OnDeleteOrUpdate";
            case "CreateColumnOnExpression":
            case "AlterColumnOnExpression":
                return name == "OnTable";
            case "CreateIndexExpression":
                return name == "OnTable" || name == "InSchema" || name == "OnColumn" ||
                    name == "Ascending" || name == "Descending" || name == "Unique" ||
                    name == "NonClustered" || name == "Clustered" || name == "WithOptions";
            case "CreateForeignKeyExpression":
                return name == "FromTable" || name == "InSchema" || name == "ForeignColumn" ||
                    name == "ForeignColumns" || name == "ToTable" || name == "PrimaryColumn" ||
                    name == "PrimaryColumns" || name == "OnDelete" || name == "OnUpdate" ||
                    name == "OnDeleteOrUpdate";
            case "CreateSequenceExpression":
                return name == "InSchema" || name == "IncrementBy" || name == "MinValue" ||
                    name == "MaxValue" || name == "StartWith" || name == "Cache" || name == "Cycle";
            case "CreateConstraintExpression":
                return name == "OnTable" || name == "WithSchema" || name == "Column" || name == "Columns";
            case "DeleteTableExpression":
            case "DeleteColumnExpression":
            case "RenameTableToExpression":
            case "RenameColumnToExpression":
                return name == "InSchema" || name == "To" || name == "IfExists";
            case "RenameTableResultExpression":
                return name == "InSchema";
            case "DeleteColumnFromExpression":
                return name == "FromTable" || name == "Column";
            case "DeleteColumnsFromExpression":
                return name == "FromTable" || name == "Column";
            case "DeleteColumnsExpression":
                return name == "InSchema";
            case "DeleteForeignKeyExpression":
                return name == "FromTable" || name == "OnTable" || name == "InSchema" ||
                    name == "ForeignColumn" || name == "ForeignColumns" || name == "ToTable" ||
                    name == "PrimaryColumn" || name == "PrimaryColumns";
            case "DeleteIndexExpression":
                return name == "OnTable" || name == "InSchema" || name == "OnColumn" ||
                    name == "OnColumns" || name == "WithOptions";
            case "DeleteSequenceExpression":
                return name == "InSchema";
            case "DeleteConstraintExpression":
                return name == "FromTable" || name == "InSchema" || name == "Column" || name == "Columns";
            case "DeleteDefaultConstraintExpression":
                return name == "OnTable" || name == "InSchema" || name == "OnColumn";
            case "DeleteDataExpression":
                return name == "InSchema" || name == "Row" || name == "Where" ||
                    name == "IsNull" || name == "AllRows";
            case "InsertExpressionRoot":
                return name == "IntoTable";
            case "InsertDataExpression":
                return name == "InSchema" || name == "Row" || name == "Rows";
            case "UpdateExpressionRoot":
                return name == "Table";
            case "UpdateDataExpression":
                return name == "InSchema" || name == "Set" || name == "Where" || name == "AllRows";
            case "RenameColumnOnExpression":
                return name == "OnTable";
            case "ExecuteExpressionRoot":
                return name == "Sql" || name == "Script" || name == "EmbeddedScript" ||
                    name == "WithConnection";
            default:
                return false;
        }
    }

    private static bool Flatten(ExpressionSyntax expression, List<Call> calls, ref string root)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        if (expression is InvocationExpressionSyntax invocation &&
            invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            if (!FlattenReceiver(memberAccess.Expression, calls, ref root))
            {
                return false;
            }

            calls.Add(new Call(memberAccess.Name.Identifier.ValueText, invocation));
            return true;
        }

        return false;
    }

    private static bool FlattenReceiver(ExpressionSyntax expression, List<Call> calls, ref string root)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        if (expression is InvocationExpressionSyntax invocation)
        {
            return Flatten(invocation, calls, ref root);
        }

        if (expression is IdentifierNameSyntax identifier)
        {
            root = identifier.Identifier.ValueText;
            return true;
        }

        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            root = memberAccess.Name.Identifier.ValueText;
            return true;
        }

        return false;
    }

    private static bool TryTranslate(
        string root,
        IReadOnlyList<Call> calls,
        SemanticModel semanticModel,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        out string sql)
    {
        sql = string.Empty;
        switch (root)
        {
            case "Create": return TryCreate(calls, semanticModel, dialect, report, out sql);
            case "Alter": return TryAlter(calls, semanticModel, dialect, report, out sql);
            case "Delete": return TryDelete(calls, semanticModel, dialect, report, out sql);
            case "Rename": return TryRename(calls, semanticModel, dialect, report, out sql);
            case "Execute": return TryExecute(calls, semanticModel, report, out sql);
            case "Insert":
            case "Update":
                sql = "-- Data migration does not change the compile-time table shape.";
                return true;
            default:
                report(Diagnostic.Create(
                    GeneratorDiagnostics.InvalidMigration,
                    calls[0].Syntax.GetLocation(),
                    $"Migration root '{root}' is not supported."));
                return false;
        }
    }

    private static bool TryCreate(
        IReadOnlyList<Call> calls,
        SemanticModel semanticModel,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        out string sql)
    {
        sql = string.Empty;
        if (calls.Count == 0)
        {
            return Invalid(calls, report, "Create requires an operation.");
        }

        if (calls[0].Name == "Column" && calls.Count >= 2 && calls[1].Name == "OnTable")
        {
            var addCalls = new List<Call>
            {
                new Call("Table", calls[1].Syntax),
                new Call("AddColumn", calls[0].Syntax),
            };
            addCalls.AddRange(calls.Skip(2));
            return TryAlter(addCalls, semanticModel, dialect, report, out sql);
        }

        if (calls[0].Name != "Table")
        {
            sql = "-- Migration operation does not change the compile-time table shape.";
            return true;
        }

        if (!TryString(calls[0], 0, semanticModel, report, out var tableName)) return false;

        string? schema = null;
        var columns = new List<ColumnBuilder>();
        ColumnBuilder? current = null;
        for (var index = 1; index < calls.Count; index++)
        {
            var call = calls[index];
            switch (call.Name)
            {
                case "InSchema":
                    if (!TrySchema(call, semanticModel, dialect, report, out schema)) return false;
                    break;
                case "WithColumn":
                    if (!TryString(call, 0, semanticModel, report, out var columnName)) return false;
                    current = new ColumnBuilder(columnName, call.Syntax.GetLocation());
                    columns.Add(current);
                    break;
                case "Nullable":
                    if (!RequireNoArguments(call, report) || !RequireColumn(current, call, report)) return false;
                    current!.Nullable = current.PrimaryKey ? false : true;
                    break;
                case "NotNullable":
                    if (!RequireNoArguments(call, report) || !RequireColumn(current, call, report)) return false;
                    current!.Nullable = false;
                    break;
                case "PrimaryKey":
                    if (!RequireAtMostOneStringArgument(call, semanticModel, report) || !RequireColumn(current, call, report)) return false;
                    current!.PrimaryKey = true;
                    current.PrimaryKeyLocation = call.Syntax.GetLocation();
                    current.Nullable = false;
                    break;
                case "Identity":
                    if (!RequireNoArguments(call, report) || !RequireColumn(current, call, report)) return false;
                    current!.Identity = true;
                    current.IdentityLocation = call.Syntax.GetLocation();
                    break;
                case "IfNotExists":
                    if (!RequireNoArguments(call, report)) return false;
                    break;
                case "WithDescription":
                    if (!TryString(call, 0, semanticModel, report, out _)) return false;
                    break;
                case "WithDefault":
                case "WithDefaultValue":
                    if (!RequireColumn(current, call, report) ||
                        !TryDefaultExpression(call, semanticModel, dialect, report, out var defaultExpression)) return false;
                    current!.DefaultExpression = defaultExpression;
                    break;
                case "WithColumnDescription":
                case "WithColumnAdditionalDescription":
                case "WithColumnAdditionalDescriptions":
                case "Indexed":
                case "Unique":
                case "Computed":
                case "ForeignKey":
                case "ReferencedBy":
                case "OnDelete":
                case "OnUpdate":
                case "OnDeleteOrUpdate":
                    if (!RequireColumn(current, call, report)) return false;
                    break;
                default:
                    if (!TrySetType(current, call, semanticModel, dialect, report)) return false;
                    break;
            }
        }

        if (columns.Count == 0)
        {
            return Invalid(calls, report, "Create.Table must declare at least one column.");
        }

        if (columns.Any(column => column.SqlType is null))
        {
            var missing = columns.First(column => column.SqlType is null);
            report(Diagnostic.Create(
                GeneratorDiagnostics.InvalidMigration,
                missing.Location,
                $"Column '{missing.Name}' must select a supported type."));
            return false;
        }

        var columnSql = new List<string>();
        foreach (var column in columns)
        {
            if (!TryColumnSql(column, dialect, isAddedColumn: false, report, out var formattedColumn))
            {
                return false;
            }

            columnSql.Add(formattedColumn);
        }

        sql = dialect.MigrationSqlWriter.CreateTable(
            Qualify(schema, tableName, dialect),
            columnSql);
        return true;
    }

    private static bool TryAlter(
        IReadOnlyList<Call> calls,
        SemanticModel semanticModel,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        out string sql)
    {
        sql = string.Empty;
        if (calls.Count == 0 || (calls[0].Name != "Table" && calls[0].Name != "Column"))
        {
            return Invalid(calls, report, "Alter supports Alter.Table or Alter.Column.");
        }

        var startIndex = 1;
        string tableName;
        var alterations = new List<AlterBuilder>();
        AlterBuilder? current = null;
        if (calls[0].Name == "Column")
        {
            if (calls.Count < 2 || calls[1].Name != "OnTable")
                return Invalid(calls, report, "Alter.Column must call OnTable.");
            if (!TryString(calls[0], 0, semanticModel, report, out var standaloneColumn) ||
                !TryString(calls[1], 0, semanticModel, report, out tableName)) return false;
            current = new AlterBuilder(standaloneColumn, false, calls[0].Syntax.GetLocation());
            alterations.Add(current);
            startIndex = 2;
        }
        else if (!TryString(calls[0], 0, semanticModel, report, out tableName))
        {
            return false;
        }

        string? schema = null;
        for (var index = startIndex; index < calls.Count; index++)
        {
            var call = calls[index];
            switch (call.Name)
            {
                case "InSchema":
                    if (!TrySchema(call, semanticModel, dialect, report, out schema)) return false;
                    break;
                case "AddColumn":
                case "AlterColumn":
                    if (!TryString(call, 0, semanticModel, report, out var columnName)) return false;
                    var columnLocation = call.Name == "AlterColumn"
                        ? call.Syntax.ArgumentList.Arguments[0].Expression.GetLocation()
                        : call.Syntax.GetLocation();
                    current = new AlterBuilder(columnName, call.Name == "AddColumn", columnLocation);
                    alterations.Add(current);
                    break;
                case "Nullable":
                    if (!RequireNoArguments(call, report) || !RequireAlter(current, call, report)) return false;
                    current!.Column.Nullable = current.Column.PrimaryKey ? false : true;
                    break;
                case "NotNullable":
                    if (!RequireNoArguments(call, report) || !RequireAlter(current, call, report)) return false;
                    current!.Column.Nullable = false;
                    break;
                case "PrimaryKey":
                    if (!RequireAtMostOneStringArgument(call, semanticModel, report) || !RequireAlter(current, call, report)) return false;
                    if (!current!.IsAdd) return Invalid(call, report, "PrimaryKey is supported only for AddColumn.");
                    current.Column.PrimaryKey = true;
                    current.Column.PrimaryKeyLocation = call.Syntax.GetLocation();
                    current.Column.Nullable = false;
                    break;
                case "Identity":
                    if (!RequireNoArguments(call, report) || !RequireAlter(current, call, report)) return false;
                    if (!current!.IsAdd) return Invalid(call, report, "Identity is supported only for AddColumn.");
                    current.Column.Identity = true;
                    current.Column.IdentityLocation = call.Syntax.GetLocation();
                    break;
                case "IfExists":
                    if (!RequireNoArguments(call, report)) return false;
                    break;
                case "ToSchema":
                    if (!TryString(call, 0, semanticModel, report, out _)) return false;
                    break;
                case "WithDescription":
                    if (!TryString(call, 0, semanticModel, report, out _)) return false;
                    break;
                case "WithDefault":
                case "WithDefaultValue":
                    if (!RequireAlter(current, call, report) ||
                        !TryDefaultExpression(call, semanticModel, dialect, report, out var defaultExpression)) return false;
                    current!.Column.DefaultExpression = defaultExpression;
                    break;
                case "SetExistingRowsTo":
                case "WithColumnDescription":
                case "WithColumnAdditionalDescription":
                case "WithColumnAdditionalDescriptions":
                case "Indexed":
                case "Unique":
                case "Computed":
                case "ForeignKey":
                case "ReferencedBy":
                case "OnDelete":
                case "OnUpdate":
                case "OnDeleteOrUpdate":
                    if (!RequireAlter(current, call, report)) return false;
                    break;
                default:
                    if (!TrySetType(current?.Column, call, semanticModel, dialect, report)) return false;
                    break;
            }
        }

        if (alterations.Count == 0)
        {
            if (calls.Any(call => call.Name == "ToSchema" || call.Name == "WithDescription"))
            {
                sql = "-- Moving a table does not change its compile-time column shape.";
                return true;
            }
            return Invalid(calls, report, "Alter.Table must call AddColumn or AlterColumn.");
        }

        var statements = new List<string>();
        foreach (var alteration in alterations)
        {
            if (alteration.IsAdd)
            {
                if (alteration.Column.SqlType is null)
                {
                    report(Diagnostic.Create(
                        GeneratorDiagnostics.InvalidMigration,
                        alteration.Column.Location,
                        $"Added column '{alteration.Column.Name}' must select a supported type."));
                    return false;
                }

                if (!TryColumnSql(
                    alteration.Column,
                    dialect,
                    isAddedColumn: true,
                    report,
                    out var formattedColumn))
                {
                    return false;
                }

                statements.Add(dialect.MigrationSqlWriter.AddColumn(
                    Qualify(schema, tableName, dialect),
                    formattedColumn));
                continue;
            }

            var qualifiedTable = Qualify(schema, tableName, dialect);
            var quotedColumn = dialect.IdentifierQuoter.QuoteIdentifier(alteration.Column.Name);
            if (!dialect.MigrationSqlWriter.TryAlterColumn(
                    qualifiedTable,
                    quotedColumn,
                    alteration.Column.SqlType,
                    alteration.Column.Nullable,
                    out var alterSql,
                    out var alterError) ||
                alterSql is null)
            {
                var detail = string.IsNullOrWhiteSpace(alterError)
                    ? "The configured database provider did not return an explanation."
                    : alterError;
                report(Diagnostic.Create(
                    GeneratorDiagnostics.InvalidMigration,
                    alteration.Column.Location,
                    $"Altered column '{alteration.Column.Name}' cannot be generated: {detail}"));
                return false;
            }

            statements.Add(alterSql);
            if (alteration.Column.DefaultExpression != null)
            {
                if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.Sqlite)
                {
                    return Invalid(
                        alteration.Column.Location,
                        report,
                        $"SQLite cannot alter the default for column '{alteration.Column.Name}' without rebuilding the table.");
                }
                statements.Add(SetColumnDefaultSql(
                    qualifiedTable,
                    quotedColumn,
                    alteration.Column.DefaultExpression,
                    dialect));
            }
        }

        sql = string.Join("\n", statements);
        return true;
    }

    private static bool TryDelete(
        IReadOnlyList<Call> calls,
        SemanticModel semanticModel,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        out string sql)
    {
        sql = string.Empty;
        if (calls.Count == 0) return Invalid(calls, report, "Delete requires Table or Column.");
        string? schema = null;
        foreach (var schemaCall in calls.Where(call => call.Name == "InSchema"))
        {
            if (!TrySchema(schemaCall, semanticModel, dialect, report, out schema)) return false;
        }

        if (calls[0].Name == "Table")
        {
            if (!TryString(calls[0], 0, semanticModel, report, out var tableName)) return false;
            if (calls.Any(call => call.Name != "Table" && call.Name != "InSchema" && call.Name != "IfExists"))
                return Invalid(calls, report, "Delete.Table supports optional InSchema and IfExists calls.");
            sql = dialect.MigrationSqlWriter.DropTable(Qualify(schema, tableName, dialect));
            return true;
        }

        if (calls[0].Name == "Column" && calls.Any(call => call.Name == "FromTable"))
        {
            var fromCall = calls.First(call => call.Name == "FromTable");
            if (!TryString(fromCall, 0, semanticModel, report, out var fromTable)) return false;
            if (calls.Any(call => call.Name != "Column" && call.Name != "FromTable" && call.Name != "InSchema"))
                return Invalid(calls, report, "Delete.Column supports FromTable and an optional InSchema call.");
            var statements = new List<string>();
            foreach (var columnCall in calls.TakeWhile(call => call.Name != "FromTable"))
            {
                if (!TryString(columnCall, 0, semanticModel, report, out var columnName)) return false;
                statements.Add(dialect.MigrationSqlWriter.DropColumn(
                    Qualify(schema, fromTable, dialect),
                    dialect.IdentifierQuoter.QuoteIdentifier(columnName)));
            }
            sql = string.Join("\n", statements);
            return true;
        }

        sql = "-- Migration operation does not change the compile-time table shape.";
        return true;
    }

    private static bool TryRename(
        IReadOnlyList<Call> calls,
        SemanticModel semanticModel,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        out string sql)
    {
        sql = string.Empty;
        if (calls.Count == 0) return Invalid(calls, report, "Rename requires Table or Column.");
        string? schema = null;
        foreach (var schemaCall in calls.Where(call => call.Name == "InSchema"))
        {
            if (!TrySchema(schemaCall, semanticModel, dialect, report, out schema)) return false;
        }

        if (calls[0].Name == "Table" && calls.Any(call => call.Name == "To"))
        {
            var toCall = calls.First(call => call.Name == "To");
            if (!TryString(calls[0], 0, semanticModel, report, out var oldTable) ||
                !TryString(toCall, 0, semanticModel, report, out var newTable)) return false;
            if (calls.Any(call => call.Name != "Table" && call.Name != "To" && call.Name != "InSchema"))
                return Invalid(calls, report, "Rename.Table supports InSchema and To only.");
            sql = dialect.MigrationSqlWriter.RenameTable(
                Qualify(schema, oldTable, dialect),
                dialect.IdentifierQuoter.QuoteIdentifier(newTable));
            return true;
        }

        if (calls[0].Name == "Column" && calls.Count >= 3 && calls.Any(call => call.Name == "OnTable") && calls.Last().Name == "To")
        {
            if (!TryString(calls[0], 0, semanticModel, report, out var oldColumn) ||
                !TryString(calls.First(call => call.Name == "OnTable"), 0, semanticModel, report, out var tableName) ||
                !TryString(calls.Last(), 0, semanticModel, report, out var newColumn)) return false;
            if (calls.Any(call => call.Name != "Column" && call.Name != "OnTable" && call.Name != "To" && call.Name != "InSchema"))
                return Invalid(calls, report, "Rename.Column supports OnTable, InSchema, and To only.");
            sql = dialect.MigrationSqlWriter.RenameColumn(
                Qualify(schema, tableName, dialect),
                dialect.IdentifierQuoter.QuoteIdentifier(oldColumn),
                dialect.IdentifierQuoter.QuoteIdentifier(newColumn));
            return true;
        }

        return Invalid(calls, report, "Rename supports Rename.Table(...).To(...) or Rename.Column(...).OnTable(...).To(...).");
    }

    private static bool TryExecute(
        IReadOnlyList<Call> calls,
        SemanticModel semanticModel,
        Action<Diagnostic> report,
        out string sql)
    {
        sql = string.Empty;
        if (calls.Count != 1 || calls[0].Name != "Sql")
        {
            return Invalid(
                calls,
                report,
                "Execute.Script, Execute.EmbeddedScript, and Execute.WithConnection cannot be evaluated at compile time. Use Execute.Sql with constant SQL when Up changes table columns.");
        }

        return TryString(calls[0], 0, semanticModel, report, out sql);
    }

    private static bool TrySetType(
        ColumnBuilder? column,
        Call call,
        SemanticModel semanticModel,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report)
    {
        if (!RequireColumn(column, call, report)) return false;
        if (column!.SqlType != null)
        {
            return Invalid(call, report, $"Column '{column.Name}' selects more than one type.");
        }

        switch (call.Name)
        {
            case "AsInt16": return TrySetMappedType(column, call, dialect, report, "int16") && RequireNoArguments(call, report);
            case "AsByte": return TrySetMappedType(column, call, dialect, report, "int16") && RequireNoArguments(call, report);
            case "AsInt32": return TrySetMappedType(column, call, dialect, report, "int32") && RequireNoArguments(call, report);
            case "AsInt64": return TrySetMappedType(column, call, dialect, report, "int64") && RequireNoArguments(call, report);
            case "AsBoolean": return TrySetMappedType(column, call, dialect, report, "boolean") && RequireNoArguments(call, report);
            case "AsFloat": return TrySetMappedType(column, call, dialect, report, "float") && RequireNoArguments(call, report);
            case "AsDouble": return TrySetMappedType(column, call, dialect, report, "double") && RequireNoArguments(call, report);
            case "AsText": return TrySetMappedType(column, call, dialect, report, "text") && RequireNoArguments(call, report);
            case "AsDate": return TrySetMappedType(column, call, dialect, report, "date") && RequireNoArguments(call, report);
            case "AsDateTime": return TrySetMappedType(column, call, dialect, report, "datetime") && RequireNoArguments(call, report);
            case "AsDateTime2": return TrySetMappedType(column, call, dialect, report, "datetime") && RequireNoArguments(call, report);
            case "AsDateTimeOffset":
                if (call.Syntax.ArgumentList.Arguments.Count > 1) return Invalid(call, report, "AsDateTimeOffset accepts at most one precision argument.");
                if (call.Syntax.ArgumentList.Arguments.Count == 1 && !TryInt(call, 0, semanticModel, report, out _)) return false;
                return TrySetMappedType(column, call, dialect, report, "datetimeoffset");
            case "AsTime": return TrySetMappedType(column, call, dialect, report, "time") && RequireNoArguments(call, report);
            case "AsGuid": return TrySetMappedType(column, call, dialect, report, "guid") && RequireNoArguments(call, report);
            case "AsBinary":
                if (call.Syntax.ArgumentList.Arguments.Count > 1) return Invalid(call, report, "AsBinary accepts at most one length argument.");
                if (call.Syntax.ArgumentList.Arguments.Count == 1 && !TryPositiveInt(call, 0, semanticModel, report, out _)) return false;
                return TrySetMappedType(column, call, dialect, report, "binary");
            case "AsJson": return TrySetMappedType(column, call, dialect, report, "json") && RequireNoArguments(call, report);
            case "AsJsonb": return TrySetMappedType(column, call, dialect, report, "jsonb") && RequireNoArguments(call, report);
            case "AsCurrency":
                return TrySetMappedType(column, call, dialect, report, "decimal", precision: 19, scale: 4) && RequireNoArguments(call, report);
            case "AsAnsiString":
                return TrySetStringLikeType(column, call, semanticModel, dialect, report, fixedLength: false);
            case "AsFixedLengthString":
            case "AsFixedLengthAnsiString":
                return TrySetStringLikeType(column, call, semanticModel, dialect, report, fixedLength: true);
            case "AsXml":
                if (call.Syntax.ArgumentList.Arguments.Count > 1) return Invalid(call, report, "AsXml accepts at most one length argument.");
                if (call.Syntax.ArgumentList.Arguments.Count == 1 && !TryPositiveInt(call, 0, semanticModel, report, out _)) return false;
                return TrySetDirectType(column, call, dialect, report, XmlType(dialect));
            case "AsCustom":
                if (!TryString(call, 0, semanticModel, report, out var customType)) return false;
                return TrySetDirectType(column, call, dialect, report, customType);
            case "AsString":
                if (call.Syntax.ArgumentList.Arguments.Count == 0)
                {
                    return TrySetMappedType(column, call, dialect, report, "string");
                }
                return TrySetStringLikeType(column, call, semanticModel, dialect, report, fixedLength: false);
            case "AsDecimal":
                if (call.Syntax.ArgumentList.Arguments.Count == 0)
                {
                    return TrySetMappedType(column, call, dialect, report, "decimal");
                }

                if (!TryInt(call, 0, semanticModel, report, out var precision) ||
                    !TryInt(call, 1, semanticModel, report, out var scale)) return false;
                if (precision <= 0 || scale < 0 || scale > precision)
                    return Invalid(call, report, "AsDecimal precision and scale must be valid compile-time constants.");
                return TrySetMappedType(
                    column,
                    call,
                    dialect,
                    report,
                    "decimal",
                    precision: precision,
                    scale: scale);
            default:
                return Invalid(call, report, $"Migration DSL call '{call.Name}' is not supported by compile-time analysis.");
        }
    }

    private static bool TrySetStringLikeType(
        ColumnBuilder column,
        Call call,
        SemanticModel semanticModel,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        bool fixedLength)
    {
        var arguments = call.Syntax.ArgumentList.Arguments;
        if (arguments.Count == 0)
        {
            if (fixedLength) return Invalid(call, report, $"{call.Name} requires a positive length.");
            return TrySetMappedType(column, call, dialect, report, "string");
        }
        if (arguments.Count > 2) return Invalid(call, report, $"{call.Name} accepts length and optional collation only.");

        var firstType = semanticModel.GetTypeInfo(arguments[0].Expression).ConvertedType;
        if (firstType?.SpecialType == SpecialType.System_String)
        {
            if (fixedLength || arguments.Count != 1)
                return Invalid(call, report, $"{call.Name} requires a length before its collation.");
            return TryString(call, 0, semanticModel, report, out _) &&
                TrySetMappedType(column, call, dialect, report, "string");
        }

        if (!TryPositiveInt(call, 0, semanticModel, report, out var length)) return false;
        if (arguments.Count == 2 && !TryString(call, 1, semanticModel, report, out _)) return false;
        return TrySetMappedType(column, call, dialect, report, "string", length: length);
    }

    private static bool TrySetDirectType(
        ColumnBuilder column,
        Call call,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        string sqlType)
    {
        var supported = dialect.TypeMapper is CobaltumOrm.Analysis.PostgreSqlTypeMapper postgreSql
            ? postgreSql.TryMapType(sqlType, out _)
            : dialect.TypeMapper.TryMap(sqlType, out _);
        if (!supported)
            return Invalid(call, report, $"Database provider '{dialect.Name}' does not recognize migration type '{sqlType}'.");
        column.SqlType = sqlType;
        column.LogicalType = "custom";
        column.TypeLocation = call.Syntax.GetLocation();
        return true;
    }

    private static string XmlType(CobaltumOrm.Analysis.IDatabaseDialect dialect)
    {
        switch (dialect.Provider)
        {
            case CobaltumOrm.Analysis.DatabaseProvider.PostgreSql: return "xml";
            case CobaltumOrm.Analysis.DatabaseProvider.SqlServer: return "xml";
            case CobaltumOrm.Analysis.DatabaseProvider.Oracle: return "XMLTYPE";
            case CobaltumOrm.Analysis.DatabaseProvider.MySql: return "longtext";
            default: return "TEXT";
        }
    }

    private static bool TrySetMappedType(
        ColumnBuilder column,
        Call call,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        string logicalType,
        int? length = null,
        int? precision = null,
        int? scale = null)
    {
        if (!TryMapMigrationType(
            call,
            dialect,
            report,
            logicalType,
            length,
            precision,
            scale,
            out var sqlType))
        {
            return false;
        }

        column.SqlType = sqlType;
        column.LogicalType = logicalType;
        column.TypeLocation = call.Syntax.GetLocation();
        return true;
    }

    private static bool TryMapMigrationType(
        Call call,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        string logicalType,
        int? length,
        int? precision,
        int? scale,
        out string sqlType)
    {
        sqlType = string.Empty;
        try
        {
            sqlType = dialect.TypeMapper.MapMigrationType(logicalType, length, precision, scale);
            return true;
        }
        catch (ArgumentException exception)
        {
            return Invalid(
                call,
                report,
                FormatProviderValidationMessage(dialect, call.Name, exception));
        }
        catch (NotSupportedException exception)
        {
            return Invalid(
                call,
                report,
                FormatProviderValidationMessage(dialect, call.Name, exception));
        }
    }

    private static bool TryColumnSql(
        ColumnBuilder column,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        bool isAddedColumn,
        Action<Diagnostic> report,
        out string sql)
    {
        sql = string.Empty;
        if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.Sqlite)
        {
            if (isAddedColumn && (column.PrimaryKey || column.Identity))
            {
                return Invalid(
                    ColumnOptionLocation(column),
                    report,
                    $"SQLite ALTER TABLE ADD COLUMN cannot add primary-key or identity column '{column.Name}'.");
            }

            if (column.Identity &&
                (!string.Equals(column.LogicalType, "int64", StringComparison.Ordinal) || !column.PrimaryKey))
            {
                return Invalid(
                    column.IdentityLocation ?? ColumnOptionLocation(column),
                    report,
                    $"SQLite identity column '{column.Name}' must be an Int64 primary key.");
            }
        }

        try
        {
            sql = dialect.MigrationSqlWriter.FormatColumn(
                dialect.IdentifierQuoter.QuoteIdentifier(column.Name),
                column.SqlType!,
                column.Nullable,
                column.PrimaryKey,
                column.Identity);
            if (column.DefaultExpression != null)
            {
                sql += " DEFAULT " + column.DefaultExpression;
            }
            return true;
        }
        catch (ArgumentException exception)
        {
            return Invalid(
                ColumnOptionLocation(column),
                report,
                FormatProviderValidationMessage(dialect, "FormatColumn", exception));
        }
        catch (NotSupportedException exception)
        {
            return Invalid(
                ColumnOptionLocation(column),
                report,
                FormatProviderValidationMessage(dialect, "FormatColumn", exception));
        }
    }

    private static string FormatProviderValidationMessage(
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        string operation,
        Exception exception)
    {
        var detail = string.IsNullOrWhiteSpace(exception.Message)
            ? "The provider rejected the requested migration operation."
            : exception.Message;
        return $"Database provider '{dialect.Name}' rejected {operation}: {detail}";
    }

    private static bool TryDefaultExpression(
        Call call,
        SemanticModel semanticModel,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        out string expression)
    {
        expression = string.Empty;
        if (call.Syntax.ArgumentList.Arguments.Count != 1)
            return Invalid(call, report, $"'{call.Name}' requires one default value.");

        var valueExpression = call.Syntax.ArgumentList.Arguments[0].Expression;
        if (valueExpression is InvocationExpressionSyntax rawInvocation &&
            semanticModel.GetSymbolInfo(rawInvocation).Symbol is IMethodSymbol rawMethod &&
            rawMethod.Name == "Insert" && rawMethod.ContainingType.Name == "RawSql" &&
            rawMethod.ContainingNamespace.ToDisplayString() == "CobaltumOrm.Migrations")
        {
            if (rawInvocation.ArgumentList.Arguments.Count != 1 ||
                !TryConstantString(rawInvocation.ArgumentList.Arguments[0].Expression, semanticModel, out expression))
            {
                report(Diagnostic.Create(
                    GeneratorDiagnostics.DynamicMigrationArgument,
                    valueExpression.GetLocation(),
                    "RawSql.Insert requires a compile-time string constant during source generation."));
                return false;
            }
            return true;
        }

        if (semanticModel.GetSymbolInfo(valueExpression).Symbol is IFieldSymbol field &&
            field.ContainingType.Name == "SystemMethods" &&
            field.ContainingNamespace.ToDisplayString() == "CobaltumOrm.Migrations")
        {
            return TrySystemMethod(field.Name, call, dialect, report, out expression);
        }

        var constant = semanticModel.GetConstantValue(valueExpression);
        if (!constant.HasValue)
        {
            report(Diagnostic.Create(
                GeneratorDiagnostics.DynamicMigrationArgument,
                valueExpression.GetLocation(),
                $"Argument to '{call.Name}' must be a compile-time constant, SystemMethods value, or RawSql.Insert call."));
            return false;
        }

        var value = constant.Value;
        if (value is null)
        {
            expression = "NULL";
            return true;
        }
        if (value is string text)
        {
            expression = SqlString(text, dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.SqlServer);
            return true;
        }
        if (value is char character)
        {
            expression = SqlString(character.ToString(), dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.SqlServer);
            return true;
        }
        if (value is bool boolean)
        {
            expression = dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.PostgreSql ||
                dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.MySql
                ? (boolean ? "TRUE" : "FALSE")
                : (boolean ? "1" : "0");
            return true;
        }
        if (value is IFormattable formattable)
        {
            expression = formattable.ToString(null, CultureInfo.InvariantCulture) ?? "NULL";
            return true;
        }

        return Invalid(call, report, $"Default value type '{value.GetType().FullName}' is not supported during source generation.");
    }

    private static bool TrySystemMethod(
        string method,
        Call call,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        out string expression)
    {
        expression = string.Empty;
        switch (method)
        {
            case "NewGuid":
                switch (dialect.Provider)
                {
                    case CobaltumOrm.Analysis.DatabaseProvider.PostgreSql: expression = "gen_random_uuid()"; break;
                    case CobaltumOrm.Analysis.DatabaseProvider.MySql: expression = "UUID()"; break;
                    case CobaltumOrm.Analysis.DatabaseProvider.SqlServer: expression = "NEWID()"; break;
                    case CobaltumOrm.Analysis.DatabaseProvider.Oracle: expression = "SYS_GUID()"; break;
                    default: expression = "lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)),2) || '-' || substr('89ab',abs(random()) % 4 + 1,1) || substr(hex(randomblob(2)),2) || '-' || hex(randomblob(6)))"; break;
                }
                return true;
            case "NewSequentialId":
                if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.SqlServer) expression = "NEWSEQUENTIALID()";
                else if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.PostgreSql) expression = "uuid_generate_v1()";
                else if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.MySql) expression = "UUID()";
                else if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.Oracle) expression = "SYS_GUID()";
                else return Invalid(call, report, "SQLite does not provide a sequential GUID function.");
                return true;
            case "CurrentDateTime":
                if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.SqlServer) expression = "GETDATE()";
                else if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.Oracle) expression = "LOCALTIMESTAMP";
                else expression = "CURRENT_TIMESTAMP";
                return true;
            case "CurrentDateTimeOffset":
                if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.SqlServer) expression = "SYSDATETIMEOFFSET()";
                else if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.Oracle) expression = "SYSTIMESTAMP";
                else expression = "CURRENT_TIMESTAMP";
                return true;
            case "CurrentUTCDateTime":
                if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.PostgreSql) expression = "CURRENT_TIMESTAMP AT TIME ZONE 'UTC'";
                else if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.MySql) expression = "UTC_TIMESTAMP()";
                else if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.SqlServer) expression = "SYSUTCDATETIME()";
                else if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.Oracle) expression = "SYS_EXTRACT_UTC(SYSTIMESTAMP)";
                else expression = "CURRENT_TIMESTAMP";
                return true;
            case "CurrentUser":
                if (dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.Sqlite)
                    return Invalid(call, report, "SQLite does not expose a current database user.");
                expression = dialect.Provider == CobaltumOrm.Analysis.DatabaseProvider.Oracle ? "USER" : "CURRENT_USER";
                return true;
            default:
                return Invalid(call, report, $"SystemMethods.{method} is not supported.");
        }
    }

    private static string SetColumnDefaultSql(
        string qualifiedTable,
        string quotedColumn,
        string defaultExpression,
        CobaltumOrm.Analysis.IDatabaseDialect dialect)
    {
        switch (dialect.Provider)
        {
            case CobaltumOrm.Analysis.DatabaseProvider.SqlServer:
                return $"ALTER TABLE {qualifiedTable} ADD DEFAULT {defaultExpression} FOR {quotedColumn};";
            case CobaltumOrm.Analysis.DatabaseProvider.Oracle:
                return $"ALTER TABLE {qualifiedTable} MODIFY ({quotedColumn} DEFAULT {defaultExpression});";
            default:
                return $"ALTER TABLE {qualifiedTable} ALTER COLUMN {quotedColumn} SET DEFAULT {defaultExpression};";
        }
    }

    private static bool TryConstantString(ExpressionSyntax expression, SemanticModel semanticModel, out string value)
    {
        var constant = semanticModel.GetConstantValue(expression);
        value = constant.HasValue && constant.Value is string text ? text : string.Empty;
        return constant.HasValue && constant.Value is string;
    }

    private static string SqlString(string value, bool unicode) =>
        (unicode ? "N" : string.Empty) + "'" + value.Replace("'", "''") + "'";

    private static Location ColumnOptionLocation(ColumnBuilder column)
    {
        if (column.IdentityLocation != null)
        {
            return column.IdentityLocation;
        }

        if (column.PrimaryKeyLocation != null)
        {
            return column.PrimaryKeyLocation;
        }

        return column.TypeLocation ?? column.Location;
    }

    private static bool TrySchema(
        Call call,
        SemanticModel semanticModel,
        CobaltumOrm.Analysis.IDatabaseDialect dialect,
        Action<Diagnostic> report,
        out string? schema)
    {
        schema = null;
        if (!TryString(call, 0, semanticModel, report, out var value))
        {
            return false;
        }

        if (!dialect.SchemaRules.SupportsSchemas && !dialect.SchemaRules.IsDefaultSchema(value))
        {
            return Invalid(
                call,
                report,
                $"Database provider '{dialect.Name}' does not support named schemas; remove InSchema(...) from this migration.");
        }

        schema = value;
        return true;
    }

    private static string Qualify(string? schema, string name, CobaltumOrm.Analysis.IDatabaseDialect dialect) =>
        dialect.IdentifierQuoter.QuoteQualifiedName(schema, name);

    private static bool TryString(
        Call call,
        int index,
        SemanticModel semanticModel,
        Action<Diagnostic> report,
        out string value)
    {
        value = string.Empty;
        if (index >= call.Syntax.ArgumentList.Arguments.Count)
        {
            return Invalid(call, report, $"'{call.Name}' is missing a required string argument.");
        }

        var expression = call.Syntax.ArgumentList.Arguments[index].Expression;
        var constant = semanticModel.GetConstantValue(expression);
        if (!constant.HasValue || !(constant.Value is string stringValue))
        {
            report(Diagnostic.Create(
                GeneratorDiagnostics.DynamicMigrationArgument,
                expression.GetLocation(),
                $"Argument {index + 1} to '{call.Name}' must be a compile-time string constant."));
            return false;
        }

        if (string.IsNullOrWhiteSpace(stringValue))
        {
            return Invalid(call, report, $"Argument {index + 1} to '{call.Name}' cannot be empty.");
        }

        value = stringValue;
        return true;
    }

    private static bool TryInt(
        Call call,
        int index,
        SemanticModel semanticModel,
        Action<Diagnostic> report,
        out int value)
    {
        value = 0;
        if (index >= call.Syntax.ArgumentList.Arguments.Count)
        {
            return Invalid(call, report, $"'{call.Name}' is missing a required integer argument.");
        }

        var expression = call.Syntax.ArgumentList.Arguments[index].Expression;
        var constant = semanticModel.GetConstantValue(expression);
        if (!constant.HasValue || constant.Value is null)
        {
            report(Diagnostic.Create(
                GeneratorDiagnostics.DynamicMigrationArgument,
                expression.GetLocation(),
                $"Argument {index + 1} to '{call.Name}' must be a compile-time integer constant."));
            return false;
        }

        try
        {
            value = Convert.ToInt32(constant.Value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return Invalid(call, report, $"Argument {index + 1} to '{call.Name}' must fit in a 32-bit integer.");
        }
        catch (InvalidCastException)
        {
            return Invalid(call, report, $"Argument {index + 1} to '{call.Name}' must fit in a 32-bit integer.");
        }
        catch (OverflowException)
        {
            return Invalid(call, report, $"Argument {index + 1} to '{call.Name}' must fit in a 32-bit integer.");
        }
    }

    private static bool TryPositiveInt(
        Call call,
        int index,
        SemanticModel semanticModel,
        Action<Diagnostic> report,
        out int value)
    {
        if (!TryInt(call, index, semanticModel, report, out value)) return false;
        return value > 0 || Invalid(call, report, $"Argument {index + 1} to '{call.Name}' must be positive.");
    }

    private static bool RequireNoArguments(Call call, Action<Diagnostic> report)
    {
        return call.Syntax.ArgumentList.Arguments.Count == 0 ||
            Invalid(call, report, $"'{call.Name}' does not accept arguments in the supported migration DSL.");
    }

    private static bool RequireAtMostOneStringArgument(
        Call call,
        SemanticModel semanticModel,
        Action<Diagnostic> report)
    {
        var count = call.Syntax.ArgumentList.Arguments.Count;
        if (count == 0) return true;
        if (count > 1) return Invalid(call, report, $"'{call.Name}' accepts at most one name argument.");
        return TryString(call, 0, semanticModel, report, out _);
    }

    private static bool RequireColumn(ColumnBuilder? column, Call call, Action<Diagnostic> report)
    {
        return column != null || Invalid(call, report, $"'{call.Name}' must follow WithColumn, AddColumn, or AlterColumn.");
    }

    private static bool RequireAlter(AlterBuilder? alteration, Call call, Action<Diagnostic> report)
    {
        return alteration != null || Invalid(call, report, $"'{call.Name}' must follow AddColumn or AlterColumn.");
    }

    private static bool Invalid(IReadOnlyList<Call> calls, Action<Diagnostic> report, string message)
    {
        var location = calls.Count == 0 ? Location.None : calls[0].Syntax.GetLocation();
        report(Diagnostic.Create(GeneratorDiagnostics.InvalidMigration, location, message));
        return false;
    }

    private static bool Invalid(Call call, Action<Diagnostic> report, string message)
    {
        report(Diagnostic.Create(GeneratorDiagnostics.InvalidMigration, call.Syntax.GetLocation(), message));
        return false;
    }

    private static bool Invalid(Location location, Action<Diagnostic> report, string message)
    {
        report(Diagnostic.Create(GeneratorDiagnostics.InvalidMigration, location, message));
        return false;
    }

    private sealed class Call
    {
        internal Call(string name, InvocationExpressionSyntax syntax)
        {
            Name = name;
            Syntax = syntax;
        }

        internal string Name { get; }
        internal InvocationExpressionSyntax Syntax { get; }
    }

    private sealed class ColumnBuilder
    {
        internal ColumnBuilder(string name, Location location)
        {
            Name = name;
            Location = location;
        }

        internal string Name { get; }
        internal Location Location { get; }
        internal string? SqlType { get; set; }
        internal string? LogicalType { get; set; }
        internal Location? TypeLocation { get; set; }
        internal bool? Nullable { get; set; }
        internal bool PrimaryKey { get; set; }
        internal Location? PrimaryKeyLocation { get; set; }
        internal bool Identity { get; set; }
        internal Location? IdentityLocation { get; set; }
        internal string? DefaultExpression { get; set; }
    }

    private sealed class AlterBuilder
    {
        internal AlterBuilder(string name, bool isAdd, Location location)
        {
            IsAdd = isAdd;
            Column = new ColumnBuilder(name, location);
        }

        internal bool IsAdd { get; }
        internal ColumnBuilder Column { get; }
    }
}

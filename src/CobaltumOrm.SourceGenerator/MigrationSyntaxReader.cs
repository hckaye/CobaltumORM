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
                return name == "Table";
            case "AlterExpressionRoot":
                return name == "Table";
            case "DeleteExpressionRoot":
                return name == "Table" || name == "Column";
            case "RenameExpressionRoot":
                return name == "Table" || name == "Column";
            case "CreateTableExpression":
                return name == "InSchema" || name == "WithColumn" ||
                    name.StartsWith("As", StringComparison.Ordinal) ||
                    name == "Nullable" || name == "NotNullable" || name == "PrimaryKey" ||
                    name == "Identity";
            case "AlterTableExpression":
                return name == "InSchema" || name == "AddColumn" || name == "AlterColumn" ||
                    name.StartsWith("As", StringComparison.Ordinal) ||
                    name == "Nullable" || name == "NotNullable" || name == "PrimaryKey" ||
                    name == "Identity";
            case "DeleteTableExpression":
            case "DeleteColumnExpression":
            case "RenameTableToExpression":
            case "RenameColumnToExpression":
                return name == "InSchema" || name == "To";
            case "DeleteColumnFromExpression":
                return name == "FromTable";
            case "RenameColumnOnExpression":
                return name == "OnTable";
            case "ExecuteExpressionRoot":
                return name == "Sql";
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
        if (calls.Count == 0 || calls[0].Name != "Table")
        {
            return Invalid(calls, report, "Create supports Create.Table(constantName) only.");
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
                    if (!RequireNoArguments(call, report) || !RequireColumn(current, call, report)) return false;
                    current!.PrimaryKey = true;
                    current.PrimaryKeyLocation = call.Syntax.GetLocation();
                    current.Nullable = false;
                    break;
                case "Identity":
                    if (!RequireNoArguments(call, report) || !RequireColumn(current, call, report)) return false;
                    current!.Identity = true;
                    current.IdentityLocation = call.Syntax.GetLocation();
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
        if (calls.Count == 0 || calls[0].Name != "Table")
        {
            return Invalid(calls, report, "Alter supports Alter.Table(constantName) only.");
        }

        if (!TryString(calls[0], 0, semanticModel, report, out var tableName)) return false;

        string? schema = null;
        var alterations = new List<AlterBuilder>();
        AlterBuilder? current = null;
        for (var index = 1; index < calls.Count; index++)
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
                    if (!RequireNoArguments(call, report) || !RequireAlter(current, call, report)) return false;
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
                default:
                    if (!TrySetType(current?.Column, call, semanticModel, dialect, report)) return false;
                    break;
            }
        }

        if (alterations.Count == 0)
        {
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
            if (calls.Any(call => call.Name != "Table" && call.Name != "InSchema"))
                return Invalid(calls, report, "Delete.Table supports only an optional InSchema call.");
            sql = dialect.MigrationSqlWriter.DropTable(Qualify(schema, tableName, dialect));
            return true;
        }

        if (calls[0].Name == "Column" && calls.Count >= 2 && calls[1].Name == "FromTable")
        {
            if (!TryString(calls[0], 0, semanticModel, report, out var columnName) ||
                !TryString(calls[1], 0, semanticModel, report, out var fromTable)) return false;
            if (calls.Any(call => call.Name != "Column" && call.Name != "FromTable" && call.Name != "InSchema"))
                return Invalid(calls, report, "Delete.Column supports FromTable and an optional InSchema call.");
            sql = dialect.MigrationSqlWriter.DropColumn(
                Qualify(schema, fromTable, dialect),
                dialect.IdentifierQuoter.QuoteIdentifier(columnName));
            return true;
        }

        return Invalid(calls, report, "Delete supports Delete.Table or Delete.Column(...).FromTable(...).");
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

        if (calls[0].Name == "Table" && calls.Last().Name == "To")
        {
            if (!TryString(calls[0], 0, semanticModel, report, out var oldTable) ||
                !TryString(calls.Last(), 0, semanticModel, report, out var newTable)) return false;
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
            return Invalid(calls, report, "Execute supports Execute.Sql(constantSql) only.");
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
            case "AsInt32": return TrySetMappedType(column, call, dialect, report, "int32") && RequireNoArguments(call, report);
            case "AsInt64": return TrySetMappedType(column, call, dialect, report, "int64") && RequireNoArguments(call, report);
            case "AsBoolean": return TrySetMappedType(column, call, dialect, report, "boolean") && RequireNoArguments(call, report);
            case "AsFloat": return TrySetMappedType(column, call, dialect, report, "float") && RequireNoArguments(call, report);
            case "AsDouble": return TrySetMappedType(column, call, dialect, report, "double") && RequireNoArguments(call, report);
            case "AsText": return TrySetMappedType(column, call, dialect, report, "text") && RequireNoArguments(call, report);
            case "AsDate": return TrySetMappedType(column, call, dialect, report, "date") && RequireNoArguments(call, report);
            case "AsDateTime": return TrySetMappedType(column, call, dialect, report, "datetime") && RequireNoArguments(call, report);
            case "AsDateTimeOffset": return TrySetMappedType(column, call, dialect, report, "datetimeoffset") && RequireNoArguments(call, report);
            case "AsTime": return TrySetMappedType(column, call, dialect, report, "time") && RequireNoArguments(call, report);
            case "AsGuid": return TrySetMappedType(column, call, dialect, report, "guid") && RequireNoArguments(call, report);
            case "AsBinary": return TrySetMappedType(column, call, dialect, report, "binary") && RequireNoArguments(call, report);
            case "AsJson": return TrySetMappedType(column, call, dialect, report, "json") && RequireNoArguments(call, report);
            case "AsJsonb": return TrySetMappedType(column, call, dialect, report, "jsonb") && RequireNoArguments(call, report);
            case "AsString":
                if (call.Syntax.ArgumentList.Arguments.Count == 0)
                {
                    return TrySetMappedType(column, call, dialect, report, "string");
                }

                if (!TryInt(call, 0, semanticModel, report, out var length)) return false;
                if (length <= 0)
                    return Invalid(call, report, "AsString length must be a positive compile-time constant.");
                return TrySetMappedType(column, call, dialect, report, "string", length: length);
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

    private static bool RequireNoArguments(Call call, Action<Diagnostic> report)
    {
        return call.Syntax.ArgumentList.Arguments.Count == 0 ||
            Invalid(call, report, $"'{call.Name}' does not accept arguments in the supported migration DSL.");
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

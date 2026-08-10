using System;
using System.Collections.Generic;
using System.Linq;

namespace CobaltumOrm.Analysis;

/// <summary>Analyzes the SQL query subset using SQL Server identifier and type rules.</summary>
public sealed class SqlServerQueryAnalyzer : IQueryAnalyzer
{
    private readonly QueryAnalyzerEngine _sqlServerEngine;

    public SqlServerQueryAnalyzer()
        : this(new SqlServerTypeMapper())
    {
    }

    internal SqlServerQueryAnalyzer(ISqlTypeMapper mapper)
    {
        if (mapper is null)
        {
            throw new ArgumentNullException(nameof(mapper));
        }

        var types = new QueryTypeProfile(mapper, SqlServerAggregateResult);
        _sqlServerEngine = new QueryAnalyzerEngine(
            new QueryDialectProfile(QuerySyntaxProfile.SqlServer, types, "SqlServer"));
    }

    /// <summary>Gets the profile used by the shared query analyzer engine.</summary>
    public QueryDialectProfile Profile => _sqlServerEngine.Profile;

    public AnalysisResult Analyze(DatabaseSchema schema, string sql)
    {
        if (schema is null || sql is null)
        {
            return _sqlServerEngine.Analyze(schema!, sql!);
        }

        var filteredSchema = SqlServerApplyDefaultSchemaResolution(schema, sql);
        return _sqlServerEngine.Analyze(filteredSchema, sql);
    }

    private static DatabaseSchema SqlServerApplyDefaultSchemaResolution(DatabaseSchema schema, string sql)
    {
        var parserDiagnostics = new List<Diagnostic>();
        var profile = new QueryDialectProfile(
            QuerySyntaxProfile.SqlServer,
            new QueryTypeProfile(new SqlServerTypeMapper(), SqlServerAggregateResult),
            "SqlServer");
        var tokens = new Lexer(sql, parserDiagnostics, profile.Syntax).Lex();
        var statement = new Parser(tokens, parserDiagnostics, profile).Parse();
        if (statement == null || parserDiagnostics.Count != 0)
        {
            return schema;
        }

        var unqualifiedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SqlServerCollectUnqualifiedTables(statement, unqualifiedTables);
        if (unqualifiedTables.Count == 0)
        {
            return schema;
        }

        var tables = schema.Tables.Where(table =>
            !unqualifiedTables.Contains(table.Name) || SqlServerIsDefaultSchema(table.Schema)).ToArray();
        return new DatabaseSchema(tables);
    }

    private static void SqlServerCollectUnqualifiedTables(
        SqlStatement statement,
        ISet<string> unqualifiedTables)
    {
        if (statement is SelectStatement select)
        {
            if (select.From != null)
            {
                SqlServerCollectTableReference(select.From, unqualifiedTables);
            }

            foreach (var join in select.Joins)
            {
                SqlServerCollectTableReference(join.Table, unqualifiedTables);
            }

            return;
        }

        if (statement is UpdateStatement update)
        {
            SqlServerCollectTableReference(update.Table, unqualifiedTables);
            return;
        }

        if (statement is InsertStatement insert)
        {
            SqlServerCollectTableReference(insert.Table, unqualifiedTables);
            return;
        }

        if (statement is DeleteStatement delete)
        {
            SqlServerCollectTableReference(delete.Table, unqualifiedTables);
        }
    }

    private static void SqlServerCollectTableReference(
        TableReference reference,
        ISet<string> unqualifiedTables)
    {
        if (reference.Schema == null)
        {
            unqualifiedTables.Add(reference.Name.Name);
        }
    }

    private static bool SqlServerIsDefaultSchema(string? schema) =>
        string.IsNullOrEmpty(schema) || string.Equals(schema, "dbo", StringComparison.OrdinalIgnoreCase);

    private static SqlValueKind SqlServerAggregateResult(string aggregateName, SqlValueKind argumentKind)
    {
        switch (aggregateName.ToLowerInvariant())
        {
            case "count":
                return SqlValueKind.Int32;
            case "sum":
                if (argumentKind == SqlValueKind.Int16)
                {
                    return SqlValueKind.Int32;
                }

                return argumentKind;
            case "avg":
                if (argumentKind == SqlValueKind.Int16 || argumentKind == SqlValueKind.Int32)
                {
                    return SqlValueKind.Int32;
                }

                if (argumentKind == SqlValueKind.Int64)
                {
                    return SqlValueKind.Int64;
                }

                return argumentKind == SqlValueKind.Float
                    ? SqlValueKind.Float
                    : argumentKind;
            default:
                return argumentKind;
        }
    }
}

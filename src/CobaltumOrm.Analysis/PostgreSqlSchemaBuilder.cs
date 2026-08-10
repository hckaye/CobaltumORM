using System;
using System.Collections.Generic;

namespace CobaltumOrm.Analysis;

/// <summary>Applies supported PostgreSQL schema statements while ignoring schema-neutral SQL.</summary>
public static class PostgreSqlSchemaBuilder
{
    /// <summary>Applies one SQL script to an existing schema.</summary>
    public static MigrationAnalysisResult ApplyScript(DatabaseSchema schema, string sql)
    {
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        var current = schema;
        var diagnostics = new List<Diagnostic>();
        var statements = PostgreSqlScriptClassifier.SplitAndClassify(sql, out var scriptError);
        if (scriptError is not null)
        {
            diagnostics.Add(new Diagnostic("DDL300", scriptError.Message, scriptError.Span));
        }

        foreach (var statement in statements)
        {
            if (statement.Kind == SqlStatementKind.Empty ||
                statement.Kind == SqlStatementKind.Select ||
                statement.Kind == SqlStatementKind.DataManipulation ||
                statement.Kind == SqlStatementKind.SchemaNeutral)
            {
                continue;
            }

            if (statement.Kind == SqlStatementKind.Unsupported)
            {
                diagnostics.Add(new Diagnostic(
                    "DDL300",
                    "This migration statement may change the queryable schema and is not supported by schema analysis.",
                    statement.Span));
                continue;
            }

            var result = PostgreSqlMigrationAnalyzer.Analyze(current, statement.Text);
            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(new Diagnostic(
                    diagnostic.Code,
                    diagnostic.Message,
                    new SourceSpan(
                        statement.Span.Start + diagnostic.Span.Start,
                        diagnostic.Span.Length)));
            }

            if (!result.HasErrors)
            {
                current = result.Schema;
            }
        }

        return new MigrationAnalysisResult(current, diagnostics);
    }
}

using System.Collections.Generic;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

internal static class TestSchema
{
    internal static DatabaseSchema Create() => new DatabaseSchema(new[]
    {
        new Table("users", new[]
        {
            new Column("id", "integer"),
            new Column("name", "text"),
            new Column("nickname", "varchar(50)", true),
            new Column("age", "smallint", true),
            new Column("balance", "numeric", true),
            new Column("score", "real"),
            new Column("ratio", "double precision", true),
            new Column("active", "boolean"),
            new Column("external_id", "uuid"),
            new Column("birth_date", "date", true),
            new Column("wake_time", "time"),
            new Column("created_at", "timestamp"),
            new Column("updated_at", "timestamptz", true),
            new Column("avatar", "bytea", true),
            new Column("code", "char(3)"),
        }),
        new Table("orders", new[]
        {
            new Column("id", "integer"),
            new Column("user_id", "integer"),
            new Column("total", "numeric"),
            new Column("discount", "numeric", true),
            new Column("note", "text", true),
        }),
        new Table("payments", new[]
        {
            new Column("id", "bigint"),
            new Column("order_id", "integer"),
            new Column("amount", "numeric"),
        }),
    });

    internal static AnalysisResult Analyze(string sql) => QueryAnalyzer.Analyze(Create(), sql);

    internal static void AssertSuccess(AnalysisResult result)
    {
        Assert.False(result.HasErrors, string.Join("\n", DiagnosticStrings(result.Diagnostics)));
    }

    internal static void AssertColumns(AnalysisResult result, params (string Name, string Type)[] expected)
    {
        AssertSuccess(result);
        Assert.Equal(expected.Length, result.Columns.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Name, result.Columns[index].Name);
            Assert.Equal(expected[index].Type, result.Columns[index].ClrType);
        }
    }

    private static IEnumerable<string> DiagnosticStrings(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            yield return diagnostic.ToString();
        }
    }
}

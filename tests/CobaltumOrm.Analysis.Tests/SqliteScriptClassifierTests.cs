using System;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class SqliteScriptClassifierTests
{
    [Fact]
    public void SplitsOnlyAtSemicolonsOutsideSQLiteStringsIdentifiersAndComments()
    {
        var sql =
            "-- leading comment;\n" +
            "CREATE TABLE [semi;table] ([value] TEXT DEFAULT 'a;--b');" +
            "SELECT \"semi;column\" FROM `semi;table`;" +
            "/* block; comment /* nested; */ still */ UPDATE items SET value = 'x;y';";

        var statements = new SqliteScriptClassifierService().SplitAndClassify(sql, out var error);

        Assert.Null(error);
        Assert.Equal(3, statements.Count);
        Assert.Equal(SqlStatementKind.SupportedTableDdl, statements[0].Kind);
        Assert.Equal(SqlStatementKind.Select, statements[1].Kind);
        Assert.Equal(SqlStatementKind.DataManipulation, statements[2].Kind);
        Assert.Contains("[semi;table]", statements[0].Text, StringComparison.Ordinal);
        Assert.Contains("'a;--b'", statements[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifiesSchemaNeutralFlywayStatements()
    {
        var statements = new SqliteScriptClassifierService().SplitAndClassify(
            "CREATE UNIQUE INDEX ix_items ON items (id); DROP INDEX ix_items; PRAGMA foreign_keys = ON;",
            out var error);

        Assert.Null(error);
        Assert.Equal(3, statements.Count);
        Assert.All(statements, item => Assert.Equal(SqlStatementKind.SchemaNeutral, item.Kind));
    }

    [Theory]
    [InlineData("SELECT 'unterminated;")]
    [InlineData("SELECT [unterminated;")]
    [InlineData("SELECT /* unterminated;")]
    public void ReportsUnterminatedSQLiteLexicalConstructs(string sql)
    {
        var statements = new SqliteScriptClassifierService().SplitAndClassify(sql, out var error);

        Assert.Empty(statements);
        Assert.NotNull(error);
        Assert.InRange(error!.Span.Start, 0, sql.Length);
    }
}

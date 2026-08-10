using System;
using System.Linq;
using CobaltumOrm.Analysis;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class MySqlScriptClassifierTests
{
    [Fact]
    public void SplitsOnlyAtSemicolonsOutsideStringsIdentifiersAndComments()
    {
        const string sql = @"
            -- first statement
            CREATE TABLE `events` (`message` text DEFAULT 'a; -- b');
            # a MySQL comment with ;
            ALTER TABLE `events` ADD COLUMN `semi;name` varchar(20) /* ; */;
            INSERT INTO `events` (`message`) VALUES ('x; y');";

        var statements = MySqlScriptClassifier.SplitAndClassify(sql, out var error);

        Assert.Null(error);
        Assert.Equal(3, statements.Count);
        Assert.All(statements, statement => Assert.True(statement.Span.Length > 0));
        Assert.Equal(SqlStatementKind.SupportedTableDdl, statements[0].Kind);
        Assert.Equal(SqlStatementKind.SupportedTableDdl, statements[1].Kind);
        Assert.Equal(SqlStatementKind.DataManipulation, statements[2].Kind);
        Assert.Contains("'a; -- b'", statements[0].Text, StringComparison.Ordinal);
        Assert.Contains("`semi;name`", statements[1].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT 1;", SqlStatementKind.Select)]
    [InlineData("UPDATE users SET id = 1;", SqlStatementKind.DataManipulation)]
    [InlineData("CREATE INDEX ix_users ON users (id);", SqlStatementKind.SchemaNeutral)]
    [InlineData("DROP INDEX ix_users ON users;", SqlStatementKind.SchemaNeutral)]
    [InlineData("RENAME TABLE users TO accounts;", SqlStatementKind.SupportedTableDdl)]
    [InlineData("USE `tenant`;", SqlStatementKind.SupportedTableDdl)]
    [InlineData("CREATE VIEW users_view AS SELECT 1;", SqlStatementKind.Unsupported)]
    public void ClassifiesMySqlStatementBoundaries(string sql, SqlStatementKind expected)
    {
        var statement = Assert.Single(MySqlScriptClassifier.SplitAndClassify(sql, out var error));

        Assert.Null(error);
        Assert.Equal(expected, statement.Kind);
    }

    [Fact]
    public void ReportsUnterminatedMySqlLexicalConstructs()
    {
        var statements = MySqlScriptClassifier.SplitAndClassify("CREATE TABLE users (name text DEFAULT 'unfinished", out var error);

        Assert.Empty(statements);
        Assert.NotNull(error);
        Assert.Contains("Unterminated MySQL string", error!.Message, StringComparison.Ordinal);
    }
}

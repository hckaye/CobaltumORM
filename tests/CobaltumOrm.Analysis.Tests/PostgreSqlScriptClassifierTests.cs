using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class PostgreSqlScriptClassifierTests
{
    [Fact]
    public void ClassifiesWithQueriesAndReturningCommandsByWhetherTheyReturnRows()
    {
        var statements = new PostgreSqlScriptClassifierService().SplitAndClassify(
            "WITH source AS (SELECT 1 AS id) SELECT id FROM source;" +
            "INSERT INTO users (id, name) VALUES (1, 'one') RETURNING id;" +
            "WITH changed AS (UPDATE users SET active = true RETURNING id) " +
            "DELETE FROM users WHERE id IN (SELECT id FROM changed);",
            out var error);

        Assert.Null(error);
        Assert.Equal(3, statements.Count);
        Assert.Equal(SqlStatementKind.Select, statements[0].Kind);
        Assert.Equal(SqlStatementKind.Select, statements[1].Kind);
        Assert.Equal(SqlStatementKind.DataManipulation, statements[2].Kind);
    }

    [Fact]
    public void ClassifiesValuesAsReturningRows()
    {
        var statements = new PostgreSqlScriptClassifierService().SplitAndClassify(
            "VALUES (1), (2); WITH rows(id) AS (VALUES (1)) SELECT id FROM rows;",
            out var error);

        Assert.Null(error);
        Assert.Equal(2, statements.Count);
        Assert.All(statements, item => Assert.Equal(SqlStatementKind.Select, item.Kind));
    }

    [Fact]
    public void IgnoresNestedReturningWhenClassifyingTheMainCommand()
    {
        var statement = Assert.Single(new PostgreSqlScriptClassifierService().SplitAndClassify(
            "WITH changed AS (UPDATE users SET active = true RETURNING id) " +
            "SELECT id FROM changed",
            out var error));

        Assert.Null(error);
        Assert.Equal(SqlStatementKind.Select, statement.Kind);
    }
}

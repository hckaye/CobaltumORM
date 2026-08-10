using System;
using CobaltumOrm.Analysis;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class OracleClassifierTests
{
    [Fact]
    public void SplitsOnlyAtSemicolonsOutsideOracleLexicalConstructs()
    {
        var sql =
            "-- header;\n" +
            "CREATE TABLE \"a;b\" (\"text\" VARCHAR2(20) DEFAULT 'x; y');\n" +
            "/* block; comment */\n" +
            "SELECT q'[value; still text]' FROM dual;\n" +
            "INSERT INTO t VALUES ('quoted '' ; value');";

        var statements = new OracleScriptClassifierService().SplitAndClassify(sql, out var error);

        Assert.Null(error);
        Assert.Equal(3, statements.Count);
        Assert.Equal(SqlStatementKind.SupportedTableDdl, statements[0].Kind);
        Assert.Equal(SqlStatementKind.Select, statements[1].Kind);
        Assert.Equal(SqlStatementKind.DataManipulation, statements[2].Kind);
    }

    [Fact]
    public void ClassifiesCommonOracleStatementsAndLeavesSchemaChangingStatementsUnsupported()
    {
        var statements = new OracleScriptClassifierService().SplitAndClassify(
            "CREATE INDEX ix_t ON t (id); DROP INDEX ix_t; ALTER SESSION SET CURRENT_SCHEMA = app; " +
            "CREATE VIEW v AS SELECT 1 FROM dual; RENAME t TO t2;",
            out var error);

        Assert.Null(error);
        Assert.Equal(SqlStatementKind.SchemaNeutral, statements[0].Kind);
        Assert.Equal(SqlStatementKind.SchemaNeutral, statements[1].Kind);
        Assert.Equal(SqlStatementKind.SchemaNeutral, statements[2].Kind);
        Assert.Equal(SqlStatementKind.Unsupported, statements[3].Kind);
        Assert.Equal(SqlStatementKind.SupportedTableDdl, statements[4].Kind);
    }

    [Fact]
    public void DoesNotPretendToAnalyzePlSqlBlocks()
    {
        var statements = new OracleScriptClassifierService().SplitAndClassify(
            "BEGIN\n  EXECUTE IMMEDIATE 'CREATE TABLE t (id NUMBER(10,0))';\nEND;",
            out var error);

        Assert.Null(error);
        var statement = Assert.Single(statements);
        Assert.Equal(SqlStatementKind.Unsupported, statement.Kind);
    }

    [Fact]
    public void ReportsUnterminatedOracleStringsAndComments()
    {
        var service = new OracleScriptClassifierService();

        var stringStatements = service.SplitAndClassify("SELECT 'unterminated;", out var stringError);
        Assert.Empty(stringStatements);
        Assert.Contains("string literal", stringError!.Message, StringComparison.OrdinalIgnoreCase);

        var commentStatements = service.SplitAndClassify("/* unterminated", out var commentError);
        Assert.Empty(commentStatements);
        Assert.Contains("block comment", commentError!.Message, StringComparison.OrdinalIgnoreCase);
    }
}

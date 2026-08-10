using System;
using CobaltumOrm.Analysis;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class OracleDialectTests
{
    [Fact]
    public void DialectExposesEveryOracleAnalysisService()
    {
        var dialect = new OracleDatabaseDialect();

        Assert.Equal(DatabaseProvider.Oracle, dialect.Provider);
        Assert.Equal("Oracle", dialect.Name);
        Assert.NotNull(dialect.QueryAnalyzer);
        Assert.NotNull(dialect.SchemaMigrationAnalyzer);
        Assert.NotNull(dialect.ScriptClassifier);
        Assert.NotNull(dialect.IdentifierQuoter);
        Assert.NotNull(dialect.TypeMapper);
        Assert.NotNull(dialect.MigrationSqlWriter);
        Assert.NotNull(dialect.SchemaRules);
    }

    [Fact]
    public void IdentifierQuoterEscapesQuotesAndRejectsUnsafeEmptyValues()
    {
        var quoter = new OracleIdentifierQuoter();

        Assert.Equal("\"A\"\"B\"", quoter.QuoteIdentifier("A\"B"));
        Assert.Equal("\"APP\".\"USERS\"", quoter.QuoteQualifiedName("APP", "USERS"));
        Assert.Equal("\"USERS\"", quoter.QuoteQualifiedName(null, "USERS"));
        Assert.Throws<ArgumentException>(() => quoter.QuoteIdentifier(" "));
        Assert.Throws<ArgumentException>(() => quoter.QuoteIdentifier("bad\0name"));
    }

    [Fact]
    public void SchemaRulesUseCurrentUserAndOracleFolding()
    {
        var rules = new OracleSchemaRules();

        Assert.True(rules.SupportsSchemas);
        Assert.Null(rules.DefaultSchema);
        Assert.True(rules.IsDefaultSchema(null));
        Assert.True(rules.IsDefaultSchema(string.Empty));
        Assert.False(rules.IsDefaultSchema("APP"));
        Assert.Equal("MIXED_NAME", rules.NormalizeUnquotedIdentifier("mixed_name"));
        Assert.Equal("MixedName", rules.NormalizeQuotedIdentifier("MixedName"));
        Assert.True(rules.AreIdentifiersEqual("users", false, "USERS"));
        Assert.False(rules.AreIdentifiersEqual("users", true, "USERS"));
        Assert.True(rules.AreIdentifiersEqual("USERS", true, "USERS"));
    }

    [Fact]
    public void OracleQueryAnalyzerUsesUpperFoldingAndColonParameters()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("ORDERS", new[]
            {
                new Column("ID", "NUMBER(10,0)"),
                new Column("AMOUNT", "NUMBER(18,4)", true),
                new Column("BINARY_VALUE", "BINARY_FLOAT"),
            }),
        });

        var result = new OracleQueryAnalyzer().Analyze(
            schema,
            "SELECT id, SUM(amount) AS total, AVG(amount) AS average, SUM(binary_value) AS single_value " +
            "FROM orders WHERE id = :id GROUP BY id");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(4, result.Columns.Count);
        Assert.Equal("id", result.Columns[0].Name);
        Assert.Equal("int", result.Columns[0].ClrType);
        Assert.Equal("decimal?", result.Columns[1].ClrType);
        Assert.Equal("decimal?", result.Columns[2].ClrType);
        Assert.Equal("float", result.Columns[3].ClrType);
        var parameter = Assert.Single(result.Parameters);
        Assert.Equal(":id", parameter.Name);
        Assert.Equal("int", parameter.ClrType);
        Assert.Same(QuerySyntaxProfile.Oracle, new OracleQueryAnalyzer().Profile.Syntax);
    }

    [Fact]
    public void OracleQueryAnalyzerRespectsQuotedCase()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("MixedTable", new[] { new Column("MixedColumn", "VARCHAR2(20)") }),
        });

        var success = new OracleQueryAnalyzer().Analyze(
            schema,
            "SELECT \"MixedColumn\" FROM \"MixedTable\"");
        Assert.Empty(success.Diagnostics);

        var failure = new OracleQueryAnalyzer().Analyze(
            schema,
            "SELECT \"mixedcolumn\" FROM \"MixedTable\"");
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "SQL203");
    }

    [Fact]
    public void OracleAggregatesUseOracleNumericResultRules()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("VALUES_TABLE", new[]
            {
                new Column("SMALL_VALUE", "NUMBER(5,0)"),
                new Column("LARGE_VALUE", "NUMBER(19,0)"),
                new Column("DOUBLE_VALUE", "BINARY_DOUBLE"),
            }),
        });

        var result = new OracleQueryAnalyzer().Analyze(
            schema,
            "SELECT SUM(small_value), AVG(large_value), MIN(small_value), MAX(double_value), COUNT(*) " +
            "FROM values_table");

        Assert.Empty(result.Diagnostics);
        Assert.Equal("decimal?", result.Columns[0].ClrType);
        Assert.Equal("decimal?", result.Columns[1].ClrType);
        Assert.Equal("short?", result.Columns[2].ClrType);
        Assert.Equal("double?", result.Columns[3].ClrType);
        Assert.Equal("long", result.Columns[4].ClrType);
    }
}

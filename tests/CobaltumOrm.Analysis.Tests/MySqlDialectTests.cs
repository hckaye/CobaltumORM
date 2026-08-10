using System;
using System.Linq;
using CobaltumOrm.Analysis;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class MySqlDialectTests
{
    [Fact]
    public void ExposesEveryMySqlAnalysisService()
    {
        var dialect = new MySqlDatabaseDialect();

        Assert.Equal(DatabaseProvider.MySql, dialect.Provider);
        Assert.Equal("MySql", dialect.Name);
        Assert.IsType<MySqlQueryAnalyzer>(dialect.QueryAnalyzer);
        Assert.IsType<MySqlSchemaMigrationAnalyzer>(dialect.SchemaMigrationAnalyzer);
        Assert.IsType<MySqlScriptClassifierService>(dialect.ScriptClassifier);
        Assert.IsType<MySqlIdentifierQuoter>(dialect.IdentifierQuoter);
        Assert.IsType<MySqlTypeMapper>(dialect.TypeMapper);
        Assert.IsType<MySqlMigrationSqlWriter>(dialect.MigrationSqlWriter);
        Assert.IsType<MySqlSchemaRules>(dialect.SchemaRules);
    }

    [Fact]
    public void QuotesIdentifiersWithoutSplittingEmbeddedDots()
    {
        var quoter = new MySqlIdentifierQuoter();

        Assert.Equal("`tenant``one.table`", quoter.QuoteIdentifier("tenant`one.table"));
        Assert.Equal("`tenant``one`.`user``data`", quoter.QuoteQualifiedName("tenant`one", "user`data"));
        Assert.Equal("`user.data`", quoter.QuoteQualifiedName(null, "user.data"));
        Assert.Throws<ArgumentException>(() => quoter.QuoteIdentifier(" "));
        Assert.Throws<ArgumentException>(() => quoter.QuoteIdentifier("bad\0name"));
    }

    [Fact]
    public void AppliesMySqlDatabaseAndIdentifierRules()
    {
        var rules = new MySqlSchemaRules();

        Assert.True(rules.SupportsSchemas);
        Assert.Null(rules.DefaultSchema);
        Assert.True(rules.IsDefaultSchema(null));
        Assert.True(rules.IsDefaultSchema(string.Empty));
        Assert.False(rules.IsDefaultSchema("tenant"));
        Assert.Equal("mixed", rules.NormalizeUnquotedIdentifier("MiXeD"));
        Assert.Equal("MiXeD", rules.NormalizeQuotedIdentifier("MiXeD"));
        Assert.True(rules.AreIdentifiersEqual("USERS", false, "users"));
        Assert.False(rules.AreIdentifiersEqual("Users", true, "users"));
    }

    [Theory]
    [InlineData("tinyint", SqlValueKind.Int16)]
    [InlineData("tinyint(1)", SqlValueKind.Bool)]
    [InlineData("smallint", SqlValueKind.Int16)]
    [InlineData("mediumint", SqlValueKind.Int32)]
    [InlineData("int(11)", SqlValueKind.Int32)]
    [InlineData("bigint", SqlValueKind.Int64)]
    [InlineData("decimal(18,4)", SqlValueKind.Decimal)]
    [InlineData("varchar(80)", SqlValueKind.String)]
    [InlineData("longtext", SqlValueKind.String)]
    [InlineData("longblob", SqlValueKind.Bytes)]
    [InlineData("json", SqlValueKind.Json)]
    [InlineData("date", SqlValueKind.DateOnly)]
    [InlineData("time(6)", SqlValueKind.TimeOnly)]
    [InlineData("datetime(6)", SqlValueKind.DateTime)]
    [InlineData("timestamp", SqlValueKind.DateTime)]
    [InlineData("timestamp(6)", SqlValueKind.DateTime)]
    [InlineData("char(36)", SqlValueKind.Guid)]
    [InlineData("CHAR(36)", SqlValueKind.Guid)]
    [InlineData("char(35)", SqlValueKind.String)]
    public void MapsCommonMySql8Types(string sqlType, SqlValueKind expected)
    {
        var mapper = new MySqlTypeMapper();

        Assert.True(mapper.TryMap(sqlType, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("int unsigned")]
    [InlineData("decimal(4,5)")]
    [InlineData("varchar(0)")]
    [InlineData("datetime(7)")]
    [InlineData("decimal(10,)")]
    public void RejectsUnsignedAndInvalidTypeModifiers(string sqlType)
    {
        Assert.False(new MySqlTypeMapper().TryMap(sqlType, out _));
    }

    [Fact]
    public void MapsMigrationLogicalTypesLikeTheMySqlRuntimeAdapter()
    {
        var mapper = new MySqlTypeMapper();

        Assert.Equal("smallint", mapper.MapMigrationType("int16"));
        Assert.Equal("int", mapper.MapMigrationType("int32"));
        Assert.Equal("bigint", mapper.MapMigrationType("int64"));
        Assert.Equal("tinyint(1)", mapper.MapMigrationType("boolean"));
        Assert.Equal("varchar(32)", mapper.MapMigrationType("string", length: 32));
        Assert.Equal("text", mapper.MapMigrationType("string"));
        Assert.Equal("decimal(18,4)", mapper.MapMigrationType("decimal", precision: 18, scale: 4));
        Assert.Equal("datetime", mapper.MapMigrationType("datetimeoffset"));
        Assert.Equal("char(36)", mapper.MapMigrationType("guid"));
        Assert.Equal("longblob", mapper.MapMigrationType("binary"));
        Assert.Equal("json", mapper.MapMigrationType("jsonb"));
        Assert.Equal("DateTimeOffset?", mapper.ToClrTypeName(SqlValueKind.DateTimeOffset, true));
        Assert.Equal("char(36)", mapper.ToDatabaseTypeName(SqlValueKind.Guid));
    }
}

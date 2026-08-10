using System;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class QueryProfileTests
{
    [Fact]
    public void MySqlProfileUsesBackticksAndBacktickEscapes()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("Odd`Table", new[] { new Column("MixedCase", "integer") }),
        });

        var result = Analyze(
            QuerySyntaxProfile.MySql,
            schema,
            "SELECT `t`.`MixedCase` FROM `Odd``Table` AS `t`");

        AssertColumns(result, ("MixedCase", "int"));
    }

    [Fact]
    public void SqlServerProfileAcceptsBracketsAndAnsiDoubleQuotes()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("Odd]Table", new[] { new Column("MixedCase", "integer") }),
        });

        var result = Analyze(
            QuerySyntaxProfile.SqlServer,
            schema,
            "SELECT [t].[MixedCase] FROM [Odd]]Table] AS \"t\"");

        AssertColumns(result, ("MixedCase", "int"));
    }

    [Fact]
    public void SqliteProfileAcceptsItsThreeIdentifierDelimiterForms()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("items", new[] { new Column("MixedCase", "integer") }),
        });

        var result = Analyze(
            QuerySyntaxProfile.Sqlite,
            schema,
            "SELECT \"t\".\"MixedCase\" FROM [items] AS `t`");

        AssertColumns(result, ("MixedCase", "int"));
    }

    [Fact]
    public void OracleProfileFoldsUnquotedIdentifiersToUppercase()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("USERS", new[] { new Column("ID", "integer") }),
        });

        var result = Analyze(QuerySyntaxProfile.Oracle, schema, "SELECT id FROM users");

        AssertColumns(result, ("id", "int"));
    }

    [Theory]
    [InlineData("@value")]
    [InlineData(":value")]
    [InlineData("$value")]
    public void SqliteProfileAcceptsAllConfiguredParameterPrefixes(string parameter)
    {
        var result = Analyze(
            QuerySyntaxProfile.Sqlite,
            TestSchema.Create(),
            "SELECT id FROM users WHERE id = " + parameter);

        TestSchema.AssertSuccess(result);
        var value = Assert.Single(result.Parameters);
        Assert.Equal(parameter, value.Name);
        Assert.Equal("int", value.ClrType);
    }

    [Fact]
    public void OracleProfileUsesColonParameters()
    {
        var result = Analyze(
            QuerySyntaxProfile.Oracle,
            TestSchema.Create(),
            "SELECT id FROM users WHERE id = :value");

        TestSchema.AssertSuccess(result);
        Assert.Equal(":value", Assert.Single(result.Parameters).Name);
    }

    [Fact]
    public void PostgreSqlProfileKeepsUnsupportedDollarParametersUnsupported()
    {
        var result = Analyze(
            QuerySyntaxProfile.PostgreSql,
            TestSchema.Create(),
            "SELECT id FROM users WHERE id = $value");

        Assert.Contains(result.Diagnostics, item => item.Code == "SQL001");
    }

    [Fact]
    public void TypeAndAggregateRulesComeFromTheProfile()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("items", new[] { new Column("amount", "whole_number") }),
        });
        var types = new QueryTypeProfile(
            new ProfileTypeMapper(),
            (aggregate, kind) => string.Equals(aggregate, "sum", StringComparison.OrdinalIgnoreCase)
                ? SqlValueKind.Decimal
                : kind);
        var profile = new QueryDialectProfile(QuerySyntaxProfile.MySql, types, "test");
        var result = new QueryAnalyzerEngine(profile).Analyze(schema, "SELECT SUM(amount) FROM items");

        AssertColumns(result, ("sum", "decimal?"));
    }

    private static AnalysisResult Analyze(QuerySyntaxProfile syntax, DatabaseSchema schema, string sql)
    {
        var profile = new QueryDialectProfile(
            syntax,
            new QueryTypeProfile(new PostgreSqlTypeMapper()),
            "test");
        return new QueryAnalyzerEngine(profile).Analyze(schema, sql);
    }

    private static void AssertColumns(AnalysisResult result, params (string Name, string Type)[] expected)
    {
        TestSchema.AssertSuccess(result);
        Assert.Equal(expected.Length, result.Columns.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Name, result.Columns[index].Name);
            Assert.Equal(expected[index].Type, result.Columns[index].ClrType);
        }
    }

    private sealed class ProfileTypeMapper : ISqlTypeMapper
    {
        public bool TryMap(string sqlType, out SqlValueKind kind)
        {
            if (string.Equals(sqlType, "whole_number", StringComparison.OrdinalIgnoreCase))
            {
                kind = SqlValueKind.Int32;
                return true;
            }

            kind = SqlValueKind.Error;
            return false;
        }

        public string ToClrTypeName(SqlValueKind kind, bool nullable)
        {
            var name = kind == SqlValueKind.Int32
                ? "int"
                : kind == SqlValueKind.Decimal ? "decimal" : "object";
            return nullable ? name + "?" : name;
        }

        public string? ToDatabaseTypeName(SqlValueKind kind) => null;

        public string MapMigrationType(
            string logicalType,
            int? length = null,
            int? precision = null,
            int? scale = null) => logicalType;
    }
}

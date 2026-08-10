using System;
using System.Collections.Generic;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class SqliteDialectTests
{
    [Fact]
    public void ExposesEverySqliteAnalysisService()
    {
        var dialect = new SqliteDatabaseDialect();

        Assert.Equal(DatabaseProvider.Sqlite, dialect.Provider);
        Assert.Equal("Sqlite", dialect.Name);
        Assert.IsType<SqliteQueryAnalyzer>(dialect.QueryAnalyzer);
        Assert.IsType<SqliteSchemaMigrationAnalyzer>(dialect.SchemaMigrationAnalyzer);
        Assert.IsType<SqliteScriptClassifierService>(dialect.ScriptClassifier);
        Assert.IsType<SqliteIdentifierQuoter>(dialect.IdentifierQuoter);
        Assert.IsType<SqliteTypeMapper>(dialect.TypeMapper);
        Assert.IsType<SqliteMigrationSqlWriter>(dialect.MigrationSqlWriter);
        Assert.IsType<SqliteSchemaRules>(dialect.SchemaRules);
    }

    [Fact]
    public void QuotesIdentifiersAndRejectsNonEmptySchemas()
    {
        var quoter = new SqliteIdentifierQuoter();

        Assert.Equal("\"odd\"\"name\"", quoter.QuoteIdentifier("odd\"name"));
        Assert.Equal("\"items\"", quoter.QuoteQualifiedName(null, "items"));
        Assert.Equal("\"items\"", quoter.QuoteQualifiedName(string.Empty, "items"));
        Assert.Throws<NotSupportedException>(() => quoter.QuoteQualifiedName("main", "items"));
    }

    [Fact]
    public void AppliesCaseInsensitiveSingleSchemaRules()
    {
        var rules = new SqliteSchemaRules();

        Assert.False(rules.SupportsSchemas);
        Assert.Null(rules.DefaultSchema);
        Assert.True(rules.IsDefaultSchema(null));
        Assert.True(rules.IsDefaultSchema(string.Empty));
        Assert.False(rules.IsDefaultSchema("main"));
        Assert.True(rules.AreIdentifiersEqual("MixedCase", true, "mixedcase"));
        Assert.True(rules.AreIdentifiersEqual("MixedCase", false, "MIXEDCASE"));
    }

    [Theory]
    [InlineData("INTEGER", SqlValueKind.Int64)]
    [InlineData("BIGINT", SqlValueKind.Int64)]
    [InlineData("CHARINT", SqlValueKind.Int64)]
    [InlineData("VARCHAR(40)", SqlValueKind.String)]
    [InlineData("CLOB", SqlValueKind.String)]
    [InlineData("BLOB", SqlValueKind.Bytes)]
    [InlineData("", SqlValueKind.Bytes)]
    [InlineData("REAL", SqlValueKind.Double)]
    [InlineData("DOUBLE PRECISION", SqlValueKind.Double)]
    [InlineData("NUMERIC(18,4)", SqlValueKind.Decimal)]
    [InlineData("BOOLEAN", SqlValueKind.Decimal)]
    public void MapsDeclaredTypesBySQLiteAffinity(string sqlType, SqlValueKind expected)
    {
        var mapper = new SqliteTypeMapper();

        Assert.True(mapper.TryMap(sqlType, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("int16", "INTEGER")]
    [InlineData("int32", "INTEGER")]
    [InlineData("int64", "INTEGER")]
    [InlineData("boolean", "INTEGER")]
    [InlineData("decimal", "NUMERIC")]
    [InlineData("float", "REAL")]
    [InlineData("double", "REAL")]
    [InlineData("string", "TEXT")]
    [InlineData("text", "TEXT")]
    [InlineData("date", "TEXT")]
    [InlineData("datetime", "TEXT")]
    [InlineData("datetimeoffset", "TEXT")]
    [InlineData("time", "TEXT")]
    [InlineData("guid", "TEXT")]
    [InlineData("json", "TEXT")]
    [InlineData("binary", "BLOB")]
    [InlineData("jsonb", "BLOB")]
    public void MapsMigrationLogicalTypesLikeTheSqliteRuntimeProvider(
        string logicalType,
        string expectedSqlType)
    {
        Assert.Equal(expectedSqlType, new SqliteTypeMapper().MapMigrationType(logicalType));
    }

    [Fact]
    public void WriterProducesValidIdentityAndTableOperations()
    {
        var writer = new SqliteMigrationSqlWriter();

        Assert.Equal(
            "\"id\" INTEGER PRIMARY KEY AUTOINCREMENT",
            writer.FormatColumn("\"id\"", "INTEGER", false, true, true));
        Assert.Equal(
            "\"name\" TEXT NOT NULL",
            writer.FormatColumn("\"name\"", "TEXT", false, false, false));
        Assert.Equal(
            "CREATE TABLE \"items\" (\"id\" INTEGER PRIMARY KEY AUTOINCREMENT);",
            writer.CreateTable(
                "\"items\"",
                new[] { "\"id\" INTEGER PRIMARY KEY AUTOINCREMENT" }));
        Assert.Equal(
            "ALTER TABLE \"items\" ADD COLUMN \"name\" TEXT;",
            writer.AddColumn("\"items\"", "\"name\" TEXT"));
        Assert.Equal(
            "ALTER TABLE \"items\" DROP COLUMN \"name\";",
            writer.DropColumn("\"items\"", "\"name\""));
        Assert.Equal(
            "ALTER TABLE \"items\" RENAME TO \"accounts\";",
            writer.RenameTable("\"items\"", "\"accounts\""));
        Assert.Equal(
            "ALTER TABLE \"items\" RENAME COLUMN \"name\" TO \"label\";",
            writer.RenameColumn("\"items\"", "\"name\"", "\"label\""));
    }

    [Fact]
    public void TryAlterColumnAlwaysReportsTheTableRebuildRequirement()
    {
        var writer = new SqliteMigrationSqlWriter();
        var requests = new[]
        {
            (Type: (string?)"TEXT", Nullable: (bool?)null),
            (Type: (string?)null, Nullable: (bool?)false),
            (Type: (string?)"INTEGER", Nullable: (bool?)true),
            (Type: (string?)null, Nullable: (bool?)null),
        };

        foreach (var request in requests)
        {
            var supported = writer.TryAlterColumn(
                "\"items\"",
                "\"value\"",
                request.Type,
                request.Nullable,
                out var sql,
                out var error);

            Assert.False(supported);
            Assert.Null(sql);
            Assert.Contains("table rebuild", error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WriterRejectsQualifiedTablesAndInvalidIdentityDefinitions()
    {
        var writer = new SqliteMigrationSqlWriter();

        Assert.Throws<NotSupportedException>(() =>
            writer.CreateTable("\"main\".\"items\"", new[] { "\"id\" INTEGER" }));
        Assert.Throws<ArgumentException>(() =>
            writer.FormatColumn("\"id\"", "INT", false, true, true));
        Assert.Throws<ArgumentException>(() =>
            writer.FormatColumn("\"id\"", "INTEGER", false, false, true));
    }
}

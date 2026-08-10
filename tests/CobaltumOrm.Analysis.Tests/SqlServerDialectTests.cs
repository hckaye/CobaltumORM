using System;
using System.Linq;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class SqlServerDialectTests
{
    [Fact]
    public void DialectExposesAllSqlServerServices()
    {
        var dialect = new SqlServerDatabaseDialect();

        Assert.Equal(DatabaseProvider.SqlServer, dialect.Provider);
        Assert.Equal("SqlServer", dialect.Name);
        Assert.IsType<SqlServerQueryAnalyzer>(dialect.QueryAnalyzer);
        Assert.IsType<SqlServerSchemaMigrationAnalyzer>(dialect.SchemaMigrationAnalyzer);
        Assert.IsType<SqlServerScriptClassifierService>(dialect.ScriptClassifier);
        Assert.IsType<SqlServerIdentifierQuoter>(dialect.IdentifierQuoter);
        Assert.IsType<SqlServerTypeMapper>(dialect.TypeMapper);
        Assert.IsType<SqlServerMigrationSqlWriter>(dialect.MigrationSqlWriter);
        Assert.IsType<SqlServerSchemaRules>(dialect.SchemaRules);
    }

    [Fact]
    public void BracketQuoterEscapesOnlyClosingBracketsAndUsesDboByDefault()
    {
        var quoter = new SqlServerIdentifierQuoter();

        Assert.Equal("[a]]b.c]", quoter.QuoteIdentifier("a]b.c"));
        Assert.Equal("[dbo].[items]", quoter.QuoteQualifiedName(null, "items"));
        Assert.Equal("[app].[items]", quoter.QuoteQualifiedName("app", "items"));
        Assert.Throws<ArgumentException>(() => quoter.QuoteIdentifier(" "));
        Assert.Throws<ArgumentException>(() => quoter.QuoteIdentifier("a\0b"));
    }

    [Fact]
    public void SchemaRulesTreatDboAndAllIdentifierSpellingsCaseInsensitively()
    {
        var rules = new SqlServerSchemaRules();

        Assert.True(rules.SupportsSchemas);
        Assert.Equal("dbo", rules.DefaultSchema);
        Assert.True(rules.IsDefaultSchema(null));
        Assert.True(rules.IsDefaultSchema("DBO"));
        Assert.False(rules.IsDefaultSchema("app"));
        Assert.Equal("MixedCase", rules.NormalizeUnquotedIdentifier("MixedCase"));
        Assert.True(rules.AreIdentifiersEqual("Name", true, "name"));
        Assert.True(rules.AreIdentifiersEqual("NAME", false, "name"));
    }

    [Theory]
    [InlineData("tinyint", SqlValueKind.Int16)]
    [InlineData("smallint", SqlValueKind.Int16)]
    [InlineData("int", SqlValueKind.Int32)]
    [InlineData("integer", SqlValueKind.Int32)]
    [InlineData("bigint", SqlValueKind.Int64)]
    [InlineData("bit", SqlValueKind.Bool)]
    [InlineData("decimal(18,4)", SqlValueKind.Decimal)]
    [InlineData("money", SqlValueKind.Decimal)]
    [InlineData("float", SqlValueKind.Double)]
    [InlineData("float(24)", SqlValueKind.Float)]
    [InlineData("real", SqlValueKind.Float)]
    [InlineData("nvarchar(max)", SqlValueKind.String)]
    [InlineData("character varying(40)", SqlValueKind.String)]
    [InlineData("varbinary(max)", SqlValueKind.Bytes)]
    [InlineData("binary(8)", SqlValueKind.Bytes)]
    [InlineData("date", SqlValueKind.DateOnly)]
    [InlineData("time(3)", SqlValueKind.TimeOnly)]
    [InlineData("datetime", SqlValueKind.DateTime)]
    [InlineData("datetime2(7)", SqlValueKind.DateTime)]
    [InlineData("datetimeoffset(7)", SqlValueKind.DateTimeOffset)]
    [InlineData("uniqueidentifier", SqlValueKind.Guid)]
    [InlineData("xml", SqlValueKind.String)]
    public void TypeMapperCoversSqlServerTypes(string sqlType, SqlValueKind expected)
    {
        var mapper = new SqlServerTypeMapper();

        Assert.True(mapper.TryMap(sqlType, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TypeMapperRejectsInvalidModifiersAndMapsMigrationTypes()
    {
        var mapper = new SqlServerTypeMapper();

        Assert.False(mapper.TryMap("decimal(39,2)", out _));
        Assert.False(mapper.TryMap("nvarchar(0)", out _));
        Assert.False(mapper.TryMap("time(8)", out _));
        Assert.Equal("int", mapper.MapMigrationType("int32"));
        Assert.Equal("decimal(18,4)", mapper.MapMigrationType("decimal", precision: 18, scale: 4));
        Assert.Equal("nvarchar(max)", mapper.MapMigrationType("jsonb"));
        Assert.Equal("nvarchar(32)", mapper.MapMigrationType("string", length: 32));
        Assert.Equal("datetime2", mapper.MapMigrationType("datetime"));
        Assert.Equal("varbinary(max)", mapper.MapMigrationType("binary"));
    }

    [Fact]
    public void QueryAnalyzerUsesBracketsDoubleQuotesParametersAndSqlServerAggregates()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table(
                "Users",
                new[]
                {
                    new Column("ID", "int"),
                    new Column("SmallValue", "smallint"),
                    new Column("BigValue", "bigint"),
                    new Column("Name", "nvarchar(100)"),
                    new Column("RealValue", "real"),
                },
                "dbo"),
        });
        var analyzer = new SqlServerQueryAnalyzer();

        var result = analyzer.Analyze(
            schema,
            "SELECT SUM([smallvalue]), SUM(bigvalue), AVG(id), AVG(realvalue), COUNT(*) " +
            "FROM [DBO].[users] WHERE [ID] = @id AND \"name\" = @name");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            new[] { "int?", "long?", "int?", "float?", "int" },
            result.Columns.Select(column => column.ClrType));
        Assert.Equal(new[] { "@id", "@name" }, result.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(new[] { "int", "string" }, result.Parameters.Select(parameter => parameter.ClrType));
    }

    [Fact]
    public void QueryAnalyzerUsesDboForUnqualifiedSqlServerTables()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("users", new[] { new Column("id", "int") }, "app"),
            new Table("users", new[] { new Column("id", "bigint") }, "dbo"),
        });

        var result = new SqlServerQueryAnalyzer().Analyze(schema, "SELECT id FROM users");

        Assert.Empty(result.Diagnostics);
        Assert.Equal("long", Assert.Single(result.Columns).ClrType);
    }

    [Fact]
    public void ClassifierKeepsSemicolonsInsideTsqlLexicalConstructs()
    {
        var classifier = new SqlServerScriptClassifierService();
        var statements = classifier.SplitAndClassify(
            "-- header;\n" +
            "CREATE TABLE [dbo].[semi;table] ([text] nvarchar(20) DEFAULT N'a; b');\n" +
            "/* ; comment */ SELECT N'x; y';\n" +
            "CREATE INDEX [ix] ON [dbo].[semi;table] ([text]);",
            out var error);

        Assert.Null(error);
        Assert.Equal(3, statements.Count);
        Assert.Equal(SqlStatementKind.SupportedTableDdl, statements[0].Kind);
        Assert.Equal(SqlStatementKind.Select, statements[1].Kind);
        Assert.Equal(SqlStatementKind.SchemaNeutral, statements[2].Kind);
        Assert.Contains("semi;table", statements[0].Text, StringComparison.Ordinal);
        Assert.Contains("a; b", statements[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationWriterGeneratesSqlServerDdlAndSafeLiteralRenames()
    {
        var writer = new SqlServerMigrationSqlWriter();

        Assert.Equal(
            "[id] int IDENTITY(1,1) NOT NULL PRIMARY KEY",
            writer.FormatColumn("[id]", "int", true, true, true));
        Assert.Equal(
            "CREATE TABLE [dbo].[users] ([id] int IDENTITY(1,1) NOT NULL PRIMARY KEY);",
            writer.CreateTable(
                "[dbo].[users]",
                new[] { writer.FormatColumn("[id]", "int", false, true, true) }));
        Assert.Equal(
            "ALTER TABLE [dbo].[users] ALTER COLUMN [name] nvarchar(80) NOT NULL;",
            SqlServerAssertAlter(writer, "[dbo].[users]", "[name]", "nvarchar(80)", false));
        Assert.Contains("@objname = N'[dbo].[old]''table]'", writer.RenameTable("[dbo].[old]'table]", "[new]"), StringComparison.Ordinal);
        Assert.Contains("@objtype = N'COLUMN'", writer.RenameColumn("[dbo].[users]", "[old]", "[new]"), StringComparison.Ordinal);

        Assert.False(writer.TryAlterColumn(
            "[dbo].[users]",
            "[name]",
            null,
            false,
            out var sql,
            out var error));
        Assert.Null(sql);
        Assert.Contains("requires a target SQL type", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedSqlRoundTripsThroughAnalyzerIncludingRenames()
    {
        var dialect = new SqlServerDatabaseDialect();
        var writer = dialect.MigrationSqlWriter;
        var id = writer.FormatColumn("[id]", "int", false, true, true);
        var name = writer.FormatColumn("[name]", "nvarchar(40)", true, false, false);
        var create = writer.CreateTable("[dbo].[users]", new[] { id, name });
        var add = writer.AddColumn("[dbo].[users]", "[email] nvarchar(100) NULL");
        Assert.True(writer.TryAlterColumn("[dbo].[users]", "[name]", "nvarchar(80)", false, out var alter, out var alterError));
        Assert.Null(alterError);

        var result = dialect.SchemaMigrationAnalyzer.Analyze(
            new DatabaseSchema(Array.Empty<Table>()),
            create + add + alter + writer.RenameTable("[dbo].[users]", "[accounts]") +
            writer.RenameColumn("[dbo].[accounts]", "[email]", "[contact]") +
            writer.DropColumn("[dbo].[accounts]", "[name]"));

        Assert.Empty(result.Diagnostics);
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal("dbo", table.Schema);
        Assert.Equal("accounts", table.Name);
        Assert.Equal(new[] { "id", "contact" }, table.Columns.Select(column => column.Name));
        Assert.Equal("nvarchar(100)", table.Columns[1].SqlType);
    }

    [Fact]
    public void FlywayDdlKeepsDefaultsAndCommonConstraints()
    {
        var result = SqlServerMigrationAnalyzer.Analyze(
            new DatabaseSchema(Array.Empty<Table>()),
            "CREATE TABLE [dbo].[accounts] (" +
            "[id] int NOT NULL, " +
            "[enabled] bit NOT NULL CONSTRAINT [DF_accounts_enabled] DEFAULT ((1)), " +
            "[name] nvarchar(80) NULL, " +
            "CONSTRAINT [PK_accounts] PRIMARY KEY CLUSTERED ([id] ASC) " +
            "WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF) ON [PRIMARY]" +
            ");\n" +
            "CREATE UNIQUE INDEX [UX_accounts_name] ON [dbo].[accounts] ([name]);\n" +
            "ALTER TABLE [dbo].[accounts] ADD CONSTRAINT [DF_accounts_name] DEFAULT (N'unknown') FOR [name];\n" +
            "ALTER TABLE [dbo].[accounts] ADD [created_at] datetime2 NOT NULL;\n" +
            "ALTER TABLE [dbo].[accounts] ALTER COLUMN [created_at] datetimeoffset NOT NULL;");

        Assert.Empty(result.Diagnostics);
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal(new[] { "id", "enabled", "name", "created_at" }, table.Columns.Select(column => column.Name));
        Assert.True(table.Columns[0].IsPrimaryKey);
        Assert.Equal("((1))", table.Columns[1].DefaultExpression);
        Assert.Equal("(N'unknown')", table.Columns[2].DefaultExpression);
        Assert.Equal("datetimeoffset", table.Columns[3].SqlType);
    }

    [Fact]
    public void CommonForeignUniqueAndCheckConstraintsDoNotLoseColumns()
    {
        var result = SqlServerMigrationAnalyzer.Analyze(
            new DatabaseSchema(Array.Empty<Table>()),
            "CREATE TABLE [dbo].[items] (" +
            "[id] int NOT NULL, " +
            "[parent_id] int NULL, " +
            "[code] nvarchar(20) NOT NULL, " +
            "CONSTRAINT [UQ_items_code] UNIQUE ([code]), " +
            "CONSTRAINT [FK_items_parent] FOREIGN KEY ([parent_id]) REFERENCES [dbo].[parents]([id]), " +
            "CONSTRAINT [CK_items_code] CHECK ([code] <> N'')" +
            ");");

        Assert.Empty(result.Diagnostics);
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal("nvarchar(20)", table.Columns[2].SqlType);
    }

    [Fact]
    public void UnsupportedSchemaChangesProduceDiagnosticsAndDoNotChangeSchema()
    {
        var original = new DatabaseSchema(new[]
        {
            new Table("users", new[] { new Column("id", "int") }, "dbo"),
        });

        var result = SqlServerSchemaBuilder.ApplyScript(original, "CREATE VIEW [dbo].[users_view] AS SELECT [id] FROM [dbo].[users];");

        Assert.NotEmpty(result.Diagnostics);
        Assert.Equal("users", Assert.Single(result.Schema.Tables).Name);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DDL300");
    }

    private static string SqlServerAssertAlter(
        SqlServerMigrationSqlWriter writer,
        string table,
        string column,
        string type,
        bool nullable)
    {
        Assert.True(writer.TryAlterColumn(table, column, type, nullable, out var sql, out var error));
        Assert.Null(error);
        return sql!;
    }
}

using System.Linq;
using CobaltumOrm.Analysis;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class OracleDdlTests
{
    [Fact]
    public void GeneratedOracleDdlRoundTripsThroughSchemaAnalysis()
    {
        var dialect = new OracleDatabaseDialect();
        var writer = dialect.MigrationSqlWriter;
        var sql = writer.CreateTable(
            dialect.IdentifierQuoter.QuoteQualifiedName("APP", "USERS"),
            new[]
            {
                writer.FormatColumn("\"id\"", "NUMBER(10,0)", false, true, true),
                writer.FormatColumn("\"name\"", "VARCHAR2(40)", true, false, false),
            });

        var created = dialect.SchemaMigrationAnalyzer.Analyze(new DatabaseSchema(new Table[0]), sql);
        Assert.Empty(created.Diagnostics);
        var table = Assert.Single(created.Schema.Tables);
        Assert.Equal("APP", table.Schema);
        Assert.Equal("USERS", table.Name);
        Assert.Equal("id", table.Columns[0].Name);
        Assert.Equal("NUMBER(10,0)", table.Columns[0].SqlType);
        Assert.True(table.Columns[0].IsIdentity);
        Assert.True(table.Columns[0].IsPrimaryKey);
        Assert.False(table.Columns[0].IsNullable);
        Assert.Equal("name", table.Columns[1].Name);
        Assert.True(table.Columns[1].IsNullable);

        var changed = dialect.SchemaMigrationAnalyzer.Analyze(
            created.Schema,
            "ALTER TABLE \"APP\".\"USERS\" ADD (\"created\" TIMESTAMP DEFAULT SYSTIMESTAMP NOT NULL);\n" +
            "ALTER TABLE \"APP\".\"USERS\" MODIFY (\"name\" VARCHAR2(80) NULL);\n" +
            "ALTER TABLE \"APP\".\"USERS\" RENAME COLUMN \"name\" TO \"display_name\";\n" +
            "ALTER TABLE \"APP\".\"USERS\" DROP COLUMN \"created\";\n" +
            "ALTER TABLE \"APP\".\"USERS\" RENAME TO \"CUSTOMERS\";");

        Assert.Empty(changed.Diagnostics);
        var finalTable = Assert.Single(changed.Schema.Tables);
        Assert.Equal("CUSTOMERS", finalTable.Name);
        Assert.Equal(new[] { "id", "display_name" }, finalTable.Columns.Select(column => column.Name));
        Assert.Equal("VARCHAR2(80)", finalTable.Columns[1].SqlType);
        Assert.True(finalTable.Columns[1].IsNullable);
    }

    [Fact]
    public void GuidMigrationTypeRoundTripsThroughWriterAnalyzerAndQueryAnalyzer()
    {
        var dialect = new OracleDatabaseDialect();
        var guidType = dialect.TypeMapper.MapMigrationType("guid");
        var column = dialect.MigrationSqlWriter.FormatColumn(
            "\"token_id\"",
            guidType,
            false,
            true,
            false);
        var sql = dialect.MigrationSqlWriter.CreateTable(
            dialect.IdentifierQuoter.QuoteQualifiedName("APP", "TOKENS"),
            new[] { column });

        var migration = dialect.SchemaMigrationAnalyzer.Analyze(
            new DatabaseSchema(new Table[0]),
            sql);

        Assert.Empty(migration.Diagnostics);
        var table = Assert.Single(migration.Schema.Tables);
        var guidColumn = Assert.Single(table.Columns);
        Assert.Equal("RAW(16)", guidColumn.SqlType);
        Assert.False(guidColumn.IsNullable);
        Assert.True(guidColumn.IsPrimaryKey);

        var query = new OracleQueryAnalyzer().Analyze(
            migration.Schema,
            "SELECT token_id FROM app.tokens");

        Assert.Empty(query.Diagnostics);
        Assert.Equal("Guid", Assert.Single(query.Columns).ClrType);
    }

    [Fact]
    public void UppercasesUnquotedDeclarationsAndPreservesQuotedDeclarations()
    {
        var result = new OracleSchemaMigrationAnalyzer().Analyze(
            new DatabaseSchema(new Table[0]),
            "create table app.users (id number(10,0), \"MixedColumn\" varchar2(20), " +
            "constraint users_pk primary key (id));");

        Assert.Empty(result.Diagnostics);
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal("APP", table.Schema);
        Assert.Equal("USERS", table.Name);
        Assert.Equal("ID", table.Columns[0].Name);
        Assert.Equal("MixedColumn", table.Columns[1].Name);
        Assert.True(table.Columns[0].IsPrimaryKey);
        Assert.False(table.Columns[0].IsNullable);
    }

    [Fact]
    public void AnalyzesRepresentativeFlywayOracleDdl()
    {
        var script =
            "CREATE TABLE flyway_schema_history (" +
            "installed_rank NUMBER(10) NOT NULL, " +
            "version VARCHAR2(50), " +
            "description VARCHAR2(200) NOT NULL, " +
            "type VARCHAR2(20) NOT NULL, " +
            "script VARCHAR2(1000) NOT NULL, " +
            "checksum NUMBER(10), " +
            "installed_by VARCHAR2(100) NOT NULL, " +
            "installed_on TIMESTAMP DEFAULT SYSTIMESTAMP NOT NULL, " +
            "execution_time NUMBER(10) NOT NULL, " +
            "success NUMBER(1) NOT NULL, " +
            "CONSTRAINT flyway_schema_history_pk PRIMARY KEY (installed_rank));" +
            "CREATE INDEX flyway_schema_history_s_idx ON flyway_schema_history (success);" +
            "ALTER TABLE flyway_schema_history ADD (" +
            "installed_description VARCHAR2(200) DEFAULT 'installed; value' NULL);";

        var result = OracleSchemaBuilder.ApplyScript(new DatabaseSchema(new Table[0]), script);

        Assert.Empty(result.Diagnostics);
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal("FLYWAY_SCHEMA_HISTORY", table.Name);
        Assert.Equal("NUMBER(10)", table.Columns[0].SqlType);
        Assert.True(table.Columns[0].IsPrimaryKey);
        Assert.Equal("SYSTIMESTAMP", table.Columns[7].DefaultExpression);
        Assert.Equal("'installed; value'", table.Columns[10].DefaultExpression);
        Assert.Equal("INSTALLED_DESCRIPTION", table.Columns[10].Name);
    }

    [Fact]
    public void AcceptsCommonInlineConstraintsAndOracleTemporaryTables()
    {
        var result = OracleSchemaBuilder.ApplyScript(
            new DatabaseSchema(new Table[0]),
            "CREATE GLOBAL TEMPORARY TABLE child_rows (" +
            "id NUMBER(10,0) PRIMARY KEY, " +
            "parent_id NUMBER(10,0) REFERENCES parent_rows(id), " +
            "amount NUMBER(10,2) CHECK (amount >= 0), " +
            "code VARCHAR2(20) UNIQUE, " +
            "created DATE DEFAULT SYSDATE NOT NULL);" );

        Assert.Empty(result.Diagnostics);
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal("CHILD_ROWS", table.Name);
        Assert.True(table.Columns[0].IsPrimaryKey);
        Assert.Equal("SYSDATE", table.Columns[4].DefaultExpression);
        Assert.False(table.Columns[4].IsNullable);
    }

    [Fact]
    public void AcceptsCommonOracleCreateOptionsAndDropCascade()
    {
        var result = OracleSchemaBuilder.ApplyScript(
            new DatabaseSchema(new Table[0]),
            "CREATE TABLE users (id NUMBER(10,0)) SEGMENT CREATION IMMEDIATE " +
            "PCTFREE 10 INITRANS 2 TABLESPACE app_data LOGGING;" +
            "ALTER TABLE users ADD (display_name VARCHAR2(80));" +
            "ALTER TABLE users DROP COLUMN display_name CASCADE CONSTRAINTS;");

        Assert.Empty(result.Diagnostics);
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal("USERS", table.Name);
        Assert.Single(table.Columns);
    }

    [Fact]
    public void ReportsDiagnosticsForNullMigrationInputs()
    {
        var analyzer = new OracleSchemaMigrationAnalyzer();
        var nullSchema = analyzer.Analyze(null!, "CREATE TABLE users (id NUMBER(10,0))");
        var nullSql = analyzer.Analyze(new DatabaseSchema(new Table[0]), null!);

        Assert.Equal("DDL000", Assert.Single(nullSchema.Diagnostics).Code);
        Assert.Equal("DDL000", Assert.Single(nullSql.Diagnostics).Code);
    }

    [Fact]
    public void RejectsUnsupportedSchemaChangingAndProceduralStatements()
    {
        var result = OracleSchemaBuilder.ApplyScript(
            new DatabaseSchema(new Table[0]),
            "CREATE VIEW users_view AS SELECT 1 FROM dual; " +
            "BEGIN EXECUTE IMMEDIATE 'CREATE TABLE hidden_table (id NUMBER(10,0))'; END;");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DDL300");
        Assert.Empty(result.Schema.Tables);
    }
}

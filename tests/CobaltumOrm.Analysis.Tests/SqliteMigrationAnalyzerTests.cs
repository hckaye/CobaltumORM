using System;
using System.Linq;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class SqliteMigrationAnalyzerTests
{
    [Fact]
    public void AppliesGeneratedSqlAndPreservesColumnMetadata()
    {
        var writer = new SqliteMigrationSqlWriter();
        var quoter = new SqliteIdentifierQuoter();
        var create = writer.CreateTable(
            quoter.QuoteQualifiedName(null, "items"),
            new[]
            {
                writer.FormatColumn(quoter.QuoteIdentifier("id"), "INTEGER", false, true, true),
                writer.FormatColumn(quoter.QuoteIdentifier("name"), "TEXT", false, false, false),
            });
        var add = writer.AddColumn(
            quoter.QuoteQualifiedName(null, "items"),
            writer.FormatColumn(quoter.QuoteIdentifier("created_at"), "TEXT", true, false, false));

        var result = SqliteMigrationAnalyzer.Analyze(
            new DatabaseSchema(Array.Empty<Table>()),
            create + add);

        SqliteMigrationAssertSuccess(result);
        var table = Assert.Single(result.Schema.Tables);
        Assert.Null(table.Schema);
        Assert.Equal("items", table.Name);
        Assert.Equal(new[] { "id", "name", "created_at" }, table.Columns.Select(item => item.Name));
        Assert.True(table.Columns[0].IsIdentity);
        Assert.True(table.Columns[0].IsPrimaryKey);
        Assert.False(table.Columns[0].IsNullable);
        Assert.Equal("TEXT", table.Columns[2].SqlType);
        Assert.True(table.Columns[2].IsNullable);
    }

    [Fact]
    public void AppliesFlywaySQLiteDdlWithConstraintsDefaultsAndComments()
    {
        var sql = @"
            -- Flyway V1
            CREATE TABLE IF NOT EXISTS `users` (
                `id` INTEGER PRIMARY KEY AUTOINCREMENT,
                `name` TEXT NOT NULL DEFAULT 'semi;value',
                `active` INTEGER NOT NULL DEFAULT 1,
                `parent_id` INTEGER REFERENCES users(id) ON DELETE CASCADE,
                CONSTRAINT `users_name_uq` UNIQUE (`name`),
                CHECK (`active` IN (0, 1))
            );
            /* Flyway may create indexes in the same migration. */
            CREATE UNIQUE INDEX `users_name_ix` ON `users` (`name`);
            ALTER TABLE `users` ADD COLUMN `created_at` TEXT DEFAULT CURRENT_TIMESTAMP;
            ALTER TABLE `users` RENAME COLUMN `name` TO `display_name`;
            ALTER TABLE `users` RENAME TO `accounts`;
            ALTER TABLE `accounts` DROP COLUMN `created_at`;
        ";

        var result = SqliteMigrationAnalyzer.Analyze(
            new DatabaseSchema(Array.Empty<Table>()),
            sql);

        SqliteMigrationAssertSuccess(result);
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal("accounts", table.Name);
        Assert.Equal(new[] { "id", "display_name", "active", "parent_id" },
            table.Columns.Select(item => item.Name));
        Assert.True(table.Columns[0].IsIdentity);
        Assert.Equal("'semi;value'", table.Columns[1].DefaultExpression);
        Assert.False(table.Columns[1].IsNullable);
        Assert.Equal("1", table.Columns[2].DefaultExpression);
        Assert.Equal("INTEGER", table.Columns[3].SqlType);
    }

    [Fact]
    public void PreservesExistingDefaultsIdentityAndSchemaWhenRenaming()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("items", new[]
            {
                new Column("id", "INTEGER", false, true, null, true),
                new Column("value", "TEXT", false, false, "'old'", false),
            }),
        });

        var result = SqliteMigrationAnalyzer.Analyze(
            schema,
            "ALTER TABLE items RENAME COLUMN value TO label; ALTER TABLE items RENAME TO archive;");

        SqliteMigrationAssertSuccess(result);
        Assert.Equal("items", schema.Tables[0].Name);
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal("archive", table.Name);
        Assert.True(table.Columns[0].IsIdentity);
        Assert.True(table.Columns[0].IsPrimaryKey);
        Assert.Equal("label", table.Columns[1].Name);
        Assert.Equal("'old'", table.Columns[1].DefaultExpression);
    }

    [Fact]
    public void DiagnosesUnsupportedSchemaChangesWithoutApplyingThem()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("items", new[] { new Column("id", "INTEGER"), new Column("value", "TEXT") }),
        });

        var result = SqliteMigrationAnalyzer.Analyze(
            schema,
            "CREATE VIEW item_view AS SELECT * FROM items; " +
            "ALTER TABLE items ALTER COLUMN value TYPE INTEGER; " +
            "CREATE TABLE main.other (id INTEGER); ");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, item => item.Code == "DDL101");
        Assert.Single(result.Schema.Tables);
        Assert.Equal("items", result.Schema.Tables[0].Name);
        Assert.Equal(new[] { "id", "value" }, result.Schema.Tables[0].Columns.Select(item => item.Name));
    }

    [Fact]
    public void RejectsInvalidIdentityAndSchemaQualifiedDdl()
    {
        var invalidIdentity = SqliteMigrationAnalyzer.Analyze(
            new DatabaseSchema(Array.Empty<Table>()),
            "CREATE TABLE items (id INT PRIMARY KEY AUTOINCREMENT);");
        var qualified = SqliteMigrationAnalyzer.Analyze(
            new DatabaseSchema(Array.Empty<Table>()),
            "CREATE TABLE main.items (id INTEGER);");

        Assert.True(invalidIdentity.HasErrors);
        Assert.Empty(invalidIdentity.Schema.Tables);
        Assert.True(qualified.HasErrors);
        Assert.Empty(qualified.Schema.Tables);
    }

    private static void SqliteMigrationAssertSuccess(MigrationAnalysisResult result)
    {
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
    }
}

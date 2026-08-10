using System;
using System.Linq;
using CobaltumOrm.Analysis;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class MySqlMigrationTests
{
    [Fact]
    public void WriterGeneratesMySql8Ddl()
    {
        var writer = new MySqlMigrationSqlWriter();

        Assert.Equal(
            "CREATE TABLE `app``data`.`widgets` (`id` int AUTO_INCREMENT NOT NULL PRIMARY KEY, `name` varchar(40));",
            writer.CreateTable(
                "`app``data`.`widgets`",
                new[]
                {
                    writer.FormatColumn("`id`", "int", false, true, true),
                    writer.FormatColumn("`name`", "varchar(40)", null, false, false),
                }));
        Assert.Equal(
            "ALTER TABLE `app``data`.`widgets` ADD COLUMN `created` datetime NOT NULL;",
            writer.AddColumn("`app``data`.`widgets`", writer.FormatColumn("`created`", "datetime", false, false, false)));
        Assert.Equal("DROP TABLE `app``data`.`widgets`;", writer.DropTable("`app``data`.`widgets`"));
        Assert.Equal(
            "ALTER TABLE `app``data`.`widgets` DROP COLUMN `obsolete`;",
            writer.DropColumn("`app``data`.`widgets`", "`obsolete`"));
        Assert.Equal(
            "RENAME TABLE `app``data`.`widgets` TO `accounts`;",
            writer.RenameTable("`app``data`.`widgets`", "`accounts`"));
        Assert.Equal(
            "ALTER TABLE `app``data`.`widgets` RENAME COLUMN `name` TO `display_name`;",
            writer.RenameColumn("`app``data`.`widgets`", "`name`", "`display_name`"));
    }

    [Fact]
    public void TryAlterColumnEmitsOneCompleteModifyDefinition()
    {
        var writer = new MySqlMigrationSqlWriter();

        Assert.True(writer.TryAlterColumn(
            "`app`.`widgets`",
            "`name`",
            "varchar(80)",
            true,
            out var sql,
            out var error));
        Assert.Null(error);
        Assert.Equal("ALTER TABLE `app`.`widgets` MODIFY COLUMN `name` varchar(80) NULL;", sql);
        Assert.DoesNotContain("ALTER COLUMN", sql!, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", sql!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAlterColumnRejectsIncompleteTargetsWithoutThrowing()
    {
        var writer = new MySqlMigrationSqlWriter();

        Assert.False(writer.TryAlterColumn("`widgets`", "`name`", null, true, out var noType, out var noTypeError));
        Assert.Null(noType);
        Assert.Contains("complete target SQL type", noTypeError, StringComparison.Ordinal);
        Assert.False(writer.TryAlterColumn("`widgets`", "`name`", "text", null, out var noNullability, out var noNullabilityError));
        Assert.Null(noNullability);
        Assert.Contains("explicit nullability", noNullabilityError, StringComparison.Ordinal);
        Assert.False(writer.TryAlterColumn("", "`name`", "text", true, out _, out var noTableError));
        Assert.Contains("qualified table", noTableError, StringComparison.Ordinal);
        Assert.False(writer.TryAlterColumn("`widgets`", "", "text", true, out _, out var noColumnError));
        Assert.Contains("quoted column", noColumnError, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTripsWriterOutputThroughMySqlSchemaAnalysis()
    {
        var dialect = new MySqlDatabaseDialect();
        var qualified = dialect.IdentifierQuoter.QuoteQualifiedName("app`data", "widget`items");
        var writer = dialect.MigrationSqlWriter;
        var create = writer.CreateTable(
            qualified,
            new[]
            {
                writer.FormatColumn(dialect.IdentifierQuoter.QuoteIdentifier("id"), dialect.TypeMapper.MapMigrationType("int32"), false, true, true),
                writer.FormatColumn(dialect.IdentifierQuoter.QuoteIdentifier("amount"), dialect.TypeMapper.MapMigrationType("decimal", precision: 18, scale: 4), null, false, false),
                writer.FormatColumn(dialect.IdentifierQuoter.QuoteIdentifier("document"), dialect.TypeMapper.MapMigrationType("jsonb"), true, false, false),
                writer.FormatColumn(dialect.IdentifierQuoter.QuoteIdentifier("external_id"), dialect.TypeMapper.MapMigrationType("guid"), false, false, false),
            });

        var result = MySqlMigrationAnalyzer.Analyze(new DatabaseSchema(Array.Empty<Table>()), create);

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(item => item.ToString())));
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal("app`data", table.Schema);
        Assert.Equal("widget`items", table.Name);
        Assert.Collection(
            table.Columns,
            id =>
            {
                Assert.Equal("int", id.SqlType);
                Assert.True(id.IsIdentity);
                Assert.True(id.IsPrimaryKey);
                Assert.False(id.IsNullable);
            },
            amount => Assert.Equal("decimal(18,4)", amount.SqlType),
            document =>
            {
                Assert.Equal("json", document.SqlType);
                Assert.True(document.IsNullable);
            },
            externalId =>
            {
                Assert.Equal("char(36)", externalId.SqlType);
                Assert.False(externalId.IsNullable);
            });

        var query = new MySqlQueryAnalyzer().Analyze(
            result.Schema,
            "SELECT `external_id` FROM " + qualified);

        Assert.False(query.HasErrors, string.Join("\n", query.Diagnostics.Select(item => item.ToString())));
        Assert.Equal("Guid", Assert.Single(query.Columns).ClrType);
    }

    [Fact]
    public void AppliesRepresentativeFlywayMySqlDdlAndPreservesColumnMetadata()
    {
        const string sql = @"
            USE `tenant``one`;
            CREATE TABLE IF NOT EXISTS `users` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `name` VARCHAR(40) NOT NULL DEFAULT 'new; user',
                `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                `payload` JSON NULL,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_users_name` (`name`),
                CONSTRAINT `fk_users_group` FOREIGN KEY (`id`) REFERENCES `groups` (`id`)
            ) ENGINE=InnoDB;
            ALTER TABLE `users` ADD COLUMN `email` VARCHAR(120) NULL AFTER `name`;
            ALTER TABLE `users` MODIFY COLUMN `email` VARCHAR(180) NOT NULL;
            ALTER TABLE `users` CHANGE COLUMN `name` `display_name` VARCHAR(80) NOT NULL DEFAULT 'guest';
            ALTER TABLE `users` ALTER COLUMN `display_name` SET DEFAULT 'member';
            ALTER TABLE `users` RENAME COLUMN `email` TO `contact;email`;
            RENAME TABLE `users` TO `accounts`;
            DROP TABLE IF EXISTS `old_users`;";

        var result = MySqlMigrationAnalyzer.Analyze(new DatabaseSchema(Array.Empty<Table>()), sql);

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(item => item.ToString())));
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal("tenant`one", table.Schema);
        Assert.Equal("accounts", table.Name);
        Assert.Equal(new[] { "id", "display_name", "contact;email", "created_at", "payload" },
            table.Columns.Select(item => item.Name).ToArray());
        Assert.True(table.Columns[0].IsIdentity);
        Assert.True(table.Columns[0].IsPrimaryKey);
        Assert.False(table.Columns[0].IsNullable);
        Assert.Equal("varchar(80)", table.Columns[1].SqlType);
        Assert.Equal("'member'", table.Columns[1].DefaultExpression);
        Assert.Equal("varchar(180)", table.Columns[2].SqlType);
        Assert.False(table.Columns[2].IsNullable);
        Assert.Equal("datetime(6)", table.Columns[3].SqlType);
        Assert.Equal("CURRENT_TIMESTAMP(6)", table.Columns[3].DefaultExpression);
    }

    [Theory]
    [InlineData("CREATE VIEW active_users AS SELECT id FROM users;")]
    [InlineData("CREATE TABLE users AS SELECT id FROM source_users;")]
    [InlineData("ALTER TABLE users ADD GENERATED COLUMN slug VARCHAR(40);")]
    [InlineData("ALTER TABLE users RENAME INDEX old_index TO new_index;")]
    public void RejectsUnsupportedSchemaChangingSyntaxWithDiagnostics(string sql)
    {
        var initial = new DatabaseSchema(new[]
        {
            new Table("users", new[] { new Column("id", "int") }),
        });

        var result = MySqlMigrationAnalyzer.Analyze(initial, sql);

        Assert.True(result.HasErrors);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Single(result.Schema.Tables);
        Assert.Single(result.Schema.Tables[0].Columns);
        Assert.Contains(result.Diagnostics, item => item.Code == "DDL300" || item.Code == "DDL101");
    }

    [Fact]
    public void DoesNotCommitEarlierActionsWhenOneAlterActionIsInvalid()
    {
        var initial = new DatabaseSchema(new[]
        {
            new Table("users", new[] { new Column("id", "int") }),
        });

        var result = MySqlMigrationAnalyzer.Analyze(
            initial,
            "ALTER TABLE users ADD COLUMN name varchar(20), DROP COLUMN missing;");

        Assert.True(result.HasErrors);
        Assert.Single(result.Schema.Tables[0].Columns);
    }
}

using System;
using System.Linq;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.MySql;
using Xunit;

namespace CobaltumOrm.MySql.Tests;

public sealed class MySqlDryRunSchemaTests
{
    [Fact]
    public void ReconstructsEverySchemaChangeGeneratedByTheAdapter()
    {
        var adapter = new MySqlMigrationAdapter();
        var schema = adapter.BuildSchema(new[]
        {
            new MigrationCommand("CREATE TABLE `app`.`old_widgets` (`id` int NOT NULL);"),
            new MigrationCommand("DROP TABLE `app`.`old_widgets`;"),
            new MigrationCommand(
                "CREATE TABLE `app`.`widgets` (" +
                "`id` int AUTO_INCREMENT NOT NULL PRIMARY KEY, " +
                "`name` varchar(40) NOT NULL, " +
                "`obsolete` text NULL);"),
            new MigrationCommand("ALTER TABLE `app`.`widgets` ADD COLUMN `created` datetime NOT NULL;"),
            new MigrationCommand("ALTER TABLE `app`.`widgets` MODIFY COLUMN `name` varchar(80) NULL;"),
            new MigrationCommand("RENAME TABLE `app`.`widgets` TO `app`.`accounts`;"),
            new MigrationCommand("ALTER TABLE `app`.`accounts` RENAME COLUMN `name` TO `display_name`;"),
            new MigrationCommand("ALTER TABLE `app`.`accounts` DROP COLUMN `obsolete`;"),
        });

        var table = Assert.Single(schema.Tables);
        Assert.Equal("app", table.SchemaName);
        Assert.Equal("accounts", table.Name);
        Assert.Collection(
            table.Columns,
            id =>
            {
                Assert.Equal("id", id.Name);
                Assert.Equal("int", id.SqlType);
                Assert.True(id.IsIdentity);
                Assert.True(id.IsPrimaryKey);
                Assert.False(id.IsNullable);
            },
            name =>
            {
                Assert.Equal("display_name", name.Name);
                Assert.Equal("varchar(80)", name.SqlType);
                Assert.True(name.IsNullable);
            },
            created =>
            {
                Assert.Equal("created", created.Name);
                Assert.Equal("datetime", created.SqlType);
                Assert.False(created.IsNullable);
            });
    }

    [Fact]
    public void SupportsFlywayCreateAlterDropAndRenameForms()
    {
        const string sql = @"
            -- Flyway comments and semicolons in strings are allowed.
            CREATE TABLE IF NOT EXISTS `tenant``one`.`users` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `name` VARCHAR(40) NOT NULL,
                PRIMARY KEY (`id`)
            ) ENGINE=InnoDB;
            ALTER TABLE `tenant``one`.`users` ADD COLUMN `email` VARCHAR(120) NULL;
            ALTER TABLE `tenant``one`.`users` MODIFY COLUMN `email` VARCHAR(180) NOT NULL;
            ALTER TABLE `tenant``one`.`users` CHANGE COLUMN `name` `display_name` VARCHAR(80) NOT NULL;
            ALTER TABLE `tenant``one`.`users` RENAME COLUMN `email` TO `contact;email`;
            RENAME TABLE `tenant``one`.`users` TO `tenant``one`.`accounts`;
            DROP TABLE IF EXISTS `tenant``one`.`old_users`;";

        var schema = new MySqlMigrationAdapter().BuildSchema(new[] { new MigrationCommand(sql) });
        var table = Assert.Single(schema.Tables);
        Assert.Equal("tenant`one", table.SchemaName);
        Assert.Equal("accounts", table.Name);
        Assert.Equal(
            new[] { "id", "display_name", "contact;email" },
            table.Columns.Select(column => column.Name).ToArray());
        Assert.Equal("varchar(80)", table.Columns[1].SqlType);
        Assert.False(table.Columns[1].IsNullable);
        Assert.True(table.Columns[0].IsPrimaryKey);
        Assert.True(table.Columns[0].IsIdentity);
    }

    [Fact]
    public void PreservesDefaultsAndSchemaNeutralSql()
    {
        var schema = new MySqlMigrationAdapter().BuildSchema(new[]
        {
            new MigrationCommand(
                "CREATE TABLE events (" +
                "id int NOT NULL DEFAULT 1, " +
                "created_at datetime DEFAULT CURRENT_TIMESTAMP, " +
                "message text DEFAULT 'contains ; and -- text');"),
            new MigrationCommand("INSERT INTO events (id) VALUES (2); UPDATE events SET id = 3; SELECT * FROM events;"),
            new MigrationCommand("ALTER TABLE events ALTER COLUMN id SET DEFAULT 5;"),
            new MigrationCommand("ALTER TABLE events ALTER COLUMN created_at DROP DEFAULT;"),
        });

        var table = Assert.Single(schema.Tables);
        Assert.Equal("5", table.Columns[0].DefaultExpression);
        Assert.Null(table.Columns[1].DefaultExpression);
        Assert.Equal("'contains ; and -- text'", table.Columns[2].DefaultExpression);
    }

    [Fact]
    public void AcceptsLowercaseAlterSyntaxAndColumnPlacement()
    {
        var schema = new MySqlMigrationAdapter().BuildSchema(new[]
        {
            new MigrationCommand("create table users (id int not null, name varchar(20));"),
            new MigrationCommand("alter table users add column first_value int first, add column last_value int after id;"),
            new MigrationCommand("alter table users modify column first_value bigint after name;"),
        });

        var table = Assert.Single(schema.Tables);
        Assert.Equal(
            new[] { "id", "last_value", "name", "first_value" },
            table.Columns.Select(column => column.Name).ToArray());
        Assert.Equal("bigint", table.Columns[3].SqlType);
    }

    [Fact]
    public void UseSetsTheDatabaseForUnqualifiedFlywayStatements()
    {
        var schema = new MySqlMigrationAdapter().BuildSchema(new[]
        {
            new MigrationCommand("USE `tenant`; CREATE TABLE users (id int NOT NULL);"),
        });

        var table = Assert.Single(schema.Tables);
        Assert.Equal("tenant", table.SchemaName);
        Assert.Equal("users", table.Name);
    }

    [Theory]
    [InlineData("CREATE VIEW active_users AS SELECT id FROM users;")]
    [InlineData("CREATE INDEX ix_users_name ON users (name);")]
    [InlineData("ALTER TABLE users ADD GENERATED COLUMN slug VARCHAR(40);")]
    [InlineData("DROP VIEW active_users;")]
    [InlineData("CREATE TABLE users AS SELECT id FROM source_users;")]
    public void RejectsUnsupportedSchemaChangingSql(string sql)
    {
        var exception = Assert.Throws<MigrationValidationException>(
            () => new MySqlMigrationAdapter().BuildSchema(new[] { new MigrationCommand(sql) }));

        Assert.Contains("could not determine the final schema", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsMalformedOrUnknownColumnDefinitionsInsteadOfReturningPartialSchema()
    {
        var exception = Assert.Throws<MigrationValidationException>(
            () => new MySqlMigrationAdapter().BuildSchema(new[]
            {
                new MigrationCommand("CREATE TABLE users (id mystery_type, name varchar(20));"),
            }));

        Assert.Contains("Unsupported MySQL column type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNullCommands()
    {
        var commands = new MigrationCommand[] { null! };
        Assert.Throws<MigrationValidationException>(() => new MySqlMigrationAdapter().BuildSchema(commands));
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.MySql;
using Xunit;

namespace CobaltumOrm.MySql.Tests;

public sealed class MySqlMigrationAdapterTests
{
    [Fact]
    public async Task CreateTableUsesBackticksQualificationAndMySql8Types()
    {
        var connection = new RecordingDbConnection();
        await new MigrationRunner(new MySqlMigrationAdapter()).MigrateUpAsync(
            connection,
            new[] { MigrationInfo.Create<CreateAllTypesMigration>(100, "Create all MySQL types") });

        var command = Assert.Single(
            connection.Commands,
            item => item.CommandText.StartsWith("CREATE TABLE `app``data`.", StringComparison.Ordinal));

        Assert.Equal(
            "CREATE TABLE `app``data`.`widget``items` (" +
            "`id` int AUTO_INCREMENT NOT NULL PRIMARY KEY, " +
            "`small_value` smallint, " +
            "`large_value` bigint, " +
            "`enabled` tinyint(1) NOT NULL, " +
            "`amount` decimal(18,4), " +
            "`unlimited_amount` decimal, " +
            "`single_value` float, " +
            "`double_value` double, " +
            "`label` text, " +
            "`code` varchar(32), " +
            "`notes` text, " +
            "`day` date, " +
            "`local_time` datetime, " +
            "`instant` datetime, " +
            "`clock` time, " +
            "`external_id` char(36), " +
            "`payload` longblob, " +
            "`document` json, " +
            "`indexed_document` json);",
            command.CommandText);
    }

    [Fact]
    public async Task AlterDropRenameAndRawSqlUseMySqlSyntaxInOperationOrder()
    {
        var connection = new RecordingDbConnection();
        await new MigrationRunner(new MySqlMigrationAdapter()).MigrateUpAsync(
            connection,
            new[] { MigrationInfo.Create<ChangeWidgetsMigration>(200, "Change widgets") });

        var commands = connection.Commands
            .Where(item => !item.CommandText.StartsWith("CREATE TABLE IF NOT EXISTS", StringComparison.Ordinal))
            .Where(item => !item.CommandText.StartsWith("SELECT `version`", StringComparison.Ordinal))
            .Where(item => !item.CommandText.StartsWith("INSERT INTO", StringComparison.Ordinal))
            .Select(item => item.CommandText)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "ALTER TABLE `app`.`widgets` ADD COLUMN `nickname` varchar(50);",
                "ALTER TABLE `app`.`widgets` MODIFY COLUMN `created_utc` datetime NOT NULL;",
                "ALTER TABLE `app`.`widgets` MODIFY COLUMN `legacy_code` text NULL;",
                "RENAME TABLE `app`.`widgets` TO `app`.`accounts`;",
                "ALTER TABLE `app`.`accounts` RENAME COLUMN `nickname` TO `display_name`;",
                "ALTER TABLE `app`.`accounts` DROP COLUMN `obsolete`;",
                "DROP TABLE `app`.`old_widgets`;",
                "UPDATE `app`.`accounts` SET `display_name` = 'unknown' WHERE `display_name` IS NULL",
            },
            commands);
    }

    [Fact]
    public void HistoryCommandsAreQualifiedAndParameterized()
    {
        var adapter = new MySqlMigrationAdapter();
        IMigrationDatabaseAdapter boundary = adapter;
        var appliedUtc = new DateTimeOffset(2026, 8, 9, 12, 34, 56, TimeSpan.FromHours(9));

        var ensure = boundary.CreateEnsureHistoryTableCommand("meta`data", "history`table");
        var read = boundary.CreateReadHistoryCommand("meta`data", "history`table");
        var insert = boundary.CreateInsertHistoryCommand(
            "meta`data",
            "history`table",
            42,
            "Create accounts",
            appliedUtc);
        var delete = boundary.CreateDeleteHistoryCommand("meta`data", "history`table", 42);
        var exists = adapter.CreateHistoryTableExistsCommand("meta`data", "history`table");

        Assert.Equal(
            "CREATE TABLE IF NOT EXISTS `meta``data`.`history``table` " +
            "(`version` bigint NOT NULL PRIMARY KEY, `description` text NOT NULL, " +
            "`applied_utc` datetime(6) NOT NULL);",
            ensure.CommandText);
        Assert.Equal(
            "SELECT `version` FROM `meta``data`.`history``table` ORDER BY `version`;",
            read.CommandText);
        Assert.Equal(
            "INSERT INTO `meta``data`.`history``table` (`version`, `description`, `applied_utc`) " +
            "VALUES (@version, @description, @applied_utc);",
            insert.CommandText);
        Assert.Equal(42L, insert.Parameters[0].Value);
        Assert.Equal("Create accounts", insert.Parameters[1].Value);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(insert.Parameters[2].Value).Offset);
        Assert.Equal(
            "DELETE FROM `meta``data`.`history``table` WHERE `version` = @version;",
            delete.CommandText);
        Assert.Equal(42L, delete.Parameters[0].Value);
        Assert.Contains("INFORMATION_SCHEMA.TABLES", exists.CommandText, StringComparison.Ordinal);
        Assert.Contains("DATABASE()", exists.CommandText, StringComparison.Ordinal);
        Assert.Equal("meta`data", exists.Parameters[0].Value);
        Assert.Equal("history`table", exists.Parameters[1].Value);
    }

    [Fact]
    public void QuoteIdentifierEscapesBackticksAndDoesNotSplitDots()
    {
        var adapter = new MySqlMigrationAdapter();

        Assert.Equal("`tenant``one.table`", adapter.QuoteIdentifier("tenant`one.table"));
        Assert.Throws<ArgumentException>(() => adapter.QuoteIdentifier(" "));
        Assert.Throws<ArgumentException>(() => adapter.QuoteIdentifier("bad\0name"));

        var command = adapter.CreateReadHistoryCommand("tenant.one", "history.table");
        Assert.Contains("`tenant.one`.`history.table`", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlterColumnRequiresATypeAndNeverEmitsPostgreSqlAlterSyntax()
    {
        var adapter = new MySqlMigrationAdapter();
        var connection = new RecordingDbConnection();

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => new MigrationRunner(adapter).MigrateUpAsync(
                connection,
                new[] { MigrationInfo.Create<NullabilityOnlyMigration>(300, "Nullability only") }));

        Assert.Contains("requires a complete column type", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            connection.Commands,
            command => command.CommandText.Contains("ALTER COLUMN", StringComparison.Ordinal));
        Assert.DoesNotContain(
            connection.Commands,
            command => command.CommandText.Contains("SET NOT NULL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypeOnlyAlterColumnIsRejectedBeforeAnyModifyCommandExecutes()
    {
        var connection = new RecordingDbConnection();

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => new MigrationRunner(new MySqlMigrationAdapter()).MigrateUpAsync(
                connection,
                new[] { MigrationInfo.Create<TypeOnlyAlterColumnMigration>(350, "Type only") }));

        Assert.Contains("requires explicit nullability", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MODIFY COLUMN", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            connection.Commands,
            command => command.CommandText.Contains("MODIFY COLUMN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HistoryParametersBindThroughTheRunnerBoundary()
    {
        var connection = new RecordingDbConnection();
        await new MigrationRunner(
                new MySqlMigrationAdapter(),
                new MigrationRunnerOptions("history`table", "meta`data"))
            .MigrateUpAsync(
                connection,
                new[] { MigrationInfo.Create<HistoryParameterMigration>(400, "Record parameters") });

        var insert = Assert.Single(
            connection.Commands,
            command => command.CommandText.StartsWith("INSERT INTO", StringComparison.Ordinal));
        Assert.Equal(400L, insert.Parameters["version"]);
        Assert.Equal("Record parameters", insert.Parameters["description"]);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(insert.Parameters["applied_utc"]).Offset);
    }

    [Fact]
    public async Task DryRunUsesReadOnlyHistoryCheckAndReconstructsTheFinalSchema()
    {
        var connection = new RecordingDbConnection { HistoryTableExists = false };
        var dryRun = await new MigrationRunner(new MySqlMigrationAdapter()).DryRunUpAsync(
            connection,
            new[] { MigrationInfo.Create<DryRunSchemaMigration>(700, "Dry run schema") });

        Assert.Single(dryRun.Entries);
        Assert.Single(dryRun.FinalSchema.Tables);
        Assert.Equal("dry_run_values", dryRun.FinalSchema.Tables[0].Name);
        Assert.Contains(
            connection.Commands,
            command => command.CommandText.StartsWith("SELECT EXISTS", StringComparison.Ordinal));
        Assert.DoesNotContain(
            connection.Commands,
            command => command.CommandText.StartsWith("CREATE TABLE IF NOT EXISTS", StringComparison.Ordinal));
        Assert.DoesNotContain(
            connection.Commands,
            command => command.CommandText.StartsWith("INSERT INTO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidationRejectsMissingTypesAndInvalidIdentityTypes()
    {
        var missingType = await Assert.ThrowsAsync<MigrationValidationException>(
            () => new MigrationRunner(new MySqlMigrationAdapter()).MigrateUpAsync(
                new RecordingDbConnection(),
                new[] { MigrationInfo.Create<MissingTypeMigration>(500, "Missing type") }));
        Assert.Contains("must declare a type", missingType.Message, StringComparison.Ordinal);

        var invalidIdentity = await Assert.ThrowsAsync<MigrationValidationException>(
            () => new MigrationRunner(new MySqlMigrationAdapter()).MigrateUpAsync(
                new RecordingDbConnection(),
                new[] { MigrationInfo.Create<InvalidIdentityMigration>(600, "Invalid identity") }));
        Assert.Contains("AUTO_INCREMENT", invalidIdentity.Message, StringComparison.Ordinal);
    }
}

[Migration(100, "Create all MySQL types")]
public sealed class CreateAllTypesMigration : Migration
{
    public override void Up()
    {
        Create.Table("widget`items")
            .InSchema("app`data")
            .WithColumn("id").AsInt32().Nullable().PrimaryKey().Identity()
            .WithColumn("small_value").AsInt16()
            .WithColumn("large_value").AsInt64()
            .WithColumn("enabled").AsBoolean().NotNullable()
            .WithColumn("amount").AsDecimal(18, 4)
            .WithColumn("unlimited_amount").AsDecimal()
            .WithColumn("single_value").AsFloat()
            .WithColumn("double_value").AsDouble()
            .WithColumn("label").AsString()
            .WithColumn("code").AsString(32)
            .WithColumn("notes").AsText()
            .WithColumn("day").AsDate()
            .WithColumn("local_time").AsDateTime()
            .WithColumn("instant").AsDateTimeOffset()
            .WithColumn("clock").AsTime()
            .WithColumn("external_id").AsGuid()
            .WithColumn("payload").AsBinary()
            .WithColumn("document").AsJson()
            .WithColumn("indexed_document").AsJsonb();
    }

    public override void Down()
    {
        Delete.Table("widget`items").InSchema("app`data");
    }
}

[Migration(200, "Change widgets")]
public sealed class ChangeWidgetsMigration : Migration
{
    public override void Up()
    {
        Alter.Table("widgets")
            .InSchema("app")
            .AddColumn("nickname").AsString(50).Nullable()
            .AlterColumn("created_utc").AsDateTimeOffset().NotNullable()
            .AlterColumn("legacy_code").AsText().Nullable();
        Rename.Table("widgets").InSchema("app").To("accounts");
        Rename.Column("nickname").OnTable("accounts").InSchema("app").To("display_name");
        Delete.Column("obsolete").FromTable("accounts").InSchema("app");
        Delete.Table("old_widgets").InSchema("app");
        Execute.Sql("UPDATE `app`.`accounts` SET `display_name` = 'unknown' WHERE `display_name` IS NULL");
    }

    public override void Down()
    {
        Execute.Sql("SELECT 'change widgets down'");
    }
}

[Migration(300, "Nullability only")]
public sealed class NullabilityOnlyMigration : Migration
{
    public override void Up()
    {
        Alter.Table("widgets").InSchema("app").AlterColumn("name").NotNullable();
    }

    public override void Down()
    {
        Execute.Sql("SELECT 1");
    }
}

[Migration(350, "Type only")]
public sealed class TypeOnlyAlterColumnMigration : Migration
{
    public override void Up()
    {
        Alter.Table("widgets").InSchema("app").AlterColumn("name").AsString(80);
    }

    public override void Down()
    {
        Execute.Sql("SELECT 1");
    }
}

[Migration(400, "Record parameters")]
public sealed class HistoryParameterMigration : Migration
{
    public override void Up()
    {
        Execute.Sql("SELECT 1");
    }

    public override void Down()
    {
        Execute.Sql("SELECT 1");
    }
}

[Migration(500, "Missing type")]
public sealed class MissingTypeMigration : Migration
{
    public override void Up()
    {
        Create.Table("missing_type").WithColumn("value");
    }

    public override void Down()
    {
        Execute.Sql("SELECT 1");
    }
}

[Migration(600, "Invalid identity")]
public sealed class InvalidIdentityMigration : Migration
{
    public override void Up()
    {
        Create.Table("invalid_identity").WithColumn("value").AsString(20).Identity();
    }

    public override void Down()
    {
        Execute.Sql("SELECT 1");
    }
}

[Migration(700, "Dry run schema")]
public sealed class DryRunSchemaMigration : Migration
{
    public override void Up()
    {
        Create.Table("dry_run_values")
            .WithColumn("id").AsInt64().NotNullable().PrimaryKey();
    }

    public override void Down()
    {
        Delete.Table("dry_run_values");
    }
}

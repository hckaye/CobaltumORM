using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.Sqlite;
using Xunit;

namespace CobaltumOrm.Sqlite.Tests;

public sealed class SqliteAdapterTests
{
    [Fact]
    public void CreateTableUsesSQLiteAffinitiesAndOnlyInt64IdentityGetsAutoincrement()
    {
        var adapter = new SqliteMigrationAdapter();
        var commands = CollectCommands(new AllTypesMigration(), adapter, true);

        var command = Assert.Single(commands);
        Assert.Equal(
            "CREATE TABLE \"widget\"\"items\" (" +
            "\"id\" INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "\"small_value\" INTEGER, " +
            "\"large_value\" INTEGER, " +
            "\"enabled\" INTEGER NOT NULL, " +
            "\"amount\" NUMERIC, " +
            "\"single_value\" REAL, " +
            "\"double_value\" REAL, " +
            "\"label\" TEXT, " +
            "\"code\" TEXT, " +
            "\"notes\" TEXT, " +
            "\"day\" TEXT, " +
            "\"local_time\" TEXT, " +
            "\"instant\" TEXT, " +
            "\"clock\" TEXT, " +
            "\"external_id\" TEXT, " +
            "\"payload\" BLOB, " +
            "\"document\" TEXT, " +
            "\"indexed_document\" BLOB);",
            command.CommandText);
    }

    [Fact]
    public void SupportedAlterOperationsUseSQLiteSyntaxAndAlterColumnIsRejected()
    {
        var adapter = new SqliteMigrationAdapter();
        var commands = CollectCommands(new ChangeWidgetsMigration(), adapter, true);

        Assert.Equal(
            new[]
            {
                "ALTER TABLE \"widgets\" ADD COLUMN \"nickname\" TEXT;",
                "ALTER TABLE \"widgets\" RENAME TO \"accounts\";",
                "ALTER TABLE \"accounts\" RENAME COLUMN \"nickname\" TO \"display_name\";",
                "ALTER TABLE \"accounts\" DROP COLUMN \"obsolete\";",
                "DROP TABLE \"old_widgets\";",
                "UPDATE \"accounts\" SET \"display_name\" = 'unknown' WHERE \"display_name\" IS NULL",
            },
            commands.Select(command => command.CommandText).ToArray());

        var exception = Assert.Throws<NotSupportedException>(() =>
            CollectCommands(new AlterWidgetsMigration(), adapter, true));
        Assert.Contains("ALTER COLUMN", exception.Message, StringComparison.Ordinal);
        Assert.Contains("table rebuild", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityRequiresAnInt64PrimaryKeyAndAddColumnRejectsIdentityAndPrimaryKey()
    {
        var adapter = new SqliteMigrationAdapter();

        Assert.Throws<MigrationValidationException>(() =>
            CollectCommands(new Int32IdentityMigration(), adapter, true));
        Assert.Throws<MigrationValidationException>(() =>
            CollectCommands(new NonPrimaryIdentityMigration(), adapter, true));
        Assert.Throws<NotSupportedException>(() =>
            CollectCommands(new AddedIdentityMigration(), adapter, true));
        Assert.Throws<NotSupportedException>(() =>
            CollectCommands(new AddedPrimaryKeyMigration(), adapter, true));
    }

    [Fact]
    public void HistoryCommandsAreQuotedAndParameterized()
    {
        var adapter = new SqliteMigrationAdapter();
        IMigrationDatabaseAdapter boundary = adapter;
        var appliedUtc = new DateTimeOffset(2026, 8, 9, 12, 34, 56, TimeSpan.FromHours(9));

        var ensure = boundary.CreateEnsureHistoryTableCommand(null, "history\"");
        var read = boundary.CreateReadHistoryCommand(null, "history\"");
        var insert = boundary.CreateInsertHistoryCommand(null, "history\"", 42, "Create accounts", appliedUtc);
        var delete = boundary.CreateDeleteHistoryCommand(null, "history\"", 42);
        var exists = adapter.CreateHistoryTableExistsCommand(null, "history\"");

        Assert.Equal(
            "CREATE TABLE IF NOT EXISTS \"history\"\"\" (\"version\" INTEGER NOT NULL PRIMARY KEY, " +
            "\"description\" TEXT NOT NULL, \"applied_utc\" TEXT NOT NULL);",
            ensure.CommandText);
        Assert.Equal("SELECT \"version\" FROM \"history\"\"\" ORDER BY \"version\";", read.CommandText);
        Assert.Equal(
            "INSERT INTO \"history\"\"\" (\"version\", \"description\", \"applied_utc\") " +
            "VALUES (@version, @description, @applied_utc);",
            insert.CommandText);
        Assert.Equal(42L, insert.Parameters[0].Value);
        Assert.Equal("Create accounts", insert.Parameters[1].Value);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(insert.Parameters[2].Value).Offset);
        Assert.Equal("DELETE FROM \"history\"\"\" WHERE \"version\" = @version;", delete.CommandText);
        Assert.Equal(42L, delete.Parameters[0].Value);
        Assert.Contains("sqlite_master", adapter.CreateHistoryTableExistsCommand(null, "history").CommandText,
            StringComparison.Ordinal);
        Assert.Equal("history\"", exists.Parameters[0].Value);
        Assert.Equal("table_name", exists.Parameters[0].Name);
    }

    [Fact]
    public void NonEmptySchemasAreRejectedForOperationsAndHistory()
    {
        var adapter = new SqliteMigrationAdapter();
        Assert.Throws<NotSupportedException>(() =>
            CollectCommands(new SchemaMigration(), adapter, true));
        Assert.Throws<NotSupportedException>(() =>
            adapter.CreateEnsureHistoryTableCommand("main", "history"));
        Assert.Throws<NotSupportedException>(() =>
            adapter.CreateReadHistoryCommand("main", "history"));
        Assert.Throws<NotSupportedException>(() =>
            adapter.CreateHistoryTableExistsCommand("main", "history"));
    }

    [Fact]
    public void DryRunReconstructsGeneratedAndFlywayTableDdl()
    {
        var adapter = new SqliteMigrationAdapter();
        var schema = adapter.BuildSchema(new[]
        {
            new MigrationCommand(
                "-- Flyway V1\n" +
                "CREATE TABLE \"audit\" (" +
                "\"id\" INTEGER PRIMARY KEY AUTOINCREMENT, " +
                "\"message\" TEXT NOT NULL DEFAULT 'first, value', " +
                "CONSTRAINT \"audit_pk\" UNIQUE (\"message\"));"),
            new MigrationCommand("INSERT INTO \"audit\" (\"message\") VALUES ('not DDL;');"),
            new MigrationCommand("ALTER TABLE \"audit\" ADD COLUMN \"created_utc\" TEXT DEFAULT CURRENT_TIMESTAMP;"),
            new MigrationCommand("ALTER TABLE \"audit\" RENAME COLUMN \"message\" TO \"event_message\";"),
            new MigrationCommand("ALTER TABLE \"audit\" DROP COLUMN \"created_utc\";"),
        });

        var table = Assert.Single(schema.Tables);
        Assert.Null(table.SchemaName);
        Assert.Equal("audit", table.Name);
        Assert.Equal(new[] { "id", "event_message" }, table.Columns.Select(column => column.Name));
        Assert.True(table.Columns[0].IsIdentity);
        Assert.True(table.Columns[0].IsPrimaryKey);
        Assert.False(table.Columns[0].IsNullable);
        Assert.Equal("'first, value'", table.Columns[1].DefaultExpression);
        Assert.False(table.Columns[1].IsNullable);
    }

    [Fact]
    public void DryRunRejectsSchemaChangesItCannotRepresent()
    {
        var adapter = new SqliteMigrationAdapter();

        Assert.Throws<MigrationValidationException>(() => adapter.BuildSchema(new[]
        {
            new MigrationCommand("CREATE TABLE \"items\" (\"id\" INTEGER); CREATE INDEX \"ix_items\" ON \"items\" (\"id\");"),
        }));
        Assert.Throws<MigrationValidationException>(() => adapter.BuildSchema(new[]
        {
            new MigrationCommand("CREATE VIEW \"item_view\" AS SELECT 1;"),
        }));
        Assert.Throws<NotSupportedException>(() => adapter.BuildSchema(new[]
        {
            new MigrationCommand("CREATE TABLE \"main\".\"items\" (\"id\" INTEGER);"),
        }));
    }

    private static IReadOnlyList<MigrationCommand> CollectCommands(
        Migration migration,
        SqliteMigrationAdapter adapter,
        bool up)
    {
        var method = typeof(Migration).GetMethod(
            "CollectOperations",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var operations = (IEnumerable<MigrationOperation>)method!.Invoke(migration, new object[] { up })!;
        return operations.SelectMany(adapter.GenerateCommands).ToArray();
    }
}

[Migration(1, "Create all SQLite types")]
public sealed class AllTypesMigration : Migration
{
    public override void Up()
    {
        Create.Table("widget\"items")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("small_value").AsInt16()
            .WithColumn("large_value").AsInt64()
            .WithColumn("enabled").AsBoolean().NotNullable()
            .WithColumn("amount").AsDecimal(18, 4)
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

    public override void Down() => Delete.Table("widget\"items");
}

[Migration(2, "Change widgets")]
public sealed class ChangeWidgetsMigration : Migration
{
    public override void Up()
    {
        Alter.Table("widgets")
            .AddColumn("nickname").AsString(50)
            .Nullable();
        Rename.Table("widgets").To("accounts");
        Rename.Column("nickname").OnTable("accounts").To("display_name");
        Delete.Column("obsolete").FromTable("accounts");
        Delete.Table("old_widgets");
        Execute.Sql("UPDATE \"accounts\" SET \"display_name\" = 'unknown' WHERE \"display_name\" IS NULL");
    }

    public override void Down()
    {
        Rename.Column("display_name").OnTable("accounts").To("nickname");
        Rename.Table("accounts").To("widgets");
        Alter.Table("widgets").AddColumn("obsolete").AsText();
    }
}

[Migration(3, "Unsupported alteration")]
public sealed class AlterWidgetsMigration : Migration
{
    public override void Up() => Alter.Table("widgets").AlterColumn("name").AsText();
    public override void Down() => Execute.Sql("SELECT 1");
}

[Migration(4, "Int32 identity")]
public sealed class Int32IdentityMigration : Migration
{
    public override void Up() => Create.Table("items").WithColumn("id").AsInt32().PrimaryKey().Identity();
    public override void Down() => Delete.Table("items");
}

[Migration(5, "Non-primary identity")]
public sealed class NonPrimaryIdentityMigration : Migration
{
    public override void Up() => Create.Table("items").WithColumn("id").AsInt64().Identity();
    public override void Down() => Delete.Table("items");
}

[Migration(6, "Added identity")]
public sealed class AddedIdentityMigration : Migration
{
    public override void Up() => Alter.Table("items").AddColumn("id").AsInt64().Identity();
    public override void Down() => Execute.Sql("SELECT 1");
}

[Migration(7, "Added primary key")]
public sealed class AddedPrimaryKeyMigration : Migration
{
    public override void Up() => Alter.Table("items").AddColumn("id").AsInt64().PrimaryKey();
    public override void Down() => Execute.Sql("SELECT 1");
}

[Migration(8, "Schema")]
public sealed class SchemaMigration : Migration
{
    public override void Up() => Create.Table("items").InSchema("main").WithColumn("id").AsInt64();
    public override void Down() => Delete.Table("items");
}

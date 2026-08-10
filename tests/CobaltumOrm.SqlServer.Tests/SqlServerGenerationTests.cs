using System;
using System.Linq;
using System.Threading.Tasks;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.SqlServer;
using Xunit;

namespace CobaltumOrm.SqlServer.Tests;

public sealed class SqlServerGenerationTests
{
    [Fact]
    public async Task CreateTableUsesDboByDefaultAndMapsSqlServerTypes()
    {
        var connection = new SqlServerFakeDbConnection();
        await new MigrationRunner(new SqlServerMigrationAdapter()).MigrateUpAsync(
            connection,
            new[] { MigrationInfo.Create<CreateAllTypesMigration>(100, "Create all SQL Server types") });

        var create = Assert.Single(
            connection.Executions,
            execution => execution.CommandText.StartsWith("CREATE TABLE [dbo].[widget]]items]", StringComparison.Ordinal));
        Assert.Equal(
            "CREATE TABLE [dbo].[widget]]items] (" +
            "[id] int IDENTITY(1,1) NOT NULL PRIMARY KEY, " +
            "[small_value] smallint, " +
            "[large_value] bigint, " +
            "[enabled] bit NOT NULL, " +
            "[amount] decimal(18,4), " +
            "[unlimited_amount] decimal, " +
            "[single_value] real, " +
            "[double_value] float, " +
            "[label] nvarchar(max), " +
            "[code] nvarchar(32), " +
            "[notes] nvarchar(max), " +
            "[day] date, " +
            "[local_time] datetime2, " +
            "[instant] datetimeoffset, " +
            "[clock] time, " +
            "[external_id] uniqueidentifier, " +
            "[payload] varbinary(max), " +
            "[document] nvarchar(max), " +
            "[indexed_document] nvarchar(max));",
            create.CommandText);
    }

    [Fact]
    public async Task AlterDropRenameAndRawSqlGenerateValidSqlServerCommands()
    {
        var connection = new SqlServerFakeDbConnection();
        await new MigrationRunner(new SqlServerMigrationAdapter()).MigrateUpAsync(
            connection,
            new[] { MigrationInfo.Create<ChangeWidgetsMigration>(200, "Change widgets") });

        var commands = connection.Executions
            .Where(execution => execution.TransactionId.HasValue)
            .Where(execution => !execution.CommandText.StartsWith("INSERT INTO", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            new[]
            {
                "ALTER TABLE [app].[widgets] ADD [nickname] nvarchar(50) NULL;",
                "ALTER TABLE [app].[widgets] ALTER COLUMN [created_utc] datetimeoffset NOT NULL;",
                "ALTER TABLE [app].[widgets] ALTER COLUMN [legacy_code] nvarchar(30) NULL;",
                "EXEC sys.sp_rename @objname = @old_name, @newname = @new_name, @objtype = N'OBJECT';",
                "EXEC sys.sp_rename @objname = @old_name, @newname = @new_name, @objtype = N'COLUMN';",
                "ALTER TABLE [app].[accounts] DROP COLUMN [obsolete];",
                "DROP TABLE [app].[old_widgets];",
                "UPDATE [app].[accounts] SET [display_name] = 'unknown' WHERE [display_name] IS NULL",
            },
            commands.Select(execution => execution.CommandText));

        var tableRename = commands[3];
        Assert.Equal("[app].[widgets]", ParameterValue(tableRename, "old_name"));
        Assert.Equal("accounts", ParameterValue(tableRename, "new_name"));
        var columnRename = commands[4];
        Assert.Equal("[app].[accounts].[nickname]", ParameterValue(columnRename, "old_name"));
        Assert.Equal("display_name", ParameterValue(columnRename, "new_name"));
    }

    [Fact]
    public void IdentifierQuotingEscapesClosingBracketsAndTreatsDotsAsLiteralCharacters()
    {
        var adapter = new SqlServerMigrationAdapter();

        Assert.Equal("[a]]b.c]", adapter.QuoteIdentifier("a]b.c"));
        Assert.Throws<ArgumentException>(() => adapter.QuoteIdentifier(""));
        Assert.Throws<ArgumentException>(() => adapter.QuoteIdentifier("name\0value"));
    }

    [Fact]
    public void HistoryCommandsUseDboByDefaultAndParametersForValues()
    {
        var adapter = new SqlServerMigrationAdapter();
        IMigrationDatabaseAdapter boundary = adapter;
        var appliedUtc = new DateTimeOffset(2026, 8, 9, 12, 34, 56, TimeSpan.FromHours(9));

        var ensure = boundary.CreateEnsureHistoryTableCommand(null, "history]");
        var read = boundary.CreateReadHistoryCommand(null, "history]");
        var insert = boundary.CreateInsertHistoryCommand(null, "history]", 42, "Create accounts", appliedUtc);
        var delete = boundary.CreateDeleteHistoryCommand(null, "history]", 42);
        var exists = ((IMigrationDryRunDatabaseAdapter)adapter)
            .CreateHistoryTableExistsCommand(null, "history]");

        Assert.Contains("sys.tables", ensure.CommandText, StringComparison.Ordinal);
        Assert.Contains("[dbo].[history]]]", ensure.CommandText, StringComparison.Ordinal);
        Assert.Equal("SELECT [version] FROM [dbo].[history]]] ORDER BY [version];", read.CommandText);
        Assert.Equal(
            "INSERT INTO [dbo].[history]]] ([version], [description], [applied_utc]) " +
            "VALUES (@version, @description, @applied_utc);",
            insert.CommandText);
        Assert.Equal(42L, insert.Parameters[0].Value);
        Assert.Equal("Create accounts", insert.Parameters[1].Value);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(insert.Parameters[2].Value).Offset);
        Assert.Equal("DELETE FROM [dbo].[history]]] WHERE [version] = @version;", delete.CommandText);
        Assert.Equal(42L, delete.Parameters[0].Value);
        Assert.Contains("sys.schemas", exists.CommandText, StringComparison.Ordinal);
        Assert.Contains("@schema_name", exists.CommandText, StringComparison.Ordinal);
        Assert.Equal("dbo", exists.Parameters[0].Value);
        Assert.Equal("history]", exists.Parameters[1].Value);
    }

    private static object? ParameterValue(SqlServerFakeExecution execution, string name)
    {
        return execution.Parameters[name];
    }
}

[Migration(100, "Create all SQL Server types")]
public sealed class CreateAllTypesMigration : Migration
{
    public override void Up()
    {
        Create.Table("widget]items")
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
        Delete.Table("widget]items");
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
            .AlterColumn("legacy_code").AsString(30).Nullable();
        Rename.Table("widgets").InSchema("app").To("accounts");
        Rename.Column("nickname").OnTable("accounts").InSchema("app").To("display_name");
        Delete.Column("obsolete").FromTable("accounts").InSchema("app");
        Delete.Table("old_widgets").InSchema("app");
        Execute.Sql("UPDATE [app].[accounts] SET [display_name] = 'unknown' WHERE [display_name] IS NULL");
    }

    public override void Down()
    {
        Execute.Sql("SELECT 'change widgets down'");
    }
}

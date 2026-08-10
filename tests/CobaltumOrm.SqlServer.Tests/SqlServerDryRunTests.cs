using System;
using System.Linq;
using System.Threading.Tasks;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.SqlServer;
using Xunit;

namespace CobaltumOrm.SqlServer.Tests;

public sealed class SqlServerDryRunTests
{
    [Fact]
    public void ReconstructsGeneratedTableChangesAndParameterizedRenames()
    {
        var adapter = new SqlServerMigrationAdapter();
        var schema = adapter.BuildSchema(new[]
        {
            new MigrationCommand(
                "CREATE TABLE [dbo].[users] (" +
                "[id] int IDENTITY(1,1) NOT NULL PRIMARY KEY, " +
                "[name] nvarchar(100) NULL CONSTRAINT [df_users_name] DEFAULT (N'new'), " +
                "[created] datetime2 NOT NULL, " +
                "CONSTRAINT [uq_users_name] UNIQUE ([name]));"),
            new MigrationCommand(
                "ALTER TABLE [dbo].[users] ADD [email] nvarchar(max) NULL;"),
            new MigrationCommand(
                "ALTER TABLE [dbo].[users] ALTER COLUMN [name] nvarchar(200) NOT NULL;"),
            new MigrationCommand(
                "EXEC sys.sp_rename @objname = @old_name, @newname = @new_name, @objtype = N'OBJECT';",
                new[]
                {
                    new MigrationCommandParameter("old_name", "[dbo].[users]"),
                    new MigrationCommandParameter("new_name", "accounts"),
                }),
            new MigrationCommand(
                "EXEC sys.sp_rename @objname = @old_name, @newname = @new_name, @objtype = N'COLUMN';",
                new[]
                {
                    new MigrationCommandParameter("old_name", "[dbo].[accounts].[email]"),
                    new MigrationCommandParameter("new_name", "email_address"),
                }),
            new MigrationCommand("ALTER TABLE [dbo].[accounts] DROP COLUMN [created];"),
        });

        var table = Assert.Single(schema.Tables);
        Assert.Equal("dbo", table.SchemaName);
        Assert.Equal("accounts", table.Name);
        Assert.Equal(new[] { "id", "name", "email_address" }, table.Columns.Select(column => column.Name));
        var id = table.Columns[0];
        Assert.Equal("int", id.SqlType);
        Assert.True(id.IsIdentity);
        Assert.True(id.IsPrimaryKey);
        var name = table.Columns[1];
        Assert.Equal("nvarchar(200)", name.SqlType);
        Assert.False(name.IsNullable);
        Assert.Equal("(N'new')", name.DefaultExpression);
    }

    [Fact]
    public void ReconstructsSupportedFlywayTableDdlAndIgnoresDataAndIndexes()
    {
        var adapter = new SqlServerMigrationAdapter();
        var schema = adapter.BuildSchema(new[]
        {
            new MigrationCommand("""
                -- Semicolons in comments and strings are not statement boundaries.
                CREATE TABLE [audit].[events] (
                    [installed_rank] int NOT NULL,
                    [version] nvarchar(50) NULL,
                    [description] nvarchar(200) NOT NULL,
                    [installed_on] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
                    [success] bit NOT NULL,
                    CONSTRAINT [pk_events] PRIMARY KEY CLUSTERED ([installed_rank] ASC)
                        WITH (PAD_INDEX = OFF) ON [PRIMARY]
                );
                INSERT INTO [audit].[events] ([installed_rank], [description])
                    VALUES (1, N'created; still data');
                UPDATE [audit].[events] SET [description] = N'updated; data';
                CREATE UNIQUE INDEX [ix_events_version] ON [audit].[events] ([version]);
                DELETE FROM [audit].[events] WHERE [installed_rank] = -1;
                """),
        });

        var table = Assert.Single(schema.Tables);
        Assert.Equal("audit", table.SchemaName);
        Assert.Equal("events", table.Name);
        Assert.Equal(5, table.Columns.Count);
        Assert.True(table.Columns[0].IsPrimaryKey);
        Assert.Equal("(SYSUTCDATETIME())", table.Columns[3].DefaultExpression);
    }

    [Fact]
    public async Task DryRunUsesSysCatalogCheckAndDoesNotChangeHistory()
    {
        var connection = new SqlServerFakeDbConnection { HistoryTableExists = false };

        var dryRun = await new MigrationRunner(new SqlServerMigrationAdapter()).DryRunUpAsync(
            connection,
            new[] { MigrationInfo.Create<DryRunCreateMigration>(501, "Create dry-run values") });

        var entry = Assert.Single(dryRun.Entries);
        Assert.Equal(MigrationDryRunDirection.Up, entry.Direction);
        Assert.StartsWith("CREATE TABLE [dbo]", Assert.Single(entry.Commands).CommandText, StringComparison.Ordinal);
        var table = Assert.Single(dryRun.FinalSchema.Tables);
        Assert.Equal("dry_run_values", table.Name);
        Assert.Empty(connection.HistoryVersions);
        Assert.Empty(connection.Transactions);
        Assert.Contains(
            connection.Executions,
            execution => execution.CommandText.StartsWith("SELECT CONVERT(bit", StringComparison.Ordinal));
        Assert.DoesNotContain(
            connection.Executions,
            execution => execution.CommandText.StartsWith("CREATE TABLE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("CREATE VIEW [dbo].[user_ids] AS SELECT [id] FROM [dbo].[users];")]
    [InlineData("ALTER TABLE [dbo].[users] SWITCH TO [dbo].[archive];")]
    [InlineData("EXEC dbo.change_schema @name = N'users';")]
    [InlineData("CREATE SCHEMA [new_schema];")]
    public void RejectsUnsupportedSchemaChangingStatements(string sql)
    {
        var adapter = new SqlServerMigrationAdapter();

        var exception = Assert.Throws<MigrationValidationException>(() =>
            adapter.BuildSchema(new[] { new MigrationCommand(sql) }));

        Assert.Contains("cannot determine", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

[Migration(501, "Create dry-run values")]
public sealed class DryRunCreateMigration : Migration
{
    public override void Up()
    {
        Create.Table("dry_run_values")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("value").AsString(100).Nullable();
    }

    public override void Down() => Delete.Table("dry_run_values");
}

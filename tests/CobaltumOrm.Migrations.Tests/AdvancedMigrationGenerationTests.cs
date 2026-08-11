using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using CobaltumOrm.Migrations.MySql;
using CobaltumOrm.Migrations.Oracle;
using CobaltumOrm.Migrations.PostgreSql;
using CobaltumOrm.Migrations.Sqlite;
using CobaltumOrm.Migrations.SqlServer;
using CobaltumOrm.Migrations.Tests.Fakes;
using Xunit;

namespace CobaltumOrm.Migrations.Tests;

public sealed class AdvancedMigrationGenerationTests
{
    [Fact]
    public void PostgreSqlAdapterGeneratesFluentMigratorStyleSchemaOperations()
    {
        var adapter = new PostgreSqlMigrationAdapter();
        var commands = new AdvancedSchemaMigration()
            .CollectOperations(true)
            .SelectMany(adapter.GenerateCommands)
            .ToArray();

        Assert.Contains(commands, command => command.CommandText == "CREATE SCHEMA \"app\";");
        Assert.Contains(commands, command =>
            command.CommandText.Contains("CREATE TABLE IF NOT EXISTS \"app\".\"users\"", StringComparison.Ordinal) &&
            command.CommandText.Contains("\"age\" smallint DEFAULT 18", StringComparison.Ordinal) &&
            command.CommandText.Contains("\"email\" character varying(320) UNIQUE", StringComparison.Ordinal) &&
            command.CommandText.Contains("REFERENCES \"app\".\"roles\" (\"id\") ON DELETE CASCADE", StringComparison.Ordinal));
        Assert.Contains(commands, command =>
            command.CommandText == "CREATE INDEX \"IX_users_name\" ON \"app\".\"users\" (\"name\" ASC);");
        Assert.Contains(commands, command =>
            command.CommandText == "CREATE UNIQUE INDEX \"IX_users_email_age\" ON \"app\".\"users\" (\"email\" ASC, \"age\" DESC);");
        Assert.Contains(commands, command =>
            command.CommandText.Contains("ADD CONSTRAINT \"FK_users_roles_explicit\" FOREIGN KEY", StringComparison.Ordinal));
        Assert.Contains(commands, command =>
            command.CommandText == "ALTER TABLE \"app\".\"users\" ADD CONSTRAINT \"UC_users_email_age\" UNIQUE (\"email\", \"age\");");
        Assert.Contains(commands, command =>
            command.CommandText == "CREATE SEQUENCE \"app\".\"user_numbers\" START WITH 100 INCREMENT BY 5 MINVALUE 100 CACHE 20 CYCLE;");
        Assert.Contains(commands, command =>
            command.CommandText == "COMMENT ON TABLE \"app\".\"users\" IS 'Application users';");
        Assert.Contains(commands, command =>
            command.CommandText == "COMMENT ON COLUMN \"app\".\"users\".\"name\" IS 'Description:Display name" +
                Environment.NewLine + "Format:Plain text';");
    }

    [Fact]
    public void PostgreSqlAdapterParameterizesDataOperationsAndPreservesRawSql()
    {
        var adapter = new PostgreSqlMigrationAdapter();
        var commands = new AdvancedDataMigration()
            .CollectOperations(true)
            .SelectMany(adapter.GenerateCommands)
            .ToArray();

        var insert = Assert.Single(commands, command => command.CommandText.StartsWith("INSERT INTO", StringComparison.Ordinal));
        Assert.Equal(
            "INSERT INTO \"app\".\"users\" (\"name\", \"enabled\") VALUES (@p0, @p1);",
            insert.CommandText);
        Assert.Equal("Ada", insert.Parameters[0].Value);
        Assert.Equal(true, insert.Parameters[1].Value);

        var update = Assert.Single(commands, command => command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal));
        Assert.Equal(
            "UPDATE \"app\".\"users\" SET \"enabled\" = @p0, \"updated_at\" = CURRENT_TIMESTAMP " +
            "WHERE \"name\" = @p1;",
            update.CommandText);

        var delete = Assert.Single(commands, command => command.CommandText.StartsWith("DELETE FROM", StringComparison.Ordinal));
        Assert.Equal(
            "DELETE FROM \"app\".\"users\" WHERE \"updated_at\" = CURRENT_TIMESTAMP;",
            delete.CommandText);
    }

    [Fact]
    public void SqlServerDescriptionsAreAddedOrUpdated()
    {
        var commands = new SqlServerDescriptionMigration()
            .CollectOperations(true)
            .SelectMany(new SqlServerMigrationAdapter().GenerateCommands)
            .Where(command => command.CommandText.Contains("extendedproperty", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(3, commands.Length);
        Assert.All(commands, command =>
        {
            Assert.Contains("sys.sp_updateextendedproperty", command.CommandText, StringComparison.Ordinal);
            Assert.Contains("sys.sp_addextendedproperty", command.CommandText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SetExistingRowsToProducesAddUpdateAndNotNullOperationsInOrder()
    {
        var adapter = new PostgreSqlMigrationAdapter();
        var commands = new PopulateAddedColumnMigration()
            .CollectOperations(true)
            .SelectMany(adapter.GenerateCommands)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "ALTER TABLE \"app\".\"users\" ADD COLUMN \"status\" character varying(20);",
                "UPDATE \"app\".\"users\" SET \"status\" = @p0;",
                "ALTER TABLE \"app\".\"users\" ALTER COLUMN \"status\" TYPE character varying(20);",
                "ALTER TABLE \"app\".\"users\" ALTER COLUMN \"status\" SET NOT NULL;",
            },
            commands.Select(command => command.CommandText));
        Assert.Equal("active", commands[1].Parameters[0].Value);

        var reverseOrderCommands = new PopulateAddedColumnReverseOrderMigration()
            .CollectOperations(true)
            .SelectMany(adapter.GenerateCommands)
            .ToArray();
        Assert.Equal(commands.Select(command => command.CommandText),
            reverseOrderCommands.Select(command => command.CommandText));
    }

    [Fact]
    public void EveryAdapterGeneratesPortableAdvancedColumnAndIndexOperations()
    {
        var operations = new PortableAdvancedMigration().CollectOperations(true);
        var adapters = new IMigrationDatabaseAdapter[]
        {
            new PostgreSqlMigrationAdapter(),
            new MySqlMigrationAdapter(),
            new SqliteMigrationAdapter(),
            new SqlServerMigrationAdapter(),
            new OracleMigrationAdapter(),
        };

        foreach (var adapter in adapters)
        {
            var commands = operations.SelectMany(adapter.GenerateCommands).ToArray();
            Assert.Contains(commands, command =>
                command.CommandText.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("portable_values", StringComparison.Ordinal));
            Assert.Contains(commands, command =>
                command.CommandText.StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("IX_portable_values_code", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EveryAdapterParameterizesPortableDataOperations()
    {
        var operations = new PortableDataMigration().CollectOperations(true);
        var adapters = new IMigrationDatabaseAdapter[]
        {
            new PostgreSqlMigrationAdapter(),
            new MySqlMigrationAdapter(),
            new SqliteMigrationAdapter(),
            new SqlServerMigrationAdapter(),
            new OracleMigrationAdapter(),
        };

        foreach (var adapter in adapters)
        {
            var commands = operations.SelectMany(adapter.GenerateCommands).ToArray();
            Assert.Equal(3, commands.Length);
            Assert.All(commands, command => Assert.False(command.AnalyzeForSchema));
            Assert.Contains(commands, command => command.CommandText.StartsWith("INSERT INTO", StringComparison.Ordinal));
            Assert.Contains(commands, command => command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal));
            Assert.Contains(commands, command => command.CommandText.StartsWith("DELETE FROM", StringComparison.Ordinal));
            Assert.All(commands, command => Assert.NotEmpty(command.Parameters));
            var parameterPrefix = adapter is OracleMigrationAdapter ? ":p0" : "@p0";
            Assert.All(commands, command => Assert.Contains(parameterPrefix, command.CommandText, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void SequencesAreGeneratedOrRejectedAccordingToDatabaseCapability()
    {
        var operation = Assert.Single(new SequenceMigration().CollectOperations(true));

        Assert.Contains("CREATE SEQUENCE", Assert.Single(new PostgreSqlMigrationAdapter().GenerateCommands(operation)).CommandText, StringComparison.Ordinal);
        Assert.Contains("CREATE SEQUENCE", Assert.Single(new SqlServerMigrationAdapter().GenerateCommands(operation)).CommandText, StringComparison.Ordinal);
        Assert.Contains("CREATE SEQUENCE", Assert.Single(new OracleMigrationAdapter().GenerateCommands(operation)).CommandText, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => new MySqlMigrationAdapter().GenerateCommands(operation));
        Assert.Throws<NotSupportedException>(() => new SqliteMigrationAdapter().GenerateCommands(operation));
    }

    [Fact]
    public async Task ExecuteWithConnectionReceivesTheActiveConnectionAndTransaction()
    {
        IDbConnection? callbackConnection = null;
        IDbTransaction? callbackTransaction = null;
        ConnectionCallbackMigration.Callback = (connection, transaction) =>
        {
            callbackConnection = connection;
            callbackTransaction = transaction;
        };

        try
        {
            var connection = new FakeDbConnection();
            var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());
            await runner.MigrateUpAsync(
                connection,
                new[] { MigrationInfo.Create<ConnectionCallbackMigration>(905, "connection callback") });

            Assert.Same(connection, callbackConnection);
            Assert.NotNull(callbackTransaction);
            Assert.True(connection.Transactions.Single().WasCommitted);
        }
        finally
        {
            ConnectionCallbackMigration.Callback = null;
        }
    }

    [Fact]
    public void IfDatabaseSelectsOperationsUsingNamesPredicatesAndDelegates()
    {
        var operations = new ConditionalMigration().CollectOperations(true);
        var postgreSql = operations.SelectMany(new PostgreSqlMigrationAdapter().GenerateCommands).ToArray();
        var mySql = operations.SelectMany(new MySqlMigrationAdapter().GenerateCommands).ToArray();
        var oracle = operations.SelectMany(new OracleMigrationAdapter().GenerateCommands).ToArray();

        Assert.Contains(postgreSql, command => command.CommandText.Contains("postgres_only", StringComparison.Ordinal));
        Assert.DoesNotContain(mySql, command => command.CommandText.Contains("postgres_only", StringComparison.Ordinal));
        Assert.Contains(mySql, command => command.CommandText == "SELECT 'mysql';");
        Assert.Contains(oracle, command => command.CommandText == "SELECT 'oracle' FROM dual;");
    }

    [Fact]
    public void ComputedColumnsUseEachDatabaseSyntax()
    {
        var operation = Assert.Single(new VirtualComputedColumnMigration().CollectOperations(true));
        var postgreSql = Assert.Single(new PostgreSqlMigrationAdapter().GenerateCommands(operation)).CommandText;
        var sqlServer = Assert.Single(new SqlServerMigrationAdapter().GenerateCommands(operation)).CommandText;
        var oracle = Assert.Single(new OracleMigrationAdapter().GenerateCommands(operation)).CommandText;

        Assert.Contains("GENERATED ALWAYS AS (lower(name)) STORED", postgreSql, StringComparison.Ordinal);
        Assert.Contains("[normalized_name] AS (lower(name))", sqlServer, StringComparison.Ordinal);
        Assert.Contains("\"normalized_name\" GENERATED ALWAYS AS (lower(name))", oracle, StringComparison.Ordinal);
    }

    [Fact]
    public void InsertRowsAcceptsDifferentAnonymousObjectShapes()
    {
        var commands = new HeterogeneousRowsMigration()
            .CollectOperations(true)
            .SelectMany(new PostgreSqlMigrationAdapter().GenerateCommands)
            .ToArray();

        Assert.Equal(2, commands.Length);
        Assert.Contains("\"number\"", commands[0].CommandText, StringComparison.Ordinal);
        Assert.Contains("\"text\"", commands[1].CommandText, StringComparison.Ordinal);
    }
}

[Migration(901, "advanced schema")]
public sealed class AdvancedSchemaMigration : Migration
{
    public override void Up()
    {
        Create.Schema("app");
        Create.Table("users")
            .InSchema("app")
            .IfNotExists()
            .WithDescription("Application users")
            .WithColumn("id").AsInt64().Identity().PrimaryKey("PK_users")
            .WithColumn("age").AsByte().WithDefaultValue(18)
            .WithColumn("name").AsAnsiString(200).WithColumnDescription("Display name")
                .WithColumnAdditionalDescription("Format", "Plain text").Indexed("IX_users_name")
            .WithColumn("email").AsString(320).Unique()
            .WithColumn("role_id").AsInt32().ForeignKey("FK_users_roles", "app", "roles", "id").OnDelete(Rule.Cascade);
        Create.Index("IX_users_email_age").OnTable("users").InSchema("app")
            .OnColumn("email").Ascending().OnColumn("age").Descending().Unique();
        Create.ForeignKey("FK_users_roles_explicit")
            .FromTable("users").InSchema("app").ForeignColumn("role_id")
            .ToTable("roles").InSchema("app").PrimaryColumn("id").OnDelete(Rule.Cascade);
        Create.UniqueConstraint("UC_users_email_age").OnTable("users").WithSchema("app").Columns("email", "age");
        Create.Sequence("user_numbers").InSchema("app").StartWith(100).IncrementBy(5).MinValue(100).Cache(20).Cycle();
    }

    public override void Down() { }
}

public sealed class SqlServerDescriptionMigration : Migration
{
    public override void Up()
    {
        Create.Table("described_values")
            .WithDescription("Described values")
            .WithColumn("id").AsInt64().PrimaryKey()
            .WithColumn("value").AsString(100)
                .WithColumnDescription("Stored value")
                .WithColumnAdditionalDescription("Format", "Plain text");
    }

    public override void Down() { }
}

[Migration(902, "advanced data")]
public sealed class AdvancedDataMigration : Migration
{
    public override void Up()
    {
        Insert.IntoTable("users").InSchema("app").Row(new { name = "Ada", enabled = true });
        Update.Table("users").InSchema("app")
            .Set(new Dictionary<string, object?>
            {
                ["enabled"] = false,
                ["updated_at"] = SystemMethods.CurrentDateTime,
            })
            .Where(new { name = "Ada" });
        Delete.FromTable("users").InSchema("app")
            .Where(new Dictionary<string, object?>
            {
                ["updated_at"] = RawSql.Insert("CURRENT_TIMESTAMP"),
            });
    }

    public override void Down() { }
}

[Migration(903, "populate added column")]
public sealed class PopulateAddedColumnMigration : Migration
{
    public override void Up()
    {
        Alter.Table("users").InSchema("app")
            .AddColumn("status").AsString(20).NotNullable().SetExistingRowsTo("active");
    }

    public override void Down() { }
}

public sealed class PopulateAddedColumnReverseOrderMigration : Migration
{
    public override void Up()
    {
        Alter.Table("users").InSchema("app")
            .AddColumn("status").AsString(20).SetExistingRowsTo("active").NotNullable();
    }

    public override void Down() { }
}

[Migration(904, "portable advanced operations")]
public sealed class PortableAdvancedMigration : Migration
{
    public override void Up()
    {
        Create.Table("portable_values")
            .WithColumn("id").AsInt64().Identity().PrimaryKey("PK_portable_values")
            .WithColumn("small_number").AsByte().WithDefaultValue(1)
            .WithColumn("amount").AsCurrency()
            .WithColumn("code").AsFixedLengthAnsiString(8).Indexed("IX_portable_values_code")
            .WithColumn("document").AsXml().Nullable()
            .WithColumn("recorded_at").AsDateTimeOffset(3).WithDefault(SystemMethods.CurrentDateTimeOffset);
    }

    public override void Down() { }
}

public sealed class PortableDataMigration : Migration
{
    public override void Up()
    {
        Insert.IntoTable("portable_values").Row(new { code = "00000001" });
        Update.Table("portable_values").Set(new { code = "00000002" }).Where(new { code = "00000001" });
        Delete.FromTable("portable_values").Where(new { code = "00000002" });
    }

    public override void Down() { }
}

public sealed class SequenceMigration : Migration
{
    public override void Up() => Create.Sequence("portable_sequence").StartWith(10).IncrementBy(2);
    public override void Down() { }
}

public sealed class ConnectionCallbackMigration : Migration
{
    internal static Action<IDbConnection, IDbTransaction>? Callback { get; set; }
    public override void Up() => IfDatabase("Postgres").Execute.WithConnection(
        (connection, transaction) => Callback?.Invoke(connection, transaction));
    public override void Down() { }
}

public sealed class ConditionalMigration : Migration
{
    public override void Up()
    {
        IfDatabase("Postgres").Create.Table("postgres_only").WithColumn("id").AsInt32();
        IfDatabase(database => database.StartsWith("My", StringComparison.Ordinal))
            .Execute.Sql("SELECT 'mysql';");
        IfDatabase("Oracle").Delegate(() => Execute.Sql("SELECT 'oracle' FROM dual;"));
    }

    public override void Down() { }
}

public sealed class VirtualComputedColumnMigration : Migration
{
    public override void Up()
    {
        Create.Table("computed_values")
            .WithColumn("name").AsString(100).NotNullable()
            .WithColumn("normalized_name").AsString(100).Computed("lower(name)");
    }

    public override void Down() { }
}

public sealed class HeterogeneousRowsMigration : Migration
{
#pragma warning disable IL2026
    public override void Up() =>
        Insert.IntoTable("portable_values").Rows(new { number = 1 }, new { text = "two" });
#pragma warning restore IL2026

    public override void Down() { }
}

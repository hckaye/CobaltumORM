using System;
using System.Threading.Tasks;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.SqlServer;
using Xunit;

namespace CobaltumOrm.SqlServer.Tests;

public sealed class SqlServerValidationTests
{
    [Fact]
    public Task RejectsTablesWithoutColumns() =>
        AssertGenerationFailsAsync<EmptyTableMigration>(401, "must declare at least one column");

    [Fact]
    public Task RejectsColumnsWithoutTypes() =>
        AssertGenerationFailsAsync<UnspecifiedColumnMigration>(402, "must declare a type");

    [Fact]
    public Task RejectsCaseInsensitiveDuplicateColumns() =>
        AssertGenerationFailsAsync<DuplicateColumnMigration>(403, "more than once");

    [Fact]
    public Task RejectsCompositeInlinePrimaryKeys() =>
        AssertGenerationFailsAsync<CompositePrimaryKeyMigration>(404, "composite primary key");

    [Fact]
    public Task RejectsIdentityOnNonIntegerColumns() =>
        AssertGenerationFailsAsync<InvalidIdentityMigration>(405, "must use AsInt16");

    [Fact]
    public Task RejectsSqlServerStringLengthsAboveNvarcharLimit() =>
        AssertGenerationFailsAsync<TooLongStringMigration>(406, "cannot exceed SQL Server's nvarchar limit");

    [Fact]
    public Task RejectsSqlServerDecimalPrecisionAboveThirtyEight() =>
        AssertGenerationFailsAsync<TooPreciseDecimalMigration>(407, "cannot exceed SQL Server's maximum");

    [Fact]
    public Task RejectsNullabilityOnlyAlterationsBecauseSqlServerNeedsAType() =>
        AssertGenerationFailsAsync<NullabilityOnlyAlterationMigration>(
            408,
            "requires a type in ALTER COLUMN");

    [Fact]
    public void RejectsEmptyHistoryDescriptions()
    {
        var adapter = new SqlServerMigrationAdapter();

        Assert.Throws<ArgumentException>(() => adapter.CreateInsertHistoryCommand(
            null,
            "history",
            1,
            " ",
            DateTimeOffset.UtcNow));
    }

    private static async Task AssertGenerationFailsAsync<TMigration>(long version, string message)
        where TMigration : Migration, new()
    {
        var exception = await Assert.ThrowsAsync<MigrationValidationException>(() =>
            new MigrationRunner(new SqlServerMigrationAdapter()).MigrateUpAsync(
                new SqlServerFakeDbConnection(),
                new[] { MigrationInfo.Create<TMigration>(version, "Invalid migration") }));

        Assert.Contains(message, exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

[Migration(401)]
public sealed class EmptyTableMigration : Migration
{
    public override void Up() => Create.Table("empty");
    public override void Down() => Delete.Table("empty");
}

[Migration(402)]
public sealed class UnspecifiedColumnMigration : Migration
{
    public override void Up() => Create.Table("missing_type").WithColumn("value");
    public override void Down() => Delete.Table("missing_type");
}

[Migration(403)]
public sealed class DuplicateColumnMigration : Migration
{
    public override void Up()
    {
        Create.Table("duplicate")
            .WithColumn("Value").AsInt32()
            .WithColumn("value").AsInt32();
    }

    public override void Down() => Delete.Table("duplicate");
}

[Migration(404)]
public sealed class CompositePrimaryKeyMigration : Migration
{
    public override void Up()
    {
        Create.Table("composite")
            .WithColumn("first").AsInt32().PrimaryKey()
            .WithColumn("second").AsInt32().PrimaryKey();
    }

    public override void Down() => Delete.Table("composite");
}

[Migration(405)]
public sealed class InvalidIdentityMigration : Migration
{
    public override void Up() => Create.Table("invalid_identity")
        .WithColumn("value").AsString(20).Identity();

    public override void Down() => Delete.Table("invalid_identity");
}

[Migration(406)]
public sealed class TooLongStringMigration : Migration
{
    public override void Up() => Create.Table("too_long")
        .WithColumn("value").AsString(4001);

    public override void Down() => Delete.Table("too_long");
}

[Migration(407)]
public sealed class TooPreciseDecimalMigration : Migration
{
    public override void Up() => Create.Table("too_precise")
        .WithColumn("value").AsDecimal(39, 2);

    public override void Down() => Delete.Table("too_precise");
}

[Migration(408)]
public sealed class NullabilityOnlyAlterationMigration : Migration
{
    public override void Up() => Alter.Table("users")
        .AlterColumn("name")
        .NotNullable();

    public override void Down() => Execute.Sql("SELECT 1");
}

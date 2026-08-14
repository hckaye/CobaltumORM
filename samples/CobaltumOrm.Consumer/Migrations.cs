using CobaltumOrm.Migrations;

namespace CobaltumOrm.Sample;

// <snippet migration-csharp>
[Migration(10, "create users")]
public sealed class CreateUsersMigration : Migration
{
    public override void Up()
    {
        Create.Table("users")
            .InSchema("app")
            .WithColumn("id").AsInt32().Identity().PrimaryKey()
            .WithColumn("email").AsString(240).NotNullable();
    }

    public override void Down()
    {
        Delete.Table("users").InSchema("app");
    }
}
// </snippet>

[Migration(30, "add created at")]
public sealed class AddCreatedAtMigration : Migration
{
    public override void Up()
    {
        Alter.Table("users")
            .InSchema("app")
            .AddColumn("created_at").AsDateTimeOffset().NotNullable();
    }

    public override void Down()
    {
        Delete.Column("created_at").FromTable("users").InSchema("app");
    }
}

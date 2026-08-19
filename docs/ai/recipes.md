# CobaltumORM task recipes

English | [日本語](recipes.ja.md)

The sample project compiles every code block in CI. Select the API from
[quick-reference.md](quick-reference.md) and look up build errors in
[diagnostics.md](diagnostics.md).

The sample schema is `app.users(id int identity primary key, email varchar(240) not null,
display_name varchar(120) null, created_at timestamptz not null)`.

## Create a table

Add a C# migration under the migration project's `Migrations` directory. Give it a version greater
than every existing migration, and write the reverse operation in `Down`.

<!-- snippet: migration-csharp -->
```csharp
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
```

## Change a table with SQL

Add a Flyway-compatible file named `V<version>__<description>.sql` and include it through
`<AdditionalFiles Include="Migrations/V*__*.sql" />`. The build applies it to the schema in version
order; the runtime applies it forward only.

<!-- snippet: migration-flyway -->
```sql
ALTER TABLE app.users ADD COLUMN display_name varchar(120) NULL;
```

## Apply migrations at runtime

`CobaltumMigrationCatalog.All` is generated from the migrations in version order. Pass it to
`MigrationRunner` with the adapter for the provider. This works under trimming and Native AOT
because no assembly scanning is involved.

<!-- snippet: migration-runner -->
```csharp
public static Task MigrateUpAsync(
    DbConnection connection,
    CancellationToken cancellationToken = default) =>
    new MigrationRunner(new PostgreSqlMigrationAdapter())
        .MigrateUpAsync(connection, CobaltumMigrationCatalog.All, cancellationToken);
```

The `cobaltum` CLI runs the same migrations without writing this code:
`cobaltum migrations up --project src/MyApp.Database`.

## Read rows with a generated result type

Pass constant SQL to `Query`. The build parses it, checks every name against the schema, and
generates a `record` whose properties are the selected columns. Values go through `WithParameter`.

<!-- snippet: checked-select -->
```csharp
public static async Task<string?> ReadDisplayNameAsync(
    DbConnection connection,
    int id,
    CancellationToken cancellationToken = default)
{
    var rows = await connection
        .Query("SELECT id, display_name FROM app.users WHERE id = @id")
        .WithParameter("@id", id, DbType.Int32)
        .ReadAsync(cancellationToken);

    return rows[0].DisplayName;
}
```

## Read rows into an existing type

`Query<T>` maps the returned columns onto `T`. The build compares column names, CLR types, and
nullability with the constructor of `T` and reports [COB009](diagnostics.md#cob009) on a mismatch.
`[ResultColumn]` sets an explicit column name when the default matching is not enough.

<!-- snippet: result-type -->
```csharp
public sealed record UserSummary(
    [ResultColumn("id")] int Id,
    [ResultColumn("display_name")] string? DisplayName);
```

<!-- snippet: result-type-query -->
```csharp
public static Task<IReadOnlyList<UserSummary>> ReadSummariesAsync(
    DbConnection connection,
    CancellationToken cancellationToken = default) =>
    connection
        .Query<UserSummary>("SELECT id, display_name FROM app.users ORDER BY id")
        .ReadAsync(cancellationToken);
```

## Reuse the same SQL under a name

Put `[Query(name, sql)]` on a non-generic `partial class` declared at namespace scope. The
attribute may be repeated. Each entry generates a result record, a parameter record, a query
definition, and an async method.

<!-- snippet: named-query -->
```csharp
[Query(
    "ByEmail",
    "SELECT id, display_name FROM app.users WHERE email = @email")]
public static partial class UserDirectory
{
}
```

<!-- snippet: named-query-call -->
```csharp
public static Task<IReadOnlyList<UserDirectory.ByEmailResult>> ReadByEmailAsync(
    DbConnection connection,
    string email,
    CancellationToken cancellationToken = default) =>
    UserDirectory.ByEmailAsync(connection, email, cancellationToken: cancellationToken);
```

Use `[Query<T>(name, sql)]` to map onto an existing result type instead of generating one.

A statement that does not return rows, such as an `INSERT`, `UPDATE`, `DELETE`, or `TRUNCATE`
without `RETURNING`, generates a command instead of a result record. The async method returns the
affected row count. `[Query<T>]` still requires a statement that returns rows.

<!-- snippet: named-command -->
```csharp
[Query(
    "DeleteByEmail",
    "DELETE FROM app.users WHERE email = @email")]
public static partial class UserDirectory
{
}
```

<!-- snippet: named-command-call -->
```csharp
public static Task<int> DeleteByEmailAsync(
    DbConnection connection,
    string email,
    CancellationToken cancellationToken = default) =>
    UserDirectory.DeleteByEmailAsync(connection, email, cancellationToken: cancellationToken);
```

## Pass a value inside interpolated SQL

An interpolation hole is replaced by a `DbParameter`, never by SQL text. Only value positions
accept a hole; `$"SELECT {columns} FROM app.users"` is rejected with
[COB103](diagnostics.md#cob103).

<!-- snippet: interpolated-query -->
```csharp
public static async Task<string?> ReadDisplayNameInterpolatedAsync(
    DbConnection connection,
    int id,
    CancellationToken cancellationToken = default)
{
    var rows = await connection
        .Query($"SELECT id, display_name FROM app.users WHERE id = {id}")
        .ReadAsync(cancellationToken);

    return rows[0].DisplayName;
}
```

## Run INSERT, UPDATE, or DELETE

Write the statement as constant SQL, bind values with `WithParameter`, and call `ExecuteAsync`,
which returns the affected row count. A statement with `RETURNING` uses `ReadAsync` instead and
gets a generated result record. Interpolated data modification is not accepted by `Query`.

<!-- snippet: constant-dml -->
```csharp
public static Task<int> RenameAsync(
    DbConnection connection,
    int id,
    string displayName,
    CancellationToken cancellationToken = default) =>
    connection
        .Query("UPDATE app.users SET display_name = @name WHERE id = @id")
        .WithParameter("@name", displayName, DbType.String)
        .WithParameter("@id", id, DbType.Int32)
        .ExecuteAsync(cancellationToken);
```

## Filter one table without assembling SQL

`Tables.<Table>` exposes the table as a typed query. `Where` and `WhereIf` add parameterized
predicates joined with `AND`, and the result record does not change. `WhereIf` does not invoke its
factory when the condition is false.

<!-- snippet: table-query -->
```csharp
public static Task<IReadOnlyList<AppUsersRow>> ReadFilteredAsync(
    DbConnection connection,
    int id,
    bool filterByEmail,
    string email,
    CancellationToken cancellationToken = default)
{
    var query = Tables.Users
        .Query()
        .Where(Tables.Users.Id.Equal(id))
        .WhereIf(filterByEmail, () => Tables.Users.Email.Equal(email));

    return connection.Query(query, transaction: null, cancellationToken);
}
```

## Run SQL the build cannot check

`NoCheckQuery` accepts SQL whose text is only known at runtime, or syntax the analyzer does not
support. Nothing about the SQL is checked at build time. `ReadAsync` returns `CobaltumRawRow`
values that keep column ordinals and names.

<!-- snippet: no-check-query -->
```csharp
public static Task<IReadOnlyList<CobaltumRawRow>> ReadDynamicAsync(
    DbConnection connection,
    string sql,
    int id,
    CancellationToken cancellationToken = default) =>
    connection
        .NoCheckQuery(sql)
        .WithParameter("@id", id, DbType.Int32)
        .ReadAsync(cancellationToken);
```

`NoCheckQuery<T>` applies the same mapping rules to an existing type. The build checks the shape of
`T` but cannot compare it with the SQL, so a missing, duplicate, or incompatible column throws
while the data reader reads the row. Extra columns are ignored.

<!-- snippet: no-check-query-result -->
```csharp
public static Task<IReadOnlyList<UserSummary>> ReadDynamicSummariesAsync(
    DbConnection connection,
    string sql,
    CancellationToken cancellationToken = default) =>
    connection
        .NoCheckQuery<UserSummary>(sql)
        .ReadAsync(cancellationToken);
```

## Verify the change

The SQL checks run during compilation. Build after every change to a query, a migration, a result
type, or the provider setting, then run the tests.

```console
dotnet build
dotnet test
```

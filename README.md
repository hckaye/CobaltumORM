# CobaltumORM

English | [日本語](README.ja.md)

[![CobaltumOrm](https://img.shields.io/nuget/v/CobaltumOrm.svg?label=CobaltumOrm)](https://www.nuget.org/packages/CobaltumOrm)
[![CobaltumOrm.Migrations](https://img.shields.io/nuget/v/CobaltumOrm.Migrations.svg?label=CobaltumOrm.Migrations)](https://www.nuget.org/packages/CobaltumOrm.Migrations)
[![CobaltumOrm.Migrations.MySql](https://img.shields.io/nuget/v/CobaltumOrm.Migrations.MySql.svg?label=CobaltumOrm.Migrations.MySql)](https://www.nuget.org/packages/CobaltumOrm.Migrations.MySql)
[![CobaltumOrm.Migrations.Oracle](https://img.shields.io/nuget/v/CobaltumOrm.Migrations.Oracle.svg?label=CobaltumOrm.Migrations.Oracle)](https://www.nuget.org/packages/CobaltumOrm.Migrations.Oracle)
[![CobaltumOrm.Migrations.PostgreSql](https://img.shields.io/nuget/v/CobaltumOrm.Migrations.PostgreSql.svg?label=CobaltumOrm.Migrations.PostgreSql)](https://www.nuget.org/packages/CobaltumOrm.Migrations.PostgreSql)
[![CobaltumOrm.Migrations.Sqlite](https://img.shields.io/nuget/v/CobaltumOrm.Migrations.Sqlite.svg?label=CobaltumOrm.Migrations.Sqlite)](https://www.nuget.org/packages/CobaltumOrm.Migrations.Sqlite)
[![CobaltumOrm.Migrations.SqlServer](https://img.shields.io/nuget/v/CobaltumOrm.Migrations.SqlServer.svg?label=CobaltumOrm.Migrations.SqlServer)](https://www.nuget.org/packages/CobaltumOrm.Migrations.SqlServer)
[![CobaltumOrm.SourceGenerator](https://img.shields.io/nuget/v/CobaltumOrm.SourceGenerator.svg?label=CobaltumOrm.SourceGenerator)](https://www.nuget.org/packages/CobaltumOrm.SourceGenerator)
[![CobaltumOrm.Tool](https://img.shields.io/nuget/v/CobaltumOrm.Tool.svg?label=CobaltumOrm.Tool)](https://www.nuget.org/packages/CobaltumOrm.Tool)
[![CobaltumOrm.Templates](https://img.shields.io/nuget/v/CobaltumOrm.Templates.svg?label=CobaltumOrm.Templates)](https://www.nuget.org/packages/CobaltumOrm.Templates)

CobaltumORM is a .NET / C# ORM whose main target is PostgreSQL. Its migration system and
compile-time SQL analysis support PostgreSQL, MySQL, SQLite, SQL Server, and Oracle.

## Table of contents

- [Features](#features)
- [Database providers](#database-providers)
- [Getting started](#getting-started)
- [Project configuration](#project-configuration)
- [Migration inputs](#migration-inputs)
- [Command-line migration management](#command-line-migration-management)
- [Generated table types](#generated-table-types)
- [Named queries](#named-queries)
- [Result types for constant `Query` SQL](#result-types-for-constant-query-sql)
- [Interpolated `Query`](#interpolated-query)
- [Constant `INSERT`, `UPDATE`, and `DELETE`](#constant-insert-update-and-delete)
- [Queries without build-time SQL checking](#queries-without-build-time-sql-checking)
- [Connections and runtime behavior](#connections-and-runtime-behavior)
- [Trimmed and Native AOT publishing](#trimmed-and-native-aot-publishing)
- [Supported environments](#supported-environments)

## Features

CobaltumORM lets applications write SQL explicitly while using type-safe data mapping and migrations. It generates types for query results and reports invalid schema names, table names, column names, and SQL at build time.

- C# migrations and Flyway-compatible SQL build the schema without connecting to a database.
- `Query("...")` and `[Query(...)]` SQL is analyzed at build time. Supported SQL is checked for syntax and references to existing schemas, tables, and columns.
- Each statement that returns rows gets a generated result type based on its column names, C# types, and nullability. This includes `SELECT` and PostgreSQL `INSERT`, `UPDATE`, or `DELETE` with `RETURNING`.
- Renaming or deleting a schema object in a migration makes old `SqlSchema` references and SQL that uses the old name fail to compile.
  - The current checker supports part of the PostgreSQL syntax used for CRUD operations. It cannot check permissions, constraints, triggers, or outcomes that depend on stored data.
- CobaltumORM does not provide EF Core-style change tracking or an equivalent to `SaveChanges`. Queries and commands are executed explicitly.

### Comparison with other .NET ORMs

| ORM | Typical use | Queries and result types |
| --- | --- | --- |
| CobaltumORM | Define SQL with `Query` or `[Query]` | Check SQL against the schema built from migrations and generate a `record` type for each statement that returns rows |
| [EF Core](https://learn.microsoft.com/en-us/ef/core/) | Use `DbContext`, entity models, LINQ, and change tracking. APIs for direct SQL execution are also available | Use LINQ projections or types in the model, including entities, keyless entities, and scalar types |
| [Dapper](https://github.com/DapperLib/Dapper) | Pass SQL and parameters to `DbConnection` extension methods such as `Query<T>` and `Execute` | Map rows to the type supplied to `Query<T>`, or return rows whose columns are resolved at runtime with `Query` |

## Database providers

PostgreSQL is the main target. Migration project creation supports these five provider values:

| Database | `--provider` value | Migration package | ADO.NET driver package | Connection type | Migration adapter |
| --- | --- | --- | --- | --- | --- |
| PostgreSQL | `PostgreSql` | `CobaltumOrm.Migrations.PostgreSql` | `Npgsql` | `NpgsqlConnection` | `PostgreSqlMigrationAdapter` |
| MySQL | `MySql` | `CobaltumOrm.Migrations.MySql` | `MySqlConnector` | `MySqlConnection` | `MySqlMigrationAdapter` |
| SQLite | `Sqlite` | `CobaltumOrm.Migrations.Sqlite` | `Microsoft.Data.Sqlite` | `SqliteConnection` | `SqliteMigrationAdapter` |
| SQL Server | `SqlServer` | `CobaltumOrm.Migrations.SqlServer` | `Microsoft.Data.SqlClient` | `SqlConnection` | `SqlServerMigrationAdapter` |
| Oracle | `Oracle` | `CobaltumOrm.Migrations.Oracle` | `Oracle.ManagedDataAccess.Core` | `OracleConnection` | `OracleMigrationAdapter` |

`cobaltum migrations init` and `dotnet new cobaltum-migrations` default to `PostgreSql`. The generated project contains only the selected migration package, driver package, connection using, and adapter. It sets `CobaltumOrmDatabaseProvider` and makes that property visible to the compiler.

Choose another provider by passing its canonical value:

```console
cobaltum migrations init MyApp.Database --provider PostgreSql
cobaltum migrations init MyApp.Database --provider MySql
cobaltum migrations init MyApp.Database --provider Sqlite
cobaltum migrations init MyApp.Database --provider SqlServer
cobaltum migrations init MyApp.Database --provider Oracle
```

For a manual migration project, select one row from the table and use the matching package references and entry point. The following PostgreSQL group shows the required project settings; replace the provider-specific package and connection lines with the row for the selected database.

```xml
<PropertyGroup>
  <CobaltumOrmDatabaseProvider>PostgreSql</CobaltumOrmDatabaseProvider>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="CobaltumOrm.Migrations.PostgreSql" Version="postgresql-adapter-version" />
  <PackageReference Include="Npgsql" Version="npgsql-version" />
  <CompilerVisibleProperty Include="CobaltumOrmDatabaseProvider" />
</ItemGroup>
```

The generated entry point uses the connection and adapter from the same row. For example, the PostgreSQL form is:

```csharp
using CobaltumOrm.Migrations.PostgreSql;
using Npgsql;

public override DbConnection CreateConnection(MigrationProjectContext context) =>
    new NpgsqlConnection(context.ConnectionString);

public override IMigrationDatabaseAdapter CreateAdapter() =>
    new PostgreSqlMigrationAdapter();
```

Development resources for MySQL, SQLite, SQL Server, and Oracle are limited. They are not primary targets. If a problem is found, file an Issue and contribute a fix, a test, or documentation.

The current verification scope is limited to the following results:

- Existing PostgreSQL E2E tests.
- Real in-memory SQLite tests.
- MySQL driver-backed tests without a MySQL server.
- SQL Server and Oracle SQL/unit tests without real-server E2E in this change.

Provider-specific limits include the following:

- SQLite `AlterColumn` requires a table rebuild and is unsupported by the provider-neutral operation.
- MySQL compile-time and runtime `AlterColumn` requires a complete type and nullability definition.
- SQL Server nullability-only `AlterColumn` needs a target type.
- Oracle PL/SQL blocks are not analyzed at compile time.
- Oracle `INTERVAL` types are unsupported by common CLR type analysis.

## Getting started

This example requires the .NET 8 SDK and a PostgreSQL database named `myapp`. Replace `tool-version`, `runtime-version`, `migrations-version`, `generator-version`, and `npgsql-version` with the package versions being used.

### 1. Install the CLI

```console
dotnet tool install --global CobaltumOrm.Tool --version tool-version
```

### 2. Create the migration and application projects

```console
mkdir MyApp
cd MyApp

cobaltum migrations init MyApp.Database \
  --output src/MyApp.Database \
  --framework net8.0 \
  --provider PostgreSql

dotnet new console \
  --name MyApp \
  --output src/MyApp \
  --framework net8.0
```

### 3. Add the first migration

Create a reversible C# migration with the CLI. `--version 1` gives the example a predictable filename.

```console
cobaltum migrations add "create users" \
  --version 1 \
  --project src/MyApp.Database
```

Replace the contents of `src/MyApp.Database/Migrations/1_CreateUsersMigration.cs` with:

```csharp
using CobaltumOrm.Migrations;

namespace MyApp.Database.Migrations;

[Migration(1, "create users")]
public sealed class CreateUsersMigration : Migration
{
    public override void Up()
    {
        Create.Table("users")
            .WithColumn("id").AsInt64().NotNullable().PrimaryKey()
            .WithColumn("display_name").AsString().Nullable();

        Execute.Sql("INSERT INTO users (id, display_name) VALUES (1, 'first user');");
    }

    public override void Down()
    {
        Delete.Table("users");
    }
}
```

The CLI creates the class, attribute, version, and empty `Up` and `Down` methods. `Up` now creates and seeds `users`; `Down` removes the table.

### 4. Connect the Query project to the migrations

Replace `src/MyApp/MyApp.csproj` with the following project definition. `CobaltumOrmMigrationProjectReference` makes the Query build read migrations from the separate executable project for SQL checking and code generation.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <CobaltumOrmGeneratedNamespace>MyApp.Database</CobaltumOrmGeneratedNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CobaltumOrm" Version="runtime-version" />
    <PackageReference Include="CobaltumOrm.Migrations" Version="migrations-version" />
    <PackageReference Include="CobaltumOrm.SourceGenerator"
                      Version="generator-version"
                      PrivateAssets="all" />
    <PackageReference Include="Npgsql" Version="npgsql-version" />
    <CobaltumOrmMigrationProjectReference
        Include="../MyApp.Database/MyApp.Database.csproj" />
    <CompilerVisibleProperty Include="CobaltumOrmGeneratedNamespace" />
  </ItemGroup>
</Project>
```

### 5. Write a checked query

Replace `src/MyApp/Program.cs` with:

```csharp
using CobaltumOrm;
using Npgsql;

await using var connection = new NpgsqlConnection(
    "Host=localhost;Database=myapp;Username=postgres;Password=postgres");
var rows = await UserQueries.ReadAllAsync(connection);

foreach (var row in rows)
{
    Console.WriteLine($"{row.Id}: {row.DisplayName}");
}

[Query("ReadAll", "SELECT id, display_name FROM users ORDER BY id")]
public static partial class UserQueries
{
}
```

Restore the migration project, then build the Query project:

```console
dotnet restore src/MyApp.Database/MyApp.Database.csproj
dotnet build src/MyApp/MyApp.csproj
```

The `[Query]` attribute gives the SQL a reusable name. The build reads `1_CreateUsersMigration.cs`, checks the `SELECT`, and generates `UserQueries.ReadAllAsync` and its result type. It does not connect to PostgreSQL. The connection string is written directly in this short example so the runtime connection is explicit.

### 6. Preview and apply the migration

Replace the generated `src/MyApp.Database/appsettings.json` with the following sample settings:

```json
{
  "ConnectionStrings": {
    "Cobaltum": "Host=localhost;Database=myapp;Username=postgres;Password=postgres"
  }
}
```

Then preview and apply the migration:

```console
cobaltum migrations up \
  --dry-run \
  --project src/MyApp.Database

cobaltum migrations up \
  --project src/MyApp.Database
```

The dry run prints the migration file, SQL, and resulting schema without changing the database. The second command creates `users`, inserts the first row, and records migration version `1`.

### 7. Run the query

```console
dotnet run --project src/MyApp
```

The application prints:

```text
1: first user
```

## Project configuration

Build-time checking and code generation are available in .NET SDK projects. Reference the required packages and add Flyway-compatible SQL files as `AdditionalFiles`.

### Using PackageReference

Replace each `Version` with the version of the package being referenced. Specify each version separately when the packages use different versions.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CobaltumOrm" Version="runtime-version" />
    <PackageReference Include="CobaltumOrm.Migrations" Version="migrations-version" />
    <PackageReference Include="CobaltumOrm.Migrations.PostgreSql" Version="postgresql-adapter-version" />
    <PackageReference Include="CobaltumOrm.SourceGenerator"
                      Version="generator-version"
                      PrivateAssets="all" />
    <AdditionalFiles Include="Migrations/V*__*.sql" />
    <CompilerVisibleProperty Include="CobaltumOrmGeneratedNamespace" />
  </ItemGroup>

  <PropertyGroup>
    <CobaltumOrmGeneratedNamespace>MyApp.Database</CobaltumOrmGeneratedNamespace>
  </PropertyGroup>
</Project>
```

`CobaltumOrmGeneratedNamespace` defaults to `CobaltumOrm.Generated`. Projects using CobaltumORM must use C# 9 or later. On target frameworks without `DateOnly` and `TimeOnly`, PostgreSQL `date` and `time` columns generate `DateTime` and `TimeSpan` properties.

## Migration inputs

### C# migrations

A C# migration inherits from `Migration` and has a `[Migration(positiveLongVersion)]` attribute. Operations are written as a FluentMigrator-style method chain. The operations in `Up` build the schema used at build time, and `Down` is used for runtime rollbacks.

```csharp
using CobaltumOrm.Migrations;

namespace MyApp.Database;

[Migration(10, "create users")]
public sealed class CreateUsersMigration : Migration
{
    public override void Up()
    {
        Create.Table("users")
            .WithColumn("id").AsInt64().Identity().PrimaryKey()
            .WithColumn("display_name").AsString(120).Nullable()
            .InSchema("accounts");

        Alter.Table("users")
            .AddColumn("created_at").AsDateTimeOffset().NotNullable()
            .InSchema("accounts");
    }

    public override void Down()
    {
        Delete.Table("users").InSchema("accounts");
    }
}
```

The following operations can currently be analyzed at build time:

- `Create.Table`, `InSchema`, `WithColumn`, `As...`, `Nullable`, `NotNullable`, `PrimaryKey`, and `Identity`
- `Alter.Table`, `AddColumn`, and `AlterColumn`
- `Delete.Table` and `Delete.Column(...).FromTable(...)`
- `Rename.Table(...).To(...)` and `Rename.Column(...).OnTable(...).To(...)`
- `Execute.Sql(constantSql)`

Names, lengths, precision, scale, and the SQL passed to `Execute.Sql` must be compile-time constants. The analyzer cannot follow `if` statements, loops, or helper methods that change what `Up` does. Unsupported method chains and control flow produce compile errors.

### Flyway-compatible SQL

CobaltumORM reads the `V<version>__<description>.sql` format used by Flyway versioned migrations. Compatibility covers the filename convention and application in version order. CobaltumORM refers to files in this format as Flyway-compatible SQL.

Add Flyway-compatible SQL files to `AdditionalFiles`. For example, use `Migrations/V20__add_display_name.sql`. The version must be a positive 64-bit integer. C# migrations and Flyway-compatible SQL files cannot use the same version. Underscores in the description are stored as spaces in the migration history.

C# migrations and Flyway-compatible SQL are applied in ascending version order. The SQL is executed without being rewritten. Flyway-compatible SQL has no down SQL, like Flyway versioned migrations, so a rollback that includes one of these files is rejected before it starts.

Supported `CREATE TABLE`, `DROP TABLE`, `ALTER TABLE`, and table or column rename statements in Flyway-compatible SQL and `Execute.Sql` are applied to the schema used at build time. Common `UNIQUE`, `FOREIGN KEY`, `CHECK`, and exclusion constraints are accepted, but the generated schema model does not expose them. Generated columns are accepted as typed columns without exposing their generation expression. Changes to column defaults are retained. `INSERT`, `UPDATE`, `DELETE`, `SELECT`, index operations, and `COMMENT` do not change table columns, so they are ignored while building the schema but retained for execution. An unsupported schema operation that may change a query result produces a compile error.

SQL statements are split according to the selected provider's lexical rules. PostgreSQL is the main target. In PostgreSQL input, semicolons inside single-quoted strings, escape strings, quoted identifiers, dollar-quoted strings, line comments, and nested block comments do not end a statement. Column defaults, nullability, primary keys, and schema-qualified table names are also retained.

Use the migration adapter for the selected provider to run migrations. The PostgreSQL form is:

```csharp
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.PostgreSql;
using MyApp.Database;

var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());
await runner.MigrateUpAsync(
    connection,
    CobaltumMigrationCatalog.All,
    cancellationToken);
```

## Command-line migration management

Install the .NET tool globally, or add the same package to a local tool manifest:

```console
dotnet tool install --global CobaltumOrm.Tool --version tool-version
```

Create the migration project with the CLI:

```console
cobaltum migrations init MyApp.Database \
  --output src/MyApp.Database \
  --framework net8.0 \
  --provider PostgreSql
```

The same project is available as a .NET project template. Install its NuGet package, then use `dotnet new`:

```console
dotnet new install CobaltumOrm.Templates@templates-version
dotnet new cobaltum-migrations \
  --name MyApp.Database \
  --output src/MyApp.Database \
  --framework net8.0 \
  --provider PostgreSql
```

Both commands create an executable migration project for the selected provider with `Program.cs`, `appsettings.json`, a unique `UserSecretsId`, and a `Migrations` directory. They also include the source generator settings required to use the project through `CobaltumOrmMigrationProjectReference`. The CLI accepts `net8.0`, `net9.0`, or `net10.0` and refuses to write into a non-empty output directory. Omit `--provider` to select PostgreSQL.

The CLI accepts provider names without case sensitivity. An invalid name lists the supported values.

The generated project does not contain a connection password. Configure `ConnectionStrings:Cobaltum` with user secrets or an environment variable before running `status`, `up`, or `down`.

To define the project manually, use the following fixed form. `OutputType`, `RootNamespace`, and `CobaltumOrmMigrationProject` must be unconditional properties. C# migrations and Flyway-compatible SQL are kept under `Migrations`.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>MyApp.Database</RootNamespace>
    <CobaltumOrmMigrationProject>true</CobaltumOrmMigrationProject>
    <CobaltumOrmGeneratedNamespace>MyApp.Database.Generated</CobaltumOrmGeneratedNamespace>
    <CobaltumOrmDatabaseProvider>PostgreSql</CobaltumOrmDatabaseProvider>
    <UserSecretsId>myapp-database-migrations</UserSecretsId>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CobaltumOrm" Version="runtime-version" />
    <PackageReference Include="CobaltumOrm.Migrations" Version="migrations-version" />
    <PackageReference Include="CobaltumOrm.Migrations.PostgreSql" Version="postgresql-adapter-version" />
    <PackageReference Include="CobaltumOrm.SourceGenerator"
                      Version="generator-version"
                      PrivateAssets="all" />
    <PackageReference Include="Npgsql" Version="npgsql-version" />
    <AdditionalFiles Include="Migrations/V*__*.sql" />
    <CompilerVisibleProperty Include="CobaltumOrmGeneratedNamespace" />
    <CompilerVisibleProperty Include="CobaltumOrmDatabaseProvider" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings*.json"
          CopyToOutputDirectory="PreserveNewest"
          CopyToPublishDirectory="PreserveNewest"
          TargetPath="CobaltumOrm.Migrations/$(AssemblyName)/%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
```

The source generator creates `CobaltumMigrationCatalog.All` in `CobaltumOrmGeneratedNamespace`. The project entry point passes this catalog to the host and uses the CobaltumORM configuration context to create the connection.

```csharp
using System.Data.Common;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.PostgreSql;
using Npgsql;

return await MigrationProjectHost.RunAsync<DatabaseMigrationProject>(
    args,
    global::MyApp.Database.Generated.CobaltumMigrationCatalog.All);

public sealed class DatabaseMigrationProject : MigrationProject
{
    public override DbConnection CreateConnection(MigrationProjectContext context) =>
        new NpgsqlConnection(context.ConnectionString);

    public override IMigrationDatabaseAdapter CreateAdapter() =>
        new PostgreSqlMigrationAdapter();
}
```

CobaltumORM follows the .NET configuration order. It reads `appsettings.json`, `appsettings.{Environment}.json`, .NET user secrets in `Development`, and environment variables. Later sources override earlier sources. The required key is `ConnectionStrings:Cobaltum`; its portable environment variable form is `ConnectionStrings__Cobaltum`. `--environment` overrides `DOTNET_ENVIRONMENT`, whose default is `Production`.

JSON files can hold environment-specific host and database names. Keep passwords and other credentials out of committed files; use user secrets for local development and environment variables or the deployment platform's secret store elsewhere.

```console
dotnet user-secrets set --project src/MyApp.Database \
  "ConnectionStrings:Cobaltum" "Host=localhost;Database=myapp;Username=myapp;Password=secret"

DOTNET_ENVIRONMENT=Staging \
ConnectionStrings__Cobaltum="Host=db;Database=myapp;Username=myapp;Password=secret" \
cobaltum migrations status
```

`--settings` selects one JSON file in place of both default appsettings files. User secrets and environment variables can still override that file.

```console
cobaltum migrations status --environment Staging
cobaltum migrations up --environment Production --settings config/migrations.production.json
```

When `--project` is omitted, the tool recursively searches the current directory for a project with `CobaltumOrmMigrationProject` set to `true`. If it finds more than one, specify the `.csproj` or a directory with `--project`.

```console
cobaltum migrations init MyApp.Database --output src/MyApp.Database --provider PostgreSql
cobaltum migrations add "create users" --project src/MyApp.Database
cobaltum migrations list --project src/MyApp.Database
cobaltum migrations status --project src/MyApp.Database
cobaltum migrations up --project src/MyApp.Database
cobaltum migrations up --dry-run --project src/MyApp.Database
cobaltum migrations down 20260810090000 --project src/MyApp.Database
cobaltum migrations down 20260810090000 --dry-run --project src/MyApp.Database
cobaltum migrations down 0 --project src/MyApp.Database
```

`init` creates the fixed migration project and uses the project name as its root namespace. `add` creates a reversible C# migration in the project's `Migrations` directory. Its default version is a UTC timestamp; use `--version` to supply a positive version greater than every existing C# or Flyway-compatible migration. `list` builds the project and lists migration definitions without opening a database connection. `status`, `up`, and `down` run with the migration project's target framework and configuration. Use `--configuration`, `--framework`, and `--no-build` when a non-default build is needed. `down 0` rolls back all reversible migrations. A rollback containing a forward-only migration is rejected before any rollback starts.

Add `--dry-run` to `up` or `down` to print each affected file under `Migrations`, the SQL that would run, and the resulting tables and columns. The command connects to the selected database only to read its migration history. It does not create the history table, execute migration SQL, or update migration history. The final schema is reconstructed from the migration definitions, using the selected provider's supported table operations as build-time schema generation. The command fails instead of showing an incomplete schema when a migration contains an unsupported statement that may change table structure.

### Using the migration project from a Query project

Add the migration project as a CobaltumORM migration input in each project that defines Query calls or uses generated schema types:

```xml
<ItemGroup>
  <CobaltumOrmMigrationProjectReference
      Include="../MyApp.Database/MyApp.Database.csproj" />
</ItemGroup>
```

The Query project reads `Migrations/**/*.cs` and `Migrations/V*__*.sql` from that project at build time. `SqlSchema`, `Tables`, row records, named Query result types, and direct `Query(...)` checks are generated from the same ordered migrations used by the CLI. A normal `ProjectReference` to the migration executable is not required when only schema generation is needed. Set `CobaltumOrmGeneratedNamespace` in the Query project when the generated types should use an application-specific namespace.

When the Query application should also use the connection defined by the migration project, add both references:

```xml
<ItemGroup>
  <ProjectReference Include="../MyApp.Database/MyApp.Database.csproj" />
  <CobaltumOrmMigrationProjectReference
      Include="../MyApp.Database/MyApp.Database.csproj" />
</ItemGroup>
```

The application can then create a connection without reading a raw environment variable or repeating its configuration key:

```csharp
using CobaltumOrm.Migrations;
using MyApp.Database;

using var database =
    MigrationProjectConnection.Create<DatabaseMigrationProject>();
var rows = await UserQueries.ReadAllAsync(database.Connection);
```

`MigrationProjectConnection` uses the same configuration order and the same `DatabaseMigrationProject.CreateConnection` implementation as the CLI. It owns both the loaded configuration and the connection; disposing it disposes both. Environment selection continues to use the standard `DOTNET_ENVIRONMENT` setting. The generated migration project copies its `appsettings*.json` files to a project-specific directory for consuming applications, so they do not replace the application's own settings files.

## Generated table types

Each table produces a `public sealed record` and a table object whose columns can be referenced with C# types. `[CobaltumTable]` records the SQL schema and table names. `[CobaltumColumn]` records each property SQL name, data type, nullability, primary key status, and default expression. When C# property names collide, the generated names use `_2`, `_3`, and subsequent suffixes. The schema name is included in the `record` name when different schemas contain tables with the same name.

`Tables.Users` provides `Query()`, `All()`, and `Where(...)`. `Where(...)` and `WhereIf(...)` can be appended to the `CobaltumQueryDefinition<TRecord>` returned by `Query()` and `All()`. Values are passed as `DbParameter` instances instead of being concatenated into SQL. Adding filters does not change the result `record` type.

```csharp
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm;
using CobaltumOrm.Sample.Generated;

public static class UsersReader
{
    public static async Task<IReadOnlyList<AppUsersRow>> ReadAllAsync(
        DbConnection connection,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        return await connection.Query(
            Tables.Users.All(),
            transaction,
            cancellationToken);
    }

    public static Task<IReadOnlyList<AppUsersRow>> ReadFilteredAsync(
        DbConnection connection,
        int id,
        bool includeDisplayName,
        string? displayName,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var query = Tables.Users
            .Query()
            .Where(Tables.Users.Id.Equal(id))
            .WhereIf(
                includeDisplayName,
                () => Tables.Users.DisplayName.Equal(displayName));

        return connection.Query(
            query,
            transaction,
            cancellationToken);
    }
}
```

`AppUsersRow`, `Tables.Users`, `Id`, and `DisplayName` in this example are generated from the `app.users` schema in the sample. `id` has the C# type `int`. When the `WhereIf` condition is `false`, its function is not called. The filter is added only when the condition is `true`.

## Named queries

Reusable SQL can be defined by adding `[Query]` to a non-generic `partial class` declared at namespace scope. The attribute can be added to the same class more than once.

```csharp
using CobaltumOrm;
using MyApp.Database;

[Query(
    "ById",
    $"SELECT {SqlSchema.Tables.AccountsUsers.Columns.Id}, {SqlSchema.Tables.AccountsUsers.Columns.DisplayName} " +
    $"FROM {SqlSchema.Tables.AccountsUsers.Name} " +
    $"WHERE {SqlSchema.Tables.AccountsUsers.Columns.Id} = @id")]
[Query(
    "ByName",
    $"SELECT {SqlSchema.Tables.AccountsUsers.Columns.Id}, {SqlSchema.Tables.AccountsUsers.Columns.DisplayName} " +
    $"FROM {SqlSchema.Tables.AccountsUsers.Name} " +
    $"WHERE {SqlSchema.Tables.AccountsUsers.Columns.DisplayName} = @name")]
public static partial class UserQueries
{
}
```

`SqlSchema` is generated from migrations and exposes schema, table, and column names as `const string` values quoted as PostgreSQL identifiers. For example, `SqlSchema.Schemas.Accounts` is a schema name, `SqlSchema.Tables.AccountsUsers.Name` is a table name, and `SqlSchema.Tables.AccountsUsers.Columns.DisplayName` is a column name. C# 10 and later can use these values in an interpolated attribute argument as shown above. In C# 9, concatenate the same constants, for example `"SELECT " + SqlSchema...`.

Only names in the current schema get `SqlSchema` members. If a migration renames `display_name` to `name`, `DisplayName` is no longer generated and the old query produces a C# compile error. Update it explicitly to use `Name`. Continue to pass values through parameters such as `@id` instead of interpolating them from `SqlSchema`.

SQL with names written directly, such as `"SELECT id FROM accounts.users"`, is also checked at build time. Supported SQL is parsed and checked against the schema after all migrations are applied. Missing schemas, tables, columns, and syntax errors produce a compile error with `COB004` and an SQL error code.

Each query generates a result type such as `ByIdResult`, a parameter type such as `ByIdParameters`, a typed query definition such as `ById`, and an async method such as `ByIdAsync`.

```csharp
var rows = await UserQueries.ByIdAsync(
    connection,
    id: 42L,
    transaction: transaction,
    cancellationToken: cancellationToken);

_ = rows[0].Id;

await connection.Query(
    UserQueries.ById,
    new UserQueries.ByIdParameters(42L),
    transaction: transaction,
    cancellationToken: cancellationToken);
```

Named query SQL, columns, and parameter types are checked against the PostgreSQL schema at build time. Named queries provide a typed API when the same SQL is used in more than one place.

## Result types for constant `Query` SQL

Passing a compile-time constant SQL string to `Query("...")` generates a `record` for the returned columns. This works for `SELECT` and for PostgreSQL data modification statements with `RETURNING`. Each column can be accessed as a C# property on the result returned by `ReadAsync`.

```csharp
using System.Data;
using System.Data.Common;
using System.Threading;
using CobaltumOrm;

public static class UserReader
{
    public static async System.Threading.Tasks.Task<(long Id, string? DisplayName)> ReadAsync(
        DbConnection connection,
        long id,
        CancellationToken cancellationToken = default)
    {
        var rows = await connection
            .Query("SELECT id, display_name FROM accounts.users WHERE id = @id")
            .WithParameter("@id", id, DbType.Int64)
            .ReadAsync(cancellationToken);

        return (rows[0].Id, rows[0].DisplayName);
    }
}
```

The `record` type for `rows` has `Id` and `DisplayName` properties. Accessing a property that is not selected, such as `rows[0].Email`, produces a normal C# compile error. Invalid SQL syntax, columns missing from the schema, and parameters whose types cannot be inferred also produce compile errors. Constant parameter names and C# types supplied to `WithParameter` are checked against the parsed SQL. These checks apply to supported statements whose contents are known at build time.

Supported `SELECT` syntax includes CTEs, recursive CTEs, `VALUES`, derived tables, correlated subqueries, `DISTINCT ON`, set operations, joins, filters, grouping, ordering, `LIMIT` / `OFFSET` / `FETCH`, row locking, `CASE`, casts, date and interval literals, inline or named windows, aggregate `FILTER`, and common scalar and aggregate functions. PostgreSQL operators covered by the analyzer include `ILIKE`, regular-expression matching, `IS DISTINCT FROM`, JSON access, containment, overlap, modulo, and exponentiation. See the [supported PostgreSQL SELECT syntax](docs/design/poc-sql-type-inference.md#supported-select-syntax) for details.

`WithParameter` passes values as `DbParameter` instances instead of concatenating them into SQL. For checked statements, the parameter `DbType` is inferred. PostgreSQL type names are also passed to the database provider for `json` and `jsonb` parameters. Missing parameter values are detected before `ReadAsync` runs.

## Interpolated `Query`

Interpolation slots can only appear where SQL values are allowed. Interpolated values are not expanded into schema names, table names, column names, or other SQL structure. They are replaced with `DbParameter` placeholders.

```csharp
using System.Data.Common;
using System.Threading;
using CobaltumOrm;

public static async System.Threading.Tasks.Task<string?> ReadByIdAsync(
    DbConnection connection,
    long id,
    CancellationToken cancellationToken = default)
{
    var rows = await connection
        .Query($"SELECT id, display_name FROM accounts.users WHERE id = {id}")
        .ReadAsync(cancellationToken);

    return rows[0].DisplayName;
}
```

At runtime, this SQL becomes a statement such as `id = @__cobaltum_value_0` and the value of `id` is passed as a parameter. Interpolating SQL structure, as in `$"SELECT {fields} FROM accounts.users"`, is rejected. Format and alignment clauses are also unavailable.

Interpolated `INSERT`, `UPDATE`, and `DELETE` statements are not supported by checked `Query` calls. Use constant SQL with `WithParameter` for these statements.

## Constant `INSERT`, `UPDATE`, and `DELETE`

Compile-time constant `INSERT`, `UPDATE`, and `DELETE` statements without `RETURNING` run with `ExecuteAsync`. A statement with `RETURNING` runs with `ReadAsync` and gets a generated result `record`.

```csharp
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm;

public static class UserWriter
{
    public static Task<int> UpdateNameAsync(
        DbConnection connection,
        long id,
        string newName,
        CancellationToken cancellationToken = default) =>
        connection
            .Query("UPDATE accounts.users SET display_name = @name WHERE id = @id")
            .WithParameter("@name", newName, DbType.String)
            .WithParameter("@id", id, DbType.Int64)
            .ExecuteAsync(cancellationToken);
}
```

`ExecuteAsync` returns the affected row count reported by the database provider. Parameter values become `DbParameter` instances, and `null` becomes `DBNull.Value`. Constant SQL is parsed at build time. Its syntax, target schema, tables, columns, expression types, and parameter types are checked against the schema after migrations are applied. Supported forms include `INSERT ... VALUES`, `DEFAULT VALUES`, `INSERT ... SELECT`, `ON CONFLICT`, `UPDATE ... FROM`, `DELETE ... USING`, `TRUNCATE`, `RETURNING`, and CTEs around data modification statements. Permissions, constraints, triggers, and outcomes that depend on stored data cannot be checked without connecting to the database. Schema changes belong in migrations rather than `Query`.

The compile-time analyzer does not cover every PostgreSQL construct. Unsupported forms include `MERGE`, table functions in `FROM`, `GROUPING SETS`, `CUBE`, `ROLLUP`, array constructors, and user-defined function result types. Window frame clauses are accepted but their contents are not semantically validated. Use `NoCheckQuery` when one of these forms is required.

When direct SQL passes a string to a PostgreSQL `json` or `jsonb` parameter, configure the Npgsql parameter directly: `WithConfiguredParameter("@document", json, DbType.String, static parameter => ((NpgsqlParameter)parameter).DataTypeName = "jsonb")`. Generated queries apply this setting automatically.

## Queries without build-time SQL checking

Use `NoCheckQuery(sql)` when SQL cannot be checked at build time or falls outside the supported syntax. It returns an untyped `CobaltumRawQuery`. `ReadAsync` returns a list of `CobaltumRawRow` values. SQL syntax, schema names, table names, column names, and the result column shape are not checked at build time.

```csharp
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm;

public static class DynamicReader
{
    public static Task<IReadOnlyList<CobaltumRawRow>> ReadAsync(
        DbConnection connection,
        string sql,
        long id,
        CancellationToken cancellationToken = default) =>
        connection
            .NoCheckQuery(sql)
            .WithParameter("@id", id, DbType.Int64)
            .ReadAsync(cancellationToken);
}
```

`QueryDynamic(sql)` remains as an equivalent API for compatibility. New code should use `NoCheckQuery(sql)` because its name indicates that the SQL is not checked.

`CobaltumRawRow` retains column ordinals and names. Values can be retrieved with `row[0]`, `row["id"]`, and `GetValues("name")`. Looking up a duplicate column name through the string indexer throws an exception because a single column cannot be selected. When SQL text does not need to be assembled for optional runtime filters, use `Tables.Users.Query().Where(...).WhereIf(condition, () => ...)`. It adds conditions without changing the generated `record` type.

## Connections and runtime behavior

Generated queries, `Query`, `NoCheckQuery`, and `QueryDynamic` use `DbConnection`, `DbCommand`, and `DbDataReader`. A `DbConnection` supplied by an ADO.NET provider such as Npgsql can be used.

A closed connection is opened with `OpenAsync(cancellationToken)` when a query runs and is closed afterward. A connection that was already open remains open. A `DbTransaction` requires an open connection and must belong to the same connection. `DbTransaction` and `CancellationToken` are passed through generated queries to the command and data reader.

## Trimmed and Native AOT publishing

The runtime and migration packages include `net8.0` and `net10.0` assemblies that are checked by the trim and AOT analyzers. The `netstandard2.0` and `netstandard2.1` assemblies remain available for older targets.

For a trimmed application, add this property to the executable project:

```xml
<PropertyGroup>
  <PublishTrimmed>true</PublishTrimmed>
  <TrimMode>full</TrimMode>
</PropertyGroup>
```

For Native AOT, use `PublishAot`. Native AOT also trims the application.

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

Publish for the target runtime, for example:

```console
dotnet publish -c Release -r linux-x64
```

The source generator creates `CobaltumMigrationCatalog.All` without scanning assemblies at runtime. Pass this catalog to `MigrationRunner` and `MigrationProjectHost`, as shown in the migration examples above. A handwritten catalog can use `MigrationInfo.Create<TMigration>(version, description)`.

CobaltumORM generates direct Npgsql parameter configuration for PostgreSQL `json` and `jsonb`; other generated provider bindings use `DbType`. PostgreSQL projects must reference Npgsql, as shown in the installation and migration project examples.

The selected ADO.NET driver must also support the target deployment mode. Resolve any trim or AOT warning reported from the driver before publishing.

## Supported environments

- Projects must use the .NET SDK project format.
- CoreCLR and Mono applications built with the .NET SDK / MSBuild are supported.
- Trimmed and Native AOT applications targeting .NET 8 or later are supported when generated migration catalogs are used.
- Compile-time `Query` checking and result type generation are unavailable with classic `mcs` / `xbuild` and normal Unity / IL2CPP projects.

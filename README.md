# CobaltumORM

English | [日本語](README.ja.md)

[![CobaltumOrm](https://img.shields.io/nuget/v/CobaltumOrm.svg?label=CobaltumOrm)](https://www.nuget.org/packages/CobaltumOrm)
[![CobaltumOrm.Analysis](https://img.shields.io/nuget/v/CobaltumOrm.Analysis.svg?label=CobaltumOrm.Analysis)](https://www.nuget.org/packages/CobaltumOrm.Analysis)
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

For coding agents: start with [coding agent tools](docs/ai/agent-tools.md), then use the
[quick reference](docs/ai/quick-reference.md), [task recipes](docs/ai/recipes.md),
[build diagnostics](docs/ai/diagnostics.md), and the [llms.txt](llms.txt) index.

## Table of contents

- [Features](#features)
- [Database providers](#database-providers)
- [Getting started](#getting-started)
- [Project configuration](#project-configuration)
- [Migration inputs](#migration-inputs)
- [Command-line migration management](#command-line-migration-management)
- [Explicit generation with the CLI](#explicit-generation-with-the-cli)
- [Generated table types](#generated-table-types)
- [Conditions](#conditions)
- [Record `INSERT`, `UPDATE`, and `DELETE`](#record-insert-update-and-delete)
- [Named queries](#named-queries)
- [Result types for constant `Query` SQL](#result-types-for-constant-query-sql)
- [Caller-supplied result types](#caller-supplied-result-types)
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
- A statement that returns rows can generate a result type or map to a type supplied through `Query<T>` or `[Query<T>]`.
- An INSERT, UPDATE, DELETE, or TRUNCATE without RETURNING can be named with `[Query]`. The generated method returns the affected row count.
- Renaming or deleting a schema object in a migration makes old `SqlSchema` references and SQL that uses the old name fail to compile.
  - The current checker supports part of the PostgreSQL syntax used for CRUD operations. It cannot check permissions, constraints, triggers, or outcomes that depend on stored data.
- Generated table records build single-row `INSERT`, `UPDATE`, and `DELETE` statements, so simple writes need no SQL.
- Conditions are built from generated columns with `=`, `<>`, `<`, `<=`, `>`, `>=`, `IS NULL`, `LIKE`, `IN`, and `BETWEEN`, and combine with `&&` and `||`.
- CobaltumORM does not provide EF Core-style change tracking or an equivalent to `SaveChanges`. Queries and commands are executed explicitly.

### Comparison with other .NET ORMs

| ORM | Typical use | Queries and result types |
| --- | --- | --- |
| CobaltumORM | Define SQL with `Query` or `[Query]` | Check SQL against the schema built from migrations, then generate a result type or validate a caller-supplied type |
| [EF Core](https://learn.microsoft.com/en-us/ef/core/) | Use `DbContext`, entity models, LINQ, and change tracking. APIs for direct SQL execution are also available | Use LINQ projections or types in the model, including entities, keyless entities, and scalar types |
| [Dapper](https://github.com/DapperLib/Dapper) | Pass SQL and parameters to `DbConnection` extension methods such as `Query<T>` and `Execute` | Map rows to the type supplied to `Query<T>`, or return rows whose columns are resolved at runtime with `Query` |

In the measured PostgreSQL read workloads, CobaltumORM performed at a similar level to the other ORMs. See the [benchmark results and instructions](benchmarks/CobaltumOrm.Benchmarks/README.md) for the measurements and how to reproduce them.

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

Replace `src/MyApp/MyApp.csproj` with the following project definition. `CobaltumOrmMigrationProjectReference` makes the Query build read migrations from the separate executable project for SQL checking and code generation. It also references the migration assembly, so runtime types such as `CobaltumMigrationCatalog` are available to the application. Give the Query project its own generated namespace, because the migration project generates the same type names in its own namespace.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <CobaltumOrmGeneratedNamespace>MyApp.Generated</CobaltumOrmGeneratedNamespace>
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

The C# API includes these provider-neutral operations:

- schemas, tables, standalone columns, table descriptions, and table moves
- column types, collations, defaults, descriptions, indexes, unique values, computed values, and foreign keys
- indexes, foreign keys, primary keys, unique constraints, and sequences
- table, column, index, foreign-key, constraint, sequence, and default removal
- table and column renames
- `Insert`, `Update`, and row deletion with parameterized values
- `Execute.Sql`, `Execute.Script`, `Execute.EmbeddedScript`, and `Execute.WithConnection`
- `IfDatabase` by provider name or predicate, including its `Delegate` form

`SystemMethods` supplies database-generated defaults such as the current timestamp or a new GUID. `RawSql.Insert` inserts a provider-specific SQL expression where a literal value is not suitable. `SetExistingRowsTo` adds a nullable column, updates existing rows with a parameter, and then applies the requested non-null constraint.

The source generator accepts the table, column, schema, index, foreign-key, constraint, sequence, and data method chains. Database names passed directly to `IfDatabase` are evaluated for the configured provider. Names, lengths, precision, scale, default literals, raw default SQL, and SQL passed to `Execute.Sql` must be compile-time constants. `Execute.Script`, `Execute.EmbeddedScript`, `Execute.WithConnection`, the predicate overload of `IfDatabase`, and `IfDatabase.Delegate` cannot be evaluated at build time. Use Flyway-compatible SQL as an `AdditionalFile` when a script must change the schema used for query generation.

The analyzer cannot follow `if` statements, loops, or helper methods that change what `Up` does. Unsupported method chains and control flow produce compile errors. An adapter throws `NotSupportedException` when the selected database lacks the requested feature, such as standalone sequences in MySQL and SQLite or named schemas in SQLite.

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
cobaltum migrations schema --project src/MyApp.Database
cobaltum migrations status --project src/MyApp.Database
cobaltum migrations up --project src/MyApp.Database
cobaltum migrations up --write-schema --project src/MyApp.Database
cobaltum migrations up --dry-run --project src/MyApp.Database
cobaltum migrations down 20260810090000 --project src/MyApp.Database
cobaltum migrations down 20260810090000 --dry-run --project src/MyApp.Database
cobaltum migrations down 0 --project src/MyApp.Database
```

`init` creates the fixed migration project and uses the project name as its root namespace. `add` creates a reversible C# migration in the project's `Migrations` directory. Its default version is a UTC timestamp; use `--version` to supply a positive version greater than every existing C# or Flyway-compatible migration. `list` builds the project and lists migration definitions without opening a database connection. `status`, `up`, and `down` run with the migration project's target framework and configuration. Use `--configuration`, `--framework`, and `--no-build` when a non-default build is needed. `down 0` rolls back all reversible migrations. A rollback containing a forward-only migration is rejected before any rollback starts.

`schema` applies every migration's `Up` definition to an empty in-memory schema and writes the resulting tables and columns as UTF-8 JSON. It does not connect to a database or load a connection string. The default output is `schema.generated.json` in the migration project directory. `--output` selects another path. Parent directories are created when needed, and an existing output file is replaced. The JSON uses two-space indentation, preserves Unicode identifiers and expressions, and keeps table and property order stable for reviewable diffs. `formatVersion` identifies the JSON contract. `schema` and `defaultExpression` are JSON `null` when they have no value.

```json
{
  "formatVersion": 1,
  "tables": [
    {
      "schema": "public",
      "name": "users",
      "columns": [
        {
          "name": "id",
          "sqlType": "bigint",
          "nullable": false,
          "primaryKey": true,
          "identity": true,
          "defaultExpression": null
        }
      ]
    }
  ]
}
```

Add `--write-schema` to `up` to write the same JSON after the migrations complete successfully. `--output` changes its path. When combined with `--dry-run`, the database is not changed and the file contains the schema expected after the planned migrations.

Add `--dry-run` to `up` or `down` to print each affected file under `Migrations`, the SQL that would run, and the resulting tables and columns. The command connects to the selected database only to read its migration history. It does not create the history table, execute migration SQL, or update migration history. The final schema is reconstructed from the migration definitions, using the selected provider's supported table operations as build-time schema generation. The command fails instead of showing an incomplete schema when a migration contains an unsupported statement that may change table structure.

### Using the migration project from a Query project

Add the migration project as a CobaltumORM migration input in each project that defines Query calls or uses generated schema types:

```xml
<ItemGroup>
  <CobaltumOrmMigrationProjectReference
      Include="../MyApp.Database/MyApp.Database.csproj" />
</ItemGroup>
```

The Query project reads `Migrations/**/*.cs` and `Migrations/V*__*.sql` from that project at build time. `SqlSchema`, `Tables`, generated row records, named Query definitions, and direct `Query(...)` checks use the same ordered migrations as the CLI. Set `CobaltumOrmGeneratedNamespace` in the Query project when the generated types should use an application-specific namespace.

The reference also references the migration assembly, so the migration project's `CobaltumMigrationCatalog` and `MigrationProject` types are available at runtime. The application can then create a connection without reading a raw environment variable or repeating its configuration key:

```csharp
using CobaltumOrm.Migrations;
using MyApp.Database;

using var database =
    MigrationProjectConnection.Create<DatabaseMigrationProject>();
var rows = await UserQueries.ReadAllAsync(database.Connection);
```

`MigrationProjectConnection` uses the same configuration order and the same `DatabaseMigrationProject.CreateConnection` implementation as the CLI. It owns both the loaded configuration and the connection; disposing it disposes both. Environment selection continues to use the standard `DOTNET_ENVIRONMENT` setting. The generated migration project copies its `appsettings*.json` files to a project-specific directory for consuming applications, so they do not replace the application's own settings files.

## Explicit generation with the CLI

The incremental source generator runs inside every build and is the default. It needs no extra configuration and suits most projects.

`cobaltum generate` writes the same C# files as the incremental source generator by running the CLI before the build. The generated files are compiled as normal source files, so generation only needs to run when the inputs change. Consider it when one of these applies:

- The schema changes rarely and the generated code can be checked in and reviewed.
- The project is large enough that analyzing every build costs more than analyzing when the schema changes.
- The build environment cannot load source generators.

The command runs the same SQL analysis, schema construction, diagnostics, `Query` transformation, and code writers as the build. Both paths call one implementation, so the files the command writes are the files the build would have produced.

### Command

```
cobaltum generate [--project <path>] [--configuration <name>] [--framework <tfm>]
                  [--provider <name>] [--generated-namespace <namespace>]
                  [--output-mode intermediate|directory|library]
                  [--output <directory>] [--library-project <path>] [--library-name <name>]
                  [--no-restore] [--verbose]
```

The command asks MSBuild for the evaluated `Compile` items, resolved references, `AdditionalFiles`, migration project inputs, and compiler properties, so it reads the same inputs as the build instead of parsing the project file itself. It never writes to your `.csproj`.

`--configuration` defaults to `Debug`. Pass `--framework` when the project targets more than one framework. `--provider` and `--generated-namespace` are needed only when the project does not set `CobaltumOrmDatabaseProvider` and `CobaltumOrmGeneratedNamespace`, or when you want to override them.

Each run writes the generator output, a rewritten copy of every source file that contains a `Query` call, a `CobaltumOrm.Generated.props` file that tells MSBuild which files to compile, and a `CobaltumOrm.generated.manifest` file that lists what the tool wrote. The next run removes only the files recorded in that manifest, so other files in the output directory are left alone. Files are written to a staging directory first: when analysis reports an error, the command prints the file, line, and diagnostic code, exits with a nonzero code, and leaves the previous output untouched.

### Output modes

`--output-mode intermediate` is the default. It writes to `obj/<Configuration>/<TargetFramework>/CobaltumOrmGenerated/` and is meant for generated code that is not checked in. Import the props file from the project:

```xml
<Import Project="obj/$(Configuration)/$(TargetFramework)/CobaltumOrmGenerated/CobaltumOrm.Generated.props"
        Condition="Exists('obj/$(Configuration)/$(TargetFramework)/CobaltumOrmGenerated/CobaltumOrm.Generated.props')" />
```

`--output-mode directory` writes to a durable directory that you choose and can check in:

```
cobaltum generate --project src/MyApp.Queries/MyApp.Queries.csproj --output-mode directory --output src/MyApp.Queries/Generated
```

```xml
<Import Project="Generated/CobaltumOrm.Generated.props" />
```

The props file sets `CobaltumOrmCompileTimeQueries` to `false`, removes the original sources that were rewritten from `Compile`, adds the generated files, and removes the CobaltumORM analyzer from the compilation. The directory must not contain the project directory, so pass a subdirectory or a directory outside the project.

`--output-mode library` writes a directory that compiles as its own C# library. With `--library-name`, the tool writes the `.csproj` as well:

```
cobaltum generate --project src/MyApp.Queries/MyApp.Queries.csproj \
  --output-mode library --output src/MyApp.Queries.Generated --library-name MyApp.Queries.Generated
```

The written project sets `EnableDefaultCompileItems` to `false`, imports the props file, and compiles the generated files, the rewritten sources, and the sources that were not rewritten. Its references are the resolved reference list of the source project, written as `HintPath` values that point at package and output directories on the machine that ran the command. A project written by `--library-name` therefore belongs to that machine. Regenerate it there instead of checking it in and building it somewhere else.

For a library project that you keep in source control and build anywhere, use `--library-project`. The destination is a `.csproj` you already own, where you declare references as normal `PackageReference` and `ProjectReference` entries. The tool writes the generated files and the props file next to it and does not modify it. Add these lines yourself:

```xml
<PropertyGroup>
  <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
</PropertyGroup>
<Import Project="CobaltumOrm.Generated.props" />
```

### Project layout when builds become slow

The incremental source generator remains suitable for normal projects. If builds become slow, especially in a large application or a project with many queries, put the `[Query]` containers, `Query` call sites, and migration reference in a dedicated query project. The application project references that query project.

```
src/
  MyApp.Database/          migration project
  MyApp.Queries/           [Query] containers and Query calls
  MyApp/                   application and UI
```

```xml
<!-- src/MyApp.Queries/MyApp.Queries.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <CobaltumOrmGeneratedNamespace>MyApp.Queries.Generated</CobaltumOrmGeneratedNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CobaltumOrm" Version="runtime-version" />
    <PackageReference Include="CobaltumOrm.Migrations" Version="migrations-version" />
    <PackageReference Include="CobaltumOrm.SourceGenerator"
                      Version="generator-version"
                      PrivateAssets="all" />
    <CobaltumOrmMigrationProjectReference Include="../MyApp.Database/MyApp.Database.csproj" />
    <CompilerVisibleProperty Include="CobaltumOrmGeneratedNamespace" />
  </ItemGroup>
</Project>
```

```xml
<!-- src/MyApp/MyApp.csproj -->
<ItemGroup>
  <ProjectReference Include="../MyApp.Queries/MyApp.Queries.csproj" />
</ItemGroup>
```

With this layout alone, the incremental source generator still performs code generation. Editing application or UI code does not repeat SQL analysis when it does not rebuild the query project.

Use `cobaltum generate` when the separated query project is still slow, the schema changes infrequently, or the build environment cannot use source generators.

```console
cobaltum generate --project src/MyApp.Queries/MyApp.Queries.csproj \
  --output-mode directory --output src/MyApp.Queries/Generated
```

Import the generated props file from the query project:

```xml
<Import Project="Generated/CobaltumOrm.Generated.props" />
```

The props file disables build-time Query analysis and source transformation, removes the incremental source generator from the compilation, and adds the files written by the CLI to `Compile`. Run the command again after changing a migration or query. Output from `--output-mode directory` can be checked in with the other source files when needed.

### Analysis cache

Normal builds and `cobaltum generate` use a local analysis cache by default. It stores the final successful database schema produced from the ordered migrations and successful SQL query analysis results, including result columns and parameters. It does not store generated C# or other build artifacts.

Entries are written under `obj/<Configuration>/<TargetFramework>/CobaltumOrm/AnalysisCache`. `dotnet clean` removes them with the rest of `obj`. A missing, unreadable, corrupt, outdated, incomplete, or concurrently replaced entry is ignored without a warning, and CobaltumORM runs the normal analysis instead. Analysis errors are not cached.

Set the following property when troubleshooting to disable reads and writes:

```xml
<PropertyGroup>
  <CobaltumOrmAnalysisCache>false</CobaltumOrmAnalysisCache>
</PropertyGroup>
```

A cache hit avoids applying the migrations again or running the SQL query parser and binder again. Roslyn compilation and symbol collection still run, as does mapping query results to C# types. C# edits can still invalidate the build even when the SQL and schema have not changed. If builds become slow, use the separated query project or explicit CLI generation described above.

## Generated table types

Each table produces a `public sealed record` and a table object whose columns can be referenced with C# types. `[CobaltumTable]` records the SQL schema and table names. `[CobaltumColumn]` records each property SQL name, data type, nullability, primary key status, and default expression. When C# property names collide, the generated names use `_2`, `_3`, and subsequent suffixes. The schema name is included in the `record` name when different schemas contain tables with the same name.

`Tables.Users` provides `Query()`, `All()`, and `Where(...)`. `Where(...)` and `WhereIf(...)` can be appended to the `CobaltumQueryDefinition<TRecord>` returned by `Query()` and `All()`. Pass the result to `connection.Query(...)` and call `ReadAsync` to run it. Values are passed as `DbParameter` instances instead of being concatenated into SQL. Adding filters does not change the result `record` type.

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
        return await connection
            .Query(Tables.Users.All(), transaction)
            .ReadAsync(cancellationToken);
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

        return connection.Query(query, transaction).ReadAsync(cancellationToken);
    }
}
```

`AppUsersRow`, `Tables.Users`, `Id`, and `DisplayName` in this example are generated from the `app.users` schema in the sample. `id` has the C# type `int`. When the `WhereIf` condition is `false`, its function is not called. The filter is added only when the condition is `true`.

## Conditions

`Where`, `WhereIf`, and `DeleteWhere` take a predicate built from a generated column. Every compared value is passed as a `DbParameter` instead of being written into the SQL text.

| Member | SQL |
| --- | --- |
| `Equal(value)`, `NotEqual(value)` | `= @p`, `<> @p`. A null value writes `IS NULL` or `IS NOT NULL` |
| `IsNull()`, `IsNotNull()` | `IS NULL`, `IS NOT NULL` |
| `LessThan(value)`, `LessThanOrEqual(value)`, `GreaterThan(value)`, `GreaterThanOrEqual(value)` | `<`, `<=`, `>`, `>=` |
| `column < value`, `column <= value`, `column > value`, `column >= value` | the same four comparisons written as C# operators |
| `Like(pattern)`, `NotLike(pattern)` | `LIKE @p`, `NOT LIKE @p` |
| `In(values)`, `NotIn(values)` | `IN (@p0, @p1)`, `NOT IN (@p0, @p1)` |
| `Between(low, high)`, `NotBetween(low, high)` | `BETWEEN @p0 AND @p1`, `NOT BETWEEN @p0 AND @p1` |

`And`, `Or`, and the `&&` and `||` operators combine two predicates. Each combination is parenthesized in the generated SQL, so mixing AND and OR keeps the grouping the C# code has. `&&` and `||` read both sides instead of short-circuiting, because both sides are parts of one SQL condition. The `&` and `|` operators do the same thing and stay available.

```csharp
var query = Tables.Users
    .Where(
        (Tables.Users.DisplayName.Like("a%") || Tables.Users.DisplayName.IsNull())
            && Tables.Users.Id.In(1, 2, 3))
    .Where(Tables.Users.CreatedAt.LessThan(cutoff));
```

The query above runs as:

```sql
SELECT "id", "email", "display_name", "created_at" FROM "app"."users"
WHERE (("display_name" LIKE @__cobaltum_where_0 OR "display_name" IS NULL)
  AND "id" IN (@__cobaltum_where_1, @__cobaltum_where_2, @__cobaltum_where_3))
  AND "created_at" < @__cobaltum_where_4
```

Separate `Where` calls are joined with AND. `WhereIf` adds a condition only when its flag is set, and `AndIf` and `OrIf` do the same inside one predicate. `CobaltumPredicate.All` and `CobaltumPredicate.Any` combine a list of predicates with AND or OR.

```csharp
var filters = new List<CobaltumPredicate<AppUsersRow>>();
if (email != null)
{
    filters.Add(Tables.Users.Email.Equal(email));
}

if (prefix != null)
{
    filters.Add(Tables.Users.DisplayName.Like(prefix + "%"));
}

var query = filters.Count == 0
    ? Tables.Users.All()
    : Tables.Users.Where(CobaltumPredicate.All(filters));
```

`In` and `NotIn` need at least one value and reject null inside the list; write `IsNull` for a null check. The relational comparisons, `Like`, and `Between` reject null for the same reason. `Like` passes the pattern as a parameter, so escaping `%` and `_` in a literal value is the caller's job.

## Record `INSERT`, `UPDATE`, and `DELETE`

The same table object builds single-row write statements from a generated `record`. `connection.Query(...)` takes the statement and `ExecuteAsync` runs it, returning the affected row count. These members cover the cases where writing the SQL by hand adds nothing. Anything past one row matched by its primary key is written as SQL.

| Member | Statement | Result |
| --- | --- | --- |
| `Insert(record)` | `INSERT` without the columns the database assigns | affected row count |
| `InsertReturning(record)` | `INSERT` reporting the stored row | the table `record` |
| `Update(record)` | `UPDATE` matched by primary key | affected row count |
| `Delete(record)` | `DELETE` matched by primary key | affected row count |
| `DeleteWhere(predicate)` | `DELETE` matched by a predicate | affected row count |

```csharp
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm;
using CobaltumOrm.Sample.Generated;

public static class UsersWriter
{
    public static async Task<AppUsersRow> AddAsync(
        DbConnection connection,
        string email,
        CancellationToken cancellationToken = default)
    {
        var stored = await connection
            .Query(Tables.Users.InsertReturning(
                new AppUsersInsertRow(email, null, DateTimeOffset.UtcNow)))
            .ReadAsync(cancellationToken);

        return stored[0];
    }

    public static Task<int> RenameAsync(
        DbConnection connection,
        AppUsersRow user,
        string displayName,
        CancellationToken cancellationToken = default) =>
        connection
            .Query(Tables.Users.Update(user with { DisplayName = displayName }))
            .ExecuteAsync(cancellationToken);

    public static Task<int> RemoveAsync(
        DbConnection connection,
        AppUsersRow user,
        CancellationToken cancellationToken = default) =>
        connection.Query(Tables.Users.Delete(user)).ExecuteAsync(cancellationToken);
}
```

`Insert` and `InsertReturning` take a second generated `record`, `AppUsersInsertRow`, that holds the columns the statement writes. Columns the database assigns, such as an identity primary key, are left out of both the statement and this `record`, so there is no unused value to pass. Every other column is written with the value the `record` holds, including columns that declare a SQL default. `Update` and `Delete` take the table `record`, because they need the primary key.

`InsertReturning` is generated for PostgreSQL and SQLite as `INSERT ... RETURNING`, and for SQL Server as `INSERT ... OUTPUT INSERTED.*`. MySQL and Oracle have no form CobaltumORM generates, so they get `Insert` only. A SQL Server table with triggers rejects `OUTPUT` without `INTO`; read the row back with a separate query in that case.

`Update` writes every column that is neither part of the primary key nor an identity column, and matches the row by the full primary key. `Delete` matches the row the same way. A table without a primary key gets `Insert` and `DeleteWhere` but neither `Update` nor `Delete`, because no column identifies one row. A table whose columns are all part of the primary key gets no `Update`, because there is nothing left to write.

`DeleteWhere` takes the same predicates `Where` takes, including conditions combined with `&&` and `||`. Every compared value becomes a `DbParameter`.

```csharp
await connection
    .Query(Tables.Users.DeleteWhere(Tables.Users.Email.Equal(address)))
    .ExecuteAsync(cancellationToken);
```

These statements are built from the schema the migrations produce, so renaming or dropping a column makes the affected call fail to compile. Concurrency, soft deletes, auditing, and batching are not handled. Use `Query` with SQL when a write needs any of them.

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

A statement that does not return rows, such as an INSERT, UPDATE, DELETE, or TRUNCATE without RETURNING, generates a command instead: a parameter type, a `CobaltumCommandDefinition`, and an async method that returns the affected row count. `[Query<TResult>]` requires a statement that returns rows.

Use `[Query<TResult>]` to use an existing result type. The parameter type, query definition, and async method are still generated, but `TResult` is not generated.

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

Supported `SELECT` syntax includes CTEs, recursive CTEs, `VALUES`, derived tables, correlated subqueries, `DISTINCT ON`, set operations, joins, filters, grouping, ordering, `LIMIT` / `OFFSET` / `FETCH`, row locking, `CASE`, casts, date and interval literals, `ARRAY[...]` constructors and subscripts, `ANY` / `ALL`, `unnest`, `generate_subscripts`, inline or named windows, aggregate `FILTER`, and common scalar and aggregate functions. PostgreSQL operators covered by the analyzer include `ILIKE`, regular-expression matching, `IS DISTINCT FROM`, JSON access, array and JSON containment, array overlap, modulo, and exponentiation. See the [supported PostgreSQL SELECT syntax](docs/design/poc-sql-type-inference.md#supported-select-syntax) for details.

PostgreSQL columns such as `integer[]`, `text[]`, and `uuid[]` generate `int[]`, `string[]`, and `Guid[]`. A nullable array column generates a nullable array reference such as `string[]?`. Array parameters use the same CLR element mapping. Generated queries set the PostgreSQL array type name on Npgsql parameters and read results with the corresponding CLR array type.

`WithParameter` passes values as `DbParameter` instances instead of concatenating them into SQL. For checked statements, the parameter `DbType` is inferred. PostgreSQL type names are also passed to the database provider for `json`, `jsonb`, and array parameters. Missing parameter values are detected before `ReadAsync` runs.

## Caller-supplied result types

`Query<TResult>(sql)` and `[Query<TResult>(name, sql)]` map returned rows to an existing type. For checked `Query`, the build compares returned column names, CLR types, and nullability with the selected constructor or writable members. It reports a compile error when the mapping is incompatible. No result type is generated when `TResult` is specified.

Names are matched without case or punctuation differences, so `display_name` matches `DisplayName`. `[ResultColumn("column_name")]` sets an explicit column name on a constructor parameter, property, or field. `[ResultColumn]` without an argument uses the parameter or member name. The attribute can be omitted when the default name matching is sufficient.

```csharp
using System.Data.Common;
using CobaltumOrm;

public readonly record struct UserId(long Value);

public sealed class UserIdHandler : IValueHandler<UserId>
{
    public UserId Read(DbDataReader reader, int ordinal) =>
        new UserId(reader.GetInt64(ordinal));
}

public sealed record UserView(
    [ResultColumn("id"), ValueHandler<UserIdHandler>] UserId Id,
    [ResultColumn] string? DisplayName);

[Query<UserView>("All", "SELECT id, display_name FROM users")]
public static partial class UserQueries
{
}

var rows = await connection
    .Query<UserView>("SELECT id, display_name FROM users")
    .ReadAsync();
```

`ValueHandler<THandler>` assigns a handler to one value. `IValueHandler<TValue>` reads the column directly from `DbDataReader`. `IValueHandler<TSource, TValue>` receives the CLR value inferred from the SQL column and converts it to the result member type.

The two-type handler also maps arrays. When a checked query returns `TSource[]` and the result member is `TValue[]`, a handler that implements `IValueHandler<TSource, TValue>` is applied to every element. A handler that implements `IValueHandler<TSource[], TArray>` converts the complete array to a wrapper or another non-array type.

```csharp
public readonly record struct CustomInt(int Value);
public sealed record CustomIntArray(int[] Values);

public sealed class CustomIntHandler : IValueHandler<int, CustomInt>
{
    public CustomInt Convert(int value) => new(value);
}

public sealed class CustomIntArrayHandler : IValueHandler<int[], CustomIntArray>
{
    public CustomIntArray Convert(int[] values) => new(values);
}

public sealed record ArrayView(
    [ValueHandler<CustomIntHandler>] CustomInt[] Numbers,
    [ResultColumn("numbers_copy"), ValueHandler<CustomIntArrayHandler>] CustomIntArray Wrapped);
```

Conversion handlers require checked `Query` SQL because the source CLR type must be known at build time. `NoCheckQuery<TResult>` continues to support `IValueHandler<TValue>`, which reads the column itself. To control the entire row, put `[ResultHandler<THandler>]` on the result type and implement `IResultHandler<TResult>`. A custom handler takes responsibility for the conversion it controls, while the SQL itself remains checked by `Query`.

Handler types must have a public parameterless constructor. One instance is cached and called directly from generated code, so handlers must be stateless and thread-safe. Result mapping does not scan types or invoke members through reflection at runtime.

A generated table `record` such as `AppUsersRow` can be used as `TResult`. The build writes the table records before the compiler runs, so a query that selects the columns of one table maps to its record without declaring a second type.

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

Compile-time constant `INSERT`, `UPDATE`, and `DELETE` statements without `RETURNING` run with `ExecuteAsync`. A statement with `RETURNING` runs with `ReadAsync`. It gets a generated result `record` unless a result type is supplied to `Query<TResult>`.

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

The compile-time analyzer does not cover every PostgreSQL construct. Unsupported forms include `MERGE`, table functions in `FROM` other than `unnest` and `generate_subscripts`, `GROUPING SETS`, `CUBE`, `ROLLUP`, multidimensional array types and constructors, array slices, and user-defined function result types. Window frame clauses are accepted but their contents are not semantically validated. Use `NoCheckQuery` when one of these forms is required.

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

`NoCheckQuery<TResult>(sql)` uses the same generated mapping rules when a result type is needed for dynamic SQL. The build can check the structure of `TResult` and its handler declarations, but it cannot compare them with dynamic SQL. Missing, duplicate, null, or incompatible columns therefore fail while the data reader reads the row. Extra columns are ignored by the generated default mapper.

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

The source generator creates `CobaltumMigrationCatalog.All` without scanning assemblies at runtime. Pass this catalog to `MigrationRunner` and `MigrationProjectHost`, as shown in the migration examples above. A handwritten catalog can use `MigrationInfo.Create<TMigration>(version, description)`. Caller-supplied query result types and their custom handlers are also called through generated code without runtime reflection.

CobaltumORM generates direct Npgsql parameter configuration for PostgreSQL `json`, `jsonb`, and array types; other generated provider bindings use `DbType`. PostgreSQL projects must reference Npgsql, as shown in the installation and migration project examples.

The selected ADO.NET driver must also support the target deployment mode. Resolve any trim or AOT warning reported from the driver before publishing.

## Supported environments

- Projects must use the .NET SDK project format.
- CoreCLR and Mono applications built with the .NET SDK / MSBuild are supported.
- Trimmed and Native AOT applications targeting .NET 8 or later are supported when generated migration catalogs are used.
- Compile-time `Query` checking and result type generation are unavailable with classic `mcs` / `xbuild` and normal Unity / IL2CPP projects.

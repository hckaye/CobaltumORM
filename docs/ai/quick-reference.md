# CobaltumORM quick reference

English | [日本語](quick-reference.ja.md)

Select query APIs from the table below. Generated names, provider settings, and required
verification commands follow. Start coding-agent setup with [agent-tools.md](agent-tools.md). Full
documentation is in [README.md](../../README.md), worked examples are in [recipes.md](recipes.md),
and build errors are in [diagnostics.md](diagnostics.md).

## What CobaltumORM does

SQL is written by hand. The build parses it, checks it against the schema that the migrations
produce, and generates the C# that executes it. No database connection is made during the build.
There is no change tracking and no `SaveChanges`. Every query and command is executed explicitly.

## Choosing a query API

| Situation | API | Result type |
| --- | --- | --- |
| SQL is a compile-time constant, or interpolated with value holes only | `connection.Query(sql)` | a `record` generated per call site |
| The rows must map to a type that already exists | `connection.Query<T>(sql)` | `T` |
| The same SQL is used in more than one place | `[Query("Name", sql)]` on a `partial class` | a generated `NameResult` |
| A named query mapping to an existing type | `[Query<T>("Name", sql)]` | `T` |
| The SQL text is only known at runtime, or uses syntax the analyzer does not support | `connection.NoCheckQuery(sql)` | `CobaltumRawRow` |
| Unchecked SQL mapped to an existing type | `connection.NoCheckQuery<T>(sql)` | `T` |
| Selecting from one table with optional filters | `Tables.<Table>.Query().Where(...)` | the generated table record |

`QueryDynamic(sql)` is the older name for `NoCheckQuery(sql)`. Write `NoCheckQuery` in new code.

## Rules the build enforces

- `Query(sql)` accepts a compile-time constant string or an interpolated string. A non-constant
  string is rejected with [COB007](diagnostics.md#cob007) or [COB100](diagnostics.md#cob100).
- Interpolation holes stand for values only. `$"SELECT {columns} FROM t"` is rejected. Each hole
  becomes a `DbParameter` named `@__cobaltum_value_0` and upward.
- Interpolated `INSERT`, `UPDATE`, and `DELETE` are not accepted by `Query`. Use constant SQL with
  `WithParameter`.
- A checked `Query` that returns rows must contain exactly one statement.
- DDL in a `Query` is rejected. Schema changes belong in migrations.
- Names and types passed to `WithParameter` are compared with the parsed SQL.
- `Query<T>` compares the returned column names, CLR types, and nullability with the constructor
  or the writable members of `T`. Names match without case or punctuation differences, so
  `display_name` matches `DisplayName`. `[ResultColumn("name")]` sets an explicit column name.
- `NoCheckQuery<T>` checks the shape of `T` but cannot compare it with the SQL. A mismatch throws
  while the data reader reads the row.

## Migrations

- A C# migration derives from `Migration` and carries `[Migration(version)]` or
  `[Migration(version, description)]`. The version is a positive 64-bit integer.
- `Up` defines the schema the build checks queries against. `Down` is used for runtime rollbacks.
- Every argument that affects the schema must be a compile-time constant. Names, lengths,
  precision, scale, default literals, and the SQL passed to `Execute.Sql` are read at build time.
- `if`, loops, and helper methods inside `Up` are rejected with [COB001](diagnostics.md#cob001).
- `Execute.Script`, `Execute.EmbeddedScript`, `Execute.WithConnection`, the predicate overload of
  `IfDatabase`, and `IfDatabase.Delegate` cannot be evaluated at build time. Add a
  Flyway-compatible SQL file as an `AdditionalFiles` entry when a script must change the schema
  used for query generation.
- Flyway-compatible SQL files use `V<version>__<description>.sql`. They have no down SQL, so a
  rollback that includes one is rejected before it starts.
- C# migrations and SQL files must not share a version. They are applied in ascending version
  order.

## Generated API surface

The generator writes these types into `CobaltumOrmGeneratedNamespace`, which defaults to
`CobaltumOrm.Generated`.

| Generated name | What it is |
| --- | --- |
| `SqlSchema.Schemas.<Schema>` | schema name as a quoted `const string` |
| `SqlSchema.Tables.<Table>.Name` | schema-qualified table name |
| `SqlSchema.Tables.<Table>.Columns.<Column>` | quoted column name |
| `<Table>Row` | `public sealed record` for one table row |
| `Tables.<Table>` | table object with `Query()`, `All()`, and `Where(...)` |
| `Tables.<Table>.<Column>` | typed column supporting `Equal(value)` |
| `<Container>.<Name>Result` | result record for a named query |
| `<Container>.<Name>Parameters` | parameter record for a named query |
| `<Container>.<Name>` | `CobaltumQueryDefinition<TParameters, TResult>` |
| `<Container>.<Name>Async` | async method taking the connection and each parameter |
| `CobaltumMigrationCatalog.All` | the ordered migration list, built without assembly scanning |

`SqlSchema` contains only names that exist in the current schema. Renaming a column in a migration
removes the old member, so SQL that still uses it fails to compile.

## Providers

| Database | `CobaltumOrmDatabaseProvider` | Driver package | Migration adapter |
| --- | --- | --- | --- |
| PostgreSQL | `PostgreSql` | `Npgsql` | `PostgreSqlMigrationAdapter` |
| MySQL | `MySql` | `MySqlConnector` | `MySqlMigrationAdapter` |
| SQLite | `Sqlite` | `Microsoft.Data.Sqlite` | `SqliteMigrationAdapter` |
| SQL Server | `SqlServer` | `Microsoft.Data.SqlClient` | `SqlServerMigrationAdapter` |
| Oracle | `Oracle` | `Oracle.ManagedDataAccess.Core` | `OracleMigrationAdapter` |

The property defaults to `PostgreSql`. It must be listed as a `CompilerVisibleProperty` for the
build to read it; an unknown value produces [COB008](diagnostics.md#cob008). PostgreSQL is the
primary target and has the widest analyzer coverage. Oracle PL/SQL blocks are not analyzed.

## Build verification is required

The SQL checks run during compilation, so unbuilt code is unverified code. After changing any
query, migration, result type, or provider setting, run the build and read the output.

```console
dotnet build
dotnet test
```

A build failure whose code starts with `COB` comes from CobaltumORM. Look the code up in
[diagnostics.md](diagnostics.md). Roslyn reports the same URL as the diagnostic help link.

## Where the checks stop

The analyzer covers the part of PostgreSQL used for CRUD. It does not check permissions,
constraints, triggers, or anything that depends on stored data. `MERGE`, `GROUPING SETS`, `CUBE`,
`ROLLUP`, array slices, multidimensional arrays, table functions other than `unnest` and
`generate_subscripts`, and user-defined function result types are outside the supported syntax.
Use `NoCheckQuery` for those.

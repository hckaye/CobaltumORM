# PoC: SQL Type Inference Engine

## Goal

Given a database schema and a supported SQL statement, validate its syntax and referenced
schema objects at compile time without a database connection or query execution. For
`SELECT`, also infer result column names, CLR types, nullability, and query parameters.

The analysis library and its test suite run independently. The C# Incremental Source
Generator (ISG) uses the same analysis entry point.

## Hard constraints

1. The core library targets `netstandard2.0` so a Roslyn source generator can load it.
   `LangVersion` may be `latest`. It has no external dependencies such as ANTLR or a
   NuGet parser package. SQL is handled by a hand-written lexer and recursive-descent
   parser.
2. Analysis is deterministic and has no I/O or global state. It uses one entry point:
   ```csharp
   AnalysisResult QueryAnalyzer.Analyze(DatabaseSchema schema, string sql);
   ```
3. CLR types are represented as strings, for example `"int"`, `"string?"`, and
   `"DateOnly"`. The generator emits source text, and some mapped types such as
   `DateOnly` do not exist on `netstandard2.0`, so the analyzer does not use
   `System.Type`.
4. Errors are diagnostics with source spans into the SQL string. They do not escape as
   exceptions. This lets the ISG report compiler errors. `AnalysisResult` may contain
   columns and parameters alongside diagnostics when partial recovery is possible.

## Dialect

PostgreSQL, subset. Identifier quoting with `"..."`, case-insensitive unquoted
identifiers, standard string literals `'...'`.

## Schema model

Migrations produce an immutable `DatabaseSchema`. It contains `Table` entries with an
optional schema name, a table name, and `Column` entries. Each column records its name,
SQL type, nullability, primary-key flag, and opaque default expression.

| PostgreSQL type | CLR type |
| --- | --- |
| `boolean` | `bool` |
| `smallint` | `short` |
| `integer` | `int` |
| `bigint` | `long` |
| `real` | `float` |
| `double precision` | `double` |
| `numeric` | `decimal` |
| `text`, `varchar(n)`, `char(n)` | `string` |
| `json`, `jsonb` | `string` |
| `uuid` | `Guid` |
| `date` | `DateOnly` |
| `time` | `TimeOnly` |
| `timestamp` | `DateTime` |
| `timestamptz` | `DateTimeOffset` |
| `interval` | `TimeSpan` |
| `bytea` | `byte[]` |
| `T[]` for a supported element type | corresponding CLR `T[]` |

Parameters mapped from `json`, `jsonb`, or a PostgreSQL array retain the PostgreSQL type name. Nullable
columns use `T?`, including `string?`, with nullable reference types enabled in the
consumer. `time with time zone` and `timetz` are unsupported because `TimeOnly` would
discard their offset; the analyzer reports a diagnostic for those types.

## Supported SELECT syntax

- Select list: `*`, `t.*`, qualified and unqualified columns, aliases, literals,
  `DISTINCT`, and `DISTINCT ON`.
- Query composition: nonrecursive and recursive CTEs, standalone and table-source
  `VALUES`, derived tables, scalar subqueries, correlated `EXISTS`, subquery `IN`, and
  `UNION`, `INTERSECT`, or `EXCEPT` with `ALL` or `DISTINCT`.
- Expressions: arithmetic `+ - * / % ^`, string `||`, comparisons, boolean operators,
  `IS [NOT] NULL`, `IS [NOT] TRUE/FALSE/UNKNOWN`, `IS [NOT] DISTINCT FROM`, `LIKE`,
  `ILIKE`, regular-expression matching, `IN`, `BETWEEN`, JSON access, containment,
  overlap, `CASE`, `CAST`, PostgreSQL `::` casts, date/time/interval literals, current
  date and timestamp values, `ARRAY[...]` constructors, array subscripts, `ANY` / `ALL`, and
  common temporal arithmetic.
- Functions: `COUNT`, `SUM`, `AVG`, `MIN`, `MAX`, `LOWER`, `UPPER`, `LENGTH`, `ABS`,
  `COALESCE`, `NULLIF`, `GREATEST`, `LEAST`, `ROUND`, `CEIL`, `CEILING`, `FLOOR`,
  `SUBSTRING`, `TRIM`, `BTRIM`, `LTRIM`, `RTRIM`, `NOW`, `TRANSACTION_TIMESTAMP`,
  `STATEMENT_TIMESTAMP`, `CLOCK_TIMESTAMP`, string length and transformation functions,
  `CONCAT`, `CONCAT_WS`, `REPLACE`, `STRPOS`, `REPEAT`, `POWER`, `RANDOM`, `DATE_TRUNC`,
  `DATE_PART`, `EXTRACT`, and `TO_CHAR`. An unknown function produces a diagnostic.
- Window functions: `ROW_NUMBER`, `RANK`, `DENSE_RANK`, `LAG`, `LEAD`, `FIRST_VALUE`,
  and `LAST_VALUE`. Inline and named window specifications support inheritance,
  `PARTITION BY`, and `ORDER BY`. Frame-clause text is accepted but is not semantically
  validated. Aggregate `DISTINCT` and `FILTER` are supported.
- `FROM` with table aliases, derived tables, lateral derived tables, `unnest`, and
  `generate_subscripts`. Joins include
  inner, left, right, full, cross, natural, `ON`, and `USING` forms.
- `WHERE`, `GROUP BY`, `HAVING`, `ORDER BY` with `NULLS FIRST` or `NULLS LAST`,
  `LIMIT`, `OFFSET`, `FETCH FIRST/NEXT`, and PostgreSQL row-locking clauses.
- Parameters use the `@name` form. Their CLR types are inferred from context. Conflicting
  contexts and parameters without an inferable type produce diagnostics.

## Supported data modification syntax

- `INSERT INTO table [(column, ...)] VALUES ...`, `DEFAULT VALUES`, and `INSERT ... SELECT`.
- `ON CONFLICT` with a column or constraint target, `DO NOTHING`, or `DO UPDATE`.
- `UPDATE table [AS alias] SET ... [FROM ...] [WHERE ...]`.
- `DELETE FROM table [AS alias] [USING ...] [WHERE ...]`.
- `TRUNCATE [TABLE] table [, ...]` with identity and cascade options.
- `RETURNING` on `INSERT`, `UPDATE`, and `DELETE`. Returned columns receive the same type
  and nullability analysis as a select list.
- CTEs can wrap data modification statements, and a data-modifying CTE with `RETURNING`
  can be referenced by the main statement.
- Schema-qualified tables and referenced columns are resolved against the migration-built
  schema. The analyzer validates assignment compatibility, boolean conditions, duplicate
  targets, row widths, source query widths, and parameter types.

The parser does not support `MERGE`, table functions in `FROM` other than `unnest` and
`generate_subscripts`, `GROUPING SETS`, `CUBE`, `ROLLUP`, multidimensional array types
and constructors, array slices, or user-defined function result types.

## Inference rules

### Name resolution

- Unqualified columns are resolved across all tables in scope. An ambiguous reference
  produces a diagnostic.
- An unknown table, column, or alias produces a diagnostic with a source span.
- An explicit alias determines the output column name. A bare column reference uses the
  source column name. Other expressions use deterministic defaults such as `case`,
  `coalesce`, or the function name.

### Nullability

- Base column nullability from schema.
- `LEFT JOIN` makes all columns of the right-side table nullable in the result;
  `RIGHT JOIN` the left side; `FULL JOIN` both sides. Effects propagate through chained
  joins (a table already forced-nullable stays nullable).
- Literals are non-null; bare `NULL` literal is null of unknown type (usable in
  `COALESCE`/`CASE`/`CAST` contexts; a result column that is *only* `NULL` without a
  cast produces a diagnostic).
- `COALESCE(a, b, ...)` is nullable only when all arguments are nullable. Its type is the
  unified argument type.
- `NULLIF(a, b)`: always nullable, type of `a`.
- `CASE`: type = unified type of all branch results; nullable if any branch nullable or
  `ELSE` missing.
- `COUNT(...)` uses `long` and is never null.
- With `GROUP BY`, `SUM`, `AVG`, `MIN`, and `MAX` are nullable when their argument is
  nullable. Without `GROUP BY`, they are always nullable because the input can be empty.
- `SUM` uses `long` for integer arguments and `decimal` for `numeric`. `AVG` uses
  `decimal` for integer or `numeric` arguments and `double` for floating-point
  arguments. `MIN` and `MAX` retain the argument type.
- Arithmetic on numeric types: unify via the usual widening lattice
  (`short`, `int`, `long`, `decimal`; and `float`, `double`). Mixing an integer type with
  `numeric` produces `decimal`, while mixing with a floating-point type produces
  `double`. The result is nullable if either operand is nullable. Integer division uses
  `int` for `int / int`. A type mismatch such as `text + int` produces a diagnostic.
- Comparison/logical operators yield `bool`; used only in boolean contexts
  (WHERE/HAVING/ON/CASE WHEN); a non-bool expression in a boolean context produces a
  diagnostic.

## Deliverables & layout

```
CobaltumOrm.sln
src/CobaltumOrm.Analysis/          netstandard2.0, zero deps
  (lexer, parser + AST, schema model, binder/type-checker, diagnostics)
tests/CobaltumOrm.Analysis.Tests/  net10.0, xUnit
docs/design/poc-sql-type-inference.md   (this file; update the "Result" section below)
```

- Comprehensive table-driven tests: every inference rule above needs positive tests, and
  every diagnostic needs at least one negative test. Include end-to-end cases like:
  ```sql
  SELECT u.id, u.name, COUNT(o.id) AS order_count, SUM(o.total) AS total_spent
  FROM users u LEFT JOIN orders o ON o.user_id = u.id
  WHERE u.created_at > @since
  GROUP BY u.id, u.name
  ```
  Expected result columns are `id:int`, `name:string`, `order_count:long`, and
  `total_spent:decimal?`. The `@since` parameter uses `DateTime`.
- `dotnet build` warning-free (TreatWarningsAsErrors), `dotnet test` green.
- Keep the parser/AST layered so later scope growth (subqueries, other statements) is
  additive: lexer / parser / binder in separate files, AST nodes as records where the
  target framework allows.

## Result

The PoC consists of a `netstandard2.0` analysis library and a `net10.0` xUnit test
project. The core project has no explicit `PackageReference` entries. Its only reported
package is the SDK's automatic `NETStandard.Library` reference. The implementation has
immutable public models, hand-written lexers, recursive-descent parsers and ASTs, and a
binder/type checker. Its entry points are `QueryAnalyzer.Analyze` and
`PostgreSqlMigrationAnalyzer.Analyze`.

The PostgreSQL migration analyzer has a separate dialect layer. It applies
semicolon-separated migration SQL to a new schema without mutating the input schema.
`ISchemaMigrationAnalyzer` and `PostgreSqlSchemaMigrationAnalyzer` define the
instance-based dialect boundary. The analyzer handles comments, quoted and unquoted
identifiers, schema-qualified table names, supported PostgreSQL type modifiers, opaque
default expressions, table creation and removal, column and table changes, nullability,
and primary keys.

`serial`, `smallserial`, and `bigserial` use the corresponding integer CLR types. `json`
and `jsonb` use `string` in generated code, while parameters retain their PostgreSQL type
name so providers do not send them as `text`. Invalid SQL, name-resolution failures,
type errors, unsupported functions or types, invalid schema changes, and parameter
inference failures return diagnostics with SQL source spans. Malformed input does not
escape from either analyzer as an exception.

The binder covers the supported SELECT syntax. This includes CTE and subquery scopes,
wildcard expansion, quoted and unquoted identifier rules, joins and propagated
nullability, set-operation compatibility, clause-level boolean checks, numeric widening,
SQL three-valued-logic nullability, CASE and casts, the listed functions and aggregate
rules, array expressions and table functions, aggregate placement, nested-aggregate
validation, GROUP BY compatibility checks, window expressions, and contextual parameter
inference with conflict detection. Results can retain inferred columns and parameters
alongside diagnostics.

The same lexer, parser, and binder validate the supported `INSERT`, `UPDATE`, and
`DELETE` forms. Data modification is checked for syntax, schema, table, and column
existence, assignment compatibility, boolean conditions, row widths, duplicate target
columns, source query compatibility, conflict actions, returned columns, and contextual
parameter types. A statement with `RETURNING` exposes result columns to the generator.

An explicit alias determines the result column name. A bare column uses its source
spelling. An unquoted function uses its lowercase function name, such as `coalesce` or
`count`. CASE uses `case`, CAST uses `cast`, and other expressions use `?column?`.
Parameter names are matched case-insensitively and retain the spelling of their first
occurrence.

The test suite covers mapped SQL types, inference rules, supported operators and
functions, CTEs, subqueries, set operations, window functions, outer-join propagation,
the end-to-end example, aggregate placement and grouping errors, schema-qualified DDL
changes, comments and quoting, diagnostics with source spans, DML name and type
validation, `RETURNING` code generation, malformed input, and unsupported statement
forms. Release builds and tests run with `TreatWarningsAsErrors`.

### Remaining scope notes

Comparisons across date/time families (`date = timestamp`) are currently rejected as
incompatible, which is stricter than PostgreSQL; revisit this with explicit implicit-cast
rules if the supported query subset grows.

- Comparison and logical result columns use `bool?` when SQL three-valued logic can
  produce UNKNOWN, even though their base type is `bool`. The generator's public contract
  needs to retain this distinction.
- `@name` parameter case rules are defined by CobaltumORM rather than PostgreSQL. The
  analyzer and generator both use case-insensitive matching.
- PostgreSQL `numeric` can exceed CLR `decimal` precision. The current mapping uses
  `decimal`; runtime materialization needs a documented overflow policy.

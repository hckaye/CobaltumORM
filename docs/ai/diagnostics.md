# CobaltumORM build diagnostics

English | [日本語](diagnostics.ja.md)

When a build error appears, find its code below to see what caused it and how to fix it. Read
[quick-reference.md](quick-reference.md) for the rules behind each code, and
[recipes.md](recipes.md) for working examples.

Two components report these codes. The Roslyn source generator reports `COB001` through `COB009`
as normal compiler diagnostics, each carrying this page as its help link. The build transform,
which runs inside MSBuild and inside `cobaltum generate`, reports `COB010` and `COB100` through
`COB109` as MSBuild errors with a file and line but no link.

Some messages embed a second code from the SQL analyzer. `SQL` codes come from the query parser
and binder, `DDL` codes from the migration parser. They identify the position inside the SQL and
are explained by the message text.

## Generator diagnostics

### COB001

Migration cannot be analyzed safely.

The build reads `Up` as source and applies its operations to an in-memory schema, so `Up` may
contain only supported migration call chains and local `const` declarations. `if`, loops, helper
method calls, unsupported chain roots, and the predicate overload of `IfDatabase` cannot be
evaluated without running the code.

Move the logic into separate migrations, or express the schema change as a Flyway-compatible SQL
file added through `AdditionalFiles`.

### COB002

Migration argument must be constant.

Names, lengths, precision, scale, default literals, `IfDatabase` database names, `RawSql.Insert`
text, and the SQL passed to `Execute.Sql` are read at build time, so each must be a compile-time
constant, a `SystemMethods` value, or a `RawSql.Insert` call. An empty string argument is rejected
for the same reason.

Replace the computed argument with a literal or a `const` field.

### COB003

Migration SQL is invalid.

SQL inside `Execute.Sql` or a Flyway-compatible file failed to parse, or it contains a schema
operation that may change a query result and is not supported by compile-time analysis. The message
carries the `DDL` code and the parser message.

Fix the SQL, or express the change with the C# migration API.

### COB004

Query SQL is invalid.

SQL in `[Query]`, `[Query<T>]`, or a `Query(...)` call failed to parse, referenced a schema, table,
or column that does not exist after all migrations are applied, or used a construct the analyzer
does not support. The message carries the `SQL` code and the analyzer message.

Correct the name or the syntax. If the construct is outside the supported syntax, use
[`NoCheckQuery`](recipes.md#run-sql-the-build-cannot-check).

### COB005

Generated name collides.

A named query would generate a member whose name is already declared in the container class.

Rename the query, or rename the conflicting member.

### COB006

Declaration is not supported by generation.

The declaration cannot be used as a migration or as a query container. A migration must be a
concrete non-generic class deriving from `CobaltumOrm.Migrations.Migration`, with a public
parameterless constructor and `Up` declared directly in source. A query container must be a
top-level non-generic `partial class`, and the `[Query]` arguments must be non-empty compile-time
string constants.

Adjust the declaration to match.

### COB007

Raw query cannot be validated at compile time.

`Query(string)` received SQL that is not known at compile time.

Make the SQL a constant, or call `NoCheckQuery` to state that the SQL is not checked.

### COB008

Generator configuration is invalid.

`CobaltumOrmGeneratedNamespace` is not a valid dot-separated C# namespace, or
`CobaltumOrmDatabaseProvider` is not one of `PostgreSql`, `MySql`, `Sqlite`, `SqlServer`, or
`Oracle`.

Correct the property value. Both properties must also appear as `CompilerVisibleProperty` items for
the build to read them.

### COB009

Query result type cannot be mapped.

The columns returned by the SQL cannot be mapped onto the type given to `[Query<T>]` or
`Query<T>(sql)`. A column is missing, its CLR type or nullability does not match, or the statement
does not return rows. When a constructor does not match, the message names the first unmatched
parameter, the column it expects, and the CLR type involved.

Align the constructor or writable members of the result type with the selected columns, or set
explicit names with `[ResultColumn("column_name")]`. To execute a statement that does not return
rows through `[Query]`, remove the type argument and use the generated command, which returns the
affected row count.

## Build transform diagnostics

### COB010

The source generator threw while `cobaltum generate` was running it.

The message is the exception message. This indicates a defect in CobaltumORM. Report it with the
migration and query sources that reproduce it.

### COB100

`Query` requires a compile-time constant or an interpolated string whose holes are all in value
positions.

Make the SQL constant, or call `NoCheckQuery` to state that the SQL is not checked. This is the
build-transform counterpart of [COB007](#cob007).

### COB101

The SQL in a `Query` call cannot be used as a checked statement.

The statement splitter failed, the SQL contains no statement, it contains a schema change, or a
`Query` that returns rows contains more than one statement.

Split the SQL into one statement per call, and declare schema changes as migrations.

### COB102

An interpolated `Query` was used for a statement that does not return rows.

Interpolated `INSERT`, `UPDATE`, and `DELETE` are not accepted.

Write the statement as constant SQL and bind values with `WithParameter`.

### COB103

The SQL type of an interpolation hole cannot be inferred.

The hole is not in a value position, so the parser produced no parameter for it. Interpolation
never expands into schema, table, or column names.

Write the SQL structure as text and use `SqlSchema` constants for names.

### COB104

An interpolation hole has the wrong CLR type.

The message names the type of the expression and the type the SQL requires. There is no implicit
conversion between them.

Convert the value, or change the column or expression the hole is compared with.

### COB105

The connection expression of a `Query` or `NoCheckQuery` call could not be resolved.

The transform rewrites the call site and needs the receiver as an expression it can copy.

Call the method on a `DbConnection` variable, field, or property instead of on the result of a
complex expression.

### COB106

An interpolation hole used an alignment or format clause.

`{value,10}` and `{value:d}` are not accepted, because the hole becomes a parameter rather than
formatted text.

Remove the clause and format the value in C# before passing it, or format it in SQL.

### COB107

A `WithParameter` name does not match the checked SQL.

The name is not one of the named parameters the SQL uses, it is bound twice, or the SQL already
receives that value through an interpolation hole.

Use the parameter names that appear in the SQL, once each.

### COB108

A `WithParameter` value has the wrong CLR type.

The message names the type of the value and the type the SQL requires. There is no implicit
conversion between them.

Convert the value before passing it.

### COB109

A query result cannot be mapped to the specified type.

`Query<T>` was applied to a statement that returns no rows, or the returned columns do not match
the constructor or writable members of `T`. When a constructor does not match, the message names
the first unmatched parameter, the column it expects, and the CLR type involved.
`NoCheckQuery<T>` reports the same code when the shape of `T` itself cannot be mapped.

Align `T` with the selected columns, set explicit names with `[ResultColumn("column_name")]`, or
select a statement that returns rows. This is the build-transform counterpart of
[COB009](#cob009).

using CobaltumOrm;
using CobaltumOrm.PostgreSql.E2E.Tests.Generated;

namespace CobaltumOrm.PostgreSql.E2E.Tests;

[Query(
    "FindByLocalTime",
    $"SELECT {SqlSchema.Tables.E2eValues.Columns.Id} FROM {SqlSchema.Tables.E2eValues.Name} " +
    $"WHERE {SqlSchema.Tables.E2eValues.Columns.LocalTime} = @local_time")]
[Query(
    "FindByDocument",
    $"SELECT {SqlSchema.Tables.E2eValues.Columns.Id} FROM {SqlSchema.Tables.E2eValues.Name} " +
    $"WHERE {SqlSchema.Tables.E2eValues.Columns.Document} = @document")]
[Query(
    "ReadNumericBoundaries",
    $"SELECT 2147483648 AS integer_literal, SUM({SqlSchema.Tables.E2eValues.Columns.BigId}) AS bigint_sum " +
    $"FROM {SqlSchema.Tables.E2eValues.Name}")]
[Query(
    "UpdateDocument",
    $"UPDATE {SqlSchema.Tables.E2eValues.Name} " +
    $"SET {SqlSchema.Tables.E2eValues.Columns.Document} = @document " +
    $"WHERE {SqlSchema.Tables.E2eValues.Columns.Id} = @id " +
    $"RETURNING {SqlSchema.Tables.E2eValues.Columns.Id}, {SqlSchema.Tables.E2eValues.Columns.Document}")]
public static partial class PostgreSqlE2EQueries
{
}

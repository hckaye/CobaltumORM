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
[Query(
    "FindByArray",
    $"SELECT {SqlSchema.Tables.E2eValues.Columns.Id}, {SqlSchema.Tables.E2eValues.Columns.Numbers}, " +
    $"{SqlSchema.Tables.E2eValues.Columns.Labels}, {SqlSchema.Tables.E2eValues.Columns.Identifiers} " +
    $"FROM {SqlSchema.Tables.E2eValues.Name} " +
    $"WHERE {SqlSchema.Tables.E2eValues.Columns.Numbers} @> @required " +
    $"AND @value = ANY({SqlSchema.Tables.E2eValues.Columns.Numbers}) " +
    $"AND @maximum >= ALL({SqlSchema.Tables.E2eValues.Columns.Numbers}) " +
    $"AND {SqlSchema.Tables.E2eValues.Columns.Labels} && @label_filter " +
    $"AND {SqlSchema.Tables.E2eValues.Columns.Identifiers} && @identifier_filter")]
[Query(
    "ReadArrayExpressions",
    $"SELECT ARRAY[7, 8, 9] AS constructed, {SqlSchema.Tables.E2eValues.Columns.Numbers}[2] AS second_item, " +
    $"{SqlSchema.Tables.E2eValues.Columns.Labels} " +
    $"FROM {SqlSchema.Tables.E2eValues.Name} WHERE {SqlSchema.Tables.E2eValues.Columns.Id} = @id")]
[Query(
    "ExpandNumbers",
    $"SELECT item FROM {SqlSchema.Tables.E2eValues.Name} " +
    $"CROSS JOIN unnest({SqlSchema.Tables.E2eValues.Columns.Numbers}) AS expanded(item) " +
    $"WHERE {SqlSchema.Tables.E2eValues.Columns.Id} = @id ORDER BY item")]
[Query(
    "ReadArraySubscripts",
    $"SELECT position FROM {SqlSchema.Tables.E2eValues.Name} " +
    $"CROSS JOIN generate_subscripts({SqlSchema.Tables.E2eValues.Columns.Numbers}, 1) AS subscripts(position) " +
    $"WHERE {SqlSchema.Tables.E2eValues.Columns.Id} = @id ORDER BY position")]
public static partial class PostgreSqlE2EQueries
{
}

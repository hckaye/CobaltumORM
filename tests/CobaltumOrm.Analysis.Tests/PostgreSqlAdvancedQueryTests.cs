using System.Linq;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class PostgreSqlAdvancedQueryTests
{
    [Fact]
    public void SupportsCtesDerivedTablesAndCorrelatedSubqueries()
    {
        var cte = TestSchema.Analyze(@"
            WITH active_users(user_id, display_name) AS (
                SELECT id, name FROM users WHERE active
            )
            SELECT a.user_id, a.display_name
            FROM active_users AS a
            WHERE EXISTS (
                SELECT 1 FROM orders o WHERE o.user_id = a.user_id
            )");
        var derived = TestSchema.Analyze(@"
            SELECT d.id, d.maximum
            FROM (
                SELECT user_id AS id, MAX(total) AS maximum
                FROM orders
                GROUP BY user_id
            ) AS d
            WHERE d.id IN (SELECT id FROM users)");
        var scalar = TestSchema.Analyze("SELECT (SELECT MAX(total) FROM orders) AS maximum");

        TestSchema.AssertColumns(cte, ("user_id", "int"), ("display_name", "string"));
        TestSchema.AssertColumns(derived, ("id", "int"), ("maximum", "decimal"));
        TestSchema.AssertColumns(scalar, ("maximum", "decimal?"));
    }

    [Fact]
    public void SupportsDataModifyingCtesAndTableColumnAliases()
    {
        var modifying = TestSchema.Analyze(@"
            WITH changed AS (
                UPDATE users SET active = false WHERE active RETURNING id, name
            )
            SELECT id, name FROM changed");
        var aliases = TestSchema.Analyze(
            "SELECT u.user_id, u.user_name FROM users AS u(user_id, user_name)");

        TestSchema.AssertColumns(modifying, ("id", "int"), ("name", "string"));
        TestSchema.AssertColumns(aliases, ("user_id", "int"), ("user_name", "string"));
    }

    [Fact]
    public void SupportsValuesStatementsCtesAndDerivedTables()
    {
        var direct = TestSchema.Analyze("VALUES (1, 'one'), (2, 'two') ORDER BY column1 LIMIT 1");
        var cte = TestSchema.Analyze(@"
            WITH labels(id, label) AS (VALUES (1, 'one'), (2, 'two'))
            SELECT id, label FROM labels");
        var derived = TestSchema.Analyze(@"
            SELECT v.id, v.label
            FROM (VALUES (1, 'one'), (2, 'two')) AS v(id, label)");

        TestSchema.AssertColumns(direct, ("column1", "int"), ("column2", "string"));
        TestSchema.AssertColumns(cte, ("id", "int"), ("label", "string"));
        TestSchema.AssertColumns(derived, ("id", "int"), ("label", "string"));
    }

    [Fact]
    public void InfersARecursiveCteFromItsNonRecursiveTerm()
    {
        var result = TestSchema.Analyze(@"
            WITH RECURSIVE numbers(n) AS (
                SELECT 1
                UNION ALL
                SELECT n + 1 FROM numbers WHERE n < 3
            )
            SELECT n FROM numbers");

        TestSchema.AssertColumns(result, ("n", "int"));
    }

    [Fact]
    public void SupportsDistinctSetOperationsAndAdditionalSelectClauses()
    {
        var distinct = TestSchema.Analyze(@"
            SELECT DISTINCT ON (user_id) user_id, total
            FROM orders
            ORDER BY user_id, total DESC NULLS LAST
            OFFSET 1 ROWS FETCH NEXT 5 ROWS ONLY");
        var union = TestSchema.Analyze(@"
            SELECT id AS value FROM users
            UNION ALL
            SELECT user_id FROM orders
            EXCEPT
            SELECT order_id FROM payments
            ORDER BY value");

        TestSchema.AssertColumns(distinct, ("user_id", "int"), ("total", "decimal"));
        TestSchema.AssertColumns(union, ("value", "int"));
    }

    [Fact]
    public void SupportsCrossNaturalAndUsingJoins()
    {
        var usingJoin = TestSchema.Analyze(
            "SELECT u.id, o.total FROM users u JOIN orders o USING (id)");
        var crossJoin = TestSchema.Analyze(
            "SELECT u.id, p.amount FROM users u CROSS JOIN payments p");
        var naturalJoin = TestSchema.Analyze(
            "SELECT u.id FROM users u NATURAL LEFT JOIN orders o");
        var usingStar = TestSchema.Analyze(
            "SELECT * FROM users u JOIN orders o USING (id)");

        TestSchema.AssertColumns(usingJoin, ("id", "int"), ("total", "decimal"));
        TestSchema.AssertColumns(crossJoin, ("id", "int"), ("amount", "decimal"));
        TestSchema.AssertColumns(naturalJoin, ("id", "int"));
        TestSchema.AssertSuccess(usingStar);
        Assert.Equal(19, usingStar.Columns.Count);
        Assert.Equal("id", usingStar.Columns[0].Name);
        Assert.Equal(1, usingStar.Columns.Count(column => column.Name == "id"));
    }

    [Fact]
    public void SupportsWindowFunctionsAggregateFiltersAndPostgreSqlOperators()
    {
        var windows = TestSchema.Analyze(@"
            SELECT
                ROW_NUMBER() OVER (PARTITION BY active ORDER BY id) AS position,
                COUNT(DISTINCT id) FILTER (WHERE active) OVER () AS active_count,
                LAG(name, 1, 'missing') OVER (ORDER BY id) AS prior_name
            FROM users");
        var operators = TestSchema.Analyze(@"
            SELECT
                name ILIKE 'a%' AS insensitive,
                name ~* '^[a-z]' AS matches,
                id IS DISTINCT FROM 1 AS different,
                ('{}'::jsonb ->> 'name') AS json_name,
                '{}'::jsonb @> '{}'::jsonb AS contains
            FROM users");

        TestSchema.AssertColumns(
            windows,
            ("position", "long"),
            ("active_count", "long"),
            ("prior_name", "string?"));
        TestSchema.AssertColumns(
            operators,
            ("insensitive", "bool"),
            ("matches", "bool"),
            ("different", "bool"),
            ("json_name", "string?"),
            ("contains", "bool"));
    }

    [Fact]
    public void SupportsNamedWindowsAndRowLockingClauses()
    {
        var result = TestSchema.Analyze(@"
            SELECT
                ROW_NUMBER() OVER ordered AS position,
                LAG(name) OVER (partitioned ORDER BY id) AS prior_name
            FROM users
            WINDOW partitioned AS (PARTITION BY active),
                   ordered AS (partitioned ORDER BY id)
            FOR UPDATE OF users NOWAIT");

        TestSchema.AssertColumns(
            result,
            ("position", "long"),
            ("prior_name", "string?"));
    }

    [Fact]
    public void SupportsTemporalAndIntervalLiteralsAndArithmetic()
    {
        var result = TestSchema.Analyze(@"
            SELECT
                DATE '2026-08-11' AS date_value,
                TIME '12:34:56' AS time_value,
                TIMESTAMP '2026-08-11 12:34:56' AS timestamp_value,
                CURRENT_TIMESTAMP AS current_value,
                CURRENT_SCHEMA AS schema_name,
                INTERVAL '2 days' AS duration,
                DATE '2026-08-11' + 2 AS later_date,
                TIMESTAMP '2026-08-11 12:34:56' - TIMESTAMP '2026-08-10 12:34:56' AS elapsed,
                EXTRACT(DAY FROM INTERVAL '2 days') AS days,
                DATE_TRUNC('day', CURRENT_TIMESTAMP) AS day_start");

        TestSchema.AssertColumns(
            result,
            ("date_value", "DateOnly"),
            ("time_value", "TimeOnly"),
            ("timestamp_value", "DateTime"),
            ("current_value", "DateTimeOffset"),
            ("schema_name", "string"),
            ("duration", "TimeSpan"),
            ("later_date", "DateOnly"),
            ("elapsed", "TimeSpan"),
            ("days", "double"),
            ("day_start", "DateTimeOffset"));
    }

    [Fact]
    public void SupportsInsertSelectOnConflictAndReturning()
    {
        var insertSelect = TestSchema.Analyze(@"
            INSERT INTO users (id, name)
            SELECT user_id, COALESCE(note, 'unknown') FROM orders
            RETURNING id, name");
        var upsert = TestSchema.Analyze(@"
            INSERT INTO users (id, name)
            VALUES (@id, @name)
            ON CONFLICT (id) DO UPDATE
            SET name = excluded.name
            WHERE users.id = @id
            RETURNING users.id, users.name");

        TestSchema.AssertColumns(insertSelect, ("id", "int"), ("name", "string"));
        TestSchema.AssertColumns(upsert, ("id", "int"), ("name", "string"));
        Assert.Equal(new[] { "@id", "@name" }, upsert.Parameters.Select(item => item.Name));
    }

    [Fact]
    public void SupportsUpdateFromDeleteUsingAndReturning()
    {
        var update = TestSchema.Analyze(@"
            UPDATE users AS u
            SET name = COALESCE(o.note, u.name)
            FROM orders AS o
            WHERE o.user_id = u.id
            RETURNING u.id, u.name");
        var delete = TestSchema.Analyze(@"
            DELETE FROM users AS u
            USING orders AS o
            WHERE o.user_id = u.id
            RETURNING u.id");
        var updateStar = TestSchema.Analyze(@"
            UPDATE users AS u SET active = false
            FROM orders AS o
            WHERE o.user_id = u.id
            RETURNING *");

        TestSchema.AssertColumns(update, ("id", "int"), ("name", "string"));
        TestSchema.AssertColumns(delete, ("id", "int"));
        TestSchema.AssertSuccess(updateStar);
        Assert.Equal(15, updateStar.Columns.Count);
        Assert.DoesNotContain(updateStar.Columns, column => column.Name == "total");
    }
}

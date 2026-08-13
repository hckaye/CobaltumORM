using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CobaltumOrm.Analysis;
using Xunit;

namespace CobaltumOrm.SourceGenerator.Tests;

public sealed class AnalysisCacheTests
{
    [Fact]
    public void SchemaHitSkipsMigrationApplicationAndReturnsSameSchema()
    {
        using var fixture = new CacheFixture();
        var cache = fixture.CreateCache(DatabaseProvider.PostgreSql);
        var migrations = Migrations((1, "create users", "CREATE TABLE users (id bigint NOT NULL)"));
        var applications = 0;

        DatabaseSchema Analyze()
        {
            applications++;
            return UserSchema();
        }

        var first = cache.GetOrAnalyzeSchema(
            migrations,
            () => new CacheComputation<DatabaseSchema>(Analyze(), true),
            out var firstHit);
        var second = fixture.CreateCache(DatabaseProvider.PostgreSql).GetOrAnalyzeSchema(
            migrations,
            () => new CacheComputation<DatabaseSchema>(Analyze(), true),
            out var secondHit);

        Assert.False(firstHit);
        Assert.True(secondHit);
        Assert.Equal(1, applications);
        AssertSchemasEqual(first, second);
    }

    [Fact]
    public void MigrationSqlProviderAndOrderChangesMiss()
    {
        using var fixture = new CacheFixture();
        var applications = 0;

        DatabaseSchema Run(DatabaseProvider provider, IReadOnlyList<SemanticMigrationInput> migrations)
        {
            return fixture.CreateCache(provider).GetOrAnalyzeSchema(
                migrations,
                () =>
                {
                    applications++;
                    return new CacheComputation<DatabaseSchema>(UserSchema(), true);
                },
                out _);
        }

        var original = new[]
        {
            new SemanticMigrationInput(1, "create users", new[]
            {
                "CREATE TABLE users (id bigint NOT NULL)",
                "ALTER TABLE users ADD COLUMN name text",
            }),
        };
        Run(DatabaseProvider.PostgreSql, original);
        Run(DatabaseProvider.PostgreSql, original);
        Run(DatabaseProvider.PostgreSql, new[]
        {
            new SemanticMigrationInput(1, "create users", new[]
            {
                "CREATE TABLE users (id integer NOT NULL)",
                "ALTER TABLE users ADD COLUMN name text",
            }),
        });
        Run(DatabaseProvider.PostgreSql, new[]
        {
            new SemanticMigrationInput(1, "create users", new[]
            {
                "ALTER TABLE users ADD COLUMN name text",
                "CREATE TABLE users (id bigint NOT NULL)",
            }),
        });
        Run(DatabaseProvider.MySql, original);

        Assert.Equal(4, applications);
    }

    [Fact]
    public void QueryHitSkipsAnalyzerAndReturnsIdenticalContract()
    {
        using var fixture = new CacheFixture();
        var analyzer = new CountingQueryAnalyzer(SuccessfulQuery());
        var sql = "SELECT id, name FROM users WHERE id = @id";
        var schema = UserSchema();

        var first = fixture.CreateCache(DatabaseProvider.PostgreSql)
            .AnalyzeQuery(schema, sql, analyzer, out var firstHit);
        var second = fixture.CreateCache(DatabaseProvider.PostgreSql)
            .AnalyzeQuery(schema, sql, analyzer, out var secondHit);

        Assert.False(firstHit);
        Assert.True(secondHit);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(first.Columns.Select(ColumnValue), second.Columns.Select(ColumnValue));
        Assert.Equal(first.Parameters.Select(ParameterValue), second.Parameters.Select(ParameterValue));
    }

    [Fact]
    public void QuerySchemaSqlAndProviderChangesMiss()
    {
        using var fixture = new CacheFixture();
        var analyzer = new CountingQueryAnalyzer(SuccessfulQuery());
        var schema = UserSchema();
        var changedSchema = new DatabaseSchema(new[]
        {
            new Table("users", new[]
            {
                new Column("id", "bigint", isPrimaryKey: true),
                new Column("display_name", "text", isNullable: true),
            }),
        });

        fixture.CreateCache(DatabaseProvider.PostgreSql).AnalyzeQuery(schema, "SELECT id FROM users", analyzer);
        fixture.CreateCache(DatabaseProvider.PostgreSql).AnalyzeQuery(schema, "SELECT id FROM users", analyzer);
        fixture.CreateCache(DatabaseProvider.PostgreSql).AnalyzeQuery(schema, "SELECT name FROM users", analyzer);
        fixture.CreateCache(DatabaseProvider.PostgreSql).AnalyzeQuery(changedSchema, "SELECT id FROM users", analyzer);
        fixture.CreateCache(DatabaseProvider.MySql).AnalyzeQuery(schema, "SELECT id FROM users", analyzer);

        Assert.Equal(4, analyzer.Calls);
    }

    [Fact]
    public void CorruptAndVersionMismatchedEntriesFallBackAndAreReplaced()
    {
        using var fixture = new CacheFixture();
        var migrations = Migrations((1, "create users", "CREATE TABLE users (id bigint NOT NULL)"));
        var applications = 0;
        DatabaseSchema Run(out bool hit) => fixture.CreateCache(DatabaseProvider.PostgreSql).GetOrAnalyzeSchema(
            migrations,
            () =>
            {
                applications++;
                return new CacheComputation<DatabaseSchema>(UserSchema(), true);
            },
            out hit);

        Run(out _);
        var path = Assert.Single(Directory.GetFiles(fixture.Directory, "schema-*.xml"));
        File.WriteAllText(path, "not xml");
        Run(out var corruptHit);
        Run(out var repairedHit);

        Assert.False(corruptHit);
        Assert.True(repairedHit);

        var document = XDocument.Load(path);
        document.Root!.SetAttributeValue("analysis", "old");
        document.Save(path);
        Run(out var versionHit);
        Run(out var currentHit);

        Assert.False(versionHit);
        Assert.True(currentHit);
        Assert.Equal(3, applications);
    }

    [Fact]
    public void ErrorResultsAreNotCached()
    {
        using var fixture = new CacheFixture();
        var error = new AnalysisResult(
            Array.Empty<ResultColumn>(),
            Array.Empty<QueryParameter>(),
            new[] { new Diagnostic("SQL001", "invalid", new SourceSpan(0, 1)) });
        var analyzer = new CountingQueryAnalyzer(error);

        var first = fixture.CreateCache(DatabaseProvider.PostgreSql)
            .AnalyzeQuery(UserSchema(), "SELECT missing FROM users", analyzer, out var firstHit);
        var second = fixture.CreateCache(DatabaseProvider.PostgreSql)
            .AnalyzeQuery(UserSchema(), "SELECT missing FROM users", analyzer, out var secondHit);

        Assert.True(first.HasErrors);
        Assert.True(second.HasErrors);
        Assert.False(firstHit);
        Assert.False(secondHit);
        Assert.Equal(2, analyzer.Calls);
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public void UnsuccessfulSchemaResultsAreNotCached()
    {
        using var fixture = new CacheFixture();
        var migrations = Migrations((1, "broken", "CREATE TABLE"));
        var applications = 0;

        void Run()
        {
            fixture.CreateCache(DatabaseProvider.PostgreSql).GetOrAnalyzeSchema(
                migrations,
                () =>
                {
                    applications++;
                    return new CacheComputation<DatabaseSchema>(
                        new DatabaseSchema(Array.Empty<Table>()),
                        false);
                },
                out _);
        }

        Run();
        Run();

        Assert.Equal(2, applications);
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public async Task ConcurrentWritersLeaveReadableEntry()
    {
        using var fixture = new CacheFixture();
        var migrations = Migrations((1, "create users", "CREATE TABLE users (id bigint NOT NULL)"));
        const int writerCount = 8;
        using var barrier = new Barrier(writerCount);
        var writers = Enumerable.Range(0, writerCount).Select(writerIndex => Task.Run(() =>
            fixture.CreateCache(DatabaseProvider.PostgreSql).GetOrAnalyzeSchema(
                migrations,
                () =>
                {
                    barrier.SignalAndWait();
                    return new CacheComputation<DatabaseSchema>(UserSchema(), true);
                },
                out _))).ToArray();

        await Task.WhenAll(writers);
        var unexpectedApplication = false;
        var schema = fixture.CreateCache(DatabaseProvider.PostgreSql).GetOrAnalyzeSchema(
            migrations,
            () =>
            {
                unexpectedApplication = true;
                return new CacheComputation<DatabaseSchema>(UserSchema(), true);
            },
            out var hit);

        Assert.True(hit);
        Assert.False(unexpectedApplication);
        Assert.Equal("users", Assert.Single(schema.Tables).Name);
    }

    [Fact]
    public void DisabledCacheAlwaysAnalyzesAndWritesNothing()
    {
        using var fixture = new CacheFixture();
        var cache = fixture.CreateCache(DatabaseProvider.PostgreSql, enabled: false);
        var analyzer = new CountingQueryAnalyzer(SuccessfulQuery());

        cache.AnalyzeQuery(UserSchema(), "SELECT id FROM users", analyzer);
        cache.AnalyzeQuery(UserSchema(), "SELECT id FROM users", analyzer);

        Assert.Equal(2, analyzer.Calls);
        Assert.False(Directory.Exists(fixture.Directory));
    }

    private static IReadOnlyList<SemanticMigrationInput> Migrations(
        params (long Version, string Description, string Sql)[] values) =>
        values.Select(value => new SemanticMigrationInput(
            value.Version,
            value.Description,
            new[] { value.Sql })).ToArray();

    private static DatabaseSchema UserSchema() => new DatabaseSchema(new[]
    {
        new Table("users", new[]
        {
            new Column("id", "bigint", isPrimaryKey: true),
            new Column("name", "text", isNullable: true, defaultExpression: "'unknown'"),
        }, "public"),
    });

    private static AnalysisResult SuccessfulQuery() => new AnalysisResult(
        new[]
        {
            new ResultColumn("id", "long"),
            new ResultColumn("name", "string?"),
        },
        new[] { new QueryParameter("id", "long", "bigint") },
        Array.Empty<Diagnostic>());

    private static string ColumnValue(ResultColumn column) => column.Name + "\0" + column.ClrType;

    private static string ParameterValue(QueryParameter parameter) =>
        parameter.Name + "\0" + parameter.ClrType + "\0" + parameter.DatabaseTypeName;

    private static void AssertSchemasEqual(DatabaseSchema expected, DatabaseSchema actual)
    {
        Assert.Equal(expected.Tables.Count, actual.Tables.Count);
        for (var tableIndex = 0; tableIndex < expected.Tables.Count; tableIndex++)
        {
            var expectedTable = expected.Tables[tableIndex];
            var actualTable = actual.Tables[tableIndex];
            Assert.Equal(expectedTable.Name, actualTable.Name);
            Assert.Equal(expectedTable.Schema, actualTable.Schema);
            Assert.Equal(expectedTable.Columns.Count, actualTable.Columns.Count);
            for (var columnIndex = 0; columnIndex < expectedTable.Columns.Count; columnIndex++)
            {
                var expectedColumn = expectedTable.Columns[columnIndex];
                var actualColumn = actualTable.Columns[columnIndex];
                Assert.Equal(expectedColumn.Name, actualColumn.Name);
                Assert.Equal(expectedColumn.SqlType, actualColumn.SqlType);
                Assert.Equal(expectedColumn.IsNullable, actualColumn.IsNullable);
                Assert.Equal(expectedColumn.IsPrimaryKey, actualColumn.IsPrimaryKey);
                Assert.Equal(expectedColumn.DefaultExpression, actualColumn.DefaultExpression);
                Assert.Equal(expectedColumn.IsIdentity, actualColumn.IsIdentity);
            }
        }
    }

    private sealed class CountingQueryAnalyzer : IQueryAnalyzer
    {
        private readonly AnalysisResult _result;
        private int _calls;

        internal CountingQueryAnalyzer(AnalysisResult result)
        {
            _result = result;
        }

        internal int Calls => _calls;

        public AnalysisResult Analyze(DatabaseSchema schema, string sql)
        {
            Interlocked.Increment(ref _calls);
            return _result;
        }
    }

    private sealed class CacheFixture : IDisposable
    {
        internal CacheFixture()
        {
            Directory = Path.Combine(
                Path.GetTempPath(),
                "CobaltumOrm.AnalysisCacheTests",
                Guid.NewGuid().ToString("N"));
        }

        internal string Directory { get; }

        internal AnalysisCache CreateCache(DatabaseProvider provider, bool enabled = true) =>
            new AnalysisCache(Directory, provider, enabled);

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}

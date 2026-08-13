using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace CobaltumOrm.SourceGenerator.Tests;

public sealed class CompileTimeQueryBuildTests
{
    [Theory]
    [InlineData("PostgreSql", "\"users\"")]
    [InlineData("MySql", "`users`")]
    [InlineData("Sqlite", "\"users\"")]
    [InlineData("SqlServer", "[dbo].[users]")]
    [InlineData("Oracle", "\"users\"")]
    public void ConfiguredProviderIsHonoredByTheCompilerTransform(string providerName, string qualifiedTable)
    {
        var result = BuildFixture(
            "using CobaltumOrm; public static class Consumer { }",
            providerName);

        Assert.True(result.Succeeded, result.Output);
        var schemaPath = Path.Combine(
            result.Directory,
            "obj",
            "Release",
            "net10.0",
            "CobaltumOrm",
            "CobaltumOrm.SqlSchema.g.cs");
        Assert.Contains(
            "Name = " + CSharpLiteral(qualifiedTable) + ";",
            File.ReadAllText(schemaPath),
            StringComparison.Ordinal);
    }

    private static readonly object PackageLock = new object();
    private static string? _packageDirectory;

    [Fact]
    public void TypedLiteralBuildsAndMissingResultFieldIsACSharpError()
    {
        var success = BuildFixture("""
            using System.Data.Common;
            using System.Threading.Tasks;
            using CobaltumOrm;

            public static class Consumer
            {
                public static async Task<string> Read(DbConnection connection)
                {
                    var rows = await connection.Query("SELECT id, name FROM users").ReadAsync();
                    _ = typeof(CreateUsersMigration);
                    return rows[0].Name + PlainSource.Suffix;
                }
            }
            """);

        Assert.True(success.Succeeded, success.Output);
        AssertTransformationInputsRemainAnalyzerVisible(success);

        var failure = BuildFixture("""
            using System.Data.Common;
            using System.Threading.Tasks;
            using CobaltumOrm;

            public static class Consumer
            {
                public static async Task<string> Read(DbConnection connection)
                {
                    var rows = await connection.Query("SELECT id, name FROM users").ReadAsync();
                    return rows[0].Email;
                }
            }
            """);

        Assert.False(failure.Succeeded, failure.Output);
        Assert.Contains("CS1061", failure.Output, StringComparison.Ordinal);
        Assert.Contains("Consumer.cs", failure.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidSqlAndUnavailableColumnsFailTheBuild()
    {
        var invalid = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;
            public static class Consumer
            {
                public static object Read(DbConnection connection) =>
                    connection.Query("SELECT id FROM").ReadAsync();
            }
            """);
        Assert.False(invalid.Succeeded, invalid.Output);
        Assert.Contains("SQL", invalid.Output, StringComparison.Ordinal);
        Assert.Contains("Consumer.cs", invalid.Output, StringComparison.Ordinal);

        var missing = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;
            public static class Consumer
            {
                public static object Read(DbConnection connection) =>
                    connection.Query("SELECT email FROM users").ReadAsync();
            }
            """);
        Assert.False(missing.Succeeded, missing.Output);
        Assert.Contains("SQL203", missing.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidLiteralDataManipulationFailsTheBuild()
    {
        var invalid = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;
            public static class Consumer
            {
                public static object Write(DbConnection connection) =>
                    connection.Query("UPDATE users name = 'changed'").ExecuteAsync();
            }
            """);
        Assert.False(invalid.Succeeded, invalid.Output);
        Assert.Contains("SQL100", invalid.Output, StringComparison.Ordinal);
        Assert.Contains("Consumer.cs", invalid.Output, StringComparison.Ordinal);

        var missing = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;
            public static class Consumer
            {
                public static object Write(DbConnection connection) =>
                    connection.Query("DELETE FROM missing WHERE id = 1").ExecuteAsync();
            }
            """);
        Assert.False(missing.Succeeded, missing.Output);
        Assert.Contains("SQL200", missing.Output, StringComparison.Ordinal);

        var missingColumn = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;
            public static class Consumer
            {
                public static object Write(DbConnection connection) =>
                    connection.Query("INSERT INTO users (missing) VALUES (1)").ExecuteAsync();
            }
            """);
        Assert.False(missingColumn.Succeeded, missingColumn.Output);
        Assert.Contains("SQL203", missingColumn.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void InterpolatedValuesAreParameterizedAndTypeChecked()
    {
        var success = BuildFixture("""
            using System.Data.Common;
            using System.Threading.Tasks;
            using CobaltumOrm;

            public static class Consumer
            {
                public static async Task<string> Read(DbConnection connection, int id)
                {
                    var rows = await connection.Query($"SELECT id, name FROM users WHERE id = {id}").ReadAsync();
                    return rows[0].Name;
                }
            }
            """);
        Assert.True(success.Succeeded, success.Output);
        var generated = File.ReadAllText(Directory
            .EnumerateFiles(success.Directory, "CobaltumOrm.RawQueries.g.cs", SearchOption.AllDirectories)
            .Single());
        Assert.Contains("WHERE id = @__cobaltum_value_0", generated, StringComparison.Ordinal);
        Assert.Contains("CobaltumParameter.Add", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE id = {id}", generated, StringComparison.Ordinal);

        var wrongType = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;
            public static class Consumer
            {
                public static object Read(DbConnection connection, string id) =>
                    connection.Query($"SELECT id, name FROM users WHERE id = {id}").ReadAsync();
            }
            """);
        Assert.False(wrongType.Succeeded, wrongType.Output);
        Assert.Contains("COB104", wrongType.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedSchemaNamesWorkInNamedAndRawQueries()
    {
        var result = BuildFixture("""
            using System.Data.Common;
            using System.Threading.Tasks;
            using CobaltumOrm;
            using CobaltumOrm.Generated;

            [Query(
                "FindByDocument",
                $"SELECT {SqlSchema.Tables.Users.Columns.Id} FROM {SqlSchema.Tables.Users.Name} WHERE {SqlSchema.Tables.Users.Columns.Document} = @document")]
            public static partial class StoredValueQueries
            {
            }

            public static class Consumer
            {
                public static async Task<int> Read(DbConnection connection, string document)
                {
                    _ = await StoredValueQueries.FindByDocumentAsync(connection, document);
                    var rows = await connection.Query(
                        $"SELECT {SqlSchema.Tables.Users.Columns.Id} FROM {SqlSchema.Tables.Users.Name} WHERE {SqlSchema.Tables.Users.Columns.Document} = {document}")
                        .ReadAsync();
                    return rows[0].Id;
                }
            }
            """);

        Assert.True(result.Succeeded, result.Output);
        var sqlSchema = File.ReadAllText(Directory
            .EnumerateFiles(result.Directory, "CobaltumOrm.SqlSchema.g.cs", SearchOption.AllDirectories)
            .Single());
        Assert.Contains("public static class Users", sqlSchema, StringComparison.Ordinal);
        Assert.Contains("Name = \"\\\"users\\\"\";", sqlSchema, StringComparison.Ordinal);
        Assert.Contains("Document = \"\\\"document\\\"\";", sqlSchema, StringComparison.Ordinal);

        var generatedQueries = string.Join("\n", Directory
            .EnumerateFiles(result.Directory, "CobaltumOrm.Queries.*.g.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.Contains("SELECT \\\"id\\\" FROM \\\"users\\\" WHERE \\\"document\\\" = @document", generatedQueries, StringComparison.Ordinal);

        var rawQueries = File.ReadAllText(Directory
            .EnumerateFiles(result.Directory, "CobaltumOrm.RawQueries.g.cs", SearchOption.AllDirectories)
            .Single());
        Assert.Contains("SELECT \\\"id\\\" FROM \\\"users\\\" WHERE \\\"document\\\" = @__cobaltum_value_0", rawQueries, StringComparison.Ordinal);
    }

    [Fact]
    public void OldGeneratedSchemaNamesFailCompilationAfterARenameMigration()
    {
        var result = BuildFixture("""
            using CobaltumOrm;
            using CobaltumOrm.Generated;
            using CobaltumOrm.Migrations;

            [Migration(2, "rename stored values")]
            public sealed class RenameStoredValuesMigration : Migration
            {
                public override void Up()
                {
                    Rename.Column("document").OnTable("users").To("payload");
                    Rename.Table("users").To("stored_values");
                }

                public override void Down()
                {
                    Rename.Table("stored_values").To("users");
                    Rename.Column("payload").OnTable("users").To("document");
                }
            }

            [Query(
                "FindByDocument",
                $"SELECT {SqlSchema.Tables.Users.Columns.Id} FROM {SqlSchema.Tables.Users.Name} WHERE {SqlSchema.Tables.Users.Columns.Document} = @document")]
            public static partial class StoredValueQueries
            {
            }
            """);

        Assert.False(result.Succeeded, result.Output);
        Assert.Contains("CS0117", result.Output, StringComparison.Ordinal);
        Assert.Contains("Users", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void OldDirectSqlNamesFailCompilationAfterARenameMigration()
    {
        var result = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;
            using CobaltumOrm.Migrations;

            [Migration(2, "rename document")]
            public sealed class RenameDocumentMigration : Migration
            {
                public override void Up()
                {
                    Rename.Column("document").OnTable("users").To("payload");
                }

                public override void Down()
                {
                    Rename.Column("payload").OnTable("users").To("document");
                }
            }

            public static class Consumer
            {
                public static object Read(DbConnection connection) =>
                    connection.Query("SELECT document FROM users").ReadAsync();
            }
            """);

        Assert.False(result.Succeeded, result.Output);
        Assert.Contains("SQL203", result.Output, StringComparison.Ordinal);
        Assert.Contains("document", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSqlSpecificParameterAndNumericTypesSurviveTheBuildTransform()
    {
        var result = BuildFixture("""
            using System;
            using System.Data.Common;
            using System.Threading.Tasks;
            using CobaltumOrm;

            public static class Consumer
            {
                public static async Task<(long Literal, decimal? Total)> Read(
                    DbConnection connection,
                    DateTime localTime,
                    string document)
                {
                    _ = await connection
                        .Query($"SELECT id FROM users WHERE local_time = {localTime} AND document = {document}")
                        .ReadAsync();
                    _ = await connection
                        .Query("SELECT id FROM users WHERE document = @document")
                        .WithParameter("@document", document)
                        .ReadAsync();
                    var values = await connection
                        .Query("SELECT 2147483648 AS literal, SUM(big_id) AS total FROM users")
                        .ReadAsync();
                    return (values[0].Literal, values[0].Total);
                }
            }
            """);

        Assert.True(result.Succeeded, result.Output);
        var generated = File.ReadAllText(Directory
            .EnumerateFiles(result.Directory, "CobaltumOrm.RawQueries.g.cs", SearchOption.AllDirectories)
            .Single());
        Assert.Contains("DbType.DateTime2", generated, StringComparison.Ordinal);
        Assert.Contains("CobaltumParameter.AddConfigured", generated, StringComparison.Ordinal);
        Assert.Contains("Npgsql.NpgsqlParameter", generated, StringComparison.Ordinal);
        var transformed = File.ReadAllText(Directory
            .EnumerateFiles(result.Directory, "*.cobaltum.cs", SearchOption.AllDirectories)
            .Single());
        Assert.Contains("DbType.String, \"jsonb\"", transformed, StringComparison.Ordinal);
        Assert.Contains("Npgsql.NpgsqlParameter", transformed, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicAndStructuralSqlRequireTheExplicitEscapeHatch()
    {
        var dynamic = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;
            public static class Consumer
            {
                public static object Read(DbConnection connection, string sql) =>
                    connection.Query(sql).ReadAsync();
            }
            """);
        Assert.False(dynamic.Succeeded, dynamic.Output);
        Assert.Contains("COB100", dynamic.Output, StringComparison.Ordinal);
        Assert.Contains("NoCheckQuery", dynamic.Output, StringComparison.Ordinal);

        var structural = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;
            public static class Consumer
            {
                public static object Read(DbConnection connection, string fields) =>
                    connection.Query($"SELECT {fields} FROM users").ReadAsync();
            }
            """);
        Assert.False(structural.Succeeded, structural.Output);
        Assert.Contains("SQL209", structural.Output, StringComparison.Ordinal);

        var escapeHatch = BuildFixture("""
            using System.Data.Common;
            using System.Threading.Tasks;
            using CobaltumOrm;
            public static class Consumer
            {
                public static Task<System.Collections.Generic.IReadOnlyList<CobaltumRawRow>> Read(
                    DbConnection connection,
                    string sql) => connection.NoCheckQuery(sql).ReadAsync();

                public static Task<System.Collections.Generic.IReadOnlyList<CobaltumRawRow>> ReadUncheckedLiteral(
                    DbConnection connection) =>
                    connection.NoCheckQuery("SELECT missing FROM missing").ReadAsync();
            }
            """);
        Assert.True(escapeHatch.Succeeded, escapeHatch.Output);
    }

    [Fact]
    public void UnchangedBuildSkipsTransformAndRestoresTransformedCompileItems()
    {
        var fixture = CreateBuildFixture(ValidQuerySource());
        var first = fixture.Build();
        Assert.True(first.Succeeded, first.Output);

        var manifestWriteTime = File.GetLastWriteTimeUtc(fixture.SuccessManifestPath);
        Thread.Sleep(100);

        var second = fixture.Build();
        Assert.True(second.Succeeded, second.Output);
        Assert.Equal(manifestWriteTime, File.GetLastWriteTimeUtc(fixture.SuccessManifestPath));
        AssertTransformationInputsRemainAnalyzerVisible(second, assertGeneratedModels: false);
    }

    [Fact]
    public void SourceEditRerunsTransform()
    {
        var fixture = CreateBuildFixture(ValidQuerySource());
        var first = fixture.Build();
        Assert.True(first.Succeeded, first.Output);
        var manifestWriteTime = File.GetLastWriteTimeUtc(fixture.SuccessManifestPath);

        Thread.Sleep(100);
        File.AppendAllText(
            fixture.ConsumerPath,
            "\npublic static class UnrelatedSourceEdit { public const int Value = 1; }\n");

        var second = fixture.Build();
        Assert.True(second.Succeeded, second.Output);
        Assert.True(File.GetLastWriteTimeUtc(fixture.SuccessManifestPath) > manifestWriteTime);
        var transformed = Directory
            .EnumerateFiles(fixture.TaskDirectory, "*.cobaltum.cs", SearchOption.TopDirectoryOnly)
            .Single();
        Assert.Contains("UnrelatedSourceEdit", File.ReadAllText(transformed), StringComparison.Ordinal);
    }

    [Fact]
    public void SourceAddAndDeleteInvalidateTransformAndRemoveStaleOutputs()
    {
        var fixture = CreateBuildFixture(ValidQuerySource());
        var first = fixture.Build();
        Assert.True(first.Succeeded, first.Output);
        var initialTransformed = Directory
            .EnumerateFiles(fixture.TaskDirectory, "*.cobaltum.cs", SearchOption.TopDirectoryOnly)
            .Single();
        var firstManifestWriteTime = File.GetLastWriteTimeUtc(fixture.SuccessManifestPath);

        Thread.Sleep(100);
        File.WriteAllText(
            Path.Combine(fixture.RootDirectory, "ZAdded.cs"),
            "public static class AddedSource { public const int Value = 1; }\n");
        var added = fixture.Build();
        Assert.True(added.Succeeded, added.Output);
        Assert.True(File.GetLastWriteTimeUtc(fixture.SuccessManifestPath) > firstManifestWriteTime);
        Assert.True(File.Exists(initialTransformed));

        var secondManifestWriteTime = File.GetLastWriteTimeUtc(fixture.SuccessManifestPath);
        Thread.Sleep(100);
        File.Delete(fixture.ConsumerPath);
        var deleted = fixture.Build();
        Assert.True(deleted.Succeeded, deleted.Output);
        Assert.True(File.GetLastWriteTimeUtc(fixture.SuccessManifestPath) > secondManifestWriteTime);
        Assert.Empty(Directory.EnumerateFiles(fixture.TaskDirectory, "*.cobaltum.cs", SearchOption.TopDirectoryOnly));

        var compileItems = File.ReadAllLines(fixture.CompileItemsPath);
        Assert.DoesNotContain(compileItems, line => Path.GetFileName(CompileItem(line).Path) == "Consumer.cs");
        Assert.DoesNotContain(compileItems, line => Path.GetFileName(CompileItem(line).Path).EndsWith(".cobaltum.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void SqlMigrationEditRerunsTransform()
    {
        var fixture = CreateBuildFixture(ValidQuerySource());
        var first = fixture.Build();
        Assert.True(first.Succeeded, first.Output);
        var manifestWriteTime = File.GetLastWriteTimeUtc(fixture.SuccessManifestPath);

        Thread.Sleep(100);
        var migrationsDirectory = Directory.CreateDirectory(Path.Combine(fixture.RootDirectory, "Migrations"));
        var sqlPath = Path.Combine(migrationsDirectory.FullName, "V2__add_email.sql");
        File.WriteAllText(sqlPath, "ALTER TABLE users ADD COLUMN email text;");

        var second = fixture.Build();
        Assert.True(second.Succeeded, second.Output);
        Assert.True(File.GetLastWriteTimeUtc(fixture.SuccessManifestPath) > manifestWriteTime);
        var schemaPath = Path.Combine(fixture.TaskDirectory, "CobaltumOrm.SqlSchema.g.cs");
        Assert.Contains("Email", File.ReadAllText(schemaPath), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingGeneratedAndTransformedOutputsRerunTransform()
    {
        var fixture = CreateBuildFixture(ValidQuerySource());
        var first = fixture.Build();
        Assert.True(first.Succeeded, first.Output);
        var schemaPath = Path.Combine(fixture.TaskDirectory, "CobaltumOrm.SqlSchema.g.cs");
        var transformedPath = Directory
            .EnumerateFiles(fixture.TaskDirectory, "*.cobaltum.cs", SearchOption.TopDirectoryOnly)
            .Single();
        var firstManifestWriteTime = File.GetLastWriteTimeUtc(fixture.SuccessManifestPath);

        File.Delete(schemaPath);
        var missingGenerated = fixture.Build();
        Assert.True(missingGenerated.Succeeded, missingGenerated.Output);
        Assert.True(File.Exists(schemaPath));
        var secondManifestWriteTime = File.GetLastWriteTimeUtc(fixture.SuccessManifestPath);
        Assert.True(secondManifestWriteTime > firstManifestWriteTime);

        Thread.Sleep(100);
        File.Delete(transformedPath);
        var missingTransformed = fixture.Build();
        Assert.True(missingTransformed.Succeeded, missingTransformed.Output);
        Assert.True(File.Exists(transformedPath));
        Assert.True(File.GetLastWriteTimeUtc(fixture.SuccessManifestPath) > secondManifestWriteTime);
    }

    [Fact]
    public void CorruptSuccessManifestOutsideOutputIsIgnoredAndNotCleaned()
    {
        var fixture = CreateBuildFixture(ValidQuerySource());
        var first = fixture.Build();
        Assert.True(first.Succeeded, first.Output);

        var externalPath = Path.GetFullPath(Path.Combine(fixture.TaskDirectory, "..", "outside.cs"));
        File.WriteAllText(externalPath, "public static class ExternalOutput { }\n");
        var escapedExternalPath = SecurityElement.Escape(externalPath);
        File.WriteAllText(fixture.SuccessManifestPath, $"""
            <CobaltumOrmTransformSuccess version="1">
              <ProcessedSources />
              <TransformedSources>
                <Source itemSpec="{escapedExternalPath}" CobaltumOrmTransformed="true" />
              </TransformedSources>
              <Outputs>
                <Output path="{escapedExternalPath}" />
              </Outputs>
            </CobaltumOrmTransformSuccess>
            """);

        var second = fixture.Build();
        Assert.True(second.Succeeded, second.Output);
        Assert.DoesNotContain(
            File.ReadAllLines(fixture.FileWritesPath),
            line => string.Equals(Path.GetFullPath(line), externalPath, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(externalPath, File.ReadAllText(fixture.SuccessManifestPath), StringComparison.Ordinal);

        var clean = fixture.Clean();
        Assert.True(clean.Succeeded, clean.Output);
        Assert.True(File.Exists(externalPath));
    }

    [Fact]
    public void CorruptSuccessManifestCaseDifferentSiblingIsIgnoredOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = CreateBuildFixture(ValidQuerySource());
        var first = fixture.Build();
        Assert.True(first.Succeeded, first.Output);

        var outputDirectory = Directory.GetParent(fixture.TaskDirectory)!.FullName;
        var outputDirectoryName = Path.GetFileName(fixture.TaskDirectory);
        var siblingName = outputDirectoryName.ToLowerInvariant();
        Assert.NotEqual(outputDirectoryName, siblingName);
        var externalDirectory = Path.Combine(outputDirectory, siblingName);
        // A case-insensitive Unix volume cannot represent a physical sibling
        // with only a case difference, but the alternate path still exercises
        // the ordinal manifest boundary check.
        Directory.CreateDirectory(externalDirectory);
        var externalPath = Path.Combine(externalDirectory, "outside.cs");
        File.WriteAllText(externalPath, "public static class ExternalOutput { }\n");

        var escapedExternalPath = SecurityElement.Escape(externalPath);
        File.WriteAllText(fixture.SuccessManifestPath, $"""
            <CobaltumOrmTransformSuccess version="1">
              <ProcessedSources />
              <TransformedSources>
                <Source itemSpec="{escapedExternalPath}" CobaltumOrmTransformed="true" />
              </TransformedSources>
              <Outputs>
                <Output path="{escapedExternalPath}" />
              </Outputs>
            </CobaltumOrmTransformSuccess>
            """);

        var second = fixture.Build();
        Assert.True(second.Succeeded, second.Output);
        Assert.DoesNotContain(
            File.ReadAllLines(fixture.FileWritesPath),
            line => string.Equals(Path.GetFullPath(line), externalPath, StringComparison.Ordinal));
        Assert.DoesNotContain(externalPath, File.ReadAllText(fixture.SuccessManifestPath), StringComparison.Ordinal);

        var clean = fixture.Clean();
        Assert.True(clean.Succeeded, clean.Output);
        Assert.True(File.Exists(externalPath));
    }

    [Fact]
    public void FailedBuildWithoutManifestIsRetriedUnchanged()
    {
        var fixture = CreateBuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;

            public static class Consumer
            {
                public static object Read(DbConnection connection) =>
                    connection.Query("SELECT email FROM users").ReadAsync();
            }
            """);

        var first = fixture.Build();
        Assert.False(first.Succeeded, first.Output);
        Assert.False(File.Exists(fixture.SuccessManifestPath));

        var second = fixture.Build();
        Assert.False(second.Succeeded, second.Output);
        Assert.Contains("SQL203", second.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderAndGeneratedNamespaceChangesRerunTransform()
    {
        var fixture = CreateBuildFixture(ValidQuerySource(), "PostgreSql");
        var first = fixture.Build();
        Assert.True(first.Succeeded, first.Output);
        var manifestWriteTime = File.GetLastWriteTimeUtc(fixture.SuccessManifestPath);

        Thread.Sleep(100);
        fixture.SetProperty("CobaltumOrmDatabaseProvider", "MySql");
        fixture.SetProperty("CobaltumOrmGeneratedNamespace", "Custom.Generated");
        var second = fixture.Build();
        Assert.True(second.Succeeded, second.Output);
        Assert.True(File.GetLastWriteTimeUtc(fixture.SuccessManifestPath) > manifestWriteTime);

        var schema = File.ReadAllText(Path.Combine(fixture.TaskDirectory, "CobaltumOrm.SqlSchema.g.cs"));
        Assert.Contains("namespace Custom.Generated", schema, StringComparison.Ordinal);
        Assert.Contains("Name = \"`users`\";", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalysisCacheModeChangeRerunsTransformAndManifestTracksCacheInputs()
    {
        var fixture = CreateBuildFixture(ValidQuerySource());
        var first = fixture.Build();
        Assert.True(first.Succeeded, first.Output);
        var manifestWriteTime = File.GetLastWriteTimeUtc(fixture.SuccessManifestPath);
        var inputManifestPath = Path.Combine(fixture.TaskDirectory, "CobaltumOrm.TransformInputs.xml");
        var expectedCacheDirectory = Path.GetFullPath(Path.Combine(fixture.TaskDirectory, "AnalysisCache"));

        Assert.Equal("true", InputProperty(inputManifestPath, "CobaltumOrmAnalysisCache"));
        Assert.Equal(
            expectedCacheDirectory,
            InputProperty(inputManifestPath, "_CobaltumOrmAnalysisCacheDirectory"));

        Thread.Sleep(100);
        fixture.SetProperty("CobaltumOrmAnalysisCache", "false");
        var second = fixture.Build();

        Assert.True(second.Succeeded, second.Output);
        Assert.True(File.GetLastWriteTimeUtc(fixture.SuccessManifestPath) > manifestWriteTime);
        Assert.Equal("false", InputProperty(inputManifestPath, "CobaltumOrmAnalysisCache"));
        Assert.Equal(
            expectedCacheDirectory,
            InputProperty(inputManifestPath, "_CobaltumOrmAnalysisCacheDirectory"));
    }

    private static string ValidQuerySource() => """
        using System.Data.Common;
        using System.Threading.Tasks;
        using CobaltumOrm;

        public static class Consumer
        {
            public static async Task<string> Read(DbConnection connection)
            {
                var rows = await connection.Query("SELECT id, name FROM users").ReadAsync();
                return rows[0].Name;
            }
        }
        """;

    [Fact]
    public void LiteralDmlReturningAndCheckedNamedParametersBuild()
    {
        var result = BuildFixture("""
            using System.Data;
            using System.Data.Common;
            using System.Threading;
            using System.Threading.Tasks;
            using CobaltumOrm;

            public static class Consumer
            {
                public static async Task<int> Update(DbConnection connection, int id, string name, CancellationToken token)
                {
                    var selected = await connection
                        .Query("SELECT id, name FROM users WHERE id = @id")
                        .WithParameter("@id", id, DbType.Int32)
                        .ReadAsync(token);
                    _ = selected[0].Name;
                    return await connection
                        .Query("UPDATE users SET name = @name WHERE id = @id")
                        .WithParameter("@name", name, DbType.String)
                        .WithParameter("@id", id, DbType.Int32)
                        .ExecuteAsync(token);
                }

                public static async Task<string> Upsert(DbConnection connection, int id, string name)
                {
                    var rows = await connection
                        .Query("INSERT INTO users (id, name) VALUES (@id, @name) ON CONFLICT (id) DO UPDATE SET name = excluded.name RETURNING id, name")
                        .WithParameter("@id", id, DbType.Int32)
                        .WithParameter("@name", name, DbType.String)
                        .ReadAsync();
                    return rows[0].Name;
                }
            }
            """);

        Assert.True(result.Succeeded, result.Output);
    }

    [Fact]
    public void ConstantCheckedParameterNamesAndClrTypesAreValidated()
    {
        var result = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;

            public static class Consumer
            {
                public static object WrongName(DbConnection connection, int id) =>
                    connection.Query("SELECT id, name FROM users WHERE id = @id")
                        .WithParameter("@missing", id)
                        .ReadAsync();

                public static object WrongType(DbConnection connection, string id) =>
                    connection.Query("SELECT id, name FROM users WHERE id = @id")
                        .WithParameter("@id", id)
                        .ReadAsync();
            }
            """);

        Assert.False(result.Succeeded, result.Output);
        Assert.Contains("COB107", result.Output, StringComparison.Ordinal);
        Assert.Contains("COB108", result.Output, StringComparison.Ordinal);
        Assert.Contains("Consumer.cs", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitResultTypesBuildWithoutGeneratingResultRecords()
    {
        var result = BuildFixture("""
            using System.Data.Common;
            using System.Threading.Tasks;
            using CobaltumOrm;

            public sealed record UserResult(int Id, string Name);

            public static class Consumer
            {
                public static async Task<UserResult> Checked(DbConnection connection)
                {
                    var rows = await connection
                        .Query<UserResult>("SELECT name, id FROM users")
                        .ReadAsync();
                    return rows[0];
                }

                public static async Task<UserResult> Unchecked(DbConnection connection, string sql)
                {
                    var rows = await connection.NoCheckQuery<UserResult>(sql).ReadAsync();
                    return rows[0];
                }
            }
            """);

        Assert.True(result.Succeeded, result.Output);
        var generated = File.ReadAllText(Directory
            .EnumerateFiles(result.Directory, "CobaltumOrm.RawQueries.g.cs", SearchOption.AllDirectories)
            .Single());
        Assert.Contains("CobaltumQueryDefinition<global::UserResult>", generated, StringComparison.Ordinal);
        Assert.Contains("return new global::UserResult(", generated, StringComparison.Ordinal);
        Assert.Contains("CobaltumResultReader.Read<int>", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("record Query0000Result", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckedExplicitResultTypeMismatchFailsTheBuild()
    {
        var result = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;

            public sealed record WrongUserResult(string Id, string Name);

            public static class Consumer
            {
                public static object Read(DbConnection connection) =>
                    connection.Query<WrongUserResult>("SELECT id, name FROM users").ReadAsync();
            }
            """);

        Assert.False(result.Succeeded, result.Output);
        Assert.Contains("COB109", result.Output, StringComparison.Ordinal);
        Assert.Contains("cannot be mapped", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectExplicitResultAttributesAndHandlersGenerateDirectCalls()
    {
        var result = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;

            public readonly record struct UserId(int Value);

            public sealed class UserIdHandler : IValueHandler<UserId>
            {
                public UserId Read(DbDataReader reader, int ordinal) =>
                    new UserId(reader.GetInt32(ordinal));
            }

            public sealed record UserResult(
                [ResultColumn("id"), ValueHandler<UserIdHandler>] UserId ExternalId,
                [ResultColumn] string Name);

            public static class Consumer
            {
                public static object Checked(DbConnection connection) =>
                    connection.Query<UserResult>("SELECT id, name FROM users").ReadAsync();

                public static object Unchecked(DbConnection connection, string sql) =>
                    connection.NoCheckQuery<UserResult>(sql).ReadAsync();
            }
            """);

        Assert.True(result.Succeeded, result.Output);
        var generated = File.ReadAllText(Directory
            .EnumerateFiles(result.Directory, "CobaltumOrm.RawQueries.g.cs", SearchOption.AllDirectories)
            .Single());
        Assert.Contains(
            "CobaltumHandlerCache<global::UserIdHandler>.Instance.Read(reader, 0)",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "CobaltumResultReader.GetOrdinal(reader, \"id\")",
            generated,
            StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckedExplicitResultTypeRequiresRows()
    {
        var result = BuildFixture("""
            using System.Data.Common;
            using CobaltumOrm;

            public sealed record UserResult(int Id, string Name);

            public static class Consumer
            {
                public static object Update(DbConnection connection) =>
                    connection.Query<UserResult>("UPDATE users SET name = 'updated'").ExecuteAsync();
            }
            """);

        Assert.False(result.Succeeded, result.Output);
        Assert.Contains("COB109", result.Output, StringComparison.Ordinal);
        Assert.Contains("requires a statement that returns rows", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildOutputIsIdenticalWithAnalysisCacheEnabledOrDisabled()
    {
        const string source = """
            using CobaltumOrm;

            [Query("AllUsers", "SELECT id, name FROM users")]
            public static partial class UserQueries { }
            """;
        var enabled = BuildFixture(source, analysisCacheEnabled: true);
        var disabled = BuildFixture(source, analysisCacheEnabled: false);

        Assert.True(enabled.Succeeded, enabled.Output);
        Assert.True(disabled.Succeeded, disabled.Output);
        Assert.DoesNotContain(" warning COB", enabled.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" error COB", enabled.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" warning COB", disabled.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" error COB", disabled.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CompilerGeneratedSources(enabled), CompilerGeneratedSources(disabled));
    }

    private static void AssertTransformationInputsRemainAnalyzerVisible(
        BuildResult result,
        bool assertGeneratedModels = true)
    {
        var taskDirectory = Path.Combine(result.Directory, "obj", "Release", "net10.0", "CobaltumOrm");
        var transformedPath = Directory
            .EnumerateFiles(taskDirectory, "*.cobaltum.cs", SearchOption.TopDirectoryOnly)
            .Single();
        Assert.Equal("0000.Consumer.cobaltum.cs", Path.GetFileName(transformedPath));

        var transformed = File.ReadAllText(transformedPath);
        var lineDirective = transformed.Substring(0, transformed.IndexOf('\n'));
        Assert.StartsWith("#line 1 \"", lineDirective, StringComparison.Ordinal);
        Assert.EndsWith("Consumer.cs\"", lineDirective, StringComparison.Ordinal);
        Assert.DoesNotContain("<auto-generated", transformed, StringComparison.OrdinalIgnoreCase);

        var taskFiles = Directory
            .EnumerateFiles(taskDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .ToArray();
        Assert.DoesNotContain(taskFiles, name => name!.Contains("Plain", StringComparison.Ordinal));
        Assert.DoesNotContain(taskFiles, name => name!.Contains("Migrations", StringComparison.Ordinal));
        Assert.Equal(
            new[] { "CobaltumOrm.RawQueries.g.cs", "CobaltumOrm.SqlSchema.g.cs" },
            taskFiles
                .Where(name => name!.EndsWith(".g.cs", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());

        var definitionsPath = Path.Combine(taskDirectory, "CobaltumOrm.RawQueries.g.cs");
        Assert.Contains("<auto-generated", File.ReadAllText(definitionsPath), StringComparison.OrdinalIgnoreCase);

        var compileItems = File.ReadAllLines(Path.Combine(
            result.Directory,
            "obj",
            "Release",
            "net10.0",
            "CobaltumCompileItems.txt"));
        Assert.Contains(compileItems, line => Path.GetFileName(CompileItem(line).Path) == "Plain.cs");
        Assert.Contains(compileItems, line => Path.GetFileName(CompileItem(line).Path) == "Migrations.cs");
        Assert.DoesNotContain(compileItems, line => Path.GetFileName(CompileItem(line).Path) == "Consumer.cs");

        var transformedItem = CompileItem(compileItems.Single(line =>
            Path.GetFileName(CompileItem(line).Path) == Path.GetFileName(transformedPath)));
        Assert.False(string.Equals("true", transformedItem.AutoGen, StringComparison.OrdinalIgnoreCase));
        Assert.False(string.Equals("true", transformedItem.DesignTime, StringComparison.OrdinalIgnoreCase));

        var definitionsItem = CompileItem(compileItems.Single(line =>
            Path.GetFileName(CompileItem(line).Path) == Path.GetFileName(definitionsPath)));
        Assert.Equal("true", definitionsItem.AutoGen, ignoreCase: true);
        Assert.Equal("true", definitionsItem.DesignTime, ignoreCase: true);

        var sqlSchemaPath = Path.Combine(taskDirectory, "CobaltumOrm.SqlSchema.g.cs");
        var sqlSchemaItem = CompileItem(compileItems.Single(line =>
            Path.GetFileName(CompileItem(line).Path) == Path.GetFileName(sqlSchemaPath)));
        Assert.Equal("true", sqlSchemaItem.AutoGen, ignoreCase: true);
        Assert.Equal("true", sqlSchemaItem.DesignTime, ignoreCase: true);

        if (assertGeneratedModels)
        {
            var generatedModels = Directory
                .EnumerateFiles(result.Directory, "CobaltumOrm.Models.g.cs", SearchOption.AllDirectories)
                .ToArray();
            Assert.Single(generatedModels);
            Assert.Contains("UsersRow", File.ReadAllText(generatedModels[0]), StringComparison.Ordinal);
        }
    }

    private static CompileItemState CompileItem(string line)
    {
        var fields = line.Split('|');
        Assert.Equal(3, fields.Length);
        return new CompileItemState(fields[0], fields[1], fields[2]);
    }

    private static Dictionary<string, string> CompilerGeneratedSources(BuildResult result) => Directory
        .EnumerateFiles(
            Path.Combine(result.Directory, "generated"),
            "*.cs",
            SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetFileName(path), File.ReadAllText, StringComparer.Ordinal);

    private static BuildResult BuildFixture(
        string source,
        string? databaseProvider = null,
        bool analysisCacheEnabled = true) =>
        CreateBuildFixture(source, databaseProvider, analysisCacheEnabled).Build();

    private static BuildFixtureHandle CreateBuildFixture(
        string source,
        string? databaseProvider = null,
        bool analysisCacheEnabled = true)
    {
        var repository = FindRepositoryRoot();
        var directory = Path.Combine(Path.GetTempPath(), "CobaltumOrm.BuildTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var taskDirectory = Path.Combine(directory, "obj", "Release", "net10.0", "CobaltumOrm");
        Directory.CreateDirectory(taskDirectory);
        File.WriteAllText(
            Path.Combine(taskDirectory, "0001.Migrations.g.cs"),
            "// <auto-generated/>\ninternal static class StaleCobaltumTransform { }");
        File.WriteAllText(Path.Combine(directory, "Consumer.cs"), source);
        File.WriteAllText(Path.Combine(directory, "Plain.cs"), """
            public static class PlainSource
            {
                public const string Suffix = "";
            }
            """);
        File.WriteAllText(Path.Combine(directory, "Migrations.cs"), """
            using CobaltumOrm.Migrations;

            [Migration(1, "create users")]
            public sealed class CreateUsersMigration : Migration
            {
                public override void Up()
                {
                    Create.Table("users")
                        .WithColumn("id").AsInt32().NotNullable()
                        .WithColumn("name").AsString().NotNullable()
                        .WithColumn("local_time").AsDateTime().NotNullable()
                        .WithColumn("document").AsJsonb().NotNullable()
                        .WithColumn("big_id").AsInt64().NotNullable();
                }

                public override void Down()
                {
                    Delete.Table("users");
                }
            }
            """);

        var runtimeProject = SecurityElement.Escape(Path.Combine(repository, "src", "CobaltumOrm", "CobaltumOrm.csproj"));
        var migrationsProject = SecurityElement.Escape(Path.Combine(repository, "src", "CobaltumOrm.Migrations", "CobaltumOrm.Migrations.csproj"));
        var sourceGeneratorTargets = SecurityElement.Escape(Path.Combine(
            repository,
            "src",
            "CobaltumOrm.SourceGenerator",
            "buildTransitive",
            "CobaltumOrm.SourceGenerator.targets"));
        var compilerTaskAssembly = SecurityElement.Escape(Path.Combine(
            repository,
            "src",
            "CobaltumOrm.Compiler",
            "bin",
            "Release",
            "netstandard2.0",
            "CobaltumOrm.Compiler.dll"));
        var providerProperty = databaseProvider is null
            ? string.Empty
            : "    <CobaltumOrmDatabaseProvider>" + SecurityElement.Escape(databaseProvider) + "</CobaltumOrmDatabaseProvider>\n";
        File.WriteAllText(Path.Combine(directory, "Fixture.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                <CobaltumOrmCompilerTaskAssembly>{{compilerTaskAssembly}}</CobaltumOrmCompilerTaskAssembly>
                <CobaltumOrmAnalysisCache>{{analysisCacheEnabled.ToString().ToLowerInvariant()}}</CobaltumOrmAnalysisCache>
            {{providerProperty}}
                <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
                <CompilerGeneratedFilesOutputPath>$(IntermediateOutputPath)generated</CompilerGeneratedFilesOutputPath>
                <RestorePackagesPath>{{SecurityElement.Escape(Path.Combine(GetPackageDirectory(repository), ".packages"))}}</RestorePackagesPath>
                <RestoreSources>https://api.nuget.org/v3/index.json</RestoreSources>
              </PropertyGroup>
              <ItemGroup>
                <Compile Remove="$(IntermediateOutputPath)generated/**/*.cs" />
                <ProjectReference Include="{{runtimeProject}}" />
                <ProjectReference Include="{{migrationsProject}}" />
                <ProjectReference Include="{{SecurityElement.Escape(Path.Combine(repository, "src", "CobaltumOrm.SourceGenerator", "CobaltumOrm.SourceGenerator.csproj"))}}"
                                  OutputItemType="Analyzer"
                                  ReferenceOutputAssembly="false" />
                <PackageReference Include="Npgsql" Version="10.0.3" />
                <AdditionalFiles Include="Migrations/V*__*.sql" />
              </ItemGroup>
              <Target Name="CaptureCobaltumCompileItems"
                      BeforeTargets="CoreCompile"
                      DependsOnTargets="CobaltumOrmTransformSources">
                <WriteLinesToFile File="$(IntermediateOutputPath)CobaltumCompileItems.txt"
                                  Lines="@(Compile->'%(FullPath)|%(AutoGen)|%(DesignTime)')"
                                  Overwrite="true" />
              </Target>
              <Target Name="CaptureCobaltumFileWrites"
                      BeforeTargets="CoreCompile"
                      DependsOnTargets="CobaltumOrmTransformSources">
                <WriteLinesToFile File="$(IntermediateOutputPath)CobaltumFileWrites.txt"
                                  Lines="@(FileWrites->'%(FullPath)')"
                                  Overwrite="true" />
              </Target>
              <Import Project="{{sourceGeneratorTargets}}" />
            </Project>
            """);

        return new BuildFixtureHandle(
            directory,
            Path.Combine(directory, "Fixture.csproj"),
            Path.Combine(directory, "Consumer.cs"),
            taskDirectory,
            Path.Combine(directory, "obj", "Release", "net10.0", "CobaltumCompileItems.txt"),
            Path.Combine(taskDirectory, "CobaltumOrm.TransformSuccess.xml"),
            Path.Combine(directory, "obj", "Release", "net10.0", "CobaltumFileWrites.txt"));
    }

    private static string CSharpLiteral(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string InputProperty(string manifestPath, string name) =>
        (string?)XDocument.Load(manifestPath)
            .Root!
            .Element("Properties")!
            .Elements("Property")
            .Single(element => string.Equals((string?)element.Attribute("name"), name, StringComparison.Ordinal))
            .Attribute("value") ?? string.Empty;

    private static string GetPackageDirectory(string repository)
    {
        lock (PackageLock)
        {
            if (_packageDirectory != null)
            {
                return _packageDirectory;
            }

            var directory = Path.Combine(Path.GetTempPath(), "CobaltumOrm.BuildTests", "packages-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var project = Path.Combine(repository, "src", "CobaltumOrm.SourceGenerator", "CobaltumOrm.SourceGenerator.csproj");
            var process = RunDotnet(repository, "pack", project, "-c", "Release", "--no-restore", "-o", directory, "--nologo");
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(process.Output);
            }

            _packageDirectory = directory;
            return directory;
        }
    }

    private static ProcessResult RunDotnet(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("dotnet did not finish within two minutes.");
        }

        Task.WaitAll(output, error);
        return new ProcessResult(process.ExitCode, output.Result + error.Result);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CobaltumOrm.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate CobaltumOrm.sln.");
    }

    private sealed class BuildFixtureHandle
    {
        internal BuildFixtureHandle(
            string rootDirectory,
            string projectPath,
            string consumerPath,
            string taskDirectory,
            string compileItemsPath,
            string successManifestPath,
            string fileWritesPath)
        {
            RootDirectory = rootDirectory;
            ProjectPath = projectPath;
            ConsumerPath = consumerPath;
            TaskDirectory = taskDirectory;
            CompileItemsPath = compileItemsPath;
            SuccessManifestPath = successManifestPath;
            FileWritesPath = fileWritesPath;
        }

        internal string RootDirectory { get; }

        internal string ProjectPath { get; }

        internal string ConsumerPath { get; }

        internal string TaskDirectory { get; }

        internal string CompileItemsPath { get; }

        internal string SuccessManifestPath { get; }

        internal string FileWritesPath { get; }

        private bool _hasBuilt;

        internal BuildResult Build(params string[] additionalArguments)
        {
            if (_hasBuilt)
            {
                var generatedDirectory = Path.Combine(RootDirectory, "generated");
                if (Directory.Exists(generatedDirectory))
                {
                    Directory.Delete(generatedDirectory, recursive: true);
                }
            }

            var arguments = new[] { "build", ProjectPath, "-c", "Release", "--nologo" }
                .Concat(_hasBuilt ? new[] { "--no-dependencies" } : Array.Empty<string>())
                .Concat(additionalArguments)
                .ToArray();
            var process = RunDotnet(RootDirectory, arguments);
            _hasBuilt = true;
            return new BuildResult(RootDirectory, process.ExitCode == 0, process.Output);
        }

        internal BuildResult Clean()
        {
            var process = RunDotnet(
                RootDirectory,
                "clean",
                ProjectPath,
                "-c",
                "Release",
                "-p:BuildProjectReferences=false",
                "--nologo");
            return new BuildResult(RootDirectory, process.ExitCode == 0, process.Output);
        }

        internal void SetProperty(string name, string value)
        {
            var lines = File.ReadAllLines(ProjectPath).ToList();
            var opening = "<" + name + ">";
            var closing = "</" + name + ">";
            var replacement = "    " + opening + SecurityElement.Escape(value) + closing;
            for (var index = 0; index < lines.Count; index++)
            {
                if (lines[index].TrimStart().StartsWith(opening, StringComparison.Ordinal))
                {
                    lines[index] = replacement;
                    File.WriteAllLines(ProjectPath, lines);
                    return;
                }
            }

            var propertyGroupEnd = lines.FindIndex(line => line.Trim() == "</PropertyGroup>");
            Assert.True(propertyGroupEnd >= 0, "Fixture property group was not found.");
            lines.Insert(propertyGroupEnd, replacement);
            File.WriteAllLines(ProjectPath, lines);
        }
    }

    private sealed record BuildResult(string Directory, bool Succeeded, string Output);
    private sealed record ProcessResult(int ExitCode, string Output);
    private sealed record CompileItemState(string Path, string AutoGen, string DesignTime);
}

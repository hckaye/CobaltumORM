using System;
using System.Linq;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class SchemaQualifiedTests
{
    [Fact]
    public void AppliesQualifiedPublicTableEvolutionAndPreservesSchemaOnRename()
    {
        var result = PostgreSqlMigrationAnalyzer.Analyze(new DatabaseSchema(Array.Empty<Table>()), @"
            CREATE TABLE public.users (id integer NOT NULL);
            ALTER TABLE public.users ADD COLUMN name text;
            ALTER TABLE public.users RENAME COLUMN name TO display_name;
            ALTER TABLE public.users ALTER COLUMN display_name SET NOT NULL;
            ALTER TABLE public.users RENAME TO customers;
            ALTER TABLE public.customers DROP COLUMN id;");

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(item => item.ToString())));
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal("public", table.Schema);
        Assert.Equal("customers", table.Name);
        var column = Assert.Single(table.Columns);
        Assert.Equal("display_name", column.Name);
        Assert.False(column.IsNullable);
    }

    [Fact]
    public void SupportsQuotedSchemaAndTableNamesThroughRenameAndSelect()
    {
        var result = PostgreSqlMigrationAnalyzer.Analyze(new DatabaseSchema(Array.Empty<Table>()), @"
            CREATE TABLE ""Tenant A"".""User""""Profile"" (""Id"" jsonb NOT NULL);
            ALTER TABLE ""Tenant A"".""User""""Profile"" RENAME TO ""Users"";");

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(item => item.ToString())));
        var table = Assert.Single(result.Schema.Tables);
        Assert.Equal("Tenant A", table.Schema);
        Assert.Equal("Users", table.Name);

        var query = QueryAnalyzer.Analyze(result.Schema, @"SELECT ""Id"" FROM ""Tenant A"".""Users""");
        TestSchema.AssertColumns(query, ("Id", "string"));
    }

    [Fact]
    public void ResolvesSameTableNameInDifferentSchemasAndKeepsAliasesDistinct()
    {
        var result = PostgreSqlMigrationAnalyzer.Analyze(new DatabaseSchema(Array.Empty<Table>()), @"
            CREATE TABLE public.users (id integer NOT NULL);
            CREATE TABLE audit.users (id json NOT NULL);");

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(item => item.ToString())));
        Assert.Equal(2, result.Schema.Tables.Count);
        Assert.Equal("public", result.Schema.Tables[0].Schema);
        Assert.Equal("audit", result.Schema.Tables[1].Schema);

        var query = QueryAnalyzer.Analyze(
            result.Schema,
            "SELECT p.id, a.id FROM public.users p JOIN audit.users a ON true");
        TestSchema.AssertColumns(query, ("id", "int"), ("id", "string"));

        var ambiguous = QueryAnalyzer.Analyze(result.Schema, "SELECT id FROM users");
        Assert.Contains(ambiguous.Diagnostics, item => item.Code == "SQL218");

        var duplicateAlias = QueryAnalyzer.Analyze(
            result.Schema,
            "SELECT p.id FROM public.users p JOIN audit.users p ON true");
        Assert.Contains(duplicateAlias.Diagnostics, item => item.Code == "SQL201");

        var dropped = PostgreSqlMigrationAnalyzer.Analyze(result.Schema, "DROP TABLE public.users;");
        Assert.False(dropped.HasErrors, string.Join("\n", dropped.Diagnostics.Select(item => item.ToString())));
        var remaining = Assert.Single(dropped.Schema.Tables);
        Assert.Equal("audit", remaining.Schema);
        Assert.Equal("users", remaining.Name);
    }

    [Fact]
    public void SupportsTheDialectAnalyzerInterface()
    {
        ISchemaMigrationAnalyzer analyzer = new PostgreSqlSchemaMigrationAnalyzer();
        var result = analyzer.Analyze(
            new DatabaseSchema(Array.Empty<Table>()),
            "CREATE TABLE public.items (payload jsonb NOT NULL);");

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(item => item.ToString())));
        Assert.Equal("public", Assert.Single(result.Schema.Tables).Schema);
    }

    [Fact]
    public void PreservesJsonAndJsonbParameterTypeNames()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("documents", new[]
            {
                new Column("document", "json"),
                new Column("indexed_document", "jsonb"),
            }),
        });

        var json = QueryAnalyzer.Analyze(schema, "SELECT CAST(@document AS json)");
        var jsonb = QueryAnalyzer.Analyze(
            schema,
            "SELECT indexed_document FROM documents WHERE indexed_document = @document");
        var jsonbLiteral = QueryAnalyzer.Analyze(
            schema,
            "SELECT indexed_document FROM documents WHERE indexed_document = '{\"active\":true}'");

        TestSchema.AssertSuccess(json);
        TestSchema.AssertSuccess(jsonb);
        TestSchema.AssertSuccess(jsonbLiteral);
        Assert.Equal("json", Assert.Single(json.Parameters).DatabaseTypeName);
        Assert.Equal("jsonb", Assert.Single(jsonb.Parameters).DatabaseTypeName);
        Assert.Equal("string", Assert.Single(json.Columns).ClrType);
        Assert.Equal("string", Assert.Single(jsonb.Columns).ClrType);
    }
}

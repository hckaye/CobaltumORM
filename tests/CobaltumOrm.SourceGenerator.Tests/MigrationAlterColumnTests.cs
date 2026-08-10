using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CobaltumOrm.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace CobaltumOrm.SourceGenerator.Tests;

public sealed class MigrationAlterColumnTests
{
    [Fact]
    public void PostgreSqlWriterPreservesAlterColumnStatementOrderAndText()
    {
        var writer = new PostgreSqlMigrationSqlWriter();

        Assert.True(writer.TryAlterColumn(
            "\"accounts\".\"users\"",
            "\"display-name\"",
            "character varying(80)",
            null,
            out var typeSql,
            out var typeError));
        Assert.Null(typeError);
        Assert.Equal(
            "ALTER TABLE \"accounts\".\"users\" ALTER COLUMN \"display-name\" TYPE character varying(80);",
            typeSql);

        Assert.True(writer.TryAlterColumn(
            "\"accounts\".\"users\"",
            "\"display-name\"",
            null,
            true,
            out var nullableSql,
            out var nullableError));
        Assert.Null(nullableError);
        Assert.Equal(
            "ALTER TABLE \"accounts\".\"users\" ALTER COLUMN \"display-name\" DROP NOT NULL;",
            nullableSql);

        Assert.True(writer.TryAlterColumn(
            "\"accounts\".\"users\"",
            "\"display-name\"",
            "character varying(80)",
            false,
            out var combinedSql,
            out var combinedError));
        Assert.Null(combinedError);
        Assert.Equal(
            "ALTER TABLE \"accounts\".\"users\" ALTER COLUMN \"display-name\" TYPE character varying(80);\n" +
            "ALTER TABLE \"accounts\".\"users\" ALTER COLUMN \"display-name\" SET NOT NULL;",
            combinedSql);
    }

    [Fact]
    public void PostgreSqlWriterExplainsWhenNoAlterTargetIsProvided()
    {
        var writer = new PostgreSqlMigrationSqlWriter();

        Assert.False(writer.TryAlterColumn(
            "\"users\"",
            "\"name\"",
            null,
            null,
            out var sql,
            out var error));
        Assert.Null(sql);
        Assert.Equal("ALTER COLUMN requires a target SQL type, target nullability, or both.", error);
    }

    [Fact]
    public void MigrationReaderReportsWriterFailureAtTheAlteredColumn()
    {
        const string source = """
            using CobaltumOrm.Migrations;

            [Migration(1)]
            public sealed class AlterUsers : Migration
            {
                public override void Up()
                {
                    Alter.Table("users").InSchema("accounts").AlterColumn("display-name").AsString(80).NotNullable();
                }

                public override void Down() { }
            }
            """;
        var writer = new FailingMigrationWriter();
        var dialect = new TestDialect(writer);
        var diagnostics = new List<RoslynDiagnostic>();

        var steps = ReadMigration(source, dialect, diagnostics, out var sourceText);

        Assert.Null(steps);
        Assert.Equal(1, writer.TryAlterColumnCallCount);
        Assert.Equal("\"accounts\".\"users\"", writer.QualifiedTable);
        Assert.Equal("\"display-name\"", writer.QuotedColumn);
        Assert.Equal("character varying(80)", writer.SqlType);
        Assert.False(writer.Nullable);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COB001", diagnostic.Id);
        Assert.Contains("test provider cannot generate a complete ALTER COLUMN definition", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(
            "\"display-name\"",
            sourceText.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    private static object? ReadMigration(
        string source,
        IDatabaseDialect dialect,
        ICollection<RoslynDiagnostic> diagnostics,
        out string sourceText)
    {
        var generation = GeneratorTestHost.Run(source);
        var syntaxTree = generation.Compilation.SyntaxTrees.Single(tree => tree.FilePath == "Consumer.cs");
        sourceText = syntaxTree.GetText().ToString();
        var semanticModel = generation.Compilation.GetSemanticModel(syntaxTree);
        var methodSyntax = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Up");
        var upMethod = (IMethodSymbol)semanticModel.GetDeclaredSymbol(methodSyntax)!;
        var readerType = typeof(CobaltumOrmGenerator).Assembly.GetType(
            "CobaltumOrm.SourceGenerator.MigrationSyntaxReader")!;
        var readMethod = readerType.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!;
        return readMethod.Invoke(
            null,
            new object?[]
            {
                upMethod,
                generation.Compilation,
                dialect,
                new Action<RoslynDiagnostic>(diagnostics.Add),
            });
    }

    private sealed class TestDialect : IDatabaseDialect
    {
        private readonly IDatabaseDialect _inner = DatabaseDialects.PostgreSqlDialect;

        internal TestDialect(ISqlMigrationWriter migrationSqlWriter)
        {
            MigrationSqlWriter = migrationSqlWriter;
        }

        public DatabaseProvider Provider => _inner.Provider;
        public string Name => _inner.Name;
        public IQueryAnalyzer QueryAnalyzer => _inner.QueryAnalyzer;
        public ISchemaMigrationAnalyzer SchemaMigrationAnalyzer => _inner.SchemaMigrationAnalyzer;
        public ISqlScriptClassifier ScriptClassifier => _inner.ScriptClassifier;
        public ISqlIdentifierQuoter IdentifierQuoter => _inner.IdentifierQuoter;
        public ISqlTypeMapper TypeMapper => _inner.TypeMapper;
        public ISqlMigrationWriter MigrationSqlWriter { get; }
        public ISchemaRules SchemaRules => _inner.SchemaRules;
    }

    private sealed class FailingMigrationWriter : ISqlMigrationWriter
    {
        private readonly ISqlMigrationWriter _inner = DatabaseDialects.PostgreSqlDialect.MigrationSqlWriter;

        internal int TryAlterColumnCallCount { get; private set; }
        internal string? QualifiedTable { get; private set; }
        internal string? QuotedColumn { get; private set; }
        internal string? SqlType { get; private set; }
        internal bool? Nullable { get; private set; }

        public string FormatColumn(string quotedName, string sqlType, bool? nullable, bool primaryKey, bool identity) =>
            _inner.FormatColumn(quotedName, sqlType, nullable, primaryKey, identity);

        public string CreateTable(string qualifiedTable, IReadOnlyList<string> columns) =>
            _inner.CreateTable(qualifiedTable, columns);

        public string AddColumn(string qualifiedTable, string column) =>
            _inner.AddColumn(qualifiedTable, column);

        public bool TryAlterColumn(
            string qualifiedTable,
            string quotedColumn,
            string? sqlType,
            bool? nullable,
            out string? sql,
            out string? error)
        {
            TryAlterColumnCallCount++;
            QualifiedTable = qualifiedTable;
            QuotedColumn = quotedColumn;
            SqlType = sqlType;
            Nullable = nullable;
            sql = null;
            error = "The test provider cannot generate a complete ALTER COLUMN definition.";
            return false;
        }

        public string DropTable(string qualifiedTable) => _inner.DropTable(qualifiedTable);

        public string DropColumn(string qualifiedTable, string quotedColumn) =>
            _inner.DropColumn(qualifiedTable, quotedColumn);

        public string RenameTable(string qualifiedTable, string quotedNewName) =>
            _inner.RenameTable(qualifiedTable, quotedNewName);

        public string RenameColumn(string qualifiedTable, string quotedOldName, string quotedNewName) =>
            _inner.RenameColumn(qualifiedTable, quotedOldName, quotedNewName);
    }
}

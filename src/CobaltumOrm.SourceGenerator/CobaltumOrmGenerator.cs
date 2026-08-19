using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CobaltumOrm.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynDiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CobaltumOrm.SourceGenerator;

/// <summary>Builds a database schema and typed query contract during C# compilation.</summary>
#if !COBALTUM_COMPILER_TASK
[Generator(LanguageNames.CSharp)]
#endif
public sealed class CobaltumOrmGenerator : IIncrementalGenerator
{
    private static readonly Regex FlywayName = new Regex(
        @"^V(?<version>[0-9]+)__(?<description>.+)\.sql$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var sqlFiles = context.AdditionalTextsProvider
            .Where(file => string.Equals(Path.GetExtension(file.Path), ".sql", StringComparison.OrdinalIgnoreCase))
            .Select((file, cancellationToken) =>
                new AdditionalSqlFile(file.Path, file.GetText(cancellationToken)?.ToString() ?? string.Empty))
            .Collect();

        var migrationFiles = context.AdditionalTextsProvider
            .Where(file => string.Equals(Path.GetExtension(file.Path), ".cs", StringComparison.OrdinalIgnoreCase))
            .Select((file, cancellationToken) =>
                new AdditionalCSharpFile(file.Path, file.GetText(cancellationToken)?.ToString() ?? string.Empty))
            .Collect();

        var input = context.CompilationProvider
            .Combine(sqlFiles)
            .Combine(migrationFiles)
            .Combine(context.AnalyzerConfigOptionsProvider);

        context.RegisterSourceOutput(input, static (productionContext, value) =>
            Execute(
                productionContext,
                value.Left.Left.Left,
                value.Left.Left.Right,
                value.Left.Right,
                value.Right));
    }

    private static void Execute(
        SourceProductionContext context,
        Compilation compilation,
        ImmutableArray<AdditionalSqlFile> sqlFiles,
        ImmutableArray<AdditionalCSharpFile> migrationFiles,
        Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider options)
    {
        void Report(RoslynDiagnostic diagnostic)
        {
            context.ReportDiagnostic(diagnostic);
        }

        var dialect = GetDatabaseDialect(options, Report);
        if (dialect is null)
        {
            return;
        }

        var analysisCache = GetAnalysisCache(options, dialect);

        var generatedNamespace = GetGeneratedNamespace(options);
        if (!IsValidNamespace(generatedNamespace))
        {
            Report(RoslynDiagnostic.Create(
                GeneratorDiagnostics.InvalidConfiguration,
                Location.None,
                $"'{generatedNamespace}' is not a valid C# namespace. Set CobaltumOrmGeneratedNamespace to dot-separated identifiers."));
            return;
        }

        var schemaCompilation = AddExternalMigrationTrees(compilation, migrationFiles);
        var schemaBuild = BuildSchema(schemaCompilation, sqlFiles, dialect, analysisCache, Report);
        var migrations = schemaBuild.Migrations;
        var schema = schemaBuild.Schema;

        var queries = CollectNamedQueries(compilation, Report);
        var validQueries = new List<QuerySource>();
        var queryAnalyses = new List<AnalysisResult>();
        foreach (var query in queries)
        {
            var statements = dialect.ScriptClassifier.SplitAndClassify(query.Sql, out var scriptError);
            if (scriptError != null)
            {
                Report(RoslynDiagnostic.Create(
                    GeneratorDiagnostics.QuerySql,
                    query.Location,
                    "SQL300",
                    scriptError.Message));
                continue;
            }

            var meaningful = statements
                .Where(statement => statement.Kind != SqlStatementKind.Empty)
                .ToArray();
            if (meaningful.Length != 1 ||
                (meaningful[0].Kind != SqlStatementKind.Select &&
                 meaningful[0].Kind != SqlStatementKind.DataManipulation))
            {
                Report(RoslynDiagnostic.Create(
                    GeneratorDiagnostics.QuerySql,
                    query.Location,
                    "SQL300",
                    "Query attribute must contain exactly one SELECT, INSERT, UPDATE, DELETE, or TRUNCATE statement."));
                continue;
            }

            var analysis = analysisCache.AnalyzeQuery(schema, query.Sql, dialect.QueryAnalyzer);
            foreach (var diagnostic in analysis.Diagnostics)
            {
                Report(RoslynDiagnostic.Create(
                    GeneratorDiagnostics.QuerySql,
                    query.Location,
                    diagnostic.Code,
                    diagnostic.Message));
            }

            if (!analysis.HasErrors)
            {
                if (analysis.Columns.Count == 0 && query.ResultType != null)
                {
                    Report(RoslynDiagnostic.Create(
                        GeneratorDiagnostics.ResultMapping,
                        query.Location,
                        "a statement that does not return rows cannot be mapped to a result type. " +
                        "Use the non-generic Query attribute to execute it and get the affected row count."));
                    continue;
                }

                if (query.ResultType != null &&
                    !ResultMappingFactory.TryCreate(
                        compilation,
                        query.ResultType,
                        analysis,
                        out _,
                        out var mappingError))
                {
                    Report(RoslynDiagnostic.Create(
                        GeneratorDiagnostics.ResultMapping,
                        query.Location,
                        mappingError));
                    continue;
                }

                validQueries.Add(query);
                queryAnalyses.Add(analysis);
            }
        }

        ValidateRawQueries(compilation, schema, dialect, analysisCache, Report);

        if (schemaBuild.HasErrors)
        {
            return;
        }

        if (compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.IsExternalInit") is null &&
            (schema.Tables.Count != 0 || validQueries.Count != 0))
        {
            context.AddSource("CobaltumOrm.IsExternalInit.g.cs", SourceText.From(GeneratedSourceWriter.WriteIsExternalInit(), System.Text.Encoding.UTF8));
        }

        if (schema.Tables.Count != 0 &&
            compilation.GetTypeByMetadataName(generatedNamespace + ".SqlSchema") is null)
        {
            context.AddSource(
                "CobaltumOrm.SqlSchema.g.cs",
                SourceText.From(
                    GeneratedSourceWriter.WriteSqlSchema(generatedNamespace, schema, dialect),
                    System.Text.Encoding.UTF8));
        }

        // The build transform writes the table records before the compiler runs, so that
        // Query<TResult> and [Query<TResult>] can name them. Generate them here only when the
        // transform is not part of this build.
        if (schema.Tables.Count != 0 &&
            compilation.GetTypeByMetadataName(generatedNamespace + ".Tables") is null)
        {
            context.AddSource(
                "CobaltumOrm.Models.g.cs",
                SourceText.From(
                    GeneratedSourceWriter.WriteModels(
                        generatedNamespace,
                        schema,
                        compilation,
                        dialect,
                        analysisCache),
                    System.Text.Encoding.UTF8));
        }

        var queryGroups = validQueries
            .Select((query, index) => new { Query = query, Analysis = queryAnalyses[index] })
            .GroupBy(item => item.Query.Owner, SymbolEqualityComparer.Default)
            .OrderBy(group => group.Key!.ToDisplayString(), StringComparer.Ordinal)
            .ToList();
        var queryHintNames = CSharpNames.Allocate(
            queryGroups,
            group => "CobaltumOrm.Queries." + SafeHintName(group.Key!.ToDisplayString()) + ".g.cs");
        foreach (var group in queryGroups)
        {
            var ownerQueries = group.Select(item => item.Query).ToList();
            var analyses = group.Select(item => item.Analysis).ToList();
            context.AddSource(
                queryHintNames[group],
                SourceText.From(GeneratedSourceWriter.WriteQueries((INamedTypeSymbol)group.Key!, ownerQueries, analyses, compilation, dialect), System.Text.Encoding.UTF8));
        }

        var compilationTrees = new HashSet<SyntaxTree>(compilation.SyntaxTrees);
        var runtimeMigrations = migrations
            .Where(item =>
                item.FlywayFile != null ||
                (item.MigrationType != null && item.MigrationType.DeclaringSyntaxReferences
                    .Any(reference => compilationTrees.Contains(reference.SyntaxTree))))
            .ToList();
        if (compilation.GetTypeByMetadataName("CobaltumOrm.Migrations.MigrationInfo") != null)
        {
            context.AddSource(
                "CobaltumOrm.FlywayMigrations.g.cs",
                SourceText.From(GeneratedSourceWriter.WriteMigrations(generatedNamespace, runtimeMigrations), System.Text.Encoding.UTF8));
        }
    }

    private static Compilation AddExternalMigrationTrees(
        Compilation compilation,
        ImmutableArray<AdditionalCSharpFile> migrationFiles)
    {
        if (migrationFiles.IsDefaultOrEmpty)
        {
            return compilation;
        }

        var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
            ?? CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = migrationFiles
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => CSharpSyntaxTree.ParseText(
                SourceText.From(file.Text, System.Text.Encoding.UTF8),
                parseOptions,
                file.Path));
        return compilation.AddSyntaxTrees(trees);
    }

    internal static CompilationSchemaResult BuildSchema(
        Compilation compilation,
        ImmutableArray<AdditionalSqlFile> sqlFiles,
        IDatabaseDialect dialect,
        AnalysisCache analysisCache,
        Action<RoslynDiagnostic> report)
    {
        var schemaHasErrors = false;
        void ReportSchema(RoslynDiagnostic diagnostic)
        {
            report(diagnostic);
            schemaHasErrors |= diagnostic.Severity == RoslynDiagnosticSeverity.Error;
        }

        var migrations = CollectCSharpMigrations(compilation, dialect, ReportSchema);
        migrations.AddRange(CollectFlywayMigrations(sqlFiles, ReportSchema));
        ValidateMigrationVersions(migrations, ReportSchema);

        var orderedMigrations = migrations
            .OrderBy(item => item.Version)
            .ThenBy(item => item.Description, StringComparer.Ordinal)
            .ToArray();
        DatabaseSchema ApplyMigrations()
        {
            var appliedSchema = new DatabaseSchema(Array.Empty<Table>());
            foreach (var migration in orderedMigrations)
            {
                foreach (var step in migration.Steps)
                {
                    var statements = dialect.ScriptClassifier.SplitAndClassify(step.Sql, out var scriptError);
                    if (scriptError != null)
                    {
                        ReportSchema(RoslynDiagnostic.Create(
                            GeneratorDiagnostics.SchemaSql,
                            MigrationSqlLocation(migration, step, scriptError.Span),
                            "DDL300",
                            scriptError.Message));
                    }

                    foreach (var statement in statements)
                    {
                        if (statement.Kind == SqlStatementKind.Empty ||
                            statement.Kind == SqlStatementKind.Select ||
                            statement.Kind == SqlStatementKind.DataManipulation ||
                            statement.Kind == SqlStatementKind.SchemaNeutral)
                        {
                            continue;
                        }

                        if (statement.Kind == SqlStatementKind.Unsupported)
                        {
                            ReportSchema(RoslynDiagnostic.Create(
                                GeneratorDiagnostics.SchemaSql,
                                MigrationSqlLocation(migration, step, statement.Span),
                                "DDL300",
                                "This migration statement may change the queryable schema and is not supported by compile-time analysis."));
                            continue;
                        }

                        var result = dialect.SchemaMigrationAnalyzer.Analyze(appliedSchema, statement.Text);
                        foreach (var diagnostic in result.Diagnostics)
                        {
                            var span = new SourceSpan(
                                statement.Span.Start + diagnostic.Span.Start,
                                diagnostic.Span.Length);
                            ReportSchema(RoslynDiagnostic.Create(
                                GeneratorDiagnostics.SchemaSql,
                                MigrationSqlLocation(migration, step, span),
                                diagnostic.Code,
                                diagnostic.Message));
                        }

                        if (!result.HasErrors)
                        {
                            appliedSchema = result.Schema;
                        }
                    }
                }
            }

            return appliedSchema;
        }

        var schema = new DatabaseSchema(Array.Empty<Table>());
        if (schemaHasErrors)
        {
            schema = ApplyMigrations();
        }
        else
        {
            var semanticMigrations = orderedMigrations
                .Select(migration => new SemanticMigrationInput(
                    migration.Version,
                    migration.Description,
                    migration.Steps.Select(step => step.Sql)))
                .ToArray();
            schema = analysisCache.GetOrAnalyzeSchema(
                semanticMigrations,
                () =>
                {
                    var analyzedSchema = ApplyMigrations();
                    return new CacheComputation<DatabaseSchema>(analyzedSchema, !schemaHasErrors);
                },
                out _);
        }

        return new CompilationSchemaResult(schema, migrations, schemaHasErrors);
    }

    private static List<MigrationSource> CollectCSharpMigrations(
        Compilation compilation,
        IDatabaseDialect dialect,
        Action<RoslynDiagnostic> report)
    {
        var migrations = new List<MigrationSource>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var migrationBaseType = compilation.GetTypeByMetadataName("CobaltumOrm.Migrations.Migration");
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            foreach (var declaration in syntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var symbol = semanticModel.GetDeclaredSymbol(declaration) as INamedTypeSymbol;
                if (symbol is null || !seen.Add(symbol))
                {
                    continue;
                }

                var migrationAttribute = symbol.GetAttributes().FirstOrDefault(attribute =>
                    string.Equals(
                        attribute.AttributeClass?.ToDisplayString(),
                        "CobaltumOrm.Migrations.MigrationAttribute",
                        StringComparison.Ordinal));
                if (migrationAttribute is null)
                {
                    continue;
                }

                var location = migrationAttribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? declaration.Identifier.GetLocation();
                if (migrationBaseType is null || !IsMigrationType(symbol, migrationBaseType))
                {
                    report(RoslynDiagnostic.Create(
                        GeneratorDiagnostics.UnsupportedDeclaration,
                        location,
                        $"Migration '{symbol.ToDisplayString()}' must derive from CobaltumOrm.Migrations.Migration."));
                    continue;
                }

                if (symbol.IsAbstract || symbol.TypeParameters.Length != 0)
                {
                    report(RoslynDiagnostic.Create(
                        GeneratorDiagnostics.UnsupportedDeclaration,
                        location,
                        $"Migration '{symbol.ToDisplayString()}' must be a concrete, non-generic class."));
                    continue;
                }

                if (!symbol.InstanceConstructors.Any(constructor =>
                        constructor.Parameters.Length == 0 &&
                        constructor.DeclaredAccessibility == Accessibility.Public))
                {
                    report(RoslynDiagnostic.Create(
                        GeneratorDiagnostics.UnsupportedDeclaration,
                        location,
                        $"Migration '{symbol.ToDisplayString()}' must have a public parameterless constructor."));
                    continue;
                }

                if (migrationAttribute.ConstructorArguments.Length == 0 ||
                    migrationAttribute.ConstructorArguments[0].Value is null)
                {
                    report(RoslynDiagnostic.Create(
                        GeneratorDiagnostics.InvalidMigration,
                        location,
                        $"Migration '{symbol.ToDisplayString()}' must have a positive constant version."));
                    continue;
                }

                var version = Convert.ToInt64(migrationAttribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
                if (version <= 0)
                {
                    report(RoslynDiagnostic.Create(
                        GeneratorDiagnostics.InvalidMigration,
                        location,
                        $"Migration '{symbol.ToDisplayString()}' must have a positive version."));
                    continue;
                }

                var description = migrationAttribute.ConstructorArguments.Length >= 2
                    ? migrationAttribute.ConstructorArguments[1].Value as string
                    : null;
                if (string.IsNullOrWhiteSpace(description))
                {
                    description = ReadableDescription(symbol.Name);
                }

                var upMethod = symbol.GetMembers("Up")
                    .OfType<IMethodSymbol>()
                    .FirstOrDefault(method => SymbolEqualityComparer.Default.Equals(method.ContainingType, symbol));
                if (upMethod is null)
                {
                    report(RoslynDiagnostic.Create(
                        GeneratorDiagnostics.UnsupportedDeclaration,
                        declaration.Identifier.GetLocation(),
                        $"Migration '{symbol.ToDisplayString()}' must declare Up directly for compile-time analysis."));
                    continue;
                }

                var steps = MigrationSyntaxReader.Read(upMethod, compilation, dialect, report);
                if (steps != null)
                {
                    migrations.Add(new MigrationSource(version, description!, location, steps, null, symbol));
                }
            }
        }

        return migrations;
    }

    private static IEnumerable<MigrationSource> CollectFlywayMigrations(
        ImmutableArray<AdditionalSqlFile> files,
        Action<RoslynDiagnostic> report)
    {
        foreach (var file in files.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            var match = FlywayName.Match(Path.GetFileName(file.Path));
            if (!match.Success)
            {
                continue;
            }

            if (!long.TryParse(match.Groups["version"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
                version <= 0)
            {
                report(RoslynDiagnostic.Create(
                    GeneratorDiagnostics.InvalidMigration,
                    SqlLocation(file, new SourceSpan(0, 0)),
                    $"Flyway file '{Path.GetFileName(file.Path)}' must use a positive 64-bit version."));
                continue;
            }

            var description = match.Groups["description"].Value.Replace('_', ' ').Trim();
            if (description.Length == 0 || string.IsNullOrWhiteSpace(file.Text))
            {
                report(RoslynDiagnostic.Create(
                    GeneratorDiagnostics.InvalidMigration,
                    SqlLocation(file, new SourceSpan(0, file.Text.Length)),
                    $"Flyway file '{Path.GetFileName(file.Path)}' must have a description and non-empty SQL."));
                continue;
            }

            var location = SqlLocation(file, new SourceSpan(0, file.Text.Length));
            yield return new MigrationSource(
                version,
                description,
                location,
                new[] { new MigrationStep(file.Text, location) },
                file,
                null);
        }
    }

    private static void ValidateMigrationVersions(
        IReadOnlyList<MigrationSource> migrations,
        Action<RoslynDiagnostic> report)
    {
        foreach (var group in migrations.GroupBy(item => item.Version).Where(group => group.Count() > 1))
        {
            var sources = string.Join(", ", group.Select(item => "'" + item.Description + "'"));
            foreach (var migration in group)
            {
                report(RoslynDiagnostic.Create(
                    GeneratorDiagnostics.InvalidMigration,
                    migration.Location,
                    $"Migration version {group.Key.ToString(CultureInfo.InvariantCulture)} is declared more than once: {sources}."));
            }
        }
    }

    private static List<QuerySource> CollectNamedQueries(Compilation compilation, Action<RoslynDiagnostic> report)
    {
        var queries = new List<QuerySource>();
        var owners = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            foreach (var declaration in syntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var owner = semanticModel.GetDeclaredSymbol(declaration) as INamedTypeSymbol;
                if (owner is null || !owners.Add(owner))
                {
                    continue;
                }

                var attributes = owner.GetAttributes().Where(attribute =>
                    IsQueryAttribute(attribute.AttributeClass)).ToList();
                if (attributes.Count == 0)
                {
                    continue;
                }

                var ownerLocation = owner.Locations.FirstOrDefault() ?? Location.None;
                if (owner.ContainingType != null || owner.TypeParameters.Length != 0 ||
                    owner.DeclaringSyntaxReferences
                        .Select(reference => reference.GetSyntax())
                        .OfType<ClassDeclarationSyntax>()
                        .Any(part => !part.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword))))
                {
                    report(RoslynDiagnostic.Create(
                        GeneratorDiagnostics.UnsupportedDeclaration,
                        ownerLocation,
                        $"Query container '{owner.ToDisplayString()}' must be a top-level, non-generic partial class."));
                    continue;
                }

                var candidates = new List<QuerySource>();
                foreach (var attribute in attributes)
                {
                    var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? ownerLocation;
                    if (attribute.ConstructorArguments.Length < 2 ||
                        !(attribute.ConstructorArguments[0].Value is string name) ||
                        !(attribute.ConstructorArguments[1].Value is string sql) ||
                        string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sql))
                    {
                        report(RoslynDiagnostic.Create(
                            GeneratorDiagnostics.UnsupportedDeclaration,
                            location,
                            "Query attribute arguments must be non-empty compile-time string constants."));
                        continue;
                    }

                    var resultType = attribute.AttributeClass?.Arity == 1
                        ? attribute.AttributeClass.TypeArguments[0]
                        : null;
                    candidates.Add(new QuerySource(owner, name, sql, location, resultType));
                }

                var existingNames = new HashSet<string>(owner.GetMembers().Select(member => member.Name), StringComparer.Ordinal);
                var generatedNames = new Dictionary<string, QuerySource>(StringComparer.Ordinal);
                foreach (var candidate in candidates)
                {
                    var baseName = CSharpNames.Pascal(candidate.Name, "Query");
                    var names = candidate.ResultType == null
                        ? new[] { baseName, baseName + "Async", baseName + "Result", baseName + "Parameters" }
                        : new[] { baseName, baseName + "Async", baseName + "Parameters" };
                    var collision = names.FirstOrDefault(name => existingNames.Contains(name) || generatedNames.ContainsKey(name));
                    if (collision != null)
                    {
                        report(RoslynDiagnostic.Create(
                            GeneratorDiagnostics.NameCollision,
                            candidate.Location,
                            $"Query '{candidate.Name}' would generate member '{collision}', which is already used in '{owner.ToDisplayString()}'."));
                        continue;
                    }

                    foreach (var name in names)
                    {
                        generatedNames.Add(name, candidate);
                    }

                    queries.Add(candidate);
                }
            }
        }

        return queries;
    }

    private static bool IsQueryAttribute(INamedTypeSymbol? attributeType)
    {
        if (attributeType == null ||
            attributeType.ContainingNamespace.ToDisplayString() != "CobaltumOrm")
        {
            return false;
        }

        var definition = attributeType.OriginalDefinition;
        return definition.MetadataName == "QueryAttribute" ||
            definition.MetadataName == "QueryAttribute`1";
    }

    private static void ValidateRawQueries(
        Compilation compilation,
        DatabaseSchema schema,
        IDatabaseDialect dialect,
        AnalysisCache analysisCache,
        Action<RoslynDiagnostic> report)
    {
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            foreach (var invocation in syntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var operation = semanticModel.GetOperation(invocation) as IInvocationOperation;
                var symbol = operation?.TargetMethod;
                var original = symbol?.ReducedFrom ?? symbol;
                if (original is null || original.Name != "Query" ||
                    original.ContainingType?.ToDisplayString() != "CobaltumOrm.CobaltumQueryExtensions" ||
                    original.Parameters.Length < 2 || original.Parameters[1].Type.SpecialType != SpecialType.System_String)
                {
                    continue;
                }

                var explicitResultType = ResultTypeArgument(semanticModel, operation!, invocation);

                var sqlArgument = operation!.Arguments.FirstOrDefault(argument =>
                    argument.Parameter?.Name == "sql" &&
                    argument.Parameter.Type.SpecialType == SpecialType.System_String);
                if (sqlArgument is null)
                {
                    continue;
                }

                var expression = sqlArgument.Value.Syntax;
                var constant = semanticModel.GetConstantValue(expression);
                if (!constant.HasValue || !(constant.Value is string sql))
                {
                    report(RoslynDiagnostic.Create(GeneratorDiagnostics.DynamicRawQuery, expression.GetLocation()));
                    continue;
                }

                var statements = dialect.ScriptClassifier.SplitAndClassify(sql, out var scriptError);
                if (scriptError != null)
                {
                    report(RoslynDiagnostic.Create(
                        GeneratorDiagnostics.QuerySql,
                        expression.GetLocation(),
                        "SQL300",
                        scriptError.Message));
                    continue;
                }

                var hasStatement = false;
                foreach (var statement in statements)
                {
                    if (statement.Kind == SqlStatementKind.Empty)
                    {
                        continue;
                    }

                    hasStatement = true;
                    if (statement.Kind == SqlStatementKind.SchemaNeutral)
                    {
                        continue;
                    }

                    if (statement.Kind != SqlStatementKind.Select &&
                        statement.Kind != SqlStatementKind.DataManipulation)
                    {
                        report(RoslynDiagnostic.Create(
                            GeneratorDiagnostics.QuerySql,
                            expression.GetLocation(),
                            "SQL300",
                            "Raw Query SQL may return rows or execute INSERT, UPDATE, DELETE, or TRUNCATE, but schema changes must be declared as migrations."));
                        continue;
                    }

                    var statementSql = statement.Text.Trim();
                    if (statementSql.EndsWith(";", StringComparison.Ordinal))
                    {
                        statementSql = statementSql.Substring(0, statementSql.Length - 1);
                    }

                    var analysis = analysisCache.AnalyzeQuery(schema, statementSql, dialect.QueryAnalyzer);
                    foreach (var diagnostic in analysis.Diagnostics)
                    {
                        report(RoslynDiagnostic.Create(
                            GeneratorDiagnostics.QuerySql,
                            expression.GetLocation(),
                            diagnostic.Code,
                            diagnostic.Message));
                    }

                    if (!analysis.HasErrors && explicitResultType != null && analysis.Columns.Count == 0)
                    {
                        report(RoslynDiagnostic.Create(
                            GeneratorDiagnostics.ResultMapping,
                            expression.GetLocation(),
                            "Query<TResult> requires a statement that returns rows"));
                    }
                    else if (!analysis.HasErrors && explicitResultType != null &&
                             !ResultMappingFactory.TryCreate(
                                 compilation,
                                 explicitResultType,
                                 analysis,
                                 out _,
                                 out var mappingError))
                    {
                        report(RoslynDiagnostic.Create(
                            GeneratorDiagnostics.ResultMapping,
                            expression.GetLocation(),
                            mappingError));
                    }
                }

                if (!hasStatement)
                {
                    report(RoslynDiagnostic.Create(
                        GeneratorDiagnostics.QuerySql,
                        expression.GetLocation(),
                        "SQL300",
                        "Raw Query SQL must contain a statement."));
                }
            }
        }
    }

    private static ITypeSymbol? ResultTypeArgument(
        SemanticModel semanticModel,
        IInvocationOperation operation,
        InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name is GenericNameSyntax genericName &&
            genericName.TypeArgumentList.Arguments.Count == 1)
        {
            return semanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]).Type;
        }

        if (invocation.Expression is GenericNameSyntax directGenericName &&
            directGenericName.TypeArgumentList.Arguments.Count == 1)
        {
            return semanticModel.GetTypeInfo(directGenericName.TypeArgumentList.Arguments[0]).Type;
        }

        var method = operation.TargetMethod;
        if (method.TypeArguments.Length == 1)
        {
            return method.TypeArguments[0];
        }

        return method.ReducedFrom?.TypeArguments.Length == 1
            ? method.ReducedFrom.TypeArguments[0]
            : null;
    }

    private static string GetGeneratedNamespace(Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider options)
    {
        return options.GlobalOptions.TryGetValue("build_property.CobaltumOrmGeneratedNamespace", out var value) &&
               !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : "CobaltumOrm.Generated";
    }

    private static IDatabaseDialect? GetDatabaseDialect(
        Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider options,
        Action<RoslynDiagnostic> report)
    {
        var providerName = options.GlobalOptions.TryGetValue(
            "build_property." + DatabaseDialects.ConfigurationPropertyName,
            out var value)
            ? value
            : null;
        if (DatabaseDialects.TryResolve(providerName, out var dialect, out var error))
        {
            return dialect;
        }

        report(RoslynDiagnostic.Create(
            GeneratorDiagnostics.InvalidConfiguration,
            Location.None,
            error ?? "The database provider configuration is invalid."));
        return null;
    }

    private static AnalysisCache GetAnalysisCache(
        Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider options,
        IDatabaseDialect dialect)
    {
        var enabled = !options.GlobalOptions.TryGetValue(
                "build_property.CobaltumOrmAnalysisCache",
                out var enabledValue) ||
            !string.Equals(enabledValue?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        var directory = options.GlobalOptions.TryGetValue(
            "build_property._CobaltumOrmAnalysisCacheDirectory",
            out var directoryValue)
            ? directoryValue
            : null;
        return new AnalysisCache(directory, dialect.Provider, enabled);
    }

    private static bool IsMigrationType(INamedTypeSymbol symbol, INamedTypeSymbol migrationBaseType)
    {
        for (var current = symbol; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, migrationBaseType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidNamespace(string value)
    {
        return value.Split('.').All(part =>
            SyntaxFacts.IsValidIdentifier(part) &&
            SyntaxFacts.GetKeywordKind(part) == SyntaxKind.None &&
            SyntaxFacts.GetContextualKeywordKind(part) == SyntaxKind.None);
    }

    private static Location SqlLocation(AdditionalSqlFile file, SourceSpan sourceSpan)
    {
        var text = SourceText.From(file.Text);
        var start = Math.Max(0, Math.Min(sourceSpan.Start, text.Length));
        var length = Math.Max(0, Math.Min(sourceSpan.Length, text.Length - start));
        var span = new TextSpan(start, length);
        return Location.Create(file.Path, span, text.Lines.GetLinePositionSpan(span));
    }

    private static Location MigrationSqlLocation(
        MigrationSource migration,
        MigrationStep step,
        SourceSpan sourceSpan)
    {
        return migration.FlywayFile is null
            ? step.Location
            : SqlLocation(migration.FlywayFile, sourceSpan);
    }

    private static string ReadableDescription(string name)
    {
        const string suffix = "Migration";
        var value = name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length
            ? name.Substring(0, name.Length - suffix.Length)
            : name;
        var characters = new List<char>();
        for (var index = 0; index < value.Length; index++)
        {
            if (index > 0 && char.IsUpper(value[index]) &&
                (char.IsLower(value[index - 1]) || char.IsDigit(value[index - 1])))
            {
                characters.Add(' ');
            }

            characters.Add(value[index]);
        }

        return new string(characters.ToArray());
    }

    private static string SafeHintName(string value)
    {
        return new string(value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
    }
}

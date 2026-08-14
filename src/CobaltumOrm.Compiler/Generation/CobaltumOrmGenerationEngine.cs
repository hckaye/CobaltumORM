using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using CobaltumOrm.Analysis;
using CobaltumOrm.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using AnalyzerConfigOptions = Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions;
using AnalyzerConfigOptionsProvider = Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace CobaltumOrm.Compiler;

/// <summary>
/// Runs SQL analysis, schema construction, raw query transformation, and code generation for a
/// single project. The MSBuild task and the command line tool both call this, so both paths see
/// the same diagnostics and produce the same files.
/// </summary>
public sealed class CobaltumOrmGenerationEngine
{
    private readonly List<GenerationDiagnostic> _diagnostics = new List<GenerationDiagnostic>();
    private bool _hasErrors;

    private CobaltumOrmGenerationEngine()
    {
    }

    /// <summary>Runs generation for one project.</summary>
    public static GenerationResult Run(GenerationRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return new CobaltumOrmGenerationEngine().Execute(request);
    }

    private GenerationResult Execute(GenerationRequest request)
    {
        var artifacts = new List<GeneratedArtifact>();
        var analyzedSources = Array.Empty<string>();
        var processedSources = new List<string>();
        GenerationResult Failed() =>
            new GenerationResult(
                false,
                _diagnostics,
                Array.Empty<GeneratedArtifact>(),
                analyzedSources,
                Array.Empty<string>());

        if (!DatabaseDialects.TryResolve(request.DatabaseProvider, out var dialect, out var providerError))
        {
            AddError("COB008", providerError ?? "The database provider configuration is invalid.", null);
            return Failed();
        }

        var analysisCache = new AnalysisCache(
            request.AnalysisCacheDirectory,
            dialect.Provider,
            request.AnalysisCacheEnabled);

        var generatedNamespace = string.IsNullOrWhiteSpace(request.GeneratedNamespace)
            ? "CobaltumOrm.Generated"
            : request.GeneratedNamespace!.Trim();

        var sourcePaths = request.SourcePaths
            .Select(NormalizePath)
            .Where(path => path.Length != 0 && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Where(path => !IsGeneratedSource(path))
            .ToArray();
        analyzedSources = sourcePaths;
        if (sourcePaths.Length == 0)
        {
            return new GenerationResult(true, _diagnostics, artifacts, analyzedSources, processedSources);
        }

        var parseOptions = CreateParseOptions(request);
        var trees = sourcePaths
            .Select(path => CSharpSyntaxTree.ParseText(
                SourceText.From(File.ReadAllText(path), Encoding.UTF8),
                parseOptions,
                path))
            .ToArray();
        var references = request.ReferencePaths
            .Select(NormalizePath)
            .Where(path => path.Length != 0 && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
        var nullable = string.Equals(request.Nullable, "enable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Nullable, "annotations", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Nullable, "warnings", StringComparison.OrdinalIgnoreCase)
            ? NullableContextOptions.Enable
            : NullableContextOptions.Disable;
        var compilation = CSharpCompilation.Create(
            "CobaltumOrm_Transform",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: nullable));
        var migrationSourcePaths = request.MigrationSourcePaths
            .Select(NormalizePath)
            .Where(path => path.Length != 0 && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var migrationTrees = migrationSourcePaths
            .Select(path => CSharpSyntaxTree.ParseText(
                SourceText.From(File.ReadAllText(path), Encoding.UTF8),
                parseOptions,
                path))
            .ToArray();
        var additionalSqlPaths = request.AdditionalFilePaths
            .Select(NormalizePath)
            .Where(path => string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var additionalSql = additionalSqlPaths
            .Select(path => new AdditionalSqlFile(path, File.ReadAllText(path)))
            .ToImmutableArray();

        var schemaCompilation = migrationTrees.Length == 0
            ? compilation
            : compilation.AddSyntaxTrees(migrationTrees);
        var schemaBuild = CobaltumOrmGenerator.BuildSchema(
            schemaCompilation,
            additionalSql,
            dialect,
            analysisCache,
            AddRoslynDiagnostic);
        if (schemaBuild.HasErrors)
        {
            return Failed();
        }

        var sqlSchemaPath = Path.Combine(request.OutputDirectory, "CobaltumOrm.SqlSchema.g.cs");
        var sqlSchemaText = GeneratedSourceWriter.WriteSqlSchema(
            generatedNamespace,
            schemaBuild.Schema,
            dialect);
        var sqlSchemaTree = CSharpSyntaxTree.ParseText(
            SourceText.From(sqlSchemaText, Encoding.UTF8),
            parseOptions,
            sqlSchemaPath);
        compilation = compilation.AddSyntaxTrees(sqlSchemaTree);

        var candidates = CollectCandidates(
            compilation,
            schemaBuild.Schema,
            sqlSchemaTree,
            dialect,
            analysisCache);
        if (_hasErrors)
        {
            return Failed();
        }

        var generatedClassName = AllocateGeneratedClassName(compilation);
        var typeEnvironment = new TypeEnvironment(compilation);
        foreach (var candidate in candidates)
        {
            candidate.TypeEnvironment = typeEnvironment;
            candidate.UseDatabaseTypeNames = dialect.Provider == DatabaseProvider.PostgreSql;
        }

        var transformedTrees = new List<SyntaxTree>();
        for (var index = 0; index < trees.Length; index++)
        {
            var tree = trees[index];
            var treeReplacements = candidates
                .Where(candidate => ReferenceEquals(candidate.Invocation.SyntaxTree, tree))
                .ToDictionary(
                    candidate => candidate.Invocation,
                    candidate => CreateReplacement(candidate, generatedClassName));
            if (treeReplacements.Count == 0)
            {
                continue;
            }

            var root = tree.GetRoot();
            var transformed = new QueryRewriter(treeReplacements).Visit(root) ?? root;
            var fileName = index.ToString("D4", CultureInfo.InvariantCulture) + "." +
                Path.GetFileNameWithoutExtension(tree.FilePath) + ".cobaltum.cs";
            var text = "#line 1 " + CSharpNames.Literal(tree.FilePath) + "\n" +
                transformed.ToFullString() + "\n#line default\n#line hidden\n";
            artifacts.Add(new GeneratedArtifact(
                fileName,
                text,
                GeneratedArtifactKind.Transformed,
                sourcePaths[index]));
            processedSources.Add(sourcePaths[index]);
            transformedTrees.Add(CSharpSyntaxTree.ParseText(
                SourceText.From(text, Encoding.UTF8),
                parseOptions,
                Path.Combine(request.OutputDirectory, fileName)));
        }

        var definitionsText = WriteDefinitions(candidates, compilation, generatedClassName);
        artifacts.Add(new GeneratedArtifact(
            "CobaltumOrm.RawQueries.g.cs",
            definitionsText,
            GeneratedArtifactKind.Generated,
            null));
        artifacts.Add(new GeneratedArtifact(
            "CobaltumOrm.SqlSchema.g.cs",
            sqlSchemaText,
            GeneratedArtifactKind.Generated,
            null));

        if (request.IncludeGeneratorOutput)
        {
            var processedSet = new HashSet<string>(processedSources, StringComparer.OrdinalIgnoreCase);
            var compileTrees = new List<SyntaxTree>();
            for (var index = 0; index < trees.Length; index++)
            {
                if (!processedSet.Contains(sourcePaths[index]))
                {
                    compileTrees.Add(trees[index]);
                }
            }

            compileTrees.AddRange(transformedTrees);
            compileTrees.Add(sqlSchemaTree);
            compileTrees.Add(CSharpSyntaxTree.ParseText(
                SourceText.From(definitionsText, Encoding.UTF8),
                parseOptions,
                Path.Combine(request.OutputDirectory, "CobaltumOrm.RawQueries.g.cs")));

            var compiledCompilation = CSharpCompilation.Create(
                "CobaltumOrm_Generate",
                compileTrees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: nullable));
            RunIncrementalGenerator(
                compiledCompilation,
                parseOptions,
                additionalSqlPaths.Concat(migrationSourcePaths).ToArray(),
                generatedNamespace,
                request.DatabaseProvider,
                request.AnalysisCacheDirectory,
                request.AnalysisCacheEnabled,
                artifacts);
            if (_hasErrors)
            {
                return Failed();
            }
        }

        return new GenerationResult(true, _diagnostics, artifacts, analyzedSources, processedSources);
    }

    private void RunIncrementalGenerator(
        CSharpCompilation compilation,
        CSharpParseOptions parseOptions,
        IReadOnlyList<string> additionalFilePaths,
        string generatedNamespace,
        string? databaseProvider,
        string? analysisCacheDirectory,
        bool analysisCacheEnabled,
        List<GeneratedArtifact> artifacts)
    {
        var additionalTexts = additionalFilePaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (AdditionalText)new FileAdditionalText(path, File.ReadAllText(path)))
            .ToImmutableArray();
        var options = new GenerationConfigOptionsProvider(
            generatedNamespace,
            databaseProvider,
            analysisCacheDirectory,
            analysisCacheEnabled);
        var driver = CSharpGeneratorDriver.Create(
            new[] { new CobaltumOrmGenerator().AsSourceGenerator() },
            additionalTexts,
            parseOptions,
            options);
        var runResult = driver.RunGenerators(compilation).GetRunResult();
        foreach (var diagnostic in runResult.Diagnostics)
        {
            AddRoslynDiagnostic(diagnostic);
        }

        foreach (var generatorResult in runResult.Results)
        {
            if (generatorResult.Exception != null)
            {
                AddError("COB010", generatorResult.Exception.Message, null);
                continue;
            }

            foreach (var source in generatorResult.GeneratedSources)
            {
                artifacts.Add(new GeneratedArtifact(
                    source.HintName,
                    source.SourceText.ToString(),
                    GeneratedArtifactKind.Generated,
                    null));
            }
        }
    }

    private static string NormalizePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private static CSharpParseOptions CreateParseOptions(GenerationRequest request)
    {
        var languageVersion = LanguageVersion.Latest;
        if (!string.IsNullOrWhiteSpace(request.LangVersion) &&
            LanguageVersionFacts.TryParse(request.LangVersion, out var configured))
        {
            languageVersion = configured;
        }

        var symbols = (request.DefineConstants ?? string.Empty)
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length != 0);
        return CSharpParseOptions.Default
            .WithLanguageVersion(languageVersion)
            .WithPreprocessorSymbols(symbols);
    }

    private List<QueryCandidate> CollectCandidates(
        CSharpCompilation compilation,
        DatabaseSchema schema,
        SyntaxTree sqlSchemaTree,
        IDatabaseDialect dialect,
        AnalysisCache analysisCache)
    {
        var pending = new List<PendingQuery>();
        foreach (var tree in compilation.SyntaxTrees.OrderBy(item => item.FilePath, StringComparer.Ordinal))
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var operation = semanticModel.GetOperation(invocation) as IInvocationOperation;
                var method = operation?.TargetMethod;
                var original = method?.ReducedFrom ?? method;
                if (original is null ||
                    original.ContainingType?.ToDisplayString() != "CobaltumOrm.CobaltumQueryExtensions" ||
                    original.Parameters.Length < 2 ||
                    original.Parameters[1].Type.SpecialType != SpecialType.System_String)
                {
                    continue;
                }

                var isCheckedQuery = original.Name == "Query";
                var invocationResultType = ResultTypeArgument(semanticModel, operation!, invocation);
                var isUncheckedTypedQuery = original.Name == "NoCheckQuery" &&
                    invocationResultType != null;
                if (!isCheckedQuery && !isUncheckedTypedQuery)
                {
                    continue;
                }

                var sqlArgument = operation!.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "sql");
                if (sqlArgument is null)
                {
                    continue;
                }

                var sqlExpression = (ExpressionSyntax)sqlArgument.Value.Syntax;
                if (isUncheckedTypedQuery)
                {
                    var uncheckedConnection = ConnectionExpression(invocation, operation);
                    if (uncheckedConnection == null)
                    {
                        AddSourceError("COB105", "The NoCheckQuery connection expression could not be resolved.", invocation.GetLocation());
                        continue;
                    }

                    var uncheckedResultType = invocationResultType!;
                    if (!ResultMappingFactory.TryCreateUnchecked(
                            compilation,
                            uncheckedResultType,
                            out var uncheckedMapping,
                            out var uncheckedMappingError))
                    {
                        AddSourceError(
                            "COB109",
                            "NoCheckQuery result cannot be mapped to the specified type: " + uncheckedMappingError,
                            invocation.GetLocation());
                        continue;
                    }

                    var uncheckedTransaction = operation.Arguments.FirstOrDefault(argument =>
                        argument.Parameter?.Name == "transaction" && !argument.IsImplicit)?.Value.Syntax as ExpressionSyntax;
                    pending.Add(new PendingQuery(
                        invocation,
                        uncheckedConnection,
                        uncheckedTransaction,
                        string.Empty,
                        null,
                        Array.Empty<InterpolationHole>(),
                        uncheckedResultType,
                        null,
                        sqlExpression,
                        uncheckedMapping));
                    continue;
                }

                var holes = new List<InterpolationHole>();
                string? sql;
                if (sqlExpression is InterpolatedStringExpressionSyntax interpolated)
                {
                    if (!TryBuildInterpolatedSql(
                            semanticModel,
                            sqlSchemaTree,
                            interpolated,
                            holes,
                            out sql))
                    {
                        continue;
                    }
                }
                else
                {
                    var constant = semanticModel.GetConstantValue(sqlExpression);
                    if (!constant.HasValue || !(constant.Value is string constantSql))
                    {
                        AddSourceError(
                            "COB100",
                            "Query requires a compile-time constant or interpolated SQL with value-only holes; use NoCheckQuery to bypass compile-time SQL validation.",
                            sqlExpression.GetLocation());
                        continue;
                    }

                    sql = constantSql;
                }

                var statements = dialect.ScriptClassifier.SplitAndClassify(sql!, out var scriptError);
                if (scriptError != null)
                {
                    AddSourceError("COB101", scriptError.Message, sqlExpression.GetLocation());
                    continue;
                }

                var meaningful = statements.Where(statement => statement.Kind != SqlStatementKind.Empty).ToArray();
                if (meaningful.Length == 0)
                {
                    AddSourceError("COB101", "Query SQL must contain a statement.", sqlExpression.GetLocation());
                    continue;
                }

                if (meaningful.Any(statement => statement.Kind == SqlStatementKind.Unsupported ||
                                                statement.Kind == SqlStatementKind.SupportedTableDdl))
                {
                    AddSourceError(
                        "COB101",
                        "Query may return rows or execute INSERT, UPDATE, DELETE, or TRUNCATE; schema changes must be declared as migrations.",
                        sqlExpression.GetLocation());
                    continue;
                }

                var validDataManipulation = true;
                foreach (var statement in meaningful.Where(statement =>
                             statement.Kind == SqlStatementKind.DataManipulation))
                {
                    var statementSql = TrimStatementTerminator(statement.Text);
                    var commandAnalysis = analysisCache.AnalyzeQuery(schema, statementSql, dialect.QueryAnalyzer);
                    foreach (var diagnostic in commandAnalysis.Diagnostics)
                    {
                        AddSourceError(diagnostic.Code, diagnostic.Message, sqlExpression.GetLocation());
                    }

                    validDataManipulation &= !commandAnalysis.HasErrors;
                }

                if (!validDataManipulation)
                {
                    continue;
                }

                var rowReturningStatements = meaningful
                    .Where(statement => statement.Kind == SqlStatementKind.Select)
                    .ToArray();
                if (rowReturningStatements.Length == 0)
                {
                    if (invocationResultType != null)
                    {
                        AddSourceError(
                            "COB109",
                            "Query<TResult> requires a statement that returns rows.",
                            invocation.GetLocation());
                        continue;
                    }

                    if (holes.Count != 0)
                    {
                        AddSourceError(
                            "COB102",
                            "Interpolated Query is supported for checked statements that return rows; use a literal DML command with WithParameter.",
                            sqlExpression.GetLocation());
                    }

                    continue;
                }

                if (rowReturningStatements.Length != 1 || meaningful.Length != 1)
                {
                    AddSourceError(
                        "COB101",
                        "A checked Query that returns rows must contain exactly one statement.",
                        sqlExpression.GetLocation());
                    continue;
                }

                var rowReturningSql = TrimStatementTerminator(rowReturningStatements[0].Text);
                var analysis = analysisCache.AnalyzeQuery(schema, rowReturningSql, dialect.QueryAnalyzer);
                if (analysis.HasErrors)
                {
                    foreach (var diagnostic in analysis.Diagnostics)
                    {
                        AddSourceError(diagnostic.Code, diagnostic.Message, sqlExpression.GetLocation());
                    }

                    continue;
                }

                var explicitResultType = invocationResultType;
                ResultMapping? resultMapping = null;
                if (explicitResultType != null &&
                    !ResultMappingFactory.TryCreate(
                        compilation,
                        explicitResultType,
                        analysis,
                        out resultMapping,
                        out var mappingError))
                {
                    AddSourceError(
                        "COB109",
                        "Query result cannot be mapped to the specified type: " + mappingError,
                        invocation.GetLocation());
                    continue;
                }

                var parameterMap = analysis.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    StringComparer.OrdinalIgnoreCase);
                var validHoles = true;
                foreach (var hole in holes)
                {
                    if (!parameterMap.TryGetValue(hole.ParameterName, out var parameter))
                    {
                        AddSourceError(
                            "COB103",
                            $"The SQL type of interpolation '{hole.Expression}' cannot be inferred from a value position.",
                            hole.Expression.GetLocation());
                        validHoles = false;
                        continue;
                    }

                    hole.ClrType = parameter.ClrType;
                    hole.DatabaseTypeName = parameter.DatabaseTypeName;
                    if (!HasImplicitConversion(compilation, semanticModel, hole.Expression, parameter.ClrType))
                    {
                        AddSourceError(
                            "COB104",
                            $"Interpolation has CLR type '{semanticModel.GetTypeInfo(hole.Expression).Type?.ToDisplayString() ?? "unknown"}', but SQL requires '{parameter.ClrType}'.",
                            hole.Expression.GetLocation());
                        validHoles = false;
                    }
                }

                if (!validHoles)
                {
                    continue;
                }

                if (!ValidateConstantWithParameters(
                        compilation,
                        semanticModel,
                        invocation,
                        analysis,
                        holes))
                {
                    continue;
                }

                var connection = ConnectionExpression(invocation, operation);
                if (connection is null)
                {
                    AddSourceError("COB105", "The Query connection expression could not be resolved.", invocation.GetLocation());
                    continue;
                }

                var transaction = operation.Arguments.FirstOrDefault(argument =>
                    argument.Parameter?.Name == "transaction" && !argument.IsImplicit)?.Value.Syntax as ExpressionSyntax;
                pending.Add(new PendingQuery(
                    invocation,
                    connection,
                    transaction,
                    rowReturningSql,
                    analysis,
                    holes,
                    explicitResultType,
                    resultMapping,
                    null,
                    null));
            }
        }

        return pending
            .OrderBy(query => query.Invocation.SyntaxTree.FilePath, StringComparer.Ordinal)
            .ThenBy(query => query.Invocation.SpanStart)
            .Select((query, index) => new QueryCandidate(query, index))
            .ToList();
    }

    private bool ValidateConstantWithParameters(
        CSharpCompilation compilation,
        SemanticModel semanticModel,
        InvocationExpressionSyntax queryInvocation,
        AnalysisResult analysis,
        IReadOnlyCollection<InterpolationHole> holes)
    {
        var parameters = analysis.Parameters.ToDictionary(
            parameter => parameter.Name,
            StringComparer.OrdinalIgnoreCase);
        var interpolationParameters = new HashSet<string>(
            holes.Select(hole => hole.ParameterName),
            StringComparer.OrdinalIgnoreCase);
        var boundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SyntaxNode current = queryInvocation;
        var valid = true;

        while (current.Parent is MemberAccessExpressionSyntax memberAccess &&
               ReferenceEquals(memberAccess.Expression, current) &&
               memberAccess.Parent is InvocationExpressionSyntax invocation)
        {
            var operation = semanticModel.GetOperation(invocation) as IInvocationOperation;
            if (memberAccess.Name.Identifier.ValueText != "WithParameter" ||
                operation?.TargetMethod.Name != "WithParameter" ||
                !IsRawQueryType(operation.TargetMethod.ContainingType))
            {
                break;
            }

            var nameArgument = operation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "name");
            var valueArgument = operation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "value");
            if (nameArgument?.Value.Syntax is ExpressionSyntax nameExpression &&
                valueArgument?.Value.Syntax is ExpressionSyntax valueExpression)
            {
                var constantName = semanticModel.GetConstantValue(nameExpression);
                if (constantName.HasValue && constantName.Value is string parameterName)
                {
                    if (interpolationParameters.Contains(parameterName) ||
                        !parameters.TryGetValue(parameterName, out var parameter))
                    {
                        AddSourceError(
                            "COB107",
                            $"Parameter '{parameterName}' is not a named parameter used by this checked query.",
                            nameExpression.GetLocation());
                        valid = false;
                    }
                    else if (!boundNames.Add(parameterName))
                    {
                        AddSourceError(
                            "COB107",
                            $"Parameter '{parameterName}' is bound more than once.",
                            nameExpression.GetLocation());
                        valid = false;
                    }
                    else if (!HasImplicitConversion(compilation, semanticModel, valueExpression, parameter.ClrType))
                    {
                        AddSourceError(
                            "COB108",
                            $"Parameter '{parameterName}' has CLR type '{semanticModel.GetTypeInfo(valueExpression).Type?.ToDisplayString() ?? "unknown"}', but SQL requires '{parameter.ClrType}'.",
                            valueExpression.GetLocation());
                        valid = false;
                    }
                }
            }

            current = invocation;
        }

        return valid;
    }

    private static bool IsRawQueryType(INamedTypeSymbol? type)
    {
        if (type == null || type.ContainingNamespace.ToDisplayString() != "CobaltumOrm")
        {
            return false;
        }

        var metadataName = type.OriginalDefinition.MetadataName;
        return metadataName == "CobaltumRawQuery" || metadataName == "MappedQuery`1";
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

        var reducedType = method.ReducedFrom?.TypeArguments.Length == 1
            ? method.ReducedFrom.TypeArguments[0]
            : null;
        if (reducedType != null)
        {
            return reducedType;
        }

        return null;
    }

    private bool TryBuildInterpolatedSql(
        SemanticModel semanticModel,
        SyntaxTree sqlSchemaTree,
        InterpolatedStringExpressionSyntax interpolated,
        ICollection<InterpolationHole> holes,
        out string? sql)
    {
        var builder = new StringBuilder();
        var staticText = string.Concat(interpolated.Contents
            .OfType<InterpolatedStringTextSyntax>()
            .Select(text => text.TextToken.ValueText));
        var index = 0;
        foreach (var content in interpolated.Contents)
        {
            if (content is InterpolatedStringTextSyntax text)
            {
                builder.Append(text.TextToken.ValueText);
                continue;
            }

            var interpolation = (InterpolationSyntax)content;
            if (interpolation.AlignmentClause != null || interpolation.FormatClause != null)
            {
                AddSourceError(
                    "COB106",
                    "SQL interpolation holes cannot use alignment or format clauses.",
                    interpolation.GetLocation());
                sql = null;
                return false;
            }

            var symbol = semanticModel.GetSymbolInfo(interpolation.Expression).Symbol as IFieldSymbol;
            var constant = semanticModel.GetConstantValue(interpolation.Expression);
            if (symbol?.IsConst == true &&
                symbol.Type.SpecialType == SpecialType.System_String &&
                symbol.Locations.Any(location => ReferenceEquals(location.SourceTree, sqlSchemaTree)) &&
                constant.HasValue &&
                constant.Value is string sqlIdentifier)
            {
                builder.Append(sqlIdentifier);
                continue;
            }

            string parameterName;
            do
            {
                parameterName = "@__cobaltum_value_" + index.ToString(CultureInfo.InvariantCulture);
                index++;
            }
            while (staticText.IndexOf(parameterName, StringComparison.OrdinalIgnoreCase) >= 0);

            builder.Append(parameterName);
            holes.Add(new InterpolationHole(parameterName, interpolation.Expression));
        }

        sql = builder.ToString();
        return true;
    }

    private static ExpressionSyntax? ConnectionExpression(
        InvocationExpressionSyntax invocation,
        IInvocationOperation operation)
    {
        var explicitArgument = operation.Arguments.FirstOrDefault(argument =>
            argument.Parameter?.Name == "connection" && !argument.IsImplicit)?.Value.Syntax as ExpressionSyntax;
        if (explicitArgument != null)
        {
            return explicitArgument;
        }

        return invocation.Expression is MemberAccessExpressionSyntax memberAccess
            ? memberAccess.Expression
            : null;
    }

    private static bool HasImplicitConversion(
        CSharpCompilation compilation,
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        string analyzerType)
    {
        var target = ResolveType(compilation, analyzerType);
        if (target is null)
        {
            return false;
        }

        var conversion = semanticModel.ClassifyConversion(expression, target);
        return conversion.Exists && conversion.IsImplicit;
    }

    private static ITypeSymbol? ResolveType(CSharpCompilation compilation, string analyzerType)
    {
        var nullable = analyzerType.EndsWith("?", StringComparison.Ordinal);
        var baseName = nullable ? analyzerType.Substring(0, analyzerType.Length - 1) : analyzerType;
        var arrayRank = 0;
        while (baseName.EndsWith("[]", StringComparison.Ordinal))
        {
            arrayRank++;
            baseName = baseName.Substring(0, baseName.Length - 2);
        }

        var metadataName = baseName switch
        {
            "bool" => "System.Boolean",
            "short" => "System.Int16",
            "int" => "System.Int32",
            "long" => "System.Int64",
            "float" => "System.Single",
            "double" => "System.Double",
            "decimal" => "System.Decimal",
            "string" => "System.String",
            "Guid" => "System.Guid",
            "DateOnly" => compilation.GetTypeByMetadataName("System.DateOnly") != null ? "System.DateOnly" : "System.DateTime",
            "TimeOnly" => compilation.GetTypeByMetadataName("System.TimeOnly") != null ? "System.TimeOnly" : "System.TimeSpan",
            "DateTime" => "System.DateTime",
            "DateTimeOffset" => "System.DateTimeOffset",
            "TimeSpan" => "System.TimeSpan",
            "byte" => "System.Byte",
            _ => "System.Object",
        };
        ITypeSymbol? type = compilation.GetTypeByMetadataName(metadataName);
        if (type is null)
        {
            return null;
        }

        while (arrayRank-- > 0)
        {
            type = compilation.CreateArrayTypeSymbol(type);
        }

        if (type is IArrayTypeSymbol)
        {
            return type.WithNullableAnnotation(NullableAnnotation.Annotated);
        }

        if ((nullable || type.IsValueType) && type.IsValueType)
        {
            var nullableType = compilation.GetTypeByMetadataName("System.Nullable`1");
            return nullableType?.Construct(type);
        }

        return type.WithNullableAnnotation(NullableAnnotation.Annotated);
    }

    private static string TrimStatementTerminator(string sql)
    {
        var result = sql.Trim();
        return result.EndsWith(";", StringComparison.Ordinal)
            ? result.Substring(0, result.Length - 1)
            : result;
    }

    private static string AllocateGeneratedClassName(Compilation compilation)
    {
        var baseName = "__CobaltumOrmRawQueries";
        var candidate = baseName;
        var suffix = 2;
        while (compilation.GetTypeByMetadataName(candidate) != null)
        {
            candidate = baseName + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    private static ExpressionSyntax CreateReplacement(QueryCandidate candidate, string generatedClassName)
    {
        if (candidate.UncheckedSqlExpression != null)
        {
            return SyntaxFactory.ParseExpression(
                    "global::CobaltumOrm.CobaltumQueryExtensions.NoCheckQueryMapped(" +
                    candidate.Connection.WithoutTrivia().ToFullString() + ", " +
                    candidate.UncheckedSqlExpression.WithoutTrivia().ToFullString() + ", " +
                    "global::" + generatedClassName + ".Materialize" +
                    candidate.Index.ToString("D4", CultureInfo.InvariantCulture) + ", " +
                    (candidate.Transaction?.WithoutTrivia().ToFullString() ?? "null") + ")")
                .WithTriviaFrom(candidate.Invocation);
        }

        var arguments = new List<string>
        {
            candidate.Connection.WithoutTrivia().ToFullString(),
            "global::" + generatedClassName + ".CreateQuery" + candidate.Index.ToString("D4", CultureInfo.InvariantCulture) +
                "(" + string.Join(", ", candidate.Holes.Select(hole => hole.Expression.WithoutTrivia().ToFullString())) + ")",
            candidate.Transaction?.WithoutTrivia().ToFullString() ?? "null",
        };
        var holeNames = new HashSet<string>(candidate.Holes.Select(hole => hole.ParameterName), StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in candidate.Analysis!.Parameters.Where(parameter => !holeNames.Contains(parameter.Name)))
        {
            var environment = candidate.TypeEnvironment;
            var databaseTypeName = candidate.UseDatabaseTypeNames ? parameter.DatabaseTypeName : null;
            var argument =
                "new global::CobaltumOrm.CobaltumExpectedParameter(" + CSharpNames.Literal(parameter.Name) +
                ", global::System.Data.DbType." + environment.DbTypeName(parameter.ClrType);
            if (databaseTypeName != null)
            {
                argument += ", " + CSharpNames.Literal(databaseTypeName);
                argument +=
                    ", static parameter => ((global::Npgsql.NpgsqlParameter)parameter).DataTypeName = " +
                    CSharpNames.Literal(databaseTypeName);
            }

            arguments.Add(argument + ")");
        }

        return SyntaxFactory.ParseExpression(
                "global::CobaltumOrm.CobaltumQueryExtensions.QueryChecked(" + string.Join(", ", arguments) + ")")
            .WithTriviaFrom(candidate.Invocation);
    }

    private static string WriteDefinitions(
        IReadOnlyList<QueryCandidate> candidates,
        Compilation compilation,
        string generatedClassName)
    {
        var environment = new TypeEnvironment(compilation);

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        if (candidates.Count != 0 &&
            compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.IsExternalInit") is null)
        {
            builder.AppendLine("namespace System.Runtime.CompilerServices");
            builder.AppendLine("{");
            builder.AppendLine("    internal static class IsExternalInit { }");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        builder.Append("internal static class ").Append(generatedClassName).AppendLine();
        builder.AppendLine("{");
        foreach (var candidate in candidates)
        {
            var suffix = candidate.Index.ToString("D4", CultureInfo.InvariantCulture);
            if (candidate.UncheckedResultMapping != null)
            {
                var uncheckedMapping = candidate.UncheckedResultMapping;
                builder.Append("    internal static ")
                    .Append(ResultMappingFactory.Display(uncheckedMapping.ResultType))
                    .Append(" Materialize").Append(suffix)
                    .AppendLine("(global::System.Data.Common.DbDataReader reader)");
                builder.AppendLine("    {");
                builder.Append("        return ")
                    .Append(ResultMappingFactory.MaterializeUncheckedExpression(
                        uncheckedMapping,
                        "NoCheckQuery<" + uncheckedMapping.ResultType.ToDisplayString() + ">"))
                    .AppendLine(";");
                builder.AppendLine("    }");
                builder.AppendLine();
                continue;
            }

            var analysis = candidate.Analysis!;
            var resultName = candidate.ExplicitResultType == null
                ? "Query" + suffix + "Result"
                : ResultMappingFactory.Display(candidate.ExplicitResultType);
            var propertyNames = CSharpNames.Allocate(
                analysis.Columns,
                column => CSharpNames.Pascal(column.Name, "Column"));
            if (candidate.ExplicitResultType == null)
            {
                builder.Append("    internal sealed record ").Append(resultName).AppendLine("(");
                for (var index = 0; index < analysis.Columns.Count; index++)
                {
                    var column = analysis.Columns[index];
                    builder.Append("        ").Append(environment.TypeName(column.ClrType)).Append(' ').Append(propertyNames[column]);
                    builder.AppendLine(index == analysis.Columns.Count - 1 ? ");" : ",");
                }

                builder.AppendLine();
            }
            builder.Append("    internal static global::CobaltumOrm.CobaltumQueryDefinition<")
                .Append(resultName).Append("> CreateQuery").Append(suffix).Append('(');
            for (var index = 0; index < candidate.Holes.Count; index++)
            {
                if (index != 0) builder.Append(", ");
                builder.Append(environment.ParameterTypeName(candidate.Holes[index].ClrType!))
                    .Append(" value").Append(index.ToString(CultureInfo.InvariantCulture));
            }

            builder.AppendLine(")");
            builder.AppendLine("    {");
            builder.Append("        return new global::CobaltumOrm.CobaltumQueryDefinition<")
                .Append(resultName).AppendLine(">(");
            builder.Append("            ").Append(CSharpNames.Literal(candidate.Sql)).AppendLine(",");
            builder.AppendLine(candidate.Holes.Count == 0 ? "            static command =>" : "            command =>");
            builder.AppendLine("            {");
            for (var index = 0; index < candidate.Holes.Count; index++)
            {
                var hole = candidate.Holes[index];
                builder.Append("                global::CobaltumOrm.CobaltumParameter.")
                    .Append(candidate.UseDatabaseTypeNames && hole.DatabaseTypeName != null
                        ? "AddConfigured"
                        : "Add")
                    .Append("(command, ")
                    .Append(CSharpNames.Literal(hole.ParameterName)).Append(", value")
                    .Append(index.ToString(CultureInfo.InvariantCulture)).Append(", global::System.Data.DbType.")
                    .Append(environment.DbTypeName(hole.ClrType!));
                if (candidate.UseDatabaseTypeNames && hole.DatabaseTypeName != null)
                {
                    builder.Append(", static parameter => ((global::Npgsql.NpgsqlParameter)parameter).DataTypeName = ")
                        .Append(CSharpNames.Literal(hole.DatabaseTypeName));
                }

                builder.AppendLine(");");
            }

            builder.AppendLine("            },");
            builder.AppendLine("            static reader =>");
            builder.AppendLine("            {");
            if (candidate.ResultMapping == null)
            {
                builder.Append("                return new ").Append(resultName).AppendLine("(");
                for (var index = 0; index < analysis.Columns.Count; index++)
                {
                    var column = analysis.Columns[index];
                    builder.Append("                    ").Append(environment.ReadExpression(
                        column.ClrType,
                        index,
                        "raw query." + column.Name));
                    builder.AppendLine(index == analysis.Columns.Count - 1 ? ");" : ",");
                }
            }
            else
            {
                builder.Append("                return ")
                    .Append(ResultMappingFactory.MaterializeExpression(
                        candidate.ResultMapping,
                        environment,
                        "raw query"))
                    .AppendLine(";");
            }

            builder.AppendLine("            });");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private void AddRoslynDiagnostic(RoslynDiagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        var isError = diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error;
        Add(new GenerationDiagnostic(
            diagnostic.Id,
            diagnostic.GetMessage(CultureInfo.CurrentCulture),
            string.IsNullOrEmpty(lineSpan.Path) ? null : lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            lineSpan.EndLinePosition.Line + 1,
            lineSpan.EndLinePosition.Character + 1,
            isError));
    }

    private void AddSourceError(string code, string message, Location location)
    {
        var lineSpan = location.GetLineSpan();
        Add(new GenerationDiagnostic(
            code,
            message,
            string.IsNullOrEmpty(lineSpan.Path) ? null : lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            lineSpan.EndLinePosition.Line + 1,
            lineSpan.EndLinePosition.Character + 1,
            true));
    }

    private void AddError(string code, string message, string? filePath)
    {
        Add(new GenerationDiagnostic(code, message, filePath, 0, 0, 0, 0, true));
    }

    private void Add(GenerationDiagnostic diagnostic)
    {
        _diagnostics.Add(diagnostic);
        _hasErrors |= diagnostic.IsError;
    }

    private static bool IsGeneratedSource(string path)
    {
        using (var reader = new StreamReader(path, Encoding.UTF8, true, 1024))
        {
            for (var line = 0; line < 4 && !reader.EndOfStream; line++)
            {
                var lineText = (reader.ReadLine() ?? string.Empty)
                    .Replace("-", string.Empty)
                    .Replace(" ", string.Empty)
                    .Replace("\t", string.Empty);
                if (lineText.IndexOf("<autogenerated", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed class FileAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        internal FileAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text, Encoding.UTF8);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    private sealed class GenerationConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        internal GenerationConfigOptionsProvider(
            string generatedNamespace,
            string? databaseProvider,
            string? analysisCacheDirectory,
            bool analysisCacheEnabled)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["build_property.CobaltumOrmGeneratedNamespace"] = generatedNamespace,
                ["build_property.CobaltumOrmAnalysisCache"] = analysisCacheEnabled ? "true" : "false",
            };
            if (!string.IsNullOrWhiteSpace(databaseProvider))
            {
                values["build_property." + DatabaseDialects.ConfigurationPropertyName] = databaseProvider!.Trim();
            }

            if (!string.IsNullOrWhiteSpace(analysisCacheDirectory))
            {
                values["build_property._CobaltumOrmAnalysisCacheDirectory"] = analysisCacheDirectory!;
            }

            GlobalOptions = new GenerationConfigOptions(values);
        }

        public override AnalyzerConfigOptions GlobalOptions { get; }

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GenerationConfigOptions.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GenerationConfigOptions.Empty;
    }

    private sealed class GenerationConfigOptions : AnalyzerConfigOptions
    {
        internal static readonly GenerationConfigOptions Empty =
            new GenerationConfigOptions(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        private readonly Dictionary<string, string> _values;

        internal GenerationConfigOptions(Dictionary<string, string> values)
        {
            _values = values;
        }

        public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
    }

    private sealed class QueryRewriter : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyDictionary<InvocationExpressionSyntax, ExpressionSyntax> _replacements;

        internal QueryRewriter(IReadOnlyDictionary<InvocationExpressionSyntax, ExpressionSyntax> replacements)
        {
            _replacements = replacements;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) =>
            _replacements.TryGetValue(node, out var replacement)
                ? replacement
                : base.VisitInvocationExpression(node);
    }

    private sealed class InterpolationHole
    {
        internal InterpolationHole(string parameterName, ExpressionSyntax expression)
        {
            ParameterName = parameterName;
            Expression = expression;
        }

        internal string ParameterName { get; }
        internal ExpressionSyntax Expression { get; }
        internal string? ClrType { get; set; }
        internal string? DatabaseTypeName { get; set; }
    }

    private class PendingQuery
    {
        internal PendingQuery(
            InvocationExpressionSyntax invocation,
            ExpressionSyntax connection,
            ExpressionSyntax? transaction,
            string sql,
            AnalysisResult? analysis,
            IReadOnlyList<InterpolationHole> holes,
            ITypeSymbol? explicitResultType,
            ResultMapping? resultMapping,
            ExpressionSyntax? uncheckedSqlExpression,
            UncheckedResultMapping? uncheckedResultMapping)
        {
            Invocation = invocation;
            Connection = connection;
            Transaction = transaction;
            Sql = sql;
            Analysis = analysis;
            Holes = holes;
            ExplicitResultType = explicitResultType;
            ResultMapping = resultMapping;
            UncheckedSqlExpression = uncheckedSqlExpression;
            UncheckedResultMapping = uncheckedResultMapping;
        }

        internal InvocationExpressionSyntax Invocation { get; }
        internal ExpressionSyntax Connection { get; }
        internal ExpressionSyntax? Transaction { get; }
        internal string Sql { get; }
        internal AnalysisResult? Analysis { get; }
        internal IReadOnlyList<InterpolationHole> Holes { get; }
        internal ITypeSymbol? ExplicitResultType { get; }
        internal ResultMapping? ResultMapping { get; }
        internal ExpressionSyntax? UncheckedSqlExpression { get; }
        internal UncheckedResultMapping? UncheckedResultMapping { get; }
    }

    private sealed class QueryCandidate : PendingQuery
    {
        internal QueryCandidate(PendingQuery query, int index)
            : base(
                query.Invocation,
                query.Connection,
                query.Transaction,
                query.Sql,
                query.Analysis,
                query.Holes,
                query.ExplicitResultType,
                query.ResultMapping,
                query.UncheckedSqlExpression,
                query.UncheckedResultMapping)
        {
            Index = index;
        }

        internal int Index { get; }
        internal TypeEnvironment TypeEnvironment { get; set; } = null!;
        internal bool UseDatabaseTypeNames { get; set; }
    }
}

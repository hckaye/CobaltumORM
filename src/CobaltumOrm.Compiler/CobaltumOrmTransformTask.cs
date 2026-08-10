using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CobaltumOrm.Analysis;
using CobaltumOrm.SourceGenerator;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using Task = Microsoft.Build.Utilities.Task;

namespace CobaltumOrm.Compiler;

public sealed class CobaltumOrmTransformTask : Task
{
    [Required]
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] References { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] AdditionalFiles { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] MigrationSources { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    public string? DefineConstants { get; set; }

    public string? LangVersion { get; set; }

    public string? Nullable { get; set; }

    public string? GeneratedNamespace { get; set; }

    public string? CobaltumOrmDatabaseProvider { get; set; }

    [Output]
    public ITaskItem[] ProcessedSources { get; private set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] TransformedSources { get; private set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        try
        {
            return Transform();
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: true);
            return false;
        }
    }

    private bool Transform()
    {
        if (!DatabaseDialects.TryResolve(CobaltumOrmDatabaseProvider, out var dialect, out var providerError))
        {
            LogConfigurationError(providerError ?? "The database provider configuration is invalid.");
            return false;
        }

        var sourceItems = Sources
            .Select(item => new SourceItem(item, ItemFullPath(item)))
            .Where(item => File.Exists(item.FullPath))
            .GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.FullPath, StringComparer.Ordinal)
            .Where(item => !IsGeneratedSource(item.FullPath))
            .ToArray();
        if (sourceItems.Length == 0)
        {
            return true;
        }

        Directory.CreateDirectory(OutputDirectory);
        var parseOptions = CreateParseOptions();
        var trees = sourceItems
            .Select(item => CSharpSyntaxTree.ParseText(
                SourceText.From(File.ReadAllText(item.FullPath), Encoding.UTF8),
                parseOptions,
                item.FullPath))
            .ToArray();
        var references = References
            .Select(ItemFullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
        var nullable = string.Equals(Nullable, "enable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Nullable, "annotations", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Nullable, "warnings", StringComparison.OrdinalIgnoreCase)
            ? NullableContextOptions.Enable
            : NullableContextOptions.Disable;
        var compilation = CSharpCompilation.Create(
            "CobaltumOrm_Transform",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: nullable));
        var migrationTrees = MigrationSources
            .Select(ItemFullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => CSharpSyntaxTree.ParseText(
                SourceText.From(File.ReadAllText(path), Encoding.UTF8),
                parseOptions,
                path))
            .ToArray();
        var additionalSql = AdditionalFiles
            .Select(ItemFullPath)
            .Where(path => string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new AdditionalSqlFile(path, File.ReadAllText(path)))
            .ToImmutableArray();

        var schemaDiagnostics = new List<RoslynDiagnostic>();
        var schemaCompilation = migrationTrees.Length == 0
            ? compilation
            : compilation.AddSyntaxTrees(migrationTrees);
        var schemaBuild = CobaltumOrmGenerator.BuildSchema(schemaCompilation, additionalSql, dialect, schemaDiagnostics.Add);
        foreach (var diagnostic in schemaDiagnostics)
        {
            LogDiagnostic(diagnostic);
        }

        if (schemaBuild.HasErrors)
        {
            return false;
        }

        var generatedNamespace = string.IsNullOrWhiteSpace(GeneratedNamespace)
            ? "CobaltumOrm.Generated"
            : GeneratedNamespace!.Trim();
        var sqlSchemaPath = Path.Combine(OutputDirectory, "CobaltumOrm.SqlSchema.g.cs");
        var sqlSchemaText = GeneratedSourceWriter.WriteSqlSchema(
            generatedNamespace,
            schemaBuild.Schema,
            dialect);
        var sqlSchemaTree = CSharpSyntaxTree.ParseText(
            SourceText.From(sqlSchemaText, Encoding.UTF8),
            parseOptions,
            sqlSchemaPath);
        compilation = compilation.AddSyntaxTrees(sqlSchemaTree);

        var candidates = CollectCandidates(compilation, schemaBuild.Schema, sqlSchemaTree, dialect);
        if (Log.HasLoggedErrors)
        {
            return false;
        }

        var generatedClassName = AllocateGeneratedClassName(compilation);
        var typeEnvironment = new TypeEnvironment(compilation);
        foreach (var candidate in candidates)
        {
            candidate.TypeEnvironment = typeEnvironment;
            candidate.UseDatabaseTypeNames = dialect.Provider == DatabaseProvider.PostgreSql;
        }

        var outputItems = new List<ITaskItem>();
        var processedItems = new List<ITaskItem>();
        var activeTransformedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            var outputPath = Path.Combine(
                OutputDirectory,
                index.ToString("D4", CultureInfo.InvariantCulture) + "." + Path.GetFileNameWithoutExtension(tree.FilePath) + ".cobaltum.cs");
            var text = "#line 1 " + CSharpNames.Literal(tree.FilePath) + "\n" +
                transformed.ToFullString() + "\n#line default\n#line hidden\n";
            WriteIfChanged(outputPath, text);
            activeTransformedPaths.Add(Path.GetFullPath(outputPath));
            processedItems.Add(new TaskItem(sourceItems[index].Original.ItemSpec));
            outputItems.Add(CreateTransformedItem(outputPath));
        }

        var definitionsPath = Path.Combine(OutputDirectory, "CobaltumOrm.RawQueries.g.cs");
        WriteIfChanged(definitionsPath, WriteDefinitions(candidates, compilation, generatedClassName));
        WriteIfChanged(sqlSchemaPath, sqlSchemaText);
        RemoveStaleOutputs(activeTransformedPaths);
        outputItems.Add(CreateGeneratedItem(definitionsPath));
        outputItems.Add(CreateGeneratedItem(sqlSchemaPath));
        ProcessedSources = processedItems.ToArray();
        TransformedSources = outputItems.ToArray();
        return true;
    }

    private CSharpParseOptions CreateParseOptions()
    {
        var languageVersion = LanguageVersion.Latest;
        if (!string.IsNullOrWhiteSpace(LangVersion) &&
            LanguageVersionFacts.TryParse(LangVersion, out var configured))
        {
            languageVersion = configured;
        }

        var symbols = (DefineConstants ?? string.Empty)
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
        IDatabaseDialect dialect)
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
                if (original is null || original.Name != "Query" ||
                    original.ContainingType?.ToDisplayString() != "CobaltumOrm.CobaltumQueryExtensions" ||
                    original.Parameters.Length < 2 ||
                    original.Parameters[1].Type.SpecialType != SpecialType.System_String)
                {
                    continue;
                }

                var sqlArgument = operation!.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "sql");
                if (sqlArgument is null)
                {
                    continue;
                }

                var sqlExpression = (ExpressionSyntax)sqlArgument.Value.Syntax;
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
                        LogSourceError(
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
                    LogSourceError("COB101", scriptError.Message, sqlExpression.GetLocation());
                    continue;
                }

                var meaningful = statements.Where(statement => statement.Kind != SqlStatementKind.Empty).ToArray();
                if (meaningful.Length == 0)
                {
                    LogSourceError("COB101", "Query SQL must contain a statement.", sqlExpression.GetLocation());
                    continue;
                }

                if (meaningful.Any(statement => statement.Kind == SqlStatementKind.Unsupported ||
                                                statement.Kind == SqlStatementKind.SupportedTableDdl))
                {
                    LogSourceError(
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
                    var commandAnalysis = dialect.QueryAnalyzer.Analyze(schema, statementSql);
                    foreach (var diagnostic in commandAnalysis.Diagnostics)
                    {
                        LogSourceError(diagnostic.Code, diagnostic.Message, sqlExpression.GetLocation());
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
                    if (holes.Count != 0)
                    {
                        LogSourceError(
                            "COB102",
                            "Interpolated Query is supported for checked statements that return rows; use a literal DML command with WithParameter.",
                            sqlExpression.GetLocation());
                    }

                    continue;
                }

                if (rowReturningStatements.Length != 1 || meaningful.Length != 1)
                {
                    LogSourceError(
                        "COB101",
                        "A checked Query that returns rows must contain exactly one statement.",
                        sqlExpression.GetLocation());
                    continue;
                }

                var rowReturningSql = TrimStatementTerminator(rowReturningStatements[0].Text);
                var analysis = dialect.QueryAnalyzer.Analyze(schema, rowReturningSql);
                if (analysis.HasErrors)
                {
                    foreach (var diagnostic in analysis.Diagnostics)
                    {
                        LogSourceError(diagnostic.Code, diagnostic.Message, sqlExpression.GetLocation());
                    }

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
                        LogSourceError(
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
                        LogSourceError(
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
                    LogSourceError("COB105", "The Query connection expression could not be resolved.", invocation.GetLocation());
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
                    holes));
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
                operation.TargetMethod.ContainingType?.ToDisplayString() != "CobaltumOrm.CobaltumRawQuery")
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
                        LogSourceError(
                            "COB107",
                            $"Parameter '{parameterName}' is not a named parameter used by this checked query.",
                            nameExpression.GetLocation());
                        valid = false;
                    }
                    else if (!boundNames.Add(parameterName))
                    {
                        LogSourceError(
                            "COB107",
                            $"Parameter '{parameterName}' is bound more than once.",
                            nameExpression.GetLocation());
                        valid = false;
                    }
                    else if (!HasImplicitConversion(compilation, semanticModel, valueExpression, parameter.ClrType))
                    {
                        LogSourceError(
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
                LogSourceError(
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
            "byte[]" => "System.Byte",
            _ => "System.Object",
        };
        var type = compilation.GetTypeByMetadataName(metadataName);
        if (type is null)
        {
            return null;
        }

        if (baseName == "byte[]")
        {
            return compilation.CreateArrayTypeSymbol(type);
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
        var arguments = new List<string>
        {
            candidate.Connection.WithoutTrivia().ToFullString(),
            "global::" + generatedClassName + ".CreateQuery" + candidate.Index.ToString("D4", CultureInfo.InvariantCulture) +
                "(" + string.Join(", ", candidate.Holes.Select(hole => hole.Expression.WithoutTrivia().ToFullString())) + ")",
            candidate.Transaction?.WithoutTrivia().ToFullString() ?? "null",
        };
        var holeNames = new HashSet<string>(candidate.Holes.Select(hole => hole.ParameterName), StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in candidate.Analysis.Parameters.Where(parameter => !holeNames.Contains(parameter.Name)))
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
            var resultName = "Query" + suffix + "Result";
            var propertyNames = CSharpNames.Allocate(
                candidate.Analysis.Columns,
                column => CSharpNames.Pascal(column.Name, "Column"));
            builder.Append("    internal sealed record ").Append(resultName).AppendLine("(");
            for (var index = 0; index < candidate.Analysis.Columns.Count; index++)
            {
                var column = candidate.Analysis.Columns[index];
                builder.Append("        ").Append(environment.TypeName(column.ClrType)).Append(' ').Append(propertyNames[column]);
                builder.AppendLine(index == candidate.Analysis.Columns.Count - 1 ? ");" : ",");
            }

            builder.AppendLine();
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
            builder.Append("                return new ").Append(resultName).AppendLine("(");
            for (var index = 0; index < candidate.Analysis.Columns.Count; index++)
            {
                var column = candidate.Analysis.Columns[index];
                builder.Append("                    ").Append(environment.ReadExpression(
                    column.ClrType,
                    index,
                    "raw query." + column.Name));
                builder.AppendLine(index == candidate.Analysis.Columns.Count - 1 ? ");" : ",");
            }

            builder.AppendLine("            });");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private void LogDiagnostic(RoslynDiagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        Log.LogError(
            "CobaltumOrm",
            diagnostic.Id,
            null,
            lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            lineSpan.EndLinePosition.Line + 1,
            lineSpan.EndLinePosition.Character + 1,
            diagnostic.GetMessage(CultureInfo.CurrentCulture));
    }

    private void LogConfigurationError(string message)
    {
        Log.LogError(
            "CobaltumOrm",
            "COB008",
            null,
            null,
            0,
            0,
            0,
            0,
            message);
    }

    private void LogSourceError(string code, string message, Location location)
    {
        var lineSpan = location.GetLineSpan();
        Log.LogError(
            "CobaltumOrm",
            code,
            null,
            lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            lineSpan.EndLinePosition.Line + 1,
            lineSpan.EndLinePosition.Character + 1,
            message);
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

    private static string ItemFullPath(ITaskItem item)
    {
        var fullPath = item.GetMetadata("FullPath");
        return string.IsNullOrWhiteSpace(fullPath)
            ? Path.GetFullPath(item.ItemSpec)
            : Path.GetFullPath(fullPath);
    }

    private static ITaskItem CreateGeneratedItem(string path)
    {
        var item = new TaskItem(path);
        item.SetMetadata("AutoGen", "true");
        item.SetMetadata("DesignTime", "true");
        item.SetMetadata("Visible", "false");
        return item;
    }

    private static ITaskItem CreateTransformedItem(string path)
    {
        var item = new TaskItem(path);
        item.SetMetadata("CobaltumOrmTransformed", "true");
        item.SetMetadata("Visible", "false");
        return item;
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private void RemoveStaleOutputs(ISet<string> activeTransformedPaths)
    {
        foreach (var path in Directory.EnumerateFiles(OutputDirectory, "*.cobaltum.cs", SearchOption.TopDirectoryOnly))
        {
            if (IsNumberedTransformOutput(path) && !activeTransformedPaths.Contains(Path.GetFullPath(path)))
            {
                File.Delete(path);
            }
        }

        foreach (var path in Directory.EnumerateFiles(OutputDirectory, "*.g.cs", SearchOption.TopDirectoryOnly))
        {
            if (IsNumberedTransformOutput(path))
            {
                File.Delete(path);
            }
        }
    }

    private static bool IsNumberedTransformOutput(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 5 &&
               name[4] == '.' &&
               name.Take(4).All(character => character >= '0' && character <= '9');
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

    private sealed class SourceItem
    {
        internal SourceItem(ITaskItem original, string fullPath)
        {
            Original = original;
            FullPath = fullPath;
        }

        internal ITaskItem Original { get; }
        internal string FullPath { get; }
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
            AnalysisResult analysis,
            IReadOnlyList<InterpolationHole> holes)
        {
            Invocation = invocation;
            Connection = connection;
            Transaction = transaction;
            Sql = sql;
            Analysis = analysis;
            Holes = holes;
        }

        internal InvocationExpressionSyntax Invocation { get; }
        internal ExpressionSyntax Connection { get; }
        internal ExpressionSyntax? Transaction { get; }
        internal string Sql { get; }
        internal AnalysisResult Analysis { get; }
        internal IReadOnlyList<InterpolationHole> Holes { get; }
    }

    private sealed class QueryCandidate : PendingQuery
    {
        internal QueryCandidate(PendingQuery query, int index)
            : base(query.Invocation, query.Connection, query.Transaction, query.Sql, query.Analysis, query.Holes)
        {
            Index = index;
        }

        internal int Index { get; }
        internal TypeEnvironment TypeEnvironment { get; set; } = null!;
        internal bool UseDatabaseTypeNames { get; set; }
    }
}

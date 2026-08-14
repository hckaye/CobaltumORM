using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CobaltumOrm.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CobaltumOrm.SourceGenerator;

internal enum ResultMappingKind
{
    Scalar,
    Constructor,
    Members,
    Handler,
}

internal enum ValueHandlerMappingKind
{
    Reader,
    Converter,
    ArrayElements,
}

internal sealed class ValueHandlerMapping
{
    internal ValueHandlerMapping(
        ITypeSymbol handlerType,
        ValueHandlerMappingKind kind,
        ITypeSymbol? sourceType = null,
        ITypeSymbol? valueType = null)
    {
        HandlerType = handlerType;
        Kind = kind;
        SourceType = sourceType;
        ValueType = valueType;
    }

    internal ITypeSymbol HandlerType { get; }
    internal ValueHandlerMappingKind Kind { get; }
    internal ITypeSymbol? SourceType { get; }
    internal ITypeSymbol? ValueType { get; }
}

internal sealed class ResultMapping
{
    internal ResultMapping(
        ITypeSymbol resultType,
        ResultMappingKind kind,
        IReadOnlyList<ResultMappingTarget> targets,
        ITypeSymbol? resultHandlerType = null)
    {
        ResultType = resultType;
        Kind = kind;
        Targets = targets;
        ResultHandlerType = resultHandlerType;
    }

    internal ITypeSymbol ResultType { get; }
    internal ResultMappingKind Kind { get; }
    internal IReadOnlyList<ResultMappingTarget> Targets { get; }
    internal ITypeSymbol? ResultHandlerType { get; }
}

internal sealed class ResultMappingTarget
{
    internal ResultMappingTarget(
        int columnOrdinal,
        string columnType,
        string columnName,
        ISymbol? target,
        ValueHandlerMapping? valueHandler = null)
    {
        ColumnOrdinal = columnOrdinal;
        ColumnType = columnType;
        ColumnName = columnName;
        Target = target;
        ValueHandler = valueHandler;
    }

    internal int ColumnOrdinal { get; }
    internal string ColumnType { get; }
    internal string ColumnName { get; }
    internal ISymbol? Target { get; }
    internal ValueHandlerMapping? ValueHandler { get; }
}

internal sealed class UncheckedResultMapping
{
    internal UncheckedResultMapping(
        ITypeSymbol resultType,
        ResultMappingKind kind,
        IReadOnlyList<UncheckedResultMappingTarget> targets,
        ITypeSymbol? resultHandlerType = null)
    {
        ResultType = resultType;
        Kind = kind;
        Targets = targets;
        ResultHandlerType = resultHandlerType;
    }

    internal ITypeSymbol ResultType { get; }
    internal ResultMappingKind Kind { get; }
    internal IReadOnlyList<UncheckedResultMappingTarget> Targets { get; }
    internal ITypeSymbol? ResultHandlerType { get; }
}

internal sealed class UncheckedResultMappingTarget
{
    internal UncheckedResultMappingTarget(
        string columnName,
        ITypeSymbol type,
        ISymbol? target,
        ITypeSymbol? valueHandlerType = null)
    {
        ColumnName = columnName;
        Type = type;
        Target = target;
        ValueHandlerType = valueHandlerType;
    }

    internal string ColumnName { get; }
    internal ITypeSymbol Type { get; }
    internal ISymbol? Target { get; }
    internal ITypeSymbol? ValueHandlerType { get; }
}

internal static class ResultMappingFactory
{
    internal static bool TryCreateUnchecked(
        Compilation compilation,
        ITypeSymbol resultType,
        out UncheckedResultMapping? mapping,
        out string? error)
    {
        mapping = null;
        error = null;
        if (resultType is ITypeParameterSymbol)
        {
            error = $"result type '{Display(resultType)}' is not known at build time";
            return false;
        }

        if (!TryGetResultHandler(
                compilation,
                resultType,
                out var resultHandlerType,
                out error))
        {
            return false;
        }

        if (resultHandlerType != null)
        {
            mapping = new UncheckedResultMapping(
                resultType,
                ResultMappingKind.Handler,
                Array.Empty<UncheckedResultMappingTarget>(),
                resultHandlerType);
            return true;
        }

        if (IsScalar(resultType))
        {
            mapping = new UncheckedResultMapping(
                resultType,
                ResultMappingKind.Scalar,
                new[] { new UncheckedResultMappingTarget("Value", resultType, null) });
            return true;
        }

        if (!(resultType is INamedTypeSymbol namedResult) || namedResult.IsAbstract)
        {
            error = $"result type '{Display(resultType)}' cannot be constructed";
            return false;
        }

        var constructors = namedResult.InstanceConstructors
            .Where(constructor => constructor.Parameters.Length != 0 &&
                compilation.IsSymbolAccessibleWithin(constructor, compilation.Assembly) &&
                !(constructor.Parameters.Length == 1 &&
                  SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, resultType)))
            .ToArray();
        if (constructors.Length > 1)
        {
            error = $"result type '{Display(resultType)}' has more than one accessible constructor; unchecked mapping cannot choose one without analyzing SQL";
            return false;
        }

        if (constructors.Length == 1)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var targets = new List<UncheckedResultMappingTarget>();
            foreach (var parameter in constructors[0].Parameters)
            {
                if (!TryGetTargetOptions(
                        compilation,
                        namedResult,
                        parameter,
                        parameter.Type,
                        out var columnName,
                        out var valueHandlerType,
                        out error))
                {
                    return false;
                }

                if (valueHandlerType != null &&
                    !TryResolveUncheckedValueHandler(
                        compilation,
                        valueHandlerType,
                        parameter.Type,
                        out error))
                {
                    return false;
                }

                if (!names.Add(NormalizeName(columnName)))
                {
                    error = $"result column name '{columnName}' is ambiguous after name matching";
                    return false;
                }

                targets.Add(new UncheckedResultMappingTarget(
                    columnName,
                    parameter.Type,
                    parameter,
                    valueHandlerType));
            }

            mapping = new UncheckedResultMapping(resultType, ResultMappingKind.Constructor, targets);
            return true;
        }

        var canCreateWithInitializer = namedResult.IsValueType || namedResult.InstanceConstructors.Any(constructor =>
            constructor.Parameters.Length == 0 && compilation.IsSymbolAccessibleWithin(constructor, compilation.Assembly));
        if (!canCreateWithInitializer)
        {
            error = $"result type '{Display(resultType)}' has no accessible constructor that generated code can use";
            return false;
        }

        var members = WritableMembers(namedResult, compilation).ToArray();
        if (members.Length == 0)
        {
            error = $"result type '{Display(resultType)}' has no accessible writable properties or fields";
            return false;
        }

        var memberTargets = new List<UncheckedResultMappingTarget>();
        var memberColumnNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var memberType = MemberType(member);
            if (!TryGetTargetOptions(
                    compilation,
                    namedResult,
                    member,
                    memberType,
                    out var columnName,
                    out var valueHandlerType,
                    out error))
            {
                return false;
            }


            if (valueHandlerType != null &&
                !TryResolveUncheckedValueHandler(
                    compilation,
                    valueHandlerType,
                    memberType,
                    out error))
            {
                return false;
            }

            if (!memberColumnNames.Add(NormalizeName(columnName)))
            {
                error = $"result column name '{columnName}' is ambiguous after name matching";
                return false;
            }

            memberTargets.Add(new UncheckedResultMappingTarget(
                columnName,
                memberType,
                member,
                valueHandlerType));
        }

        mapping = new UncheckedResultMapping(
            resultType,
            ResultMappingKind.Members,
            memberTargets);
        return true;
    }

    internal static bool TryCreate(
        Compilation compilation,
        ITypeSymbol resultType,
        AnalysisResult analysis,
        out ResultMapping? mapping,
        out string? error)
    {
        mapping = null;
        error = null;
        if (!(compilation is CSharpCompilation csharpCompilation))
        {
            error = "result mapping requires a C# compilation";
            return false;
        }

        if (resultType is ITypeParameterSymbol)
        {
            error = $"result type '{Display(resultType)}' is not known at build time";
            return false;
        }

        if (!TryGetResultHandler(
                compilation,
                resultType,
                out var resultHandlerType,
                out error))
        {
            return false;
        }

        if (resultHandlerType != null)
        {
            mapping = new ResultMapping(
                resultType,
                ResultMappingKind.Handler,
                Array.Empty<ResultMappingTarget>(),
                resultHandlerType);
            return true;
        }

        if (analysis.Columns.Count == 1 &&
            TryResolveColumnType(csharpCompilation, analysis.Columns[0].ClrType, out var scalarSource) &&
            IsCompatible(csharpCompilation, scalarSource!, resultType))
        {
            mapping = new ResultMapping(
                resultType,
                ResultMappingKind.Scalar,
                new[] { new ResultMappingTarget(
                    0,
                    analysis.Columns[0].ClrType,
                    analysis.Columns[0].Name,
                    null) });
            return true;
        }

        if (!(resultType is INamedTypeSymbol namedResult) || namedResult.IsAbstract)
        {
            error = $"result type '{Display(resultType)}' cannot be constructed";
            return false;
        }

        var columnsByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < analysis.Columns.Count; index++)
        {
            var normalized = NormalizeName(analysis.Columns[index].Name);
            if (normalized.Length == 0 || columnsByName.ContainsKey(normalized))
            {
                error = $"returned column '{analysis.Columns[index].Name}' is ambiguous after name matching";
                return false;
            }

            columnsByName.Add(normalized, index);
        }

        var matchingConstructors = new List<IReadOnlyList<ResultMappingTarget>>();
        foreach (var constructor in namedResult.InstanceConstructors)
        {
            if (constructor.IsStatic || constructor.Parameters.Length != analysis.Columns.Count ||
                !compilation.IsSymbolAccessibleWithin(constructor, compilation.Assembly))
            {
                continue;
            }

            var targets = new List<ResultMappingTarget>();
            var usedColumnOrdinals = new HashSet<int>();
            var valid = true;
            foreach (var parameter in constructor.Parameters)
            {
                if (!TryGetTargetOptions(
                        compilation,
                        namedResult,
                        parameter,
                        parameter.Type,
                        out var columnName,
                        out var valueHandlerType,
                        out error))
                {
                    return false;
                }

                if (!columnsByName.TryGetValue(NormalizeName(columnName), out var columnOrdinal) ||
                    !usedColumnOrdinals.Add(columnOrdinal) ||
                    !TryResolveColumnType(csharpCompilation, analysis.Columns[columnOrdinal].ClrType, out var sourceType))
                {
                    valid = false;
                    break;
                }

                ValueHandlerMapping? valueHandler = null;
                if (valueHandlerType == null)
                {
                    if (!IsCompatible(csharpCompilation, sourceType!, parameter.Type))
                    {
                        valid = false;
                        break;
                    }
                }
                else if (!TryResolveValueHandler(
                             compilation,
                             valueHandlerType,
                             sourceType!,
                             parameter.Type,
                             out valueHandler,
                             out error))
                {
                    return false;
                }

                targets.Add(new ResultMappingTarget(
                    columnOrdinal,
                    analysis.Columns[columnOrdinal].ClrType,
                    analysis.Columns[columnOrdinal].Name,
                    parameter,
                    valueHandler));
            }

            if (valid)
            {
                matchingConstructors.Add(targets);
            }
        }

        if (matchingConstructors.Count == 1)
        {
            mapping = new ResultMapping(resultType, ResultMappingKind.Constructor, matchingConstructors[0]);
            return true;
        }

        if (matchingConstructors.Count > 1)
        {
            error = $"result type '{Display(resultType)}' has more than one accessible constructor matching the returned columns";
            return false;
        }

        var canCreateWithInitializer = namedResult.IsValueType || namedResult.InstanceConstructors.Any(constructor =>
            constructor.Parameters.Length == 0 && compilation.IsSymbolAccessibleWithin(constructor, compilation.Assembly));
        if (!canCreateWithInitializer)
        {
            error = $"result type '{Display(resultType)}' has no accessible constructor matching all returned columns";
            return false;
        }

        var writableMembers = new List<(ISymbol Member, string ColumnName, ITypeSymbol? HandlerType)>();
        foreach (var member in WritableMembers(namedResult, compilation))
        {
            if (!TryGetTargetOptions(
                    compilation,
                    namedResult,
                    member,
                    MemberType(member),
                    out var memberColumnName,
                    out var memberHandlerType,
                    out error))
            {
                return false;
            }

            writableMembers.Add((member, memberColumnName, memberHandlerType));
        }

        var membersByName = writableMembers
            .GroupBy(member => NormalizeName(member.ColumnName), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var memberTargets = new List<ResultMappingTarget>();
        for (var columnOrdinal = 0; columnOrdinal < analysis.Columns.Count; columnOrdinal++)
        {
            var column = analysis.Columns[columnOrdinal];
            if (!membersByName.TryGetValue(NormalizeName(column.Name), out var members) || members.Length != 1)
            {
                error = $"returned column '{column.Name}' does not match exactly one accessible writable property or field on '{Display(resultType)}'";
                return false;
            }

            var member = members[0];
            var memberType = MemberType(member.Member);
            if (!TryResolveColumnType(csharpCompilation, column.ClrType, out var sourceType))
            {
                error = $"returned column '{column.Name}' has CLR type '{ColumnDisplay(column.ClrType)}', which cannot be assigned to '{Display(memberType)}' member '{member.Member.Name}'";
                return false;
            }

            ValueHandlerMapping? valueHandler = null;
            if (member.HandlerType == null)
            {
                if (!IsCompatible(csharpCompilation, sourceType!, memberType))
                {
                    error = $"returned column '{column.Name}' has CLR type '{ColumnDisplay(column.ClrType)}', which cannot be assigned to '{Display(memberType)}' member '{member.Member.Name}'";
                    return false;
                }
            }
            else if (!TryResolveValueHandler(
                         compilation,
                         member.HandlerType,
                         sourceType!,
                         memberType,
                         out valueHandler,
                         out error))
            {
                return false;
            }

            memberTargets.Add(new ResultMappingTarget(
                columnOrdinal,
                column.ClrType,
                column.Name,
                member.Member,
                valueHandler));
        }

        mapping = new ResultMapping(resultType, ResultMappingKind.Members, memberTargets);
        return true;
    }

    internal static string MaterializeExpression(
        ResultMapping mapping,
        TypeEnvironment environment,
        string context)
    {
        if (mapping.Kind == ResultMappingKind.Handler)
        {
            return HandlerInstance(mapping.ResultHandlerType!) + ".Read(reader)";
        }

        string Read(ResultMappingTarget target)
        {
            var value = environment.ReadExpression(
                target.ColumnType,
                target.ColumnOrdinal,
                context + "." + target.Target?.Name);
            if (target.ValueHandler == null)
            {
                return value;
            }

            var handler = HandlerInstance(target.ValueHandler.HandlerType);
            switch (target.ValueHandler.Kind)
            {
                case ValueHandlerMappingKind.Reader:
                    return handler + ".Read(reader, " +
                        target.ColumnOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                case ValueHandlerMappingKind.Converter:
                    return handler + ".Convert(" + value + ")";
                case ValueHandlerMappingKind.ArrayElements:
                    var method = target.ColumnType.EndsWith("?", StringComparison.Ordinal)
                        ? "ConvertNullable"
                        : "Convert";
                    return "global::CobaltumOrm.CobaltumArrayHandler." + method + "<" +
                        Display(target.ValueHandler.SourceType!) + ", " +
                        Display(target.ValueHandler.ValueType!) + ", " +
                        Display(target.ValueHandler.HandlerType) + ">(" + value + ", " + handler + ")";
                default:
                    throw new InvalidOperationException("Unknown value handler mapping kind.");
            }
        }

        if (mapping.Kind == ResultMappingKind.Scalar)
        {
            return environment.ReadExpression(
                mapping.Targets[0].ColumnType,
                mapping.Targets[0].ColumnOrdinal,
                context);
        }

        var typeName = Display(mapping.ResultType);
        if (mapping.Kind == ResultMappingKind.Constructor)
        {
            return "new " + typeName + "(" + string.Join(", ", mapping.Targets.Select(Read)) + ")";
        }

        var builder = new StringBuilder();
        builder.Append("new ").Append(typeName).AppendLine();
        builder.Append("                {");
        for (var index = 0; index < mapping.Targets.Count; index++)
        {
            var target = mapping.Targets[index];
            builder.AppendLine();
            builder.Append("                    ").Append(EscapeIdentifier(target.Target!.Name))
                .Append(" = ").Append(Read(target));
            builder.Append(index == mapping.Targets.Count - 1 ? string.Empty : ",");
        }

        builder.AppendLine();
        builder.Append("                }");
        return builder.ToString();
    }

    internal static string MaterializeUncheckedExpression(
        UncheckedResultMapping mapping,
        string context)
    {
        if (mapping.Kind == ResultMappingKind.Handler)
        {
            return HandlerInstance(mapping.ResultHandlerType!) + ".Read(reader)";
        }

        string Read(UncheckedResultMappingTarget target)
        {
            var ordinal = "global::CobaltumOrm.CobaltumResultReader.GetOrdinal(reader, " +
                CSharpNames.Literal(target.ColumnName) + ")";
            return target.ValueHandlerType == null
                ? "global::CobaltumOrm.CobaltumResultReader.Read<" + Display(target.Type) + ">(" +
                    "reader, " + CSharpNames.Literal(target.ColumnName) + ", " +
                    CSharpNames.Literal(context + "." + (target.Target?.Name ?? target.ColumnName)) + ", " +
                    (IsNullable(target.Type) ? "true" : "false") + ")"
                : HandlerInstance(target.ValueHandlerType) + ".Read(reader, " + ordinal + ")";
        }

        if (mapping.Kind == ResultMappingKind.Scalar)
        {
            return "global::CobaltumOrm.CobaltumResultReader.Read<" + Display(mapping.ResultType) +
                ">(reader, reader.GetName(0), " + CSharpNames.Literal(context) + ", " +
                (IsNullable(mapping.ResultType) ? "true" : "false") + ")";
        }

        var typeName = Display(mapping.ResultType);
        if (mapping.Kind == ResultMappingKind.Constructor)
        {
            return "new " + typeName + "(" + string.Join(", ", mapping.Targets.Select(Read)) + ")";
        }

        var builder = new StringBuilder();
        builder.Append("new ").Append(typeName).AppendLine();
        builder.Append("                {");
        for (var index = 0; index < mapping.Targets.Count; index++)
        {
            var target = mapping.Targets[index];
            builder.AppendLine();
            builder.Append("                    ").Append(EscapeIdentifier(target.Target!.Name))
                .Append(" = ").Append(Read(target));
            builder.Append(index == mapping.Targets.Count - 1 ? string.Empty : ",");
        }

        builder.AppendLine();
        builder.Append("                }");
        return builder.ToString();
    }

    internal static string Display(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string HandlerInstance(ITypeSymbol handlerType) =>
        "global::CobaltumOrm.CobaltumHandlerCache<" + Display(handlerType) + ">.Instance";

    private static IEnumerable<ISymbol> WritableMembers(INamedTypeSymbol type, Compilation compilation)
    {
        var hiddenNames = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (!hiddenNames.Add(member.Name) || member.IsStatic)
                {
                    continue;
                }

                if (member is IPropertySymbol property && property.Parameters.Length == 0 &&
                    property.SetMethod != null &&
                    compilation.IsSymbolAccessibleWithin(property.SetMethod, compilation.Assembly))
                {
                    yield return property;
                }
                else if (member is IFieldSymbol field && !field.IsReadOnly && !field.IsConst &&
                         compilation.IsSymbolAccessibleWithin(field, compilation.Assembly))
                {
                    yield return field;
                }
            }
        }
    }

    private static bool TryGetResultHandler(
        Compilation compilation,
        ITypeSymbol resultType,
        out ITypeSymbol? handlerType,
        out string? error)
    {
        handlerType = null;
        error = null;
        var attributes = resultType.GetAttributes()
            .Where(attribute => IsAttribute(attribute, "ResultHandlerAttribute`1"))
            .ToArray();
        if (attributes.Length == 0)
        {
            return true;
        }

        if (attributes.Length != 1)
        {
            error = $"result type '{Display(resultType)}' declares more than one result handler";
            return false;
        }

        handlerType = attributes[0].AttributeClass!.TypeArguments[0];
        return ValidateHandler(
            compilation,
            handlerType,
            "IResultHandler`1",
            resultType,
            out error);
    }

    private static bool TryGetTargetOptions(
        Compilation compilation,
        INamedTypeSymbol resultType,
        ISymbol target,
        ITypeSymbol targetType,
        out string columnName,
        out ITypeSymbol? handlerType,
        out string? error)
    {
        columnName = target.Name;
        handlerType = null;
        error = null;
        var attributes = TargetAttributes(resultType, target).ToArray();
        var columnAttributes = attributes
            .Where(attribute => IsAttribute(attribute, "ResultColumnAttribute"))
            .ToArray();
        if (columnAttributes.Length > 1)
        {
            error = $"result member '{target.Name}' declares more than one result column name";
            return false;
        }

        if (columnAttributes.Length == 1 &&
            columnAttributes[0].ConstructorArguments.Length == 1 &&
            columnAttributes[0].ConstructorArguments[0].Value is string configuredName)
        {
            columnName = configuredName;
        }

        if (NormalizeName(columnName).Length == 0)
        {
            error = $"result member '{target.Name}' has an empty result column name";
            return false;
        }

        var handlerAttributes = attributes
            .Where(attribute => IsAttribute(attribute, "ValueHandlerAttribute`1"))
            .ToArray();
        if (handlerAttributes.Length > 1)
        {
            error = $"result member '{target.Name}' declares more than one value handler";
            return false;
        }

        if (handlerAttributes.Length == 0)
        {
            return true;
        }

        handlerType = handlerAttributes[0].AttributeClass!.TypeArguments[0];
        return ValidateValueHandlerDeclaration(
            compilation,
            handlerType,
            targetType,
            out error);
    }

    private static bool ValidateValueHandlerDeclaration(
        Compilation compilation,
        ITypeSymbol handlerType,
        ITypeSymbol targetType,
        out string? error)
    {
        if (!(compilation is CSharpCompilation csharpCompilation))
        {
            error = "value handlers require a C# compilation";
            return false;
        }

        if (!ValidateConstructibleHandler(compilation, handlerType, out error))
        {
            return false;
        }

        var namedHandler = (INamedTypeSymbol)handlerType;
        var hasReader = HandlerInterfaces(namedHandler, "IValueHandler`1", 1)
            .Any(@interface => TypesEqual(@interface.TypeArguments[0], targetType));
        var hasConverter = HandlerInterfaces(namedHandler, "IValueHandler`2", 2)
            .Any(@interface => IsCompatible(
                csharpCompilation,
                @interface.TypeArguments[1],
                targetType));
        var hasElementConverter = targetType is IArrayTypeSymbol targetArray &&
            HandlerInterfaces(namedHandler, "IValueHandler`2", 2)
                .Any(@interface => TypesEqual(@interface.TypeArguments[1], targetArray.ElementType));
        if (hasReader || hasConverter || hasElementConverter)
        {
            return true;
        }

        error = $"handler type '{Display(handlerType)}' must implement 'IValueHandler<{Display(targetType)}>', " +
            $"'IValueHandler<TSource, {Display(targetType)}>', or a matching array element handler";
        return false;
    }

    private static bool TryResolveValueHandler(
        Compilation compilation,
        ITypeSymbol handlerType,
        ITypeSymbol sourceType,
        ITypeSymbol targetType,
        out ValueHandlerMapping? mapping,
        out string? error)
    {
        mapping = null;
        error = null;
        if (!(compilation is CSharpCompilation csharpCompilation))
        {
            error = "value handlers require a C# compilation";
            return false;
        }

        if (!ValidateConstructibleHandler(compilation, handlerType, out error))
        {
            return false;
        }

        var namedHandler = (INamedTypeSymbol)handlerType;
        var matches = new List<ValueHandlerMapping>();
        foreach (var @interface in HandlerInterfaces(namedHandler, "IValueHandler`1", 1))
        {
            if (TypesEqual(@interface.TypeArguments[0], targetType))
            {
                matches.Add(new ValueHandlerMapping(handlerType, ValueHandlerMappingKind.Reader));
            }
        }

        foreach (var @interface in HandlerInterfaces(namedHandler, "IValueHandler`2", 2))
        {
            var handlerSource = @interface.TypeArguments[0];
            var handlerValue = @interface.TypeArguments[1];
            if (IsCompatible(csharpCompilation, sourceType, handlerSource) &&
                IsCompatible(csharpCompilation, handlerValue, targetType))
            {
                matches.Add(new ValueHandlerMapping(
                    handlerType,
                    ValueHandlerMappingKind.Converter,
                    handlerSource,
                    handlerValue));
            }

            if (sourceType is IArrayTypeSymbol sourceArray &&
                targetType is IArrayTypeSymbol targetArray &&
                (!IsNullable(sourceType) || IsNullable(targetType)) &&
                TypesEqual(handlerSource, sourceArray.ElementType) &&
                TypesEqual(handlerValue, targetArray.ElementType))
            {
                matches.Add(new ValueHandlerMapping(
                    handlerType,
                    ValueHandlerMappingKind.ArrayElements,
                    handlerSource,
                    handlerValue));
            }
        }

        if (matches.Count == 1)
        {
            mapping = matches[0];
            return true;
        }

        if (matches.Count > 1)
        {
            error = $"handler type '{Display(handlerType)}' has more than one conversion applicable to " +
                $"'{Display(sourceType)}' and '{Display(targetType)}'";
            return false;
        }

        error = $"handler type '{Display(handlerType)}' cannot convert returned CLR type " +
            $"'{Display(sourceType)}' to '{Display(targetType)}'";
        return false;
    }

    private static bool TryResolveUncheckedValueHandler(
        Compilation compilation,
        ITypeSymbol handlerType,
        ITypeSymbol targetType,
        out string? error)
    {
        if (!ValidateConstructibleHandler(compilation, handlerType, out error))
        {
            return false;
        }

        var namedHandler = (INamedTypeSymbol)handlerType;
        if (HandlerInterfaces(namedHandler, "IValueHandler`1", 1)
            .Any(@interface => TypesEqual(@interface.TypeArguments[0], targetType)))
        {
            return true;
        }

        error = $"conversion handler '{Display(handlerType)}' requires checked SQL so its source CLR type is known";
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> HandlerInterfaces(
        INamedTypeSymbol handlerType,
        string metadataName,
        int arity) =>
        handlerType.AllInterfaces.Where(@interface =>
            @interface.OriginalDefinition.MetadataName == metadataName &&
            @interface.ContainingNamespace.ToDisplayString() == "CobaltumOrm" &&
            @interface.TypeArguments.Length == arity);

    private static bool TypesEqual(ITypeSymbol left, ITypeSymbol right) =>
        SymbolEqualityComparer.IncludeNullability.Equals(left, right);

    private static IEnumerable<AttributeData> TargetAttributes(INamedTypeSymbol resultType, ISymbol target)
    {
        foreach (var attribute in target.GetAttributes())
        {
            yield return attribute;
        }

        if (!(target is IParameterSymbol))
        {
            yield break;
        }

        foreach (var member in resultType.GetMembers().Where(member =>
                     string.Equals(member.Name, target.Name, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var attribute in member.GetAttributes())
            {
                yield return attribute;
            }
        }
    }

    private static bool ValidateHandler(
        Compilation compilation,
        ITypeSymbol handlerType,
        string requiredInterfaceMetadataName,
        ITypeSymbol valueType,
        out string? error)
    {
        if (!ValidateConstructibleHandler(compilation, handlerType, out error))
        {
            return false;
        }

        var namedHandler = (INamedTypeSymbol)handlerType;
        var implementsHandler = namedHandler.AllInterfaces.Any(@interface =>
            @interface.OriginalDefinition.MetadataName == requiredInterfaceMetadataName &&
            @interface.ContainingNamespace.ToDisplayString() == "CobaltumOrm" &&
            @interface.TypeArguments.Length == 1 &&
            SymbolEqualityComparer.IncludeNullability.Equals(@interface.TypeArguments[0], valueType));
        if (!implementsHandler)
        {
            var interfaceName = requiredInterfaceMetadataName.StartsWith("IValue", StringComparison.Ordinal)
                ? "IValueHandler<" + Display(valueType) + ">"
                : "IResultHandler<" + Display(valueType) + ">";
            error = $"handler type '{Display(handlerType)}' must implement '{interfaceName}'";
            return false;
        }

        return true;
    }

    private static bool ValidateConstructibleHandler(
        Compilation compilation,
        ITypeSymbol handlerType,
        out string? error)
    {
        error = null;
        if (!(handlerType is INamedTypeSymbol namedHandler) || namedHandler.IsAbstract ||
            !compilation.IsSymbolAccessibleWithin(handlerType, compilation.Assembly))
        {
            error = $"handler type '{Display(handlerType)}' cannot be constructed by generated code";
            return false;
        }

        if (!namedHandler.IsValueType && !namedHandler.InstanceConstructors.Any(constructor =>
                constructor.Parameters.Length == 0 &&
                constructor.DeclaredAccessibility == Accessibility.Public))
        {
            error = $"handler type '{Display(handlerType)}' must have a public parameterless constructor";
            return false;
        }

        return true;
    }

    private static bool IsAttribute(AttributeData attribute, string metadataName) =>
        attribute.AttributeClass?.OriginalDefinition.MetadataName == metadataName &&
        attribute.AttributeClass.ContainingNamespace.ToDisplayString() == "CobaltumOrm";

    private static ITypeSymbol MemberType(ISymbol symbol) =>
        symbol is IPropertySymbol property ? property.Type : ((IFieldSymbol)symbol).Type;

    private static bool IsCompatible(
        CSharpCompilation compilation,
        ITypeSymbol source,
        ITypeSymbol target)
    {
        if (IsNullable(source) && !IsNullable(target))
        {
            return false;
        }

        var conversion = compilation.ClassifyConversion(source, target);
        return conversion.Exists && conversion.IsImplicit;
    }

    private static bool IsScalar(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol nullable &&
            nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            type = nullable.TypeArguments[0];
        }

        if (type.TypeKind == TypeKind.Enum || type is IArrayTypeSymbol)
        {
            return true;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_Char:
            case SpecialType.System_String:
            case SpecialType.System_Object:
                return true;
        }

        var display = type.ToDisplayString();
        return display == "System.Guid" || display == "System.DateTime" ||
            display == "System.DateTimeOffset" || display == "System.TimeSpan" ||
            display == "System.DateOnly" || display == "System.TimeOnly";
    }

    private static bool IsNullable(ITypeSymbol type)
    {
        if (type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            return true;
        }

        return type is INamedTypeSymbol named &&
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }

    private static bool TryResolveColumnType(
        CSharpCompilation compilation,
        string analyzerType,
        out ITypeSymbol? type)
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
        type = compilation.GetTypeByMetadataName(metadataName);
        if (type == null)
        {
            return false;
        }

        while (arrayRank-- > 0)
        {
            type = compilation.CreateArrayTypeSymbol(type);
        }

        if (type is IArrayTypeSymbol)
        {
            type = type.WithNullableAnnotation(nullable
                ? NullableAnnotation.Annotated
                : NullableAnnotation.NotAnnotated);
            return true;
        }

        if (nullable && type.IsValueType)
        {
            var nullableType = compilation.GetTypeByMetadataName("System.Nullable`1");
            type = nullableType?.Construct(type);
            return type != null;
        }

        type = type.WithNullableAnnotation(nullable
            ? NullableAnnotation.Annotated
            : NullableAnnotation.NotAnnotated);
        return true;
    }

    private static string NormalizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None &&
        SyntaxFacts.GetContextualKeywordKind(identifier) == SyntaxKind.None
            ? identifier
            : "@" + identifier;

    private static string ColumnDisplay(string analyzerType) =>
        analyzerType.EndsWith("?", StringComparison.Ordinal)
            ? analyzerType.Substring(0, analyzerType.Length - 1) + "?"
            : analyzerType;
}

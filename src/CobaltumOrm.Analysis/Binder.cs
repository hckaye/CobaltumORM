using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CobaltumOrm.Analysis;

internal sealed class Binder
{
    private readonly DatabaseSchema _schema;
    private readonly List<Diagnostic> _diagnostics;
    private readonly QuerySyntaxProfile _syntax;
    private readonly QueryTypeProfile _types;
    private readonly List<ScopeTable> _scope = new List<ScopeTable>();
    private readonly List<int> _scopeStarts = new List<int>();
    private readonly List<CommonTableRelation> _commonTables = new List<CommonTableRelation>();
    private readonly Dictionary<string, ParameterState> _parameters =
        new Dictionary<string, ParameterState>(StringComparer.OrdinalIgnoreCase);
    private readonly List<ParameterState> _parameterOrder = new List<ParameterState>();
    private IReadOnlyList<NamedWindow> _currentWindows = Array.Empty<NamedWindow>();
    private bool _hasGroupBy;

    internal Binder(DatabaseSchema schema, List<Diagnostic> diagnostics)
        : this(schema, diagnostics, QueryDialectProfiles.PostgreSql)
    {
    }

    internal Binder(
        DatabaseSchema schema,
        List<Diagnostic> diagnostics,
        QueryDialectProfile profile)
    {
        _schema = schema;
        _diagnostics = diagnostics;
        var selectedProfile = profile ?? throw new ArgumentNullException(nameof(profile));
        _syntax = selectedProfile.Syntax;
        _types = selectedProfile.Types;
    }

    internal AnalysisResult Bind(SqlStatement statement)
    {
        var columns = BindStatement(statement);
        return new AnalysisResult(FinishColumns(columns), FinishParameters(), _diagnostics);
    }

    private IReadOnlyList<BoundColumn> BindStatement(SqlStatement statement)
    {
        var with = statement as WithStatement;
        if (with != null) return BindWith(with);

        var select = statement as SelectStatement;
        if (select != null) return BindSelect(select);

        var values = statement as ValuesStatement;
        if (values != null) return BindValues(values);

        var update = statement as UpdateStatement;
        if (update != null) return BindUpdate(update);

        var insert = statement as InsertStatement;
        if (insert != null) return BindInsert(insert);

        var delete = statement as DeleteStatement;
        if (delete != null) return BindDelete(delete);

        var truncate = statement as TruncateStatement;
        if (truncate != null) return BindTruncate(truncate);

        Report("SQL999", "The SQL statement could not be analyzed.", new SourceSpan(0, 0));
        return Array.Empty<BoundColumn>();
    }

    private IReadOnlyList<BoundColumn> BindWith(WithStatement statement)
    {
        var relationStart = _commonTables.Count;
        foreach (var expression in statement.Expressions)
        {
            if (_commonTables.Any(item => IdentifiersEquivalent(item.Name, expression.Name)))
            {
                Report("SQL201", $"CTE name '{expression.Name.Name}' is already in scope.", expression.Name.Span);
            }

            CommonTableRelation? recursiveRelation = null;
            if (statement.Recursive && expression.Statement is SelectStatement recursiveSelect)
            {
                var seed = new SelectStatement(
                    recursiveSelect.Items,
                    recursiveSelect.From,
                    recursiveSelect.Joins,
                    recursiveSelect.Where,
                    recursiveSelect.GroupBy,
                    recursiveSelect.Having,
                    recursiveSelect.OrderBy,
                    recursiveSelect.Limit,
                    recursiveSelect.Offset,
                    recursiveSelect.Distinct,
                    recursiveSelect.DistinctOn,
                    Array.Empty<SetOperation>(),
                    recursiveSelect.Windows,
                    recursiveSelect.LockTables);
                var seedColumns = ApplyCteColumnNames(expression, BindNestedStatement(seed, true).ToList());
                recursiveRelation = new CommonTableRelation(expression.Name, seedColumns);
                _commonTables.Add(recursiveRelation);
            }

            var columns = ApplyCteColumnNames(
                expression,
                BindNestedStatement(expression.Statement, true).ToList());
            if (recursiveRelation == null)
            {
                _commonTables.Add(new CommonTableRelation(expression.Name, columns));
            }
            else
            {
                recursiveRelation.Columns = columns;
            }
        }

        var result = BindStatement(statement.Statement);
        _commonTables.RemoveRange(relationStart, _commonTables.Count - relationStart);
        return result;
    }

    private IReadOnlyList<BoundColumn> ApplyCteColumnNames(
        CommonTableExpression expression,
        List<BoundColumn> columns)
    {
        if (expression.ColumnNames.Count == 0)
        {
            return columns;
        }

        if (expression.ColumnNames.Count != columns.Count)
        {
            Report(
                "SQL219",
                $"CTE '{expression.Name.Name}' declares {expression.ColumnNames.Count} column name(s), but its query returns {columns.Count} column(s).",
                expression.Name.Span);
        }

        for (var index = 0; index < columns.Count && index < expression.ColumnNames.Count; index++)
        {
            columns[index] = new BoundColumn(
                expression.ColumnNames[index].Name,
                columns[index].Type,
                expression.ColumnNames[index].Span);
        }

        return columns;
    }

    private IReadOnlyList<BoundColumn> BindSelect(SelectStatement statement)
    {
        var scopeStart = _scope.Count;
        _scopeStarts.Add(scopeStart);
        var previousHasGroupBy = _hasGroupBy;
        var previousWindows = _currentWindows;
        _currentWindows = statement.Windows;
        var needsUnqualifiedStar = statement.Items.Any(item =>
            item.Expression is StarExpression star && star.Qualifier == null);
        var unqualifiedStarColumns = new List<BoundColumn>();
        if (statement.From != null)
        {
            var from = AddTable(statement.From);
            if (needsUnqualifiedStar)
            {
                unqualifiedStarColumns.AddRange(ScopeColumns(from));
            }
        }

        foreach (var join in statement.Joins)
        {
            var priorCount = _scope.Count;
            var joined = AddTable(join.Table);
            if (join.Kind == JoinKind.Left || join.Kind == JoinKind.Full)
            {
                joined.ForcedNullable = true;
            }

            if (join.Kind == JoinKind.Right || join.Kind == JoinKind.Full)
            {
                for (var index = 0; index < priorCount; index++)
                {
                    _scope[index].ForcedNullable = true;
                }

                if (needsUnqualifiedStar)
                {
                    for (var index = 0; index < unqualifiedStarColumns.Count; index++)
                    {
                        var column = unqualifiedStarColumns[index];
                        unqualifiedStarColumns[index] = new BoundColumn(
                            column.Name,
                            column.Type.WithNullable(true),
                            column.Span);
                    }
                }
            }

            var joinColumns = JoinColumnNames(join, joined, priorCount);
            ValidateJoinColumns(joinColumns, joined, priorCount);
            if (needsUnqualifiedStar)
            {
                unqualifiedStarColumns = MergeJoinStarColumns(
                    unqualifiedStarColumns,
                    joined,
                    joinColumns,
                    join.Kind);
            }
        }

        foreach (var expression in statement.DistinctOn)
        {
            BindExpression(expression);
        }

        BindNamedWindows(statement.Windows);
        foreach (var table in statement.LockTables)
        {
            FindScopeTable(table, true);
        }

        _hasGroupBy = statement.GroupBy.Count != 0;
        ValidateQueryStructure(statement);

        foreach (var join in statement.Joins)
        {
            if (join.On != null)
            {
                BindBooleanContext(join.On, "JOIN ON");
            }
        }

        if (statement.Where != null)
        {
            BindBooleanContext(statement.Where, "WHERE");
        }

        foreach (var expression in statement.GroupBy)
        {
            BindExpression(expression);
        }

        if (statement.Having != null)
        {
            BindBooleanContext(statement.Having, "HAVING");
        }

        if (statement.Limit != null)
        {
            BindIntegerContext(statement.Limit, "LIMIT");
        }

        if (statement.Offset != null)
        {
            BindIntegerContext(statement.Offset, "OFFSET");
        }

        var boundColumns = BindSelectItems(
            statement.Items,
            null,
            statement.From == null ? null : unqualifiedStarColumns);
        foreach (var orderItem in statement.OrderBy)
        {
            if (!IsOutputName(orderItem.Expression, boundColumns))
            {
                BindExpression(orderItem.Expression);
            }
        }

        var result = boundColumns.ToList();
        foreach (var operation in statement.SetOperations)
        {
            var localScope = _scope.Skip(scopeStart).ToList();
            _scope.RemoveRange(scopeStart, _scope.Count - scopeStart);
            var right = BindNestedStatement(operation.Right, true);
            _scope.AddRange(localScope);
            if (right.Count != result.Count)
            {
                Report(
                    "SQL221",
                    $"{operation.Kind.ToString().ToUpperInvariant()} queries must return the same number of columns.",
                    operation.Span);
                continue;
            }

            for (var index = 0; index < result.Count; index++)
            {
                var leftType = result[index].Type;
                var rightType = right[index].Type;
                if (!_types.TryUnify(leftType.Type, rightType.Type, out var type))
                {
                    Report(
                        "SQL207",
                        $"{operation.Kind.ToString().ToUpperInvariant()} column {index + 1} has incompatible types.",
                        operation.Span);
                    continue;
                }

                result[index] = new BoundColumn(
                    result[index].Name,
                    new TypeInfo(type, IsNullable(leftType) || IsNullable(rightType)),
                    result[index].Span);
            }
        }

        _scope.RemoveRange(scopeStart, _scope.Count - scopeStart);
        _scopeStarts.RemoveAt(_scopeStarts.Count - 1);
        _hasGroupBy = previousHasGroupBy;
        _currentWindows = previousWindows;
        return result;
    }

    private void BindNamedWindows(IReadOnlyList<NamedWindow> windows)
    {
        var names = new List<SqlIdentifier>();
        foreach (var window in windows)
        {
            if (names.Any(name => IdentifiersEquivalent(name, window.Name)))
            {
                Report("SQL201", $"Window name '{window.Name.Name}' is already defined.", window.Name.Span);
            }
            else
            {
                names.Add(window.Name);
            }

            BindWindowSpecification(window.Specification);
        }
    }

    private void BindWindowSpecification(WindowSpecification specification)
    {
        if (specification.Name != null &&
            !_currentWindows.Any(window => IdentifiersEquivalent(window.Name, specification.Name)))
        {
            Report("SQL200", $"Unknown window '{specification.Name.Name}'.", specification.Name.Span);
        }

        foreach (var partition in specification.PartitionBy)
        {
            BindExpression(partition);
        }

        foreach (var order in specification.OrderBy)
        {
            BindExpression(order.Expression);
        }
    }

    private IReadOnlyList<BoundColumn> BindValues(ValuesStatement statement)
    {
        if (statement.Rows.Count == 0)
        {
            return Array.Empty<BoundColumn>();
        }

        var width = statement.Rows[0].Count;
        foreach (var row in statement.Rows)
        {
            if (row.Count != width)
            {
                Report(
                    "SQL219",
                    $"VALUES row has {row.Count} column(s), but the first row has {width}.",
                    row.Count == 0 ? new SourceSpan(0, 0) : row[0].Span);
            }
        }

        var result = new List<BoundColumn>();
        for (var columnIndex = 0; columnIndex < width; columnIndex++)
        {
            var expressions = new List<Expression>();
            var types = new List<TypeInfo>();
            foreach (var row in statement.Rows)
            {
                if (columnIndex >= row.Count) continue;
                expressions.Add(row[columnIndex]);
                types.Add(BindExpression(row[columnIndex]));
            }

            var span = expressions.Count == 0 ? new SourceSpan(0, 0) : expressions[0].Span;
            var type = UnifyExpressions(
                expressions,
                types,
                span,
                $"VALUES column {columnIndex + 1} has incompatible types.");
            result.Add(new BoundColumn(
                "column" + (columnIndex + 1).ToString(CultureInfo.InvariantCulture),
                type.Kind == SqlValueKind.Error
                    ? ErrorType()
                    : new TypeInfo(type, types.Any(IsNullable)),
                span));
        }

        foreach (var order in statement.OrderBy)
        {
            if (!IsOutputName(order.Expression, result))
            {
                BindExpression(order.Expression);
            }
        }

        if (statement.Limit != null) BindIntegerContext(statement.Limit, "LIMIT");
        if (statement.Offset != null) BindIntegerContext(statement.Offset, "OFFSET");
        return result;
    }

    private List<SqlIdentifier> JoinColumnNames(JoinClause join, ScopeTable joined, int priorCount)
    {
        var names = new List<SqlIdentifier>(join.UsingColumns);
        if (join.Natural)
        {
            foreach (var column in ScopeColumns(joined))
            {
                if (_scope.Take(priorCount).Any(table => FindScopeColumn(table, column.Name) != null))
                {
                    names.Add(new SqlIdentifier(column.Name, false, join.Span));
                }
            }
        }

        return names;
    }

    private void ValidateJoinColumns(
        IReadOnlyList<SqlIdentifier> names,
        ScopeTable joined,
        int priorCount)
    {
        foreach (var name in names)
        {
            var right = FindScopeColumn(joined, name.Name);
            if (right == null)
            {
                Report("SQL203", $"JOIN column '{name.Name}' does not exist on the joined table.", name.Span);
                continue;
            }

            var leftMatches = _scope.Take(priorCount)
                .Select(table => FindScopeColumn(table, name.Name))
                .Where(column => column != null)
                .Cast<BoundColumn>()
                .ToList();
            if (leftMatches.Count == 0)
            {
                Report("SQL203", $"JOIN column '{name.Name}' does not exist on the left side.", name.Span);
            }
            else if (leftMatches.Count > 1)
            {
                Report("SQL204", $"JOIN column '{name.Name}' is ambiguous on the left side.", name.Span);
            }
            else if (!AreCompatible(leftMatches[0].Type, right.Type))
            {
                Report("SQL207", $"JOIN column '{name.Name}' has incompatible types.", name.Span);
            }
        }
    }

    private List<BoundColumn> MergeJoinStarColumns(
        IReadOnlyList<BoundColumn> leftColumns,
        ScopeTable joined,
        IReadOnlyList<SqlIdentifier> joinColumns,
        JoinKind kind)
    {
        var rightColumns = ScopeColumns(joined).ToList();
        if (joinColumns.Count == 0)
        {
            return leftColumns.Concat(rightColumns).ToList();
        }

        var result = new List<BoundColumn>();
        foreach (var name in joinColumns)
        {
            var left = leftColumns.FirstOrDefault(column => MatchesOutputName(column, name));
            var right = rightColumns.FirstOrDefault(column => MatchesOutputName(column, name));
            if (left == null || right == null) continue;

            var type = kind == JoinKind.Right
                ? right.Type
                : left.Type;
            if (kind == JoinKind.Full)
            {
                type = _types.TryUnify(left.Type.Type, right.Type.Type, out var unified)
                    ? new TypeInfo(unified, IsNullable(left.Type) || IsNullable(right.Type))
                    : ErrorType();
            }

            result.Add(new BoundColumn(name.Name, type, name.Span));
        }

        result.AddRange(leftColumns.Where(column =>
            !joinColumns.Any(name => MatchesOutputName(column, name))));
        result.AddRange(rightColumns.Where(column =>
            !joinColumns.Any(name => MatchesOutputName(column, name))));
        return result;
    }

    private bool MatchesOutputName(BoundColumn column, SqlIdentifier identifier) =>
        _syntax.AreIdentifiersEqual(identifier.Name, identifier.IsQuoted, column.Name);

    private IEnumerable<BoundColumn> ScopeColumns(ScopeTable table)
    {
        if (table.DerivedColumns != null)
        {
            return table.DerivedColumns;
        }

        if (table.Table == null)
        {
            return Array.Empty<BoundColumn>();
        }

        return table.Table.Columns.Select(column => new BoundColumn(
            column.Name,
            ColumnType(column, table.ForcedNullable, table.EffectiveName.Span),
            table.EffectiveName.Span));
    }

    private BoundColumn? FindScopeColumn(ScopeTable table, string name)
    {
        return ScopeColumns(table).FirstOrDefault(column =>
            string.Equals(
                _syntax.NormalizeUnquotedIdentifier(column.Name),
                _syntax.NormalizeUnquotedIdentifier(name),
                _syntax.UnquotedIdentifierComparison));
    }

    private IReadOnlyList<BoundColumn> BindUpdate(UpdateStatement statement)
    {
        var scopeStart = _scope.Count;
        _scopeStarts.Add(scopeStart);
        var target = AddTable(statement.Table);
        foreach (var table in statement.From)
        {
            AddTable(table);
        }
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in statement.Assignments)
        {
            var identifier = ReferencedIdentifier(assignment.Column);
            if (!assigned.Add(identifier))
            {
                Report(
                    "SQL219",
                    $"Column '{assignment.Column.Name}' is assigned more than once.",
                    assignment.Column.Span);
            }

            var column = target.Table == null ? null : FindColumn(target.Table, assignment.Column);
            if (column == null)
            {
                if (target.Table != null)
                {
                    Report(
                        "SQL203",
                        $"Unknown column '{assignment.Column.Name}' on '{statement.Table.Name.Name}'.",
                        assignment.Column.Span);
                }

                BindExpression(assignment.Value);
                continue;
            }

            BindAssignedExpression(column, assignment.Value);
        }

        if (statement.Where != null)
        {
            ValidateAggregatePlacement(statement.Where, AggregateContext.Where, 0);
            BindBooleanContext(statement.Where, "WHERE");
        }

        var result = BindSelectItems(statement.Returning, target);
        _scope.RemoveRange(scopeStart, _scope.Count - scopeStart);
        _scopeStarts.RemoveAt(_scopeStarts.Count - 1);
        return result;
    }

    private IReadOnlyList<BoundColumn> BindInsert(InsertStatement statement)
    {
        var table = ResolveTable(statement.Table);
        var targetColumns = new List<Column?>();
        if (table != null)
        {
            if (statement.Columns.Count == 0)
            {
                foreach (var column in table.Columns)
                {
                    targetColumns.Add(column);
                }
            }
            else
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var identifier in statement.Columns)
                {
                    var key = ReferencedIdentifier(identifier);
                    if (!used.Add(key))
                    {
                        Report("SQL219", $"Column '{identifier.Name}' appears more than once in INSERT.", identifier.Span);
                    }

                    var column = FindColumn(table, identifier);
                    if (column == null)
                    {
                        Report(
                            "SQL203",
                            $"Unknown column '{identifier.Name}' on '{statement.Table.Name.Name}'.",
                            identifier.Span);
                    }

                    targetColumns.Add(column);
                }
            }
        }

        if (!statement.UsesDefaultValues)
        {
            foreach (var row in statement.Rows)
            {
                if (table != null && row.Count != targetColumns.Count)
                {
                    Report(
                        "SQL219",
                        $"INSERT supplies {row.Count} value(s), but {targetColumns.Count} target column(s) were selected.",
                        row.Count == 0 ? statement.Table.Name.Span : row[0].Span);
                }

                for (var index = 0; index < row.Count; index++)
                {
                    if (index < targetColumns.Count && targetColumns[index] != null)
                    {
                        BindAssignedExpression(targetColumns[index]!, row[index]);
                    }
                    else
                    {
                        BindExpression(row[index]);
                    }
                }
            }
        }

        if (statement.Source != null)
        {
            var sourceColumns = BindNestedStatement(statement.Source, true);
            if (table != null && sourceColumns.Count != targetColumns.Count)
            {
                Report(
                    "SQL219",
                    $"INSERT query returns {sourceColumns.Count} column(s), but {targetColumns.Count} target column(s) were selected.",
                    statement.Table.Name.Span);
            }

            for (var index = 0; index < sourceColumns.Count && index < targetColumns.Count; index++)
            {
                if (targetColumns[index] == null) continue;
                var targetType = ColumnType(targetColumns[index]!, false, statement.Table.Name.Span);
                if (!AreCompatible(targetType, sourceColumns[index].Type))
                {
                    Report(
                        "SQL207",
                        $"INSERT query column {index + 1} is not compatible with target column '{targetColumns[index]!.Name}'.",
                        statement.Table.Name.Span);
                }
            }
        }

        var scopeStart = _scope.Count;
        _scopeStarts.Add(scopeStart);
        ScopeTable? returningTarget = null;
        if (table != null && (statement.OnConflict != null || statement.Returning.Count != 0))
        {
            returningTarget = AddTable(statement.Table);
        }

        BindOnConflict(statement, table);
        var result = BindSelectItems(statement.Returning, returningTarget);
        _scope.RemoveRange(scopeStart, _scope.Count - scopeStart);
        _scopeStarts.RemoveAt(_scopeStarts.Count - 1);

        return result;
    }

    private IReadOnlyList<BoundColumn> BindDelete(DeleteStatement statement)
    {
        var scopeStart = _scope.Count;
        _scopeStarts.Add(scopeStart);
        var target = AddTable(statement.Table);
        foreach (var table in statement.Using)
        {
            AddTable(table);
        }
        if (statement.Where != null)
        {
            ValidateAggregatePlacement(statement.Where, AggregateContext.Where, 0);
            BindBooleanContext(statement.Where, "WHERE");
        }

        var result = BindSelectItems(statement.Returning, target);
        _scope.RemoveRange(scopeStart, _scope.Count - scopeStart);
        _scopeStarts.RemoveAt(_scopeStarts.Count - 1);
        return result;
    }

    private IReadOnlyList<BoundColumn> BindTruncate(TruncateStatement statement)
    {
        foreach (var table in statement.Tables)
        {
            ResolveTable(table);
        }

        return Array.Empty<BoundColumn>();
    }

    private void BindOnConflict(InsertStatement statement, Table? table)
    {
        var conflict = statement.OnConflict;
        if (conflict == null)
        {
            return;
        }

        if (table != null)
        {
            foreach (var identifier in conflict.TargetColumns)
            {
                if (FindColumn(table, identifier) == null)
                {
                    Report(
                        "SQL203",
                        $"Unknown conflict target column '{identifier.Name}' on '{statement.Table.Name.Name}'.",
                        identifier.Span);
                }
            }
        }

        if (conflict.TargetWhere != null)
        {
            BindBooleanContext(conflict.TargetWhere, "ON CONFLICT WHERE");
        }

        if (conflict.DoNothing)
        {
            return;
        }

        ScopeTable? excluded = null;
        if (table != null)
        {
            var excludedName = new SqlIdentifier("excluded", false, statement.Table.Name.Span);
            excluded = new ScopeTable(table, excludedName);
            _scope.Add(excluded);
        }

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in conflict.Assignments)
        {
            var key = ReferencedIdentifier(assignment.Column);
            if (!assigned.Add(key))
            {
                Report("SQL219", $"Column '{assignment.Column.Name}' is assigned more than once.", assignment.Column.Span);
            }

            var column = table == null ? null : FindColumn(table, assignment.Column);
            if (column == null)
            {
                if (table != null)
                {
                    Report("SQL203", $"Unknown column '{assignment.Column.Name}' in ON CONFLICT.", assignment.Column.Span);
                }

                BindExpression(assignment.Value);
            }
            else
            {
                BindAssignedExpression(column, assignment.Value);
            }
        }

        if (conflict.UpdateWhere != null)
        {
            BindBooleanContext(conflict.UpdateWhere, "ON CONFLICT DO UPDATE WHERE");
        }

        if (excluded != null)
        {
            _scope.Remove(excluded);
        }
    }

    private void BindAssignedExpression(Column column, Expression expression)
    {
        if (expression is DefaultExpression)
        {
            return;
        }

        ValidateAggregatePlacement(expression, AggregateContext.DataModification, 0);
        var target = ColumnType(column, false, expression.Span);
        var value = BindExpression(expression, target.Type);
        if (!AreCompatible(target, value))
        {
            Report(
                "SQL207",
                $"The value assigned to column '{column.Name}' is not compatible with its SQL type '{column.SqlType}'.",
                expression.Span);
        }
    }

    private IReadOnlyList<BoundColumn> BindNestedStatement(SqlStatement statement, bool preserveOuterScope)
    {
        var savedScope = preserveOuterScope ? null : _scope.ToList();
        var savedStarts = preserveOuterScope ? null : _scopeStarts.ToList();
        if (savedScope != null)
        {
            _scope.Clear();
            _scopeStarts.Clear();
        }

        var scopeStart = _scope.Count;
        var previousHasGroupBy = _hasGroupBy;
        var result = BindStatement(statement);
        if (_scope.Count > scopeStart)
        {
            _scope.RemoveRange(scopeStart, _scope.Count - scopeStart);
        }

        _hasGroupBy = previousHasGroupBy;
        if (savedScope != null)
        {
            _scope.AddRange(savedScope);
            _scopeStarts.AddRange(savedStarts!);
        }

        return result;
    }

    private void ValidateQueryStructure(SelectStatement statement)
    {
        var hasAggregate = false;
        foreach (var item in statement.Items)
        {
            hasAggregate |= ValidateAggregatePlacement(item.Expression, AggregateContext.Select, 0);
        }

        foreach (var join in statement.Joins)
        {
            if (join.On != null)
            {
                ValidateAggregatePlacement(join.On, AggregateContext.JoinOn, 0);
            }
        }

        if (statement.Where != null)
        {
            ValidateAggregatePlacement(statement.Where, AggregateContext.Where, 0);
        }

        foreach (var expression in statement.GroupBy)
        {
            hasAggregate |= ValidateAggregatePlacement(expression, AggregateContext.GroupBy, 0);
        }

        if (statement.Having != null)
        {
            hasAggregate |= ValidateAggregatePlacement(statement.Having, AggregateContext.Having, 0);
        }

        foreach (var order in statement.OrderBy)
        {
            hasAggregate |= ValidateAggregatePlacement(order.Expression, AggregateContext.OrderBy, 0);
        }

        if (statement.GroupBy.Count == 0 && !hasAggregate)
        {
            return;
        }

        foreach (var item in statement.Items)
        {
            if (!IsGroupCompatible(item.Expression, statement.GroupBy))
            {
                Report(
                    "SQL216",
                    "A selected expression must use only grouped columns or aggregate expressions.",
                    item.Expression.Span);
            }
        }

        if (statement.Having != null && !IsGroupCompatible(statement.Having, statement.GroupBy))
        {
            Report(
                "SQL216",
                "A HAVING expression must use only grouped columns or aggregate expressions.",
                statement.Having.Span);
        }

        foreach (var order in statement.OrderBy)
        {
            if (IsOutputAlias(order.Expression, statement.Items))
            {
                continue;
            }

            if (!IsGroupCompatible(order.Expression, statement.GroupBy))
            {
                Report(
                    "SQL216",
                    "An ORDER BY expression must use only grouped columns or aggregate expressions.",
                    order.Expression.Span);
            }
        }
    }

    private bool ValidateAggregatePlacement(Expression expression, AggregateContext context, int aggregateDepth)
    {
        var function = expression as FunctionExpression;
        if (function != null)
        {
            var isAggregate = IsAggregateFunction(function);
            var countsForGrouping = isAggregate && function.Window == null;
            var nextDepth = aggregateDepth;
            if (isAggregate)
            {
                if (aggregateDepth > 0)
                {
                    Report("SQL215", "Aggregate functions cannot contain another aggregate function.", function.Span);
                }

                if (context == AggregateContext.Where || context == AggregateContext.JoinOn ||
                    context == AggregateContext.DataModification)
                {
                    Report(
                        "SQL214",
                        $"Aggregate functions are not allowed in {AggregateContextName(context)}.",
                        function.Span);
                }
                else if (context == AggregateContext.GroupBy)
                {
                    Report("SQL214", "Aggregate functions are not allowed in GROUP BY.", function.Span);
                }

                nextDepth++;
            }

            var found = countsForGrouping;
            foreach (var argument in function.Arguments)
            {
                found |= ValidateAggregatePlacement(argument, context, nextDepth);
            }

            if (function.Filter != null)
            {
                found |= ValidateAggregatePlacement(function.Filter, context, nextDepth);
            }

            if (function.Window != null)
            {
                foreach (var partition in function.Window.PartitionBy)
                {
                    found |= ValidateAggregatePlacement(partition, AggregateContext.OrderBy, 0);
                }

                foreach (var order in function.Window.OrderBy)
                {
                    found |= ValidateAggregatePlacement(order.Expression, AggregateContext.OrderBy, 0);
                }
            }

            return found;
        }

        var unary = expression as UnaryExpression;
        if (unary != null)
        {
            return ValidateAggregatePlacement(unary.Operand, context, aggregateDepth);
        }

        var array = expression as ArrayExpression;
        if (array != null)
        {
            var found = false;
            foreach (var element in array.Elements)
            {
                found |= ValidateAggregatePlacement(element, context, aggregateDepth);
            }

            return found;
        }

        var subscript = expression as ArraySubscriptExpression;
        if (subscript != null)
        {
            return ValidateAggregatePlacement(subscript.Array, context, aggregateDepth) |
                ValidateAggregatePlacement(subscript.Index, context, aggregateDepth);
        }

        var quantified = expression as QuantifiedComparisonExpression;
        if (quantified != null)
        {
            return ValidateAggregatePlacement(quantified.Left, context, aggregateDepth) |
                ValidateAggregatePlacement(quantified.Array, context, aggregateDepth);
        }

        var binary = expression as BinaryExpression;
        if (binary != null)
        {
            return ValidateAggregatePlacement(binary.Left, context, aggregateDepth) |
                ValidateAggregatePlacement(binary.Right, context, aggregateDepth);
        }

        var isNull = expression as IsNullExpression;
        if (isNull != null)
        {
            return ValidateAggregatePlacement(isNull.Operand, context, aggregateDepth);
        }

        var isTruth = expression as IsTruthExpression;
        if (isTruth != null)
        {
            return ValidateAggregatePlacement(isTruth.Operand, context, aggregateDepth);
        }

        var inExpression = expression as InExpression;
        if (inExpression != null)
        {
            var found = ValidateAggregatePlacement(inExpression.Operand, context, aggregateDepth);
            foreach (var value in inExpression.Values)
            {
                found |= ValidateAggregatePlacement(value, context, aggregateDepth);
            }

            return found;
        }

        var between = expression as BetweenExpression;
        if (between != null)
        {
            return ValidateAggregatePlacement(between.Operand, context, aggregateDepth) |
                ValidateAggregatePlacement(between.Lower, context, aggregateDepth) |
                ValidateAggregatePlacement(between.Upper, context, aggregateDepth);
        }

        var cast = expression as CastExpression;
        if (cast != null)
        {
            return ValidateAggregatePlacement(cast.Operand, context, aggregateDepth);
        }

        var caseExpression = expression as CaseExpression;
        if (caseExpression != null)
        {
            var found = caseExpression.Operand != null &&
                ValidateAggregatePlacement(caseExpression.Operand, context, aggregateDepth);
            foreach (var clause in caseExpression.Clauses)
            {
                found |= ValidateAggregatePlacement(clause.Condition, context, aggregateDepth);
                found |= ValidateAggregatePlacement(clause.Result, context, aggregateDepth);
            }

            if (caseExpression.ElseExpression != null)
            {
                found |= ValidateAggregatePlacement(caseExpression.ElseExpression, context, aggregateDepth);
            }

            return found;
        }

        return false;
    }

    private bool IsGroupCompatible(Expression expression, IReadOnlyList<Expression> groupBy)
    {
        foreach (var groupExpression in groupBy)
        {
            if (ExpressionsEquivalent(expression, groupExpression))
            {
                return true;
            }
        }

        var function = expression as FunctionExpression;
        if (function != null)
        {
            if (IsAggregateFunction(function) && function.Window == null)
            {
                return true;
            }

            foreach (var argument in function.Arguments)
            {
                if (!IsGroupCompatible(argument, groupBy))
                {
                    return false;
                }
            }

            return true;
        }

        var column = expression as ColumnExpression;
        if (column != null)
        {
            return groupBy.Any(item =>
            {
                var groupColumn = item as ColumnExpression;
                return groupColumn != null && ColumnsEquivalent(column, groupColumn);
            });
        }

        if (expression is LiteralExpression || expression is ParameterExpression)
        {
            return true;
        }

        var array = expression as ArrayExpression;
        if (array != null)
        {
            return array.Elements.All(element => IsGroupCompatible(element, groupBy));
        }

        var subscript = expression as ArraySubscriptExpression;
        if (subscript != null)
        {
            return IsGroupCompatible(subscript.Array, groupBy) &&
                IsGroupCompatible(subscript.Index, groupBy);
        }

        var quantified = expression as QuantifiedComparisonExpression;
        if (quantified != null)
        {
            return IsGroupCompatible(quantified.Left, groupBy) &&
                IsGroupCompatible(quantified.Array, groupBy);
        }

        if (expression is StarExpression)
        {
            return false;
        }

        var unary = expression as UnaryExpression;
        if (unary != null)
        {
            return IsGroupCompatible(unary.Operand, groupBy);
        }

        var binary = expression as BinaryExpression;
        if (binary != null)
        {
            return IsGroupCompatible(binary.Left, groupBy) && IsGroupCompatible(binary.Right, groupBy);
        }

        var isNull = expression as IsNullExpression;
        if (isNull != null)
        {
            return IsGroupCompatible(isNull.Operand, groupBy);
        }

        var isTruth = expression as IsTruthExpression;
        if (isTruth != null)
        {
            return IsGroupCompatible(isTruth.Operand, groupBy);
        }

        var inExpression = expression as InExpression;
        if (inExpression != null)
        {
            return IsGroupCompatible(inExpression.Operand, groupBy) &&
                inExpression.Values.All(value => IsGroupCompatible(value, groupBy));
        }

        var between = expression as BetweenExpression;
        if (between != null)
        {
            return IsGroupCompatible(between.Operand, groupBy) &&
                IsGroupCompatible(between.Lower, groupBy) &&
                IsGroupCompatible(between.Upper, groupBy);
        }

        var cast = expression as CastExpression;
        if (cast != null)
        {
            return IsGroupCompatible(cast.Operand, groupBy);
        }

        var caseExpression = expression as CaseExpression;
        if (caseExpression != null)
        {
            if (caseExpression.Operand != null && !IsGroupCompatible(caseExpression.Operand, groupBy))
            {
                return false;
            }

            foreach (var clause in caseExpression.Clauses)
            {
                if (!IsGroupCompatible(clause.Condition, groupBy) ||
                    !IsGroupCompatible(clause.Result, groupBy))
                {
                    return false;
                }
            }

            return caseExpression.ElseExpression == null ||
                IsGroupCompatible(caseExpression.ElseExpression, groupBy);
        }

        return true;
    }

    private bool IsOutputAlias(Expression expression, IReadOnlyList<SelectItem> items)
    {
        var column = expression as ColumnExpression;
        if (column == null || column.Qualifier != null)
        {
            return false;
        }

        foreach (var item in items)
        {
            if (item.Alias != null)
            {
                if (IdentifiersEquivalent(column.Name, item.Alias))
                {
                    return true;
                }

                continue;
            }

            var name = DefaultOutputName(item.Expression);
            if (_syntax.AreIdentifiersEqual(column.Name.Name, column.Name.IsQuoted, name))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsAggregateFunction(FunctionExpression expression)
    {
        if (expression.Name.IsQuoted)
        {
            return false;
        }

        var name = expression.Name.Name.ToLowerInvariant();
        return name == "count" || name == "sum" || name == "avg" || name == "min" || name == "max";
    }

    private static string AggregateContextName(AggregateContext context)
    {
        switch (context)
        {
            case AggregateContext.Where: return "WHERE";
            case AggregateContext.JoinOn: return "JOIN ON";
            case AggregateContext.GroupBy: return "GROUP BY";
            case AggregateContext.DataModification: return "a data modification expression";
            default: return "this clause";
        }
    }

    private bool ExpressionsEquivalent(Expression left, Expression right)
    {
        if (left.GetType() != right.GetType())
        {
            return false;
        }

        var leftColumn = left as ColumnExpression;
        var rightColumn = right as ColumnExpression;
        if (leftColumn != null && rightColumn != null)
        {
            return ColumnsEquivalent(leftColumn, rightColumn);
        }

        var leftStar = left as StarExpression;
        var rightStar = right as StarExpression;
        if (leftStar != null && rightStar != null)
        {
            return (leftStar.Qualifier == null && rightStar.Qualifier == null) ||
                leftStar.Qualifier != null && rightStar.Qualifier != null &&
                IdentifiersEquivalent(leftStar.Qualifier, rightStar.Qualifier);
        }

        var leftLiteral = left as LiteralExpression;
        var rightLiteral = right as LiteralExpression;
        if (leftLiteral != null && rightLiteral != null)
        {
            return leftLiteral.Kind == rightLiteral.Kind && Equals(leftLiteral.Value, rightLiteral.Value);
        }

        var leftParameter = left as ParameterExpression;
        var rightParameter = right as ParameterExpression;
        if (leftParameter != null && rightParameter != null)
        {
            return string.Equals(leftParameter.Name, rightParameter.Name, StringComparison.OrdinalIgnoreCase);
        }

        var leftArray = left as ArrayExpression;
        var rightArray = right as ArrayExpression;
        if (leftArray != null && rightArray != null)
        {
            return leftArray.Elements.Count == rightArray.Elements.Count &&
                leftArray.Elements.Zip(rightArray.Elements, ExpressionsEquivalent).All(item => item);
        }

        var leftSubscript = left as ArraySubscriptExpression;
        var rightSubscript = right as ArraySubscriptExpression;
        if (leftSubscript != null && rightSubscript != null)
        {
            return ExpressionsEquivalent(leftSubscript.Array, rightSubscript.Array) &&
                ExpressionsEquivalent(leftSubscript.Index, rightSubscript.Index);
        }

        var leftQuantified = left as QuantifiedComparisonExpression;
        var rightQuantified = right as QuantifiedComparisonExpression;
        if (leftQuantified != null && rightQuantified != null)
        {
            return leftQuantified.Operator == rightQuantified.Operator &&
                leftQuantified.Quantifier == rightQuantified.Quantifier &&
                ExpressionsEquivalent(leftQuantified.Left, rightQuantified.Left) &&
                ExpressionsEquivalent(leftQuantified.Array, rightQuantified.Array);
        }

        var leftUnary = left as UnaryExpression;
        var rightUnary = right as UnaryExpression;
        if (leftUnary != null && rightUnary != null)
        {
            return leftUnary.Operator == rightUnary.Operator &&
                ExpressionsEquivalent(leftUnary.Operand, rightUnary.Operand);
        }

        var leftBinary = left as BinaryExpression;
        var rightBinary = right as BinaryExpression;
        if (leftBinary != null && rightBinary != null)
        {
            return leftBinary.Operator == rightBinary.Operator &&
                ExpressionsEquivalent(leftBinary.Left, rightBinary.Left) &&
                ExpressionsEquivalent(leftBinary.Right, rightBinary.Right);
        }

        var leftIsNull = left as IsNullExpression;
        var rightIsNull = right as IsNullExpression;
        if (leftIsNull != null && rightIsNull != null)
        {
            return leftIsNull.Negated == rightIsNull.Negated &&
                ExpressionsEquivalent(leftIsNull.Operand, rightIsNull.Operand);
        }

        var leftIn = left as InExpression;
        var rightIn = right as InExpression;
        if (leftIn != null && rightIn != null)
        {
            return leftIn.Negated == rightIn.Negated &&
                ExpressionsEquivalent(leftIn.Operand, rightIn.Operand) &&
                leftIn.Values.Count == rightIn.Values.Count &&
                leftIn.Values.Zip(rightIn.Values, ExpressionsEquivalent).All(item => item);
        }

        var leftBetween = left as BetweenExpression;
        var rightBetween = right as BetweenExpression;
        if (leftBetween != null && rightBetween != null)
        {
            return leftBetween.Negated == rightBetween.Negated &&
                ExpressionsEquivalent(leftBetween.Operand, rightBetween.Operand) &&
                ExpressionsEquivalent(leftBetween.Lower, rightBetween.Lower) &&
                ExpressionsEquivalent(leftBetween.Upper, rightBetween.Upper);
        }

        var leftFunction = left as FunctionExpression;
        var rightFunction = right as FunctionExpression;
        if (leftFunction != null && rightFunction != null)
        {
            return IdentifiersEquivalent(leftFunction.Name, rightFunction.Name) &&
                leftFunction.Arguments.Count == rightFunction.Arguments.Count &&
                leftFunction.Arguments.Zip(rightFunction.Arguments, ExpressionsEquivalent).All(item => item);
        }

        var leftCast = left as CastExpression;
        var rightCast = right as CastExpression;
        if (leftCast != null && rightCast != null)
        {
            return string.Equals(leftCast.SqlType, rightCast.SqlType, StringComparison.OrdinalIgnoreCase) &&
                ExpressionsEquivalent(leftCast.Operand, rightCast.Operand);
        }

        var leftCase = left as CaseExpression;
        var rightCase = right as CaseExpression;
        if (leftCase != null && rightCase != null)
        {
            if (leftCase.Operand == null != (rightCase.Operand == null) ||
                leftCase.Clauses.Count != rightCase.Clauses.Count)
            {
                return false;
            }

            if (leftCase.Operand != null && !ExpressionsEquivalent(leftCase.Operand, rightCase.Operand!))
            {
                return false;
            }

            for (var index = 0; index < leftCase.Clauses.Count; index++)
            {
                if (!ExpressionsEquivalent(leftCase.Clauses[index].Condition, rightCase.Clauses[index].Condition) ||
                    !ExpressionsEquivalent(leftCase.Clauses[index].Result, rightCase.Clauses[index].Result))
                {
                    return false;
                }
            }

            return leftCase.ElseExpression == null && rightCase.ElseExpression == null ||
                leftCase.ElseExpression != null && rightCase.ElseExpression != null &&
                ExpressionsEquivalent(leftCase.ElseExpression, rightCase.ElseExpression);
        }

        return false;
    }

    private bool ColumnsEquivalent(ColumnExpression left, ColumnExpression right)
    {
        if (!IdentifiersEquivalent(left.Name, right.Name))
        {
            return false;
        }

        return left.Qualifier == null || right.Qualifier == null ||
            IdentifiersEquivalent(left.Qualifier, right.Qualifier);
    }

    private bool IdentifiersEquivalent(SqlIdentifier left, SqlIdentifier right)
        => _syntax.AreIdentifiersEquivalent(left.Name, left.IsQuoted, right.Name, right.IsQuoted);

    private ScopeTable AddTable(TableReference reference)
    {
        var effectiveName = reference.Alias ?? reference.Name;
        var localStart = _scopeStarts.Count == 0 ? 0 : _scopeStarts[_scopeStarts.Count - 1];
        if (_scope.Skip(localStart).Any(item => IdentifiersEquivalent(item.EffectiveName, effectiveName)))
        {
            Report("SQL201", $"Table name or alias '{effectiveName.Name}' is already in scope.", effectiveName.Span);
        }

        ScopeTable result;
        if (reference.Function != null)
        {
            var columns = BindTableFunction(reference).ToList();
            ApplyTableColumnAliases(reference, effectiveName, columns);
            result = new ScopeTable(columns, effectiveName);
        }
        else if (reference.Subquery != null)
        {
            var columns = BindNestedStatement(reference.Subquery, reference.Lateral).ToList();
            ApplyTableColumnAliases(reference, effectiveName, columns);

            result = new ScopeTable(columns, effectiveName);
        }
        else
        {
            var commonTable = reference.Schema == null ? FindCommonTable(reference.Name) : null;
            var resolved = commonTable != null
                ? new ScopeTable(commonTable.Columns, effectiveName)
                : new ScopeTable(ResolveTable(reference), effectiveName);
            if (reference.ColumnAliases.Count == 0)
            {
                result = resolved;
            }
            else
            {
                var columns = ScopeColumns(resolved).ToList();
                ApplyTableColumnAliases(reference, effectiveName, columns);
                result = new ScopeTable(columns, effectiveName);
            }
        }

        _scope.Add(result);
        return result;
    }

    private IReadOnlyList<BoundColumn> BindTableFunction(TableReference reference)
    {
        var function = reference.Function!;
        var name = function.Name.IsQuoted
            ? function.Name.Name
            : function.Name.Name.ToLowerInvariant();
        if (!(_types.Mapper is PostgreSqlTypeMapper))
        {
            foreach (var argument in function.Arguments)
            {
                BindExpression(argument);
            }

            Report("SQL206", $"Unknown table function '{function.Name.Name}'.", function.Name.Span);
            return Array.Empty<BoundColumn>();
        }

        if (name == "unnest")
        {
            if (function.Arguments.Count == 0)
            {
                Report("SQL212", "UNNEST requires at least one array argument.", function.Span);
                return Array.Empty<BoundColumn>();
            }

            var columns = new List<BoundColumn>();
            for (var index = 0; index < function.Arguments.Count; index++)
            {
                var argument = BindExpression(function.Arguments[index]);
                if (argument.IsKnown && !argument.IsArray)
                {
                    Report("SQL207", "UNNEST requires array arguments.", function.Arguments[index].Span);
                    continue;
                }

                var columnName = function.Arguments.Count == 1 && reference.Alias != null &&
                    reference.ColumnAliases.Count == 0
                    ? reference.Alias.Name
                    : "unnest";
                var elementType = argument.IsKnown
                    ? argument.Type.ElementType
                    : new SqlTypeShape(SqlValueKind.Unknown);
                columns.Add(new BoundColumn(
                    columnName,
                    new TypeInfo(elementType, true, false, argument.ParameterName),
                    function.Arguments[index].Span));
            }

            return columns;
        }

        if (name == "generate_subscripts")
        {
            if (function.Arguments.Count < 2 || function.Arguments.Count > 3)
            {
                foreach (var argument in function.Arguments) BindExpression(argument);
                Report("SQL212", "GENERATE_SUBSCRIPTS expects two or three arguments.", function.Span);
                return Array.Empty<BoundColumn>();
            }

            var array = BindExpression(function.Arguments[0]);
            if (array.IsKnown && !array.IsArray)
            {
                Report("SQL207", "GENERATE_SUBSCRIPTS requires an array first argument.", function.Arguments[0].Span);
            }

            BindIntegerContext(function.Arguments[1], "GENERATE_SUBSCRIPTS dimension");
            if (function.Arguments.Count == 3)
            {
                var reverse = BindExpression(function.Arguments[2], SqlValueKind.Bool);
                EnsureKind(reverse, SqlValueKind.Bool, function.Arguments[2].Span, "GENERATE_SUBSCRIPTS reverse must be boolean.");
            }

            var columnName = reference.Alias != null && reference.ColumnAliases.Count == 0
                ? reference.Alias.Name
                : "generate_subscripts";
            return new[]
            {
                new BoundColumn(columnName, new TypeInfo(SqlValueKind.Int32, false), function.Span),
            };
        }

        foreach (var argument in function.Arguments)
        {
            BindExpression(argument);
        }

        Report("SQL206", $"Unknown table function '{function.Name.Name}'.", function.Name.Span);
        return Array.Empty<BoundColumn>();
    }

    private void ApplyTableColumnAliases(
        TableReference reference,
        SqlIdentifier effectiveName,
        List<BoundColumn> columns)
    {
        if (reference.ColumnAliases.Count > columns.Count)
        {
            Report(
                "SQL219",
                $"Table '{effectiveName.Name}' declares {reference.ColumnAliases.Count} column alias(es), but has {columns.Count} column(s).",
                effectiveName.Span);
        }

        for (var index = 0; index < columns.Count && index < reference.ColumnAliases.Count; index++)
        {
            columns[index] = new BoundColumn(
                reference.ColumnAliases[index].Name,
                columns[index].Type,
                reference.ColumnAliases[index].Span);
        }
    }

    private CommonTableRelation? FindCommonTable(SqlIdentifier name)
    {
        for (var index = _commonTables.Count - 1; index >= 0; index--)
        {
            if (IdentifiersEquivalent(_commonTables[index].Name, name))
            {
                return _commonTables[index];
            }
        }

        return null;
    }

    private Table? ResolveTable(TableReference reference)
    {
        var table = FindSchemaTable(reference, out var ambiguous);
        if (table == null && !ambiguous)
        {
            Report("SQL200", $"Unknown table '{TableReferenceName(reference)}'.", reference.Name.Span);
        }

        return table;
    }

    private Table? FindSchemaTable(TableReference reference, out bool ambiguous)
    {
        var matches = new List<Table>();
        foreach (var table in _schema.Tables)
        {
            if (!SchemaIdentifierMatches(reference.Name, table.Name))
            {
                continue;
            }

            if (reference.Schema != null &&
                (table.Schema == null || !SchemaIdentifierMatches(reference.Schema, table.Schema)))
            {
                continue;
            }

            matches.Add(table);
        }

        ambiguous = matches.Count > 1;
        if (ambiguous)
        {
            Report(
                "SQL218",
                $"Table '{TableReferenceName(reference)}' is ambiguous; qualify it with a schema or alias.",
                reference.Name.Span);
            return null;
        }

        return matches.Count == 0 ? null : matches[0];
    }

    private static string TableReferenceName(TableReference reference) =>
        reference.Schema == null
            ? reference.Name.Name
            : reference.Schema.Name + "." + reference.Name.Name;

    private IReadOnlyList<BoundColumn> BindSelectItems(
        IReadOnlyList<SelectItem> items,
        ScopeTable? unqualifiedStarTable = null,
        IReadOnlyList<BoundColumn>? unqualifiedStarColumns = null)
    {
        var result = new List<BoundColumn>();
        foreach (var item in items)
        {
            var star = item.Expression as StarExpression;
            if (star != null)
            {
                if (item.Alias != null)
                {
                    Report("SQL213", "A wildcard select item cannot have an alias.", item.Alias.Span);
                }

                if (star.Qualifier == null && unqualifiedStarTable != null)
                {
                    ExpandTable(unqualifiedStarTable, star.Span, result);
                }
                else if (star.Qualifier == null && unqualifiedStarColumns != null)
                {
                    foreach (var column in unqualifiedStarColumns)
                    {
                        result.Add(new BoundColumn(column.Name, column.Type, star.Span));
                    }
                }
                else
                {
                    ExpandStar(star, result);
                }
                continue;
            }

            var type = BindExpression(item.Expression);
            var name = item.Alias?.Name ?? DefaultOutputName(item.Expression);
            result.Add(new BoundColumn(name, type, item.Expression.Span));
        }

        return result;
    }

    private void ExpandStar(StarExpression star, List<BoundColumn> result)
    {
        if (_scope.Count == 0)
        {
            Report("SQL213", "A wildcard requires a table in FROM.", star.Span);
            return;
        }

        if (star.Qualifier != null)
        {
            var table = FindScopeTable(star.Qualifier, true);
            if (table != null)
            {
                ExpandTable(table, star.Span, result);
            }

            return;
        }

        foreach (var table in _scope)
        {
            ExpandTable(table, star.Span, result);
        }
    }

    private void ExpandTable(ScopeTable scopeTable, SourceSpan span, List<BoundColumn> result)
    {
        if (scopeTable.DerivedColumns != null)
        {
            foreach (var column in scopeTable.DerivedColumns)
            {
                result.Add(new BoundColumn(
                    column.Name,
                    column.Type.WithNullable(column.Type.Nullable || scopeTable.ForcedNullable),
                    span));
            }

            return;
        }

        if (scopeTable.Table == null)
        {
            return;
        }

        foreach (var column in scopeTable.Table.Columns)
        {
            if (!_types.TryMapType(column.SqlType, out var type))
            {
                Report("SQL205", $"Unsupported SQL type '{column.SqlType}' on column '{column.Name}'.", span);
                continue;
            }

            result.Add(new BoundColumn(
                column.Name,
                new TypeInfo(type, column.IsNullable || scopeTable.ForcedNullable),
                span));
        }
    }

    private TypeInfo BindExpression(Expression expression, SqlValueKind expected = SqlValueKind.Unknown) =>
        BindExpression(expression, new SqlTypeShape(expected));

    private TypeInfo BindExpression(Expression expression, SqlTypeShape expected)
    {
        var parameter = expression as ParameterExpression;
        if (parameter != null)
        {
            return BindParameter(parameter, expected);
        }

        if (expression is DefaultExpression)
        {
            Report("SQL219", "DEFAULT is only valid as a direct INSERT or UPDATE value.", expression.Span);
            return ErrorType();
        }

        var literal = expression as LiteralExpression;
        if (literal != null) return BindLiteral(literal);

        var array = expression as ArrayExpression;
        if (array != null) return BindArray(array, expected);

        var subscript = expression as ArraySubscriptExpression;
        if (subscript != null) return BindArraySubscript(subscript);

        var quantified = expression as QuantifiedComparisonExpression;
        if (quantified != null) return BindQuantifiedComparison(quantified);

        var column = expression as ColumnExpression;
        if (column != null) return BindColumn(column);

        var unary = expression as UnaryExpression;
        if (unary != null) return BindUnary(unary);

        var binary = expression as BinaryExpression;
        if (binary != null) return BindBinary(binary);

        var isNull = expression as IsNullExpression;
        if (isNull != null)
        {
            BindExpression(isNull.Operand);
            return new TypeInfo(SqlValueKind.Bool, false);
        }

        var isTruth = expression as IsTruthExpression;
        if (isTruth != null)
        {
            var operand = BindExpression(isTruth.Operand, SqlValueKind.Bool);
            EnsureKind(operand, SqlValueKind.Bool, isTruth.Operand.Span, "IS TRUE/FALSE/UNKNOWN requires a boolean operand.");
            return operand.IsError ? operand : new TypeInfo(SqlValueKind.Bool, false);
        }

        var inExpression = expression as InExpression;
        if (inExpression != null) return BindIn(inExpression);

        var between = expression as BetweenExpression;
        if (between != null) return BindBetween(between);

        var function = expression as FunctionExpression;
        if (function != null) return BindFunction(function);

        var cast = expression as CastExpression;
        if (cast != null) return BindCast(cast);

        var caseExpression = expression as CaseExpression;
        if (caseExpression != null) return BindCase(caseExpression);

        var subquery = expression as SubqueryExpression;
        if (subquery != null)
        {
            var columns = BindNestedStatement(subquery.Query, true);
            if (columns.Count != 1)
            {
                Report("SQL222", "A scalar subquery must return exactly one column.", subquery.Span);
                return ErrorType();
            }

            var type = columns[0].Type;
            return type.WithNullable(true);
        }

        var exists = expression as ExistsExpression;
        if (exists != null)
        {
            BindNestedStatement(exists.Query, true);
            return new TypeInfo(SqlValueKind.Bool, false);
        }

        if (expression is StarExpression)
        {
            Report("SQL213", "A wildcard is only valid as a select item or COUNT argument.", expression.Span);
            return ErrorType();
        }

        Report("SQL999", "The expression could not be analyzed.", expression.Span);
        return ErrorType();
    }

    private TypeInfo BindLiteral(LiteralExpression expression)
    {
        switch (expression.Kind)
        {
            case LiteralKind.Integer:
                var text = (string)(expression.Value ?? string.Empty);
                int int32Value;
                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int32Value))
                {
                    return new TypeInfo(SqlValueKind.Int32, false);
                }

                long int64Value;
                return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int64Value)
                    ? new TypeInfo(SqlValueKind.Int64, false)
                    : new TypeInfo(SqlValueKind.Decimal, false);
            case LiteralKind.Decimal: return new TypeInfo(SqlValueKind.Decimal, false);
            case LiteralKind.String: return new TypeInfo(SqlValueKind.String, false);
            case LiteralKind.Boolean: return new TypeInfo(SqlValueKind.Bool, false);
            case LiteralKind.Date: return new TypeInfo(SqlValueKind.DateOnly, false);
            case LiteralKind.Time: return new TypeInfo(SqlValueKind.TimeOnly, false);
            case LiteralKind.Timestamp: return new TypeInfo(SqlValueKind.DateTime, false);
            case LiteralKind.TimestampWithTimeZone: return new TypeInfo(SqlValueKind.DateTimeOffset, false);
            case LiteralKind.Interval: return new TypeInfo(SqlValueKind.Interval, false);
            case LiteralKind.Null: return new TypeInfo(SqlValueKind.Unknown, true, true);
            default: return ErrorType();
        }
    }

    private TypeInfo BindArray(ArrayExpression expression, SqlTypeShape expected)
    {
        var expectedElement = expected.IsArray
            ? expected.ElementType
            : new SqlTypeShape(SqlValueKind.Unknown);
        var elements = new List<TypeInfo>();
        var elementType = expectedElement;
        foreach (var element in expression.Elements)
        {
            var type = BindExpression(element, expectedElement);
            elements.Add(type);
            if (type.IsArray)
            {
                Report("SQL207", "Nested ARRAY constructors are not supported.", element.Span);
                return ErrorType();
            }

            if (!_types.TryUnify(elementType, type.Type, out elementType))
            {
                Report("SQL207", "ARRAY elements must have compatible types.", expression.Span);
                return ErrorType();
            }
        }

        if (!elementType.IsKnown)
        {
            return new TypeInfo(new SqlTypeShape(SqlValueKind.Unknown, true), false);
        }

        for (var index = 0; index < expression.Elements.Count; index++)
        {
            ApplyExpected(expression.Elements[index], elementType);
            elements[index] = Refresh(elements[index]);
        }

        return new TypeInfo(elementType.ToArray(), false);
    }

    private TypeInfo BindArraySubscript(ArraySubscriptExpression expression)
    {
        var array = BindExpression(expression.Array);
        var index = BindExpression(expression.Index, SqlValueKind.Int32);
        if (index.IsArray || index.IsKnown && !_types.IsInteger(index.Kind))
        {
            Report("SQL207", "An array subscript must be an integer.", expression.Index.Span);
            return ErrorType();
        }

        if (array.IsKnown && !array.IsArray)
        {
            Report("SQL207", "Array subscripting requires an array value.", expression.Array.Span);
            return ErrorType();
        }

        return array.IsKnown
            ? new TypeInfo(array.Type.ElementType, true)
            : new TypeInfo(SqlValueKind.Unknown, true, false, array.ParameterName);
    }

    private TypeInfo BindQuantifiedComparison(QuantifiedComparisonExpression expression)
    {
        var left = BindExpression(expression.Left);
        var expectedArray = left.IsKnown
            ? left.Type.ToArray()
            : new SqlTypeShape(SqlValueKind.Unknown, true);
        var array = BindExpression(expression.Array, expectedArray);
        if (array.IsKnown && !array.IsArray)
        {
            Report("SQL207", $"{expression.Quantifier.ToString().ToUpperInvariant()} requires an array expression.", expression.Array.Span);
            return ErrorType();
        }

        if (array.IsKnown)
        {
            ApplyExpected(expression.Left, array.Type.ElementType);
            left = Refresh(left);
        }

        if (left.IsKnown && array.IsKnown && !AreCompatible(left, new TypeInfo(array.Type.ElementType, array.Nullable)))
        {
            Report("SQL207", "The quantified comparison uses incompatible element types.", expression.Span);
            return ErrorType();
        }

        // PostgreSQL arrays can contain null elements even when the array itself is not null.
        // A strict ANY/ALL comparison can therefore produce null when no decisive match exists.
        return new TypeInfo(SqlValueKind.Bool, true);
    }

    private TypeInfo BindParameter(ParameterExpression expression, SqlTypeShape expected)
    {
        ParameterState state;
        if (!_parameters.TryGetValue(expression.Name, out state!))
        {
            state = new ParameterState(expression.Name, expression.Span);
            _parameters.Add(expression.Name, state);
            _parameterOrder.Add(state);
        }

        if (expected.Kind != SqlValueKind.Unknown && expected.Kind != SqlValueKind.Error)
        {
            Constrain(state, expected, expression.Span);
        }

        return state.Type.Kind == SqlValueKind.Unknown
            ? new TypeInfo(SqlValueKind.Unknown, false, false, state.Name)
            : new TypeInfo(state.Type, false, false, state.Name);
    }

    private void Constrain(ParameterState state, SqlTypeShape type, SourceSpan span)
    {
        if (state.Type.Kind == SqlValueKind.Unknown)
        {
            state.Type = type;
            return;
        }

        if (!state.Type.Equals(type))
        {
            Report(
                "SQL210",
                $"Parameter '{state.Name}' is used as both {_types.ToClrName(state.Type, false)} and {_types.ToClrName(type, false)}.",
                span);
        }
    }

    private TypeInfo BindColumn(ColumnExpression expression)
    {
        if (expression.Qualifier != null)
        {
            var scopeTable = FindScopeTable(expression.Qualifier, true);
            if (scopeTable == null)
            {
                return ErrorType();
            }

            if (scopeTable.DerivedColumns != null)
            {
                var derived = FindBoundColumn(scopeTable.DerivedColumns, expression.Name);
                if (derived == null)
                {
                    Report(
                        "SQL203",
                        $"Unknown column '{expression.Name.Name}' on '{expression.Qualifier.Name}'.",
                        expression.Name.Span);
                    return ErrorType();
                }

                return derived.Type.WithNullable(derived.Type.Nullable || scopeTable.ForcedNullable);
            }

            if (scopeTable.Table == null)
            {
                return ErrorType();
            }

            var column = FindColumn(scopeTable.Table, expression.Name);
            if (column == null)
            {
                Report(
                    "SQL203",
                    $"Unknown column '{expression.Name.Name}' on '{expression.Qualifier.Name}'.",
                    expression.Name.Span);
                return ErrorType();
            }

            return ColumnType(column, scopeTable.ForcedNullable, expression.Name.Span);
        }

        var frameCount = _scopeStarts.Count == 0 ? 1 : _scopeStarts.Count;
        for (var frame = frameCount - 1; frame >= 0; frame--)
        {
            var matches = new List<ColumnMatch>();
            var start = _scopeStarts.Count == 0 ? 0 : _scopeStarts[frame];
            var end = frame + 1 < _scopeStarts.Count ? _scopeStarts[frame + 1] : _scope.Count;
            for (var scopeIndex = start; scopeIndex < end; scopeIndex++)
            {
                var scopeTable = _scope[scopeIndex];
                if (scopeTable.DerivedColumns != null)
                {
                    var derived = FindBoundColumn(scopeTable.DerivedColumns, expression.Name);
                    if (derived != null)
                    {
                        matches.Add(new ColumnMatch(
                            derived.Type.WithNullable(derived.Type.Nullable || scopeTable.ForcedNullable)));
                    }

                    continue;
                }

                if (scopeTable.Table == null)
                {
                    continue;
                }

                var column = FindColumn(scopeTable.Table, expression.Name);
                if (column != null)
                {
                    matches.Add(new ColumnMatch(ColumnType(column, scopeTable.ForcedNullable, expression.Name.Span)));
                }
            }

            if (matches.Count == 1)
            {
                return matches[0].Type;
            }

            if (matches.Count > 1)
            {
                Report("SQL204", $"Column '{expression.Name.Name}' is ambiguous.", expression.Name.Span);
                return ErrorType();
            }
        }

        Report("SQL203", $"Unknown column '{expression.Name.Name}'.", expression.Name.Span);
        return ErrorType();
    }

    private BoundColumn? FindBoundColumn(IReadOnlyList<BoundColumn> columns, SqlIdentifier identifier)
    {
        BoundColumn? result = null;
        foreach (var column in columns)
        {
            if (!_syntax.AreIdentifiersEqual(identifier.Name, identifier.IsQuoted, column.Name))
            {
                continue;
            }

            if (result != null)
            {
                Report("SQL204", $"Column '{identifier.Name}' is ambiguous.", identifier.Span);
                return null;
            }

            result = column;
        }

        return result;
    }

    private TypeInfo ColumnType(Column column, bool forcedNullable, SourceSpan span)
    {
        if (!_types.TryMapType(column.SqlType, out var type))
        {
            Report("SQL205", $"Unsupported SQL type '{column.SqlType}' on column '{column.Name}'.", span);
            return ErrorType();
        }

        return new TypeInfo(type, column.IsNullable || forcedNullable);
    }

    private TypeInfo BindUnary(UnaryExpression expression)
    {
        if (expression.Operator == "NOT")
        {
            var operand = BindExpression(expression.Operand, SqlValueKind.Bool);
            EnsureKind(operand, SqlValueKind.Bool, expression.Operand.Span, "NOT requires a boolean operand.");
            return operand.IsError ? operand : new TypeInfo(SqlValueKind.Bool, operand.Nullable || operand.IsNullLiteral);
        }

        var numeric = BindExpression(expression.Operand);
        if (numeric.IsKnown && (numeric.IsArray || !_types.IsNumeric(numeric.Kind)))
        {
            Report("SQL207", $"Unary '{expression.Operator}' requires a numeric operand.", expression.Span);
            return ErrorType();
        }

        return numeric;
    }

    private TypeInfo BindBinary(BinaryExpression expression)
    {
        switch (expression.Operator)
        {
            case "AND":
            case "OR": return BindLogical(expression);
            case "=":
            case "<>":
            case "<":
            case "<=":
            case ">":
            case ">=": return BindComparison(expression);
            case "LIKE":
            case "NOT LIKE":
            case "ILIKE":
            case "NOT ILIKE":
            case "~":
            case "~*":
            case "!~":
            case "!~*": return BindLike(expression);
            case "IS DISTINCT FROM":
            case "IS NOT DISTINCT FROM": return BindDistinctComparison(expression);
            case "@>":
            case "<@":
            case "&&": return BindContainment(expression);
            case "->":
            case "#>": return BindJsonAccess(expression, false);
            case "->>":
            case "#>>": return BindJsonAccess(expression, true);
            case "||": return BindConcat(expression);
            case "+":
            case "-":
            case "*":
            case "/":
            case "%":
            case "^": return BindArithmetic(expression);
            default:
                Report("SQL999", $"Unknown operator '{expression.Operator}'.", expression.Span);
                return ErrorType();
        }
    }

    private TypeInfo BindLogical(BinaryExpression expression)
    {
        var left = BindExpression(expression.Left, SqlValueKind.Bool);
        var right = BindExpression(expression.Right, SqlValueKind.Bool);
        EnsureKind(left, SqlValueKind.Bool, expression.Left.Span, $"{expression.Operator} requires boolean operands.");
        EnsureKind(right, SqlValueKind.Bool, expression.Right.Span, $"{expression.Operator} requires boolean operands.");
        if (left.IsError || right.IsError) return ErrorType();
        return new TypeInfo(SqlValueKind.Bool, IsNullable(left) || IsNullable(right));
    }

    private TypeInfo BindComparison(BinaryExpression expression)
    {
        var left = BindExpression(expression.Left);
        var right = BindExpression(expression.Right);
        InferPair(expression.Left, ref left, expression.Right, ref right);
        if (!AreCompatible(left, right))
        {
            Report("SQL207", $"Operator '{expression.Operator}' cannot compare the supplied types.", expression.Span);
            return ErrorType();
        }

        return new TypeInfo(SqlValueKind.Bool, IsNullable(left) || IsNullable(right));
    }

    private TypeInfo BindDistinctComparison(BinaryExpression expression)
    {
        var compared = BindComparison(expression);
        return compared.IsError ? compared : new TypeInfo(SqlValueKind.Bool, false);
    }

    private TypeInfo BindContainment(BinaryExpression expression)
    {
        var left = BindExpression(expression.Left);
        var right = BindExpression(
            expression.Right,
            left.IsKnown ? left.Type : new SqlTypeShape(SqlValueKind.Unknown));
        InferPair(expression.Left, ref left, expression.Right, ref right);
        if (!AreCompatible(left, right))
        {
            Report("SQL207", $"Operator '{expression.Operator}' requires compatible operands.", expression.Span);
            return ErrorType();
        }

        var arrays = left.IsArray || right.IsArray;
        var json = !arrays && expression.Operator != "&&" &&
            (left.Kind == SqlValueKind.Json || left.Kind == SqlValueKind.JsonBinary || !left.IsKnown) &&
            (right.Kind == SqlValueKind.Json || right.Kind == SqlValueKind.JsonBinary || !right.IsKnown);
        if (!arrays && !json)
        {
            Report("SQL207", $"Operator '{expression.Operator}' requires array operands or compatible JSON operands.", expression.Span);
            return ErrorType();
        }

        return new TypeInfo(SqlValueKind.Bool, IsNullable(left) || IsNullable(right));
    }

    private TypeInfo BindJsonAccess(BinaryExpression expression, bool returnsText)
    {
        var left = BindExpression(expression.Left);
        var right = BindExpression(expression.Right);
        if (left.IsKnown && (left.IsArray || left.Kind != SqlValueKind.Json && left.Kind != SqlValueKind.JsonBinary))
        {
            Report("SQL207", $"Operator '{expression.Operator}' requires a json or jsonb left operand.", expression.Left.Span);
            return ErrorType();
        }

        if (right.IsKnown && (right.IsArray ||
            right.Kind != SqlValueKind.Int16 && right.Kind != SqlValueKind.Int32 &&
            right.Kind != SqlValueKind.Int64 && right.Kind != SqlValueKind.String))
        {
            Report("SQL207", $"Operator '{expression.Operator}' requires a text or integer path operand.", expression.Right.Span);
            return ErrorType();
        }

        return new TypeInfo(
            returnsText ? SqlValueKind.String : left.Kind,
            true);
    }

    private TypeInfo BindLike(BinaryExpression expression)
    {
        var left = BindExpression(expression.Left, SqlValueKind.String);
        var right = BindExpression(expression.Right, SqlValueKind.String);
        var valid = EnsureKind(left, SqlValueKind.String, expression.Left.Span, "LIKE requires string operands.");
        valid &= EnsureKind(right, SqlValueKind.String, expression.Right.Span, "LIKE requires string operands.");
        return valid
            ? new TypeInfo(SqlValueKind.Bool, IsNullable(left) || IsNullable(right))
            : ErrorType();
    }

    private TypeInfo BindConcat(BinaryExpression expression)
    {
        var left = BindExpression(expression.Left);
        var right = BindExpression(
            expression.Right,
            left.IsKnown ? left.Type : new SqlTypeShape(SqlValueKind.Unknown));
        InferPair(expression.Left, ref left, expression.Right, ref right);
        if (left.IsArray || right.IsArray)
        {
            if (!AreCompatible(left, right) || left.IsKnown && !left.IsArray || right.IsKnown && !right.IsArray)
            {
                Report("SQL207", "Array concatenation requires compatible array operands.", expression.Span);
                return ErrorType();
            }

            var type = left.IsKnown ? left.Type : right.Type;
            return new TypeInfo(type, IsNullable(left) || IsNullable(right));
        }

        left = BindExpression(expression.Left, SqlValueKind.String);
        right = BindExpression(expression.Right, SqlValueKind.String);
        var valid = EnsureKind(left, SqlValueKind.String, expression.Left.Span, "String concatenation requires string operands.");
        valid &= EnsureKind(right, SqlValueKind.String, expression.Right.Span, "String concatenation requires string operands.");
        return valid
            ? new TypeInfo(SqlValueKind.String, IsNullable(left) || IsNullable(right))
            : ErrorType();
    }

    private TypeInfo BindArithmetic(BinaryExpression expression)
    {
        var left = BindExpression(expression.Left);
        var right = BindExpression(expression.Right);
        InferPair(expression.Left, ref left, expression.Right, ref right);
        if (left.IsError || right.IsError) return ErrorType();
        if (left.IsArray || right.IsArray)
        {
            Report("SQL207", $"Operator '{expression.Operator}' does not accept array operands.", expression.Span);
            return ErrorType();
        }

        var temporal = BindTemporalArithmetic(expression, left, right);
        if (temporal.HasValue)
        {
            return temporal.Value;
        }

        if ((left.IsKnown && (left.IsArray || !_types.IsNumeric(left.Kind))) ||
            (right.IsKnown && (right.IsArray || !_types.IsNumeric(right.Kind))))
        {
            Report("SQL207", $"Operator '{expression.Operator}' requires numeric operands.", expression.Span);
            return ErrorType();
        }

        SqlValueKind result;
        if (!_types.TryUnify(left.Kind, right.Kind, out result))
        {
            return new TypeInfo(SqlValueKind.Unknown, IsNullable(left) || IsNullable(right), false, left.ParameterName ?? right.ParameterName);
        }

        return new TypeInfo(result, IsNullable(left) || IsNullable(right));
    }

    private TypeInfo? BindTemporalArithmetic(
        BinaryExpression expression,
        TypeInfo left,
        TypeInfo right)
    {
        var nullable = IsNullable(left) || IsNullable(right);
        var op = expression.Operator;
        if (op == "+")
        {
            if (left.Kind == SqlValueKind.DateOnly && _types.IsInteger(right.Kind) ||
                right.Kind == SqlValueKind.DateOnly && _types.IsInteger(left.Kind))
            {
                return new TypeInfo(SqlValueKind.DateOnly, nullable);
            }

            if (left.Kind == SqlValueKind.DateOnly && right.Kind == SqlValueKind.Interval ||
                right.Kind == SqlValueKind.DateOnly && left.Kind == SqlValueKind.Interval)
            {
                return new TypeInfo(SqlValueKind.DateTime, nullable);
            }

            if (IsTimestamp(left.Kind) && right.Kind == SqlValueKind.Interval)
            {
                return new TypeInfo(left.Kind, nullable);
            }

            if (IsTimestamp(right.Kind) && left.Kind == SqlValueKind.Interval)
            {
                return new TypeInfo(right.Kind, nullable);
            }

            if (left.Kind == SqlValueKind.TimeOnly && right.Kind == SqlValueKind.Interval ||
                right.Kind == SqlValueKind.TimeOnly && left.Kind == SqlValueKind.Interval)
            {
                return new TypeInfo(SqlValueKind.TimeOnly, nullable);
            }

            if (left.Kind == SqlValueKind.Interval && right.Kind == SqlValueKind.Interval)
            {
                return new TypeInfo(SqlValueKind.Interval, nullable);
            }
        }

        if (op == "-")
        {
            if (left.Kind == SqlValueKind.DateOnly && _types.IsInteger(right.Kind))
            {
                return new TypeInfo(SqlValueKind.DateOnly, nullable);
            }

            if (left.Kind == SqlValueKind.DateOnly && right.Kind == SqlValueKind.DateOnly)
            {
                return new TypeInfo(SqlValueKind.Int32, nullable);
            }

            if (left.Kind == SqlValueKind.DateOnly && right.Kind == SqlValueKind.Interval)
            {
                return new TypeInfo(SqlValueKind.DateTime, nullable);
            }

            if (IsTimestamp(left.Kind) && right.Kind == SqlValueKind.Interval)
            {
                return new TypeInfo(left.Kind, nullable);
            }

            if (IsTimestamp(left.Kind) && left.Kind == right.Kind ||
                left.Kind == SqlValueKind.TimeOnly && right.Kind == SqlValueKind.TimeOnly ||
                left.Kind == SqlValueKind.Interval && right.Kind == SqlValueKind.Interval)
            {
                return new TypeInfo(SqlValueKind.Interval, nullable);
            }

            if (left.Kind == SqlValueKind.TimeOnly && right.Kind == SqlValueKind.Interval)
            {
                return new TypeInfo(SqlValueKind.TimeOnly, nullable);
            }
        }

        if ((op == "*" && left.Kind == SqlValueKind.Interval && _types.IsNumeric(right.Kind)) ||
            (op == "*" && right.Kind == SqlValueKind.Interval && _types.IsNumeric(left.Kind)) ||
            (op == "/" && left.Kind == SqlValueKind.Interval && _types.IsNumeric(right.Kind)))
        {
            return new TypeInfo(SqlValueKind.Interval, nullable);
        }

        return null;
    }

    private static bool IsTimestamp(SqlValueKind kind) =>
        kind == SqlValueKind.DateTime || kind == SqlValueKind.DateTimeOffset;

    private TypeInfo BindIn(InExpression expression)
    {
        var operand = BindExpression(expression.Operand);
        if (expression.Subquery != null)
        {
            var columns = BindNestedStatement(expression.Subquery, true);
            if (columns.Count != 1)
            {
                Report("SQL222", "An IN subquery must return exactly one column.", expression.Span);
                return ErrorType();
            }

            var right = columns[0].Type;
            InferPair(expression.Operand, ref operand, expression.Operand, ref right);
            if (!AreCompatible(operand, right))
            {
                Report("SQL207", "IN compares incompatible types.", expression.Span);
                return ErrorType();
            }

            return new TypeInfo(SqlValueKind.Bool, IsNullable(operand) || IsNullable(right));
        }

        var values = new List<TypeInfo>();
        foreach (var value in expression.Values)
        {
            values.Add(BindExpression(
                value,
                operand.IsKnown ? operand.Type : new SqlTypeShape(SqlValueKind.Unknown)));
        }

        var common = operand.IsKnown ? operand.Type : FirstKnown(values);
        if (common.Kind != SqlValueKind.Unknown)
        {
            ApplyExpected(expression.Operand, common);
            operand = Refresh(operand);
            for (var index = 0; index < expression.Values.Count; index++)
            {
                ApplyExpected(expression.Values[index], common);
                values[index] = Refresh(values[index]);
            }
        }

        var valid = expression.Values.Count != 0;
        if (!valid)
        {
            Report("SQL207", "IN requires at least one list value.", expression.Span);
        }

        foreach (var value in values)
        {
            if (!AreCompatible(operand, value))
            {
                valid = false;
                Report("SQL207", "IN values must be compatible with the tested expression.", expression.Span);
                break;
            }
        }

        return valid
            ? new TypeInfo(SqlValueKind.Bool, IsNullable(operand) || values.Any(IsNullable))
            : ErrorType();
    }

    private TypeInfo BindBetween(BetweenExpression expression)
    {
        var operand = BindExpression(expression.Operand);
        var lower = BindExpression(
            expression.Lower,
            operand.IsKnown ? operand.Type : new SqlTypeShape(SqlValueKind.Unknown));
        var upper = BindExpression(
            expression.Upper,
            operand.IsKnown ? operand.Type : new SqlTypeShape(SqlValueKind.Unknown));
        var common = operand.IsKnown ? operand.Type : (lower.IsKnown ? lower.Type : upper.Type);
        if (common.Kind != SqlValueKind.Unknown)
        {
            ApplyExpected(expression.Operand, common);
            ApplyExpected(expression.Lower, common);
            ApplyExpected(expression.Upper, common);
            operand = Refresh(operand);
            lower = Refresh(lower);
            upper = Refresh(upper);
        }

        if (!AreCompatible(operand, lower) || !AreCompatible(operand, upper))
        {
            Report("SQL207", "BETWEEN bounds must be compatible with the tested expression.", expression.Span);
            return ErrorType();
        }

        return new TypeInfo(SqlValueKind.Bool, IsNullable(operand) || IsNullable(lower) || IsNullable(upper));
    }

    private TypeInfo BindCast(CastExpression expression)
    {
        if (!_types.TryMapType(expression.SqlType, out var type))
        {
            BindExpression(expression.Operand);
            Report("SQL205", $"Unsupported SQL type '{expression.SqlType}'.", expression.Span);
            return ErrorType();
        }

        var operand = BindExpression(expression.Operand, type);
        return new TypeInfo(type, IsNullable(operand));
    }

    private TypeInfo BindCase(CaseExpression expression)
    {
        if (expression.Operand == null)
        {
            foreach (var clause in expression.Clauses)
            {
                BindBooleanContext(clause.Condition, "CASE WHEN");
            }
        }
        else
        {
            var operand = BindExpression(expression.Operand);
            foreach (var clause in expression.Clauses)
            {
                var condition = BindExpression(
                    clause.Condition,
                    operand.IsKnown ? operand.Type : new SqlTypeShape(SqlValueKind.Unknown));
                InferPair(expression.Operand, ref operand, clause.Condition, ref condition);
                if (!AreCompatible(operand, condition))
                {
                    Report("SQL207", "Simple CASE values must be type-compatible.", clause.Condition.Span);
                }
            }
        }

        var resultExpressions = expression.Clauses.Select(item => item.Result).ToList();
        if (expression.ElseExpression != null)
        {
            resultExpressions.Add(expression.ElseExpression);
        }

        var resultTypes = resultExpressions.Select(item => BindExpression(item)).ToList();
        var type = UnifyExpressions(resultExpressions, resultTypes, expression.Span, "CASE result types are incompatible.");
        var nullable = expression.ElseExpression == null || resultTypes.Any(IsNullable);
        var allNull = resultTypes.Count != 0 && resultTypes.All(item => item.IsNullLiteral || item.Kind == SqlValueKind.Unknown);
        return type.Kind == SqlValueKind.Error
            ? ErrorType()
            : new TypeInfo(type, nullable, allNull);
    }

    private TypeInfo BindFunction(FunctionExpression expression)
    {
        if (expression.Filter != null)
        {
            BindBooleanContext(expression.Filter, "FILTER WHERE");
        }

        if (expression.Window != null)
        {
            BindWindowSpecification(expression.Window);
        }

        var name = expression.Name.IsQuoted ? expression.Name.Name : expression.Name.Name.ToLowerInvariant();
        if ((name == "row_number" || name == "rank" || name == "dense_rank" ||
             name == "lag" || name == "lead" || name == "first_value" || name == "last_value") &&
            expression.Window == null)
        {
            Report("SQL212", $"{expression.Name.Name.ToUpperInvariant()} requires an OVER clause.", expression.Span);
        }

        switch (name)
        {
            case "count": return BindCount(expression);
            case "sum": return BindAggregate(expression, AggregateKind.Sum);
            case "avg": return BindAggregate(expression, AggregateKind.Avg);
            case "min": return BindAggregate(expression, AggregateKind.Min);
            case "max": return BindAggregate(expression, AggregateKind.Max);
            case "lower": return BindStringFunction(expression, SqlValueKind.String);
            case "upper": return BindStringFunction(expression, SqlValueKind.String);
            case "length": return BindStringFunction(expression, SqlValueKind.Int32);
            case "char_length": return BindStringFunction(expression, SqlValueKind.Int32);
            case "character_length": return BindStringFunction(expression, SqlValueKind.Int32);
            case "octet_length": return BindStringFunction(expression, SqlValueKind.Int32);
            case "initcap": return BindStringFunction(expression, SqlValueKind.String);
            case "reverse": return BindStringFunction(expression, SqlValueKind.String);
            case "md5": return BindStringFunction(expression, SqlValueKind.String);
            case "abs": return BindAbs(expression);
            case "coalesce": return BindCoalesce(expression);
            case "nullif": return BindNullIf(expression);
            case "row_number":
            case "rank":
            case "dense_rank":
                return RequireArgumentCount(expression, 0)
                    ? new TypeInfo(SqlValueKind.Int64, false)
                    : ErrorType();
            case "lag":
            case "lead":
            case "first_value":
            case "last_value": return BindValueWindowFunction(expression);
            case "greatest":
            case "least": return BindVariadicCommonType(expression);
            case "round":
            case "ceil":
            case "ceiling":
            case "floor": return BindNumericFunction(expression);
            case "power": return BindPowerFunction(expression);
            case "random":
                return RequireArgumentCount(expression, 0)
                    ? new TypeInfo(SqlValueKind.Double, false)
                    : ErrorType();
            case "substring":
            case "trim":
            case "btrim":
            case "ltrim":
            case "rtrim": return BindFirstStringArgument(expression);
            case "replace": return BindStringArguments(expression, 3, SqlValueKind.String);
            case "strpos": return BindStringArguments(expression, 2, SqlValueKind.Int32);
            case "repeat": return BindRepeat(expression);
            case "concat": return BindConcatFunction(expression, false);
            case "concat_ws": return BindConcatFunction(expression, true);
            case "date_trunc": return BindDateTrunc(expression);
            case "date_part":
            case "extract": return BindDatePart(expression);
            case "to_char": return BindToChar(expression);
            case "now":
            case "transaction_timestamp":
            case "statement_timestamp":
            case "clock_timestamp":
                return RequireArgumentCount(expression, 0)
                    ? new TypeInfo(SqlValueKind.DateTimeOffset, false)
                    : ErrorType();
            default:
                foreach (var argument in expression.Arguments)
                {
                    BindExpression(argument);
                }

                Report("SQL206", $"Unknown function '{expression.Name.Name}'.", expression.Name.Span);
                return ErrorType();
        }
    }

    private TypeInfo BindValueWindowFunction(FunctionExpression expression)
    {
        if (expression.Arguments.Count == 0 || expression.Arguments.Count > 3)
        {
            foreach (var argument in expression.Arguments) BindExpression(argument);
            Report("SQL212", $"{expression.Name.Name.ToUpperInvariant()} expects between 1 and 3 arguments.", expression.Span);
            return ErrorType();
        }

        var value = BindExpression(expression.Arguments[0]);
        if (expression.Arguments.Count > 1)
        {
            BindExpression(expression.Arguments[1], SqlValueKind.Int32);
        }

        if (expression.Arguments.Count > 2)
        {
            var fallback = BindExpression(expression.Arguments[2], value.Type);
            if (!AreCompatible(value, fallback))
            {
                Report("SQL207", "Window-function value and default arguments are incompatible.", expression.Span);
            }
        }

        return expression.Name.Name.Equals("lag", StringComparison.OrdinalIgnoreCase) ||
               expression.Name.Name.Equals("lead", StringComparison.OrdinalIgnoreCase)
            ? value.WithNullable(true)
            : value;
    }

    private TypeInfo BindVariadicCommonType(FunctionExpression expression)
    {
        if (expression.Arguments.Count == 0)
        {
            Report("SQL212", $"{expression.Name.Name.ToUpperInvariant()} requires at least one argument.", expression.Span);
            return ErrorType();
        }

        var types = expression.Arguments.Select(argument => BindExpression(argument)).ToList();
        var type = UnifyExpressions(expression.Arguments.ToList(), types, expression.Span, "Function arguments are incompatible.");
        return type.Kind == SqlValueKind.Error
            ? ErrorType()
            : new TypeInfo(type, types.Any(IsNullable));
    }

    private TypeInfo BindNumericFunction(FunctionExpression expression)
    {
        if (expression.Arguments.Count == 0 || expression.Arguments.Count > 2)
        {
            foreach (var argument in expression.Arguments) BindExpression(argument);
            Report("SQL212", $"{expression.Name.Name.ToUpperInvariant()} expects one or two arguments.", expression.Span);
            return ErrorType();
        }

        var value = BindExpression(expression.Arguments[0]);
        if (value.IsKnown && (value.IsArray || !_types.IsNumeric(value.Kind)))
        {
            Report("SQL207", $"{expression.Name.Name.ToUpperInvariant()} requires a numeric argument.", expression.Arguments[0].Span);
            return ErrorType();
        }

        if (expression.Arguments.Count == 2)
        {
            BindExpression(expression.Arguments[1], SqlValueKind.Int32);
        }

        return value;
    }

    private TypeInfo BindPowerFunction(FunctionExpression expression)
    {
        if (!RequireArgumentCount(expression, 2)) return ErrorType();
        var left = BindExpression(expression.Arguments[0]);
        var right = BindExpression(expression.Arguments[1]);
        if ((left.IsKnown && (left.IsArray || !_types.IsNumeric(left.Kind))) ||
            (right.IsKnown && (right.IsArray || !_types.IsNumeric(right.Kind))))
        {
            Report("SQL207", "POWER requires numeric arguments.", expression.Span);
            return ErrorType();
        }

        var kind = left.Kind == SqlValueKind.Float || left.Kind == SqlValueKind.Double ||
                   right.Kind == SqlValueKind.Float || right.Kind == SqlValueKind.Double
            ? SqlValueKind.Double
            : left.IsKnown || right.IsKnown
                ? SqlValueKind.Decimal
                : SqlValueKind.Unknown;
        return new TypeInfo(kind, IsNullable(left) || IsNullable(right));
    }

    private TypeInfo BindStringArguments(
        FunctionExpression expression,
        int count,
        SqlValueKind resultKind)
    {
        if (!RequireArgumentCount(expression, count)) return ErrorType();
        var nullable = false;
        foreach (var argument in expression.Arguments)
        {
            var type = BindExpression(argument, SqlValueKind.String);
            if (!EnsureKind(type, SqlValueKind.String, argument.Span, "Function requires string arguments."))
            {
                return ErrorType();
            }

            nullable |= IsNullable(type);
        }

        return new TypeInfo(resultKind, nullable);
    }

    private TypeInfo BindRepeat(FunctionExpression expression)
    {
        if (!RequireArgumentCount(expression, 2)) return ErrorType();
        var text = BindExpression(expression.Arguments[0], SqlValueKind.String);
        var count = BindExpression(expression.Arguments[1], SqlValueKind.Int32);
        var valid = EnsureKind(text, SqlValueKind.String, expression.Arguments[0].Span, "REPEAT requires a string first argument.");
        valid &= EnsureKind(count, SqlValueKind.Int32, expression.Arguments[1].Span, "REPEAT requires an integer count.");
        return valid
            ? new TypeInfo(SqlValueKind.String, IsNullable(text) || IsNullable(count))
            : ErrorType();
    }

    private TypeInfo BindConcatFunction(FunctionExpression expression, bool hasSeparator)
    {
        if (expression.Arguments.Count < (hasSeparator ? 2 : 1))
        {
            foreach (var argument in expression.Arguments) BindExpression(argument);
            Report("SQL212", $"{expression.Name.Name.ToUpperInvariant()} has too few arguments.", expression.Span);
            return ErrorType();
        }

        var separator = hasSeparator
            ? BindExpression(expression.Arguments[0], SqlValueKind.String)
            : new TypeInfo(SqlValueKind.String, false);
        for (var index = hasSeparator ? 1 : 0; index < expression.Arguments.Count; index++)
        {
            BindExpression(expression.Arguments[index]);
        }

        return new TypeInfo(SqlValueKind.String, hasSeparator && IsNullable(separator));
    }

    private TypeInfo BindDateTrunc(FunctionExpression expression)
    {
        if (!RequireArgumentCount(expression, 2)) return ErrorType();
        var field = BindExpression(expression.Arguments[0], SqlValueKind.String);
        var value = BindExpression(expression.Arguments[1]);
        var valid = EnsureKind(field, SqlValueKind.String, expression.Arguments[0].Span, "DATE_TRUNC requires a text field name.");
        valid &= !value.IsArray &&
            (IsTimestamp(value.Kind) || value.Kind == SqlValueKind.Interval || !value.IsKnown);
        if (!valid)
        {
            Report("SQL207", "DATE_TRUNC requires a timestamp or interval value.", expression.Arguments[1].Span);
            return ErrorType();
        }

        return new TypeInfo(value.Kind, IsNullable(field) || IsNullable(value));
    }

    private TypeInfo BindDatePart(FunctionExpression expression)
    {
        if (!RequireArgumentCount(expression, 2)) return ErrorType();
        var field = BindExpression(expression.Arguments[0], SqlValueKind.String);
        var value = BindExpression(expression.Arguments[1]);
        var valid = EnsureKind(field, SqlValueKind.String, expression.Arguments[0].Span, "Date-part extraction requires a text field name.");
        valid &= !value.IsArray &&
            (value.Kind == SqlValueKind.DateOnly || value.Kind == SqlValueKind.TimeOnly ||
             IsTimestamp(value.Kind) || value.Kind == SqlValueKind.Interval || !value.IsKnown);
        if (!valid)
        {
            Report("SQL207", "Date-part extraction requires a date, time, timestamp, or interval value.", expression.Arguments[1].Span);
            return ErrorType();
        }

        return new TypeInfo(SqlValueKind.Double, IsNullable(field) || IsNullable(value));
    }

    private TypeInfo BindToChar(FunctionExpression expression)
    {
        if (!RequireArgumentCount(expression, 2)) return ErrorType();
        var value = BindExpression(expression.Arguments[0]);
        var format = BindExpression(expression.Arguments[1], SqlValueKind.String);
        if (value.IsArray)
        {
            Report("SQL207", "TO_CHAR does not accept an array value.", expression.Arguments[0].Span);
            return ErrorType();
        }

        return EnsureKind(format, SqlValueKind.String, expression.Arguments[1].Span, "TO_CHAR requires a text format.")
            ? new TypeInfo(SqlValueKind.String, IsNullable(value) || IsNullable(format))
            : ErrorType();
    }

    private TypeInfo BindFirstStringArgument(FunctionExpression expression)
    {
        if (expression.Arguments.Count == 0)
        {
            Report("SQL212", $"{expression.Name.Name.ToUpperInvariant()} requires at least one argument.", expression.Span);
            return ErrorType();
        }

        var first = BindExpression(expression.Arguments[0], SqlValueKind.String);
        for (var index = 1; index < expression.Arguments.Count; index++)
        {
            BindExpression(expression.Arguments[index]);
        }

        return EnsureKind(first, SqlValueKind.String, expression.Arguments[0].Span, "Function requires a string argument.")
            ? new TypeInfo(SqlValueKind.String, IsNullable(first))
            : ErrorType();
    }

    private TypeInfo BindCount(FunctionExpression expression)
    {
        if (!RequireArgumentCount(expression, 1)) return ErrorType();
        if (!(expression.Arguments[0] is StarExpression))
        {
            BindExpression(expression.Arguments[0]);
        }

        return new TypeInfo(_types.AggregateResult("count", SqlValueKind.Int64), false);
    }

    private TypeInfo BindAggregate(FunctionExpression expression, AggregateKind aggregate)
    {
        if (!RequireArgumentCount(expression, 1)) return ErrorType();
        if (expression.Arguments[0] is StarExpression)
        {
            Report("SQL212", $"{expression.Name.Name.ToUpperInvariant()} does not accept '*'.", expression.Arguments[0].Span);
            return ErrorType();
        }

        var argument = BindExpression(expression.Arguments[0]);
        if (argument.IsError) return argument;
        if ((aggregate == AggregateKind.Sum || aggregate == AggregateKind.Avg) &&
            argument.IsKnown && (argument.IsArray || !_types.IsNumeric(argument.Kind)))
        {
            Report("SQL207", $"{expression.Name.Name.ToUpperInvariant()} requires a numeric argument.", expression.Arguments[0].Span);
            return ErrorType();
        }

        var nullable = _hasGroupBy ? IsNullable(argument) : true;
        var resultType = argument.IsArray &&
            (aggregate == AggregateKind.Min || aggregate == AggregateKind.Max)
            ? argument.Type
            : new SqlTypeShape(_types.AggregateResult(expression.Name.Name, argument.Kind));
        return new TypeInfo(resultType, nullable, argument.IsNullLiteral, argument.ParameterName);
    }

    private TypeInfo BindStringFunction(FunctionExpression expression, SqlValueKind resultKind)
    {
        if (!RequireArgumentCount(expression, 1)) return ErrorType();
        var argument = BindExpression(expression.Arguments[0], SqlValueKind.String);
        if (!EnsureKind(argument, SqlValueKind.String, expression.Arguments[0].Span, $"{expression.Name.Name.ToUpperInvariant()} requires a string argument."))
        {
            return ErrorType();
        }

        return new TypeInfo(resultKind, IsNullable(argument));
    }

    private TypeInfo BindAbs(FunctionExpression expression)
    {
        if (!RequireArgumentCount(expression, 1)) return ErrorType();
        var argument = BindExpression(expression.Arguments[0]);
        if (argument.IsKnown && (argument.IsArray || !_types.IsNumeric(argument.Kind)))
        {
            Report("SQL207", "ABS requires a numeric argument.", expression.Arguments[0].Span);
            return ErrorType();
        }

        return argument;
    }

    private TypeInfo BindCoalesce(FunctionExpression expression)
    {
        if (expression.Arguments.Count == 0)
        {
            Report("SQL212", "COALESCE requires at least one argument.", expression.Span);
            return ErrorType();
        }

        var expressions = expression.Arguments.ToList();
        var types = expressions.Select(item => BindExpression(item)).ToList();
        var type = UnifyExpressions(expressions, types, expression.Span, "COALESCE argument types are incompatible.");
        var nullable = types.All(IsNullable);
        var allNull = types.All(item => item.IsNullLiteral || item.Kind == SqlValueKind.Unknown);
        return type.Kind == SqlValueKind.Error
            ? ErrorType()
            : new TypeInfo(type, nullable, allNull);
    }

    private TypeInfo BindNullIf(FunctionExpression expression)
    {
        if (!RequireArgumentCount(expression, 2)) return ErrorType();
        var first = BindExpression(expression.Arguments[0]);
        var second = BindExpression(expression.Arguments[1]);
        InferPair(expression.Arguments[0], ref first, expression.Arguments[1], ref second);
        if (!AreCompatible(first, second))
        {
            Report("SQL207", "NULLIF arguments must be type-compatible.", expression.Span);
            return ErrorType();
        }

        var type = first.IsKnown ? first.Type : second.Type;
        return new TypeInfo(type, true, first.IsNullLiteral && !second.IsKnown, first.ParameterName ?? second.ParameterName);
    }

    private SqlTypeShape UnifyExpressions(
        IReadOnlyList<Expression> expressions,
        IList<TypeInfo> types,
        SourceSpan span,
        string message)
    {
        var commonType = new SqlTypeShape(SqlValueKind.Unknown);
        foreach (var candidate in types)
        {
            if (!candidate.IsKnown) continue;
            if (!_types.TryUnify(commonType, candidate.Type, out var unified))
            {
                Report("SQL207", message, span);
                return new SqlTypeShape(SqlValueKind.Error);
            }

            commonType = unified;
        }

        if (commonType.Kind != SqlValueKind.Unknown)
        {
            for (var index = 0; index < expressions.Count; index++)
            {
                ApplyExpected(expressions[index], commonType);
                types[index] = Refresh(types[index]);
            }
        }

        return commonType;
    }

    private void InferPair(Expression leftExpression, ref TypeInfo left, Expression rightExpression, ref TypeInfo right)
    {
        if (left.IsKnown && rightExpression is ParameterExpression)
        {
            ApplyExpected(rightExpression, left.Type);
            right = Refresh(right);
        }

        if (right.IsKnown && leftExpression is ParameterExpression)
        {
            ApplyExpected(leftExpression, right.Type);
            left = Refresh(left);
        }
    }

    private void ApplyExpected(Expression expression, SqlValueKind kind) =>
        ApplyExpected(expression, new SqlTypeShape(kind));

    private void ApplyExpected(Expression expression, SqlTypeShape type)
    {
        var parameter = expression as ParameterExpression;
        if (parameter == null)
        {
            return;
        }

        ParameterState state;
        if (_parameters.TryGetValue(parameter.Name, out state!))
        {
            Constrain(state, type, parameter.Span);
        }
    }

    private TypeInfo Refresh(TypeInfo type)
    {
        if (type.ParameterName == null)
        {
            return type;
        }

        ParameterState state;
        return _parameters.TryGetValue(type.ParameterName, out state!) && state.Type.Kind != SqlValueKind.Unknown
            ? new TypeInfo(state.Type, type.Nullable, type.IsNullLiteral, type.ParameterName)
            : type;
    }

    private void BindBooleanContext(Expression expression, string context)
    {
        var type = BindExpression(expression, SqlValueKind.Bool);
        EnsureKind(type, SqlValueKind.Bool, expression.Span, $"{context} requires a boolean expression.", "SQL208");
    }

    private void BindIntegerContext(Expression expression, string context)
    {
        var type = BindExpression(expression, SqlValueKind.Int64);
        if (type.IsKnown && (type.IsArray || !_types.IsInteger(type.Kind)))
        {
            Report("SQL207", $"{context} requires an integer expression.", expression.Span);
        }
    }

    private bool EnsureKind(TypeInfo type, SqlValueKind expected, SourceSpan span, string message, string code = "SQL207")
    {
        if (type.IsError) return false;
        if (type.IsKnown && (type.IsArray || type.Kind != expected))
        {
            Report(code, message, span);
            return false;
        }

        return true;
    }

    private bool AreCompatible(TypeInfo left, TypeInfo right)
    {
        if (left.IsError || right.IsError) return true;
        if (!left.IsKnown || !right.IsKnown) return true;
        return _types.TryUnify(left.Type, right.Type, out _);
    }

    private static bool IsNullable(TypeInfo type) => type.Nullable || type.IsNullLiteral;

    private static SqlTypeShape FirstKnown(IEnumerable<TypeInfo> types)
    {
        foreach (var type in types)
        {
            if (type.IsKnown) return type.Type;
        }

        return new SqlTypeShape(SqlValueKind.Unknown);
    }

    private bool RequireArgumentCount(FunctionExpression expression, int count)
    {
        if (expression.Arguments.Count == count)
        {
            return true;
        }

        foreach (var argument in expression.Arguments)
        {
            BindExpression(argument);
        }

        Report(
            "SQL212",
            $"{expression.Name.Name.ToUpperInvariant()} expects {count} argument{(count == 1 ? string.Empty : "s")}.",
            expression.Span);
        return false;
    }

    private ScopeTable? FindScopeTable(SqlIdentifier identifier, bool report)
    {
        for (var index = _scope.Count - 1; index >= 0; index--)
        {
            var table = _scope[index];
            if (IdentifiersEquivalent(table.EffectiveName, identifier))
            {
                return table;
            }
        }

        if (report)
        {
            Report("SQL202", $"Unknown table name or alias '{identifier.Name}'.", identifier.Span);
        }

        return null;
    }

    private Column? FindColumn(Table table, SqlIdentifier identifier)
    {
        foreach (var column in table.Columns)
        {
            if (SchemaIdentifierMatches(identifier, column.Name))
            {
                return column;
            }
        }

        return null;
    }

    private bool SchemaIdentifierMatches(SqlIdentifier identifier, string? schemaName)
    {
        if (schemaName == null)
        {
            return false;
        }

        return _syntax.AreIdentifiersEqual(identifier.Name, identifier.IsQuoted, schemaName);
    }

    private string ReferencedIdentifier(SqlIdentifier identifier) =>
        _syntax.NormalizeIdentifierForComparison(identifier.Name, identifier.IsQuoted);

    private string DefaultOutputName(Expression expression)
    {
        var column = expression as ColumnExpression;
        if (column != null) return column.Name.Name;
        var function = expression as FunctionExpression;
        if (function != null)
        {
            return function.Name.IsQuoted
                ? function.Name.Name
                : _syntax.NormalizeUnquotedIdentifier(function.Name.Name);
        }

        if (expression is CaseExpression) return "case";
        if (expression is CastExpression) return "cast";
        return "?column?";
    }

    private bool IsOutputName(Expression expression, IReadOnlyList<BoundColumn> columns)
    {
        var column = expression as ColumnExpression;
        if (column == null || column.Qualifier != null)
        {
            return false;
        }

        return columns.Any(item =>
            _syntax.AreIdentifiersEqual(column.Name.Name, column.Name.IsQuoted, item.Name));
    }

    private IReadOnlyList<QueryParameter> FinishParameters()
    {
        var result = new List<QueryParameter>();
        foreach (var parameter in _parameterOrder)
        {
            if (parameter.Type.Kind == SqlValueKind.Unknown)
            {
                Report("SQL209", $"The type of parameter '{parameter.Name}' cannot be inferred.", parameter.FirstSpan);
                continue;
            }

            result.Add(new QueryParameter(
                parameter.Name,
                _types.ToClrName(parameter.Type, false),
                _types.ToDatabaseTypeName(parameter.Type)));
        }

        return result;
    }

    private IReadOnlyList<ResultColumn> FinishColumns(IReadOnlyList<BoundColumn> columns)
    {
        var result = new List<ResultColumn>();
        foreach (var column in columns)
        {
            var type = Refresh(column.Type);
            if (type.IsError)
            {
                continue;
            }

            if (type.Kind == SqlValueKind.Unknown)
            {
                if (type.IsNullLiteral || type.ParameterName == null)
                {
                    Report("SQL211", $"Result column '{column.Name}' is NULL with no inferable type; use CAST.", column.Span);
                }

                continue;
            }

            result.Add(new ResultColumn(column.Name, _types.ToClrName(type.Type, type.Nullable)));
        }

        return result;
    }

    private void Report(string code, string message, SourceSpan span) =>
        _diagnostics.Add(new Diagnostic(code, message, span));

    private static TypeInfo ErrorType() => new TypeInfo(SqlValueKind.Error, true);

    private sealed class ScopeTable
    {
        internal ScopeTable(Table? table, SqlIdentifier effectiveName)
        {
            Table = table;
            EffectiveName = effectiveName;
        }

        internal ScopeTable(IReadOnlyList<BoundColumn> columns, SqlIdentifier effectiveName)
        {
            DerivedColumns = columns;
            EffectiveName = effectiveName;
        }

        internal Table? Table { get; }
        internal IReadOnlyList<BoundColumn>? DerivedColumns { get; }
        internal SqlIdentifier EffectiveName { get; }
        internal bool ForcedNullable { get; set; }
    }

    private sealed class ParameterState
    {
        internal ParameterState(string name, SourceSpan firstSpan)
        {
            Name = name;
            FirstSpan = firstSpan;
        }

        internal string Name { get; }
        internal SourceSpan FirstSpan { get; }
        internal SqlTypeShape Type { get; set; }
    }

    private sealed class BoundColumn
    {
        internal BoundColumn(string name, TypeInfo type, SourceSpan span)
        {
            Name = name;
            Type = type;
            Span = span;
        }

        internal string Name { get; }
        internal TypeInfo Type { get; }
        internal SourceSpan Span { get; }
    }

    private sealed class ColumnMatch
    {
        internal ColumnMatch(TypeInfo type)
        {
            Type = type;
        }

        internal TypeInfo Type { get; }
    }

    private sealed class CommonTableRelation
    {
        internal CommonTableRelation(SqlIdentifier name, IReadOnlyList<BoundColumn> columns)
        {
            Name = name;
            Columns = columns;
        }

        internal SqlIdentifier Name { get; }
        internal IReadOnlyList<BoundColumn> Columns { get; set; }
    }

    private enum AggregateKind
    {
        Sum,
        Avg,
        Min,
        Max,
    }

    private enum AggregateContext
    {
        Select,
        JoinOn,
        Where,
        GroupBy,
        Having,
        OrderBy,
        DataModification,
    }
}

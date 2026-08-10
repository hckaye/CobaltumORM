using System.Collections.Generic;

namespace CobaltumOrm.Analysis;

internal sealed class SqlIdentifier
{
    internal SqlIdentifier(string name, bool isQuoted, SourceSpan span)
    {
        Name = name;
        IsQuoted = isQuoted;
        Span = span;
    }

    internal string Name { get; }
    internal bool IsQuoted { get; }
    internal SourceSpan Span { get; }
}

internal sealed class SqlQualifiedName
{
    internal SqlQualifiedName(SqlIdentifier? schema, SqlIdentifier name, SourceSpan span)
    {
        Schema = schema;
        Name = name;
        Span = span;
    }

    internal SqlIdentifier? Schema { get; }
    internal SqlIdentifier Name { get; }
    internal SourceSpan Span { get; }
}

internal abstract class SqlStatement
{
}

internal sealed class WithStatement : SqlStatement
{
    internal WithStatement(IReadOnlyList<CommonTableExpression> expressions, SqlStatement statement, bool recursive)
    {
        Expressions = expressions;
        Statement = statement;
        Recursive = recursive;
    }

    internal IReadOnlyList<CommonTableExpression> Expressions { get; }
    internal SqlStatement Statement { get; }
    internal bool Recursive { get; }
}

internal sealed class CommonTableExpression
{
    internal CommonTableExpression(
        SqlIdentifier name,
        IReadOnlyList<SqlIdentifier> columnNames,
        SqlStatement statement)
    {
        Name = name;
        ColumnNames = columnNames;
        Statement = statement;
    }

    internal SqlIdentifier Name { get; }
    internal IReadOnlyList<SqlIdentifier> ColumnNames { get; }
    internal SqlStatement Statement { get; }
}

internal sealed class SelectStatement : SqlStatement
{
    internal SelectStatement(
        IReadOnlyList<SelectItem> items,
        TableReference? from,
        IReadOnlyList<JoinClause> joins,
        Expression? where,
        IReadOnlyList<Expression> groupBy,
        Expression? having,
        IReadOnlyList<OrderItem> orderBy,
        Expression? limit,
        Expression? offset,
        bool distinct = false,
        IReadOnlyList<Expression>? distinctOn = null,
        IReadOnlyList<SetOperation>? setOperations = null,
        IReadOnlyList<NamedWindow>? windows = null,
        IReadOnlyList<SqlIdentifier>? lockTables = null)
    {
        Items = items;
        From = from;
        Joins = joins;
        Where = where;
        GroupBy = groupBy;
        Having = having;
        OrderBy = orderBy;
        Limit = limit;
        Offset = offset;
        Distinct = distinct;
        DistinctOn = distinctOn ?? new List<Expression>();
        SetOperations = setOperations ?? new List<SetOperation>();
        Windows = windows ?? new List<NamedWindow>();
        LockTables = lockTables ?? new List<SqlIdentifier>();
    }

    internal IReadOnlyList<SelectItem> Items { get; }
    internal TableReference? From { get; }
    internal IReadOnlyList<JoinClause> Joins { get; }
    internal Expression? Where { get; }
    internal IReadOnlyList<Expression> GroupBy { get; }
    internal Expression? Having { get; }
    internal IReadOnlyList<OrderItem> OrderBy { get; }
    internal Expression? Limit { get; }
    internal Expression? Offset { get; }
    internal bool Distinct { get; }
    internal IReadOnlyList<Expression> DistinctOn { get; }
    internal IReadOnlyList<SetOperation> SetOperations { get; }
    internal IReadOnlyList<NamedWindow> Windows { get; }
    internal IReadOnlyList<SqlIdentifier> LockTables { get; }
}

internal sealed class NamedWindow
{
    internal NamedWindow(SqlIdentifier name, WindowSpecification specification)
    {
        Name = name;
        Specification = specification;
    }

    internal SqlIdentifier Name { get; }
    internal WindowSpecification Specification { get; }
}

internal sealed class ValuesStatement : SqlStatement
{
    internal ValuesStatement(
        IReadOnlyList<IReadOnlyList<Expression>> rows,
        IReadOnlyList<OrderItem> orderBy,
        Expression? limit,
        Expression? offset)
    {
        Rows = rows;
        OrderBy = orderBy;
        Limit = limit;
        Offset = offset;
    }

    internal IReadOnlyList<IReadOnlyList<Expression>> Rows { get; }
    internal IReadOnlyList<OrderItem> OrderBy { get; }
    internal Expression? Limit { get; }
    internal Expression? Offset { get; }
}

internal enum SetOperationKind
{
    Union,
    Intersect,
    Except,
}

internal sealed class SetOperation
{
    internal SetOperation(SetOperationKind kind, bool all, SelectStatement right, SourceSpan span)
    {
        Kind = kind;
        All = all;
        Right = right;
        Span = span;
    }

    internal SetOperationKind Kind { get; }
    internal bool All { get; }
    internal SelectStatement Right { get; }
    internal SourceSpan Span { get; }
}

internal sealed class UpdateStatement : SqlStatement
{
    internal UpdateStatement(
        TableReference table,
        IReadOnlyList<UpdateAssignment> assignments,
        Expression? where,
        IReadOnlyList<TableReference>? from = null,
        IReadOnlyList<SelectItem>? returning = null)
    {
        Table = table;
        Assignments = assignments;
        Where = where;
        From = from ?? new List<TableReference>();
        Returning = returning ?? new List<SelectItem>();
    }

    internal TableReference Table { get; }
    internal IReadOnlyList<UpdateAssignment> Assignments { get; }
    internal Expression? Where { get; }
    internal IReadOnlyList<TableReference> From { get; }
    internal IReadOnlyList<SelectItem> Returning { get; }
}

internal sealed class UpdateAssignment
{
    internal UpdateAssignment(SqlIdentifier column, Expression value)
    {
        Column = column;
        Value = value;
    }

    internal SqlIdentifier Column { get; }
    internal Expression Value { get; }
}

internal sealed class InsertStatement : SqlStatement
{
    internal InsertStatement(
        TableReference table,
        IReadOnlyList<SqlIdentifier> columns,
        IReadOnlyList<IReadOnlyList<Expression>> rows,
        bool usesDefaultValues,
        SqlStatement? source = null,
        OnConflictClause? onConflict = null,
        IReadOnlyList<SelectItem>? returning = null)
    {
        Table = table;
        Columns = columns;
        Rows = rows;
        UsesDefaultValues = usesDefaultValues;
        Source = source;
        OnConflict = onConflict;
        Returning = returning ?? new List<SelectItem>();
    }

    internal TableReference Table { get; }
    internal IReadOnlyList<SqlIdentifier> Columns { get; }
    internal IReadOnlyList<IReadOnlyList<Expression>> Rows { get; }
    internal bool UsesDefaultValues { get; }
    internal SqlStatement? Source { get; }
    internal OnConflictClause? OnConflict { get; }
    internal IReadOnlyList<SelectItem> Returning { get; }
}

internal sealed class OnConflictClause
{
    internal OnConflictClause(
        IReadOnlyList<SqlIdentifier> targetColumns,
        SqlIdentifier? constraint,
        Expression? targetWhere,
        bool doNothing,
        IReadOnlyList<UpdateAssignment> assignments,
        Expression? updateWhere)
    {
        TargetColumns = targetColumns;
        Constraint = constraint;
        TargetWhere = targetWhere;
        DoNothing = doNothing;
        Assignments = assignments;
        UpdateWhere = updateWhere;
    }

    internal IReadOnlyList<SqlIdentifier> TargetColumns { get; }
    internal SqlIdentifier? Constraint { get; }
    internal Expression? TargetWhere { get; }
    internal bool DoNothing { get; }
    internal IReadOnlyList<UpdateAssignment> Assignments { get; }
    internal Expression? UpdateWhere { get; }
}

internal sealed class DeleteStatement : SqlStatement
{
    internal DeleteStatement(
        TableReference table,
        Expression? where,
        IReadOnlyList<TableReference>? usingTables = null,
        IReadOnlyList<SelectItem>? returning = null)
    {
        Table = table;
        Where = where;
        Using = usingTables ?? new List<TableReference>();
        Returning = returning ?? new List<SelectItem>();
    }

    internal TableReference Table { get; }
    internal Expression? Where { get; }
    internal IReadOnlyList<TableReference> Using { get; }
    internal IReadOnlyList<SelectItem> Returning { get; }
}

internal sealed class TruncateStatement : SqlStatement
{
    internal TruncateStatement(IReadOnlyList<TableReference> tables) => Tables = tables;
    internal IReadOnlyList<TableReference> Tables { get; }
}

internal sealed class SelectItem
{
    internal SelectItem(Expression expression, SqlIdentifier? alias)
    {
        Expression = expression;
        Alias = alias;
    }

    internal Expression Expression { get; }
    internal SqlIdentifier? Alias { get; }
}

internal sealed class TableReference
{
    internal TableReference(
        SqlIdentifier? schema,
        SqlIdentifier name,
        SqlIdentifier? alias,
        IReadOnlyList<SqlIdentifier>? columnAliases = null)
    {
        Schema = schema;
        Name = name;
        Alias = alias;
        Subquery = null;
        ColumnAliases = columnAliases ?? new List<SqlIdentifier>();
        Lateral = false;
    }

    internal TableReference(
        SqlStatement subquery,
        SqlIdentifier alias,
        IReadOnlyList<SqlIdentifier> columnAliases,
        bool lateral)
    {
        Schema = null;
        Name = alias;
        Alias = alias;
        Subquery = subquery;
        ColumnAliases = columnAliases;
        Lateral = lateral;
    }

    internal SqlIdentifier? Schema { get; }
    internal SqlIdentifier Name { get; }
    internal SqlIdentifier? Alias { get; }
    internal SqlStatement? Subquery { get; }
    internal IReadOnlyList<SqlIdentifier> ColumnAliases { get; }
    internal bool Lateral { get; }
}

internal enum JoinKind
{
    Inner,
    Left,
    Right,
    Full,
    Cross,
}

internal sealed class JoinClause
{
    internal JoinClause(
        JoinKind kind,
        TableReference table,
        Expression? on,
        IReadOnlyList<SqlIdentifier>? usingColumns,
        bool natural,
        SourceSpan span)
    {
        Kind = kind;
        Table = table;
        On = on;
        UsingColumns = usingColumns ?? new List<SqlIdentifier>();
        Natural = natural;
        Span = span;
    }

    internal JoinKind Kind { get; }
    internal TableReference Table { get; }
    internal Expression? On { get; }
    internal IReadOnlyList<SqlIdentifier> UsingColumns { get; }
    internal bool Natural { get; }
    internal SourceSpan Span { get; }
}

internal sealed class OrderItem
{
    internal OrderItem(Expression expression, bool descending, bool? nullsFirst = null)
    {
        Expression = expression;
        Descending = descending;
        NullsFirst = nullsFirst;
    }

    internal Expression Expression { get; }
    internal bool Descending { get; }
    internal bool? NullsFirst { get; }
}

internal abstract class Expression
{
    protected Expression(SourceSpan span) => Span = span;
    internal SourceSpan Span { get; }
}

internal sealed class StarExpression : Expression
{
    internal StarExpression(SqlIdentifier? qualifier, SourceSpan span) : base(span) => Qualifier = qualifier;
    internal SqlIdentifier? Qualifier { get; }
}

internal sealed class ColumnExpression : Expression
{
    internal ColumnExpression(SqlIdentifier? qualifier, SqlIdentifier name, SourceSpan span) : base(span)
    {
        Qualifier = qualifier;
        Name = name;
    }

    internal SqlIdentifier? Qualifier { get; }
    internal SqlIdentifier Name { get; }
}

internal enum LiteralKind
{
    Integer,
    Decimal,
    String,
    Boolean,
    Date,
    Time,
    Timestamp,
    TimestampWithTimeZone,
    Interval,
    Null,
}

internal sealed class LiteralExpression : Expression
{
    internal LiteralExpression(LiteralKind kind, object? value, SourceSpan span) : base(span)
    {
        Kind = kind;
        Value = value;
    }

    internal LiteralKind Kind { get; }
    internal object? Value { get; }
}

internal sealed class ParameterExpression : Expression
{
    internal ParameterExpression(string name, SourceSpan span) : base(span) => Name = name;
    internal string Name { get; }
}

internal sealed class DefaultExpression : Expression
{
    internal DefaultExpression(SourceSpan span) : base(span)
    {
    }
}

internal sealed class UnaryExpression : Expression
{
    internal UnaryExpression(string op, Expression operand, SourceSpan span) : base(span)
    {
        Operator = op;
        Operand = operand;
    }

    internal string Operator { get; }
    internal Expression Operand { get; }
}

internal sealed class BinaryExpression : Expression
{
    internal BinaryExpression(Expression left, string op, Expression right, SourceSpan span) : base(span)
    {
        Left = left;
        Operator = op;
        Right = right;
    }

    internal Expression Left { get; }
    internal string Operator { get; }
    internal Expression Right { get; }
}

internal sealed class IsNullExpression : Expression
{
    internal IsNullExpression(Expression operand, bool negated, SourceSpan span) : base(span)
    {
        Operand = operand;
        Negated = negated;
    }

    internal Expression Operand { get; }
    internal bool Negated { get; }
}

internal sealed class IsTruthExpression : Expression
{
    internal IsTruthExpression(Expression operand, LiteralKind test, bool negated, SourceSpan span) : base(span)
    {
        Operand = operand;
        Test = test;
        Negated = negated;
    }

    internal Expression Operand { get; }
    internal LiteralKind Test { get; }
    internal bool Negated { get; }
}

internal sealed class InExpression : Expression
{
    internal InExpression(
        Expression operand,
        IReadOnlyList<Expression> values,
        SqlStatement? subquery,
        bool negated,
        SourceSpan span) : base(span)
    {
        Operand = operand;
        Values = values;
        Subquery = subquery;
        Negated = negated;
    }

    internal Expression Operand { get; }
    internal IReadOnlyList<Expression> Values { get; }
    internal SqlStatement? Subquery { get; }
    internal bool Negated { get; }
}

internal sealed class BetweenExpression : Expression
{
    internal BetweenExpression(Expression operand, Expression lower, Expression upper, bool negated, SourceSpan span) : base(span)
    {
        Operand = operand;
        Lower = lower;
        Upper = upper;
        Negated = negated;
    }

    internal Expression Operand { get; }
    internal Expression Lower { get; }
    internal Expression Upper { get; }
    internal bool Negated { get; }
}

internal sealed class FunctionExpression : Expression
{
    internal FunctionExpression(
        SqlIdentifier name,
        IReadOnlyList<Expression> arguments,
        SourceSpan span,
        bool distinct = false,
        Expression? filter = null,
        WindowSpecification? window = null) : base(span)
    {
        Name = name;
        Arguments = arguments;
        Distinct = distinct;
        Filter = filter;
        Window = window;
    }

    internal SqlIdentifier Name { get; }
    internal IReadOnlyList<Expression> Arguments { get; }
    internal bool Distinct { get; }
    internal Expression? Filter { get; }
    internal WindowSpecification? Window { get; }
}

internal sealed class WindowSpecification
{
    internal WindowSpecification(
        SqlIdentifier? name,
        IReadOnlyList<Expression> partitionBy,
        IReadOnlyList<OrderItem> orderBy)
    {
        Name = name;
        PartitionBy = partitionBy;
        OrderBy = orderBy;
    }

    internal SqlIdentifier? Name { get; }
    internal IReadOnlyList<Expression> PartitionBy { get; }
    internal IReadOnlyList<OrderItem> OrderBy { get; }
}

internal sealed class SubqueryExpression : Expression
{
    internal SubqueryExpression(SqlStatement query, SourceSpan span) : base(span) => Query = query;
    internal SqlStatement Query { get; }
}

internal sealed class ExistsExpression : Expression
{
    internal ExistsExpression(SqlStatement query, SourceSpan span) : base(span) => Query = query;
    internal SqlStatement Query { get; }
}

internal sealed class CastExpression : Expression
{
    internal CastExpression(Expression operand, string sqlType, SourceSpan span) : base(span)
    {
        Operand = operand;
        SqlType = sqlType;
    }

    internal Expression Operand { get; }
    internal string SqlType { get; }
}

internal sealed class WhenClause
{
    internal WhenClause(Expression condition, Expression result)
    {
        Condition = condition;
        Result = result;
    }

    internal Expression Condition { get; }
    internal Expression Result { get; }
}

internal sealed class CaseExpression : Expression
{
    internal CaseExpression(Expression? operand, IReadOnlyList<WhenClause> clauses, Expression? elseExpression, SourceSpan span) : base(span)
    {
        Operand = operand;
        Clauses = clauses;
        ElseExpression = elseExpression;
    }

    internal Expression? Operand { get; }
    internal IReadOnlyList<WhenClause> Clauses { get; }
    internal Expression? ElseExpression { get; }
}

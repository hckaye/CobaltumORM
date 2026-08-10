using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CobaltumOrm.Analysis;

public readonly struct SourceSpan : IEquatable<SourceSpan>
{
    public SourceSpan(int start, int length)
    {
        Start = start;
        Length = length;
    }

    public int Start { get; }
    public int Length { get; }

    public bool Equals(SourceSpan other) => Start == other.Start && Length == other.Length;
    public override bool Equals(object? obj) => obj is SourceSpan other && Equals(other);
    public override int GetHashCode() => (Start * 397) ^ Length;
    public override string ToString() => $"[{Start}..{Start + Length})";
}

public enum DiagnosticSeverity
{
    Error,
}

public sealed class Diagnostic
{
    public Diagnostic(string code, string message, SourceSpan span)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Span = span;
    }

    public string Code { get; }
    public string Message { get; }
    public SourceSpan Span { get; }
    public DiagnosticSeverity Severity => DiagnosticSeverity.Error;

    public override string ToString() => $"{Code} {Span}: {Message}";
}

public sealed class Column
{
    public Column(
        string name,
        string sqlType,
        bool isNullable = false,
        bool isPrimaryKey = false,
        string? defaultExpression = null,
        bool isIdentity = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        SqlType = sqlType ?? throw new ArgumentNullException(nameof(sqlType));
        IsNullable = isNullable;
        IsPrimaryKey = isPrimaryKey;
        DefaultExpression = defaultExpression;
        IsIdentity = isIdentity;
    }

    public string Name { get; }
    public string SqlType { get; }
    public bool IsNullable { get; }
    public bool IsPrimaryKey { get; }
    public string? DefaultExpression { get; }
    public bool IsIdentity { get; }
}

public sealed class Table
{
    private readonly ReadOnlyCollection<Column> _columns;

    public Table(string name, IEnumerable<Column> columns)
        : this(name, columns, null)
    {
    }

    public Table(string name, IEnumerable<Column> columns, string? schema)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Schema = schema;
        if (columns is null)
        {
            throw new ArgumentNullException(nameof(columns));
        }

        _columns = Array.AsReadOnly(columns.ToArray());
    }

    public string Name { get; }
    public string? Schema { get; }
    public IReadOnlyList<Column> Columns => _columns;
}

public sealed class DatabaseSchema
{
    private readonly ReadOnlyCollection<Table> _tables;

    public DatabaseSchema(IEnumerable<Table> tables)
    {
        if (tables is null)
        {
            throw new ArgumentNullException(nameof(tables));
        }

        _tables = Array.AsReadOnly(tables.ToArray());
    }

    public IReadOnlyList<Table> Tables => _tables;
}

public sealed class ResultColumn
{
    public ResultColumn(string name, string clrType)
    {
        Name = name;
        ClrType = clrType;
    }

    public string Name { get; }
    public string ClrType { get; }
}

public sealed class QueryParameter
{
    public QueryParameter(string name, string clrType, string? databaseTypeName = null)
    {
        Name = name;
        ClrType = clrType;
        DatabaseTypeName = databaseTypeName;
    }

    public string Name { get; }
    public string ClrType { get; }
    public string? DatabaseTypeName { get; }
}

public sealed class AnalysisResult
{
    private readonly ReadOnlyCollection<ResultColumn> _columns;
    private readonly ReadOnlyCollection<QueryParameter> _parameters;
    private readonly ReadOnlyCollection<Diagnostic> _diagnostics;

    internal AnalysisResult(
        IEnumerable<ResultColumn> columns,
        IEnumerable<QueryParameter> parameters,
        IEnumerable<Diagnostic> diagnostics)
    {
        _columns = Array.AsReadOnly(columns.ToArray());
        _parameters = Array.AsReadOnly(parameters.ToArray());
        _diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public IReadOnlyList<ResultColumn> Columns => _columns;
    public IReadOnlyList<QueryParameter> Parameters => _parameters;
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
    public bool HasErrors => _diagnostics.Count != 0;
}

public interface ISchemaMigrationAnalyzer
{
    MigrationAnalysisResult Analyze(DatabaseSchema schema, string sql);
}

public interface IQueryAnalyzer
{
    AnalysisResult Analyze(DatabaseSchema schema, string sql);
}

public sealed class MigrationAnalysisResult
{
    private readonly ReadOnlyCollection<Diagnostic> _diagnostics;

    internal MigrationAnalysisResult(DatabaseSchema schema, IEnumerable<Diagnostic> diagnostics)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public DatabaseSchema Schema { get; }
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
    public bool HasErrors => _diagnostics.Count != 0;
}

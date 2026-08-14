using System;

namespace CobaltumOrm.Analysis;

public enum SqlValueKind
{
    Unknown,
    Error,
    Bool,
    Int16,
    Int32,
    Int64,
    Float,
    Double,
    Decimal,
    String,
    Json,
    JsonBinary,
    Guid,
    DateOnly,
    TimeOnly,
    DateTime,
    DateTimeOffset,
    Interval,
    Bytes,
}

internal readonly struct TypeInfo
{
    internal TypeInfo(
        SqlValueKind kind,
        bool nullable,
        bool isNullLiteral = false,
        string? parameterName = null,
        bool isArray = false)
        : this(new SqlTypeShape(kind, isArray), nullable, isNullLiteral, parameterName)
    {
    }

    internal TypeInfo(
        SqlTypeShape type,
        bool nullable,
        bool isNullLiteral = false,
        string? parameterName = null)
    {
        Type = type;
        Nullable = nullable;
        IsNullLiteral = isNullLiteral;
        ParameterName = parameterName;
    }

    internal SqlTypeShape Type { get; }
    internal SqlValueKind Kind => Type.Kind;
    internal bool IsArray => Type.IsArray;
    internal bool Nullable { get; }
    internal bool IsNullLiteral { get; }
    internal string? ParameterName { get; }
    internal bool IsKnown => Kind != SqlValueKind.Unknown && Kind != SqlValueKind.Error;
    internal bool IsError => Kind == SqlValueKind.Error;

    internal TypeInfo WithNullable(bool nullable) => new TypeInfo(Type, nullable, IsNullLiteral, ParameterName);
}

internal readonly struct SqlTypeShape : IEquatable<SqlTypeShape>
{
    internal SqlTypeShape(SqlValueKind kind, bool isArray = false)
    {
        Kind = kind;
        IsArray = isArray;
    }

    internal SqlValueKind Kind { get; }
    internal bool IsArray { get; }
    internal bool IsKnown => Kind != SqlValueKind.Unknown && Kind != SqlValueKind.Error;

    internal SqlTypeShape ToArray() => new SqlTypeShape(Kind, true);
    internal SqlTypeShape ElementType => new SqlTypeShape(Kind);

    public bool Equals(SqlTypeShape other) => Kind == other.Kind && IsArray == other.IsArray;
    public override bool Equals(object? obj) => obj is SqlTypeShape other && Equals(other);
    public override int GetHashCode() => ((int)Kind * 397) ^ (IsArray ? 1 : 0);
}

internal static class SqlTypeMapper
{
    internal static string ToClrName(SqlValueKind kind, bool nullable)
    {
        string name;
        switch (kind)
        {
            case SqlValueKind.Bool: name = "bool"; break;
            case SqlValueKind.Int16: name = "short"; break;
            case SqlValueKind.Int32: name = "int"; break;
            case SqlValueKind.Int64: name = "long"; break;
            case SqlValueKind.Float: name = "float"; break;
            case SqlValueKind.Double: name = "double"; break;
            case SqlValueKind.Decimal: name = "decimal"; break;
            case SqlValueKind.String: name = "string"; break;
            case SqlValueKind.Json: name = "string"; break;
            case SqlValueKind.JsonBinary: name = "string"; break;
            case SqlValueKind.Guid: name = "Guid"; break;
            case SqlValueKind.DateOnly: name = "DateOnly"; break;
            case SqlValueKind.TimeOnly: name = "TimeOnly"; break;
            case SqlValueKind.DateTime: name = "DateTime"; break;
            case SqlValueKind.DateTimeOffset: name = "DateTimeOffset"; break;
            case SqlValueKind.Interval: name = "TimeSpan"; break;
            case SqlValueKind.Bytes: name = "byte[]"; break;
            default: name = "object"; break;
        }

        return nullable ? name + "?" : name;
    }

    internal static string ToClrName(SqlTypeShape type, bool nullable)
    {
        var name = ToClrName(type.Kind, false);
        if (type.IsArray)
        {
            name += "[]";
        }

        return nullable ? name + "?" : name;
    }

    internal static bool IsNumeric(SqlValueKind kind) =>
        kind == SqlValueKind.Int16 || kind == SqlValueKind.Int32 || kind == SqlValueKind.Int64 ||
        kind == SqlValueKind.Float || kind == SqlValueKind.Double || kind == SqlValueKind.Decimal;

    internal static bool IsInteger(SqlValueKind kind) =>
        kind == SqlValueKind.Int16 || kind == SqlValueKind.Int32 || kind == SqlValueKind.Int64;

    internal static bool IsFloat(SqlValueKind kind) => kind == SqlValueKind.Float || kind == SqlValueKind.Double;

    internal static bool TryUnify(SqlValueKind left, SqlValueKind right, out SqlValueKind result)
    {
        if (left == SqlValueKind.Unknown)
        {
            result = right;
            return right != SqlValueKind.Error;
        }

        if (right == SqlValueKind.Unknown)
        {
            result = left;
            return left != SqlValueKind.Error;
        }

        if (left == right)
        {
            result = left;
            return left != SqlValueKind.Error;
        }

        if (left == SqlValueKind.String && (right == SqlValueKind.Json || right == SqlValueKind.JsonBinary))
        {
            result = right;
            return true;
        }

        if (right == SqlValueKind.String && (left == SqlValueKind.Json || left == SqlValueKind.JsonBinary))
        {
            result = left;
            return true;
        }

        if (!IsNumeric(left) || !IsNumeric(right))
        {
            result = SqlValueKind.Error;
            return false;
        }

        if (IsFloat(left) || IsFloat(right))
        {
            result = SqlValueKind.Double;
            return true;
        }

        if (left == SqlValueKind.Decimal || right == SqlValueKind.Decimal)
        {
            result = SqlValueKind.Decimal;
            return true;
        }

        result = IntegerRank(left) >= IntegerRank(right) ? left : right;
        return true;
    }

    private static int IntegerRank(SqlValueKind kind)
    {
        switch (kind)
        {
            case SqlValueKind.Int16: return 1;
            case SqlValueKind.Int32: return 2;
            case SqlValueKind.Int64: return 3;
            default: return 0;
        }
    }

}

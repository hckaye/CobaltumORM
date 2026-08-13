using System;
using System.ComponentModel;
using System.Data.Common;

namespace CobaltumOrm;

/// <summary>Maps one returned column to a result member.</summary>
public interface IValueHandler<TValue>
{
    /// <summary>Reads one value from the current row.</summary>
    TValue Read(DbDataReader reader, int ordinal);
}

/// <summary>Maps the current database row to a result value.</summary>
public interface IResultHandler<TResult>
{
    /// <summary>Reads the current row.</summary>
    TResult Read(DbDataReader reader);
}

/// <summary>Specifies the returned column name for a result member or constructor parameter.</summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false,
    Inherited = true)]
public sealed class ResultColumnAttribute : Attribute
{
    /// <summary>Uses the result member or constructor parameter name as the column name.</summary>
    public ResultColumnAttribute()
    {
    }

    /// <summary>Initializes a returned column name override.</summary>
    public ResultColumnAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A result column name is required.", nameof(name));
        }

        Name = name;
    }

    /// <summary>Gets the returned column name.</summary>
    public string? Name { get; }
}

/// <summary>Specifies a generated-code value handler for one result member.</summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false,
    Inherited = true)]
public sealed class ValueHandlerAttribute<THandler> : Attribute
{
}

/// <summary>Specifies a generated-code handler for an entire result type.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class ResultHandlerAttribute<THandler> : Attribute
{
}

/// <summary>Stores one handler instance for generated mapping code.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class CobaltumHandlerCache<THandler>
    where THandler : new()
{
    /// <summary>Gets the handler instance used by generated mapping code.</summary>
    public static THandler Instance { get; } = new THandler();
}

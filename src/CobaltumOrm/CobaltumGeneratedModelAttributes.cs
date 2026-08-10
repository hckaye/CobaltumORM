using System;

namespace CobaltumOrm;

/// <summary>Describes the database table represented by a generated record.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CobaltumTableAttribute : Attribute
{
    /// <summary>Initializes table metadata.</summary>
    public CobaltumTableAttribute(string? schema, string name)
    {
        Schema = schema;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>Gets the optional database schema.</summary>
    public string? Schema { get; }

    /// <summary>Gets the unqualified database table name.</summary>
    public string Name { get; }
}

/// <summary>Describes the database column represented by a generated record property.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class CobaltumColumnAttribute : Attribute
{
    /// <summary>Initializes column metadata.</summary>
    public CobaltumColumnAttribute(
        string name,
        string sqlType,
        bool isNullable,
        bool isPrimaryKey,
        string? defaultExpression)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        SqlType = sqlType ?? throw new ArgumentNullException(nameof(sqlType));
        IsNullable = isNullable;
        IsPrimaryKey = isPrimaryKey;
        DefaultExpression = defaultExpression;
    }

    /// <summary>Gets the database column name.</summary>
    public string Name { get; }

    /// <summary>Gets the PostgreSQL type as declared by the migrations.</summary>
    public string SqlType { get; }

    /// <summary>Gets whether the column accepts database nulls.</summary>
    public bool IsNullable { get; }

    /// <summary>Gets whether the column belongs to the primary key.</summary>
    public bool IsPrimaryKey { get; }

    /// <summary>Gets the SQL default expression, when one was declared.</summary>
    public string? DefaultExpression { get; }
}

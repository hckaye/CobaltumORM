using System;

namespace CobaltumOrm;

/// <summary>
/// Declares a named SQL query on a class. The attribute may be repeated across
/// declarations of a partial class so a generator can collect multiple queries.
/// A statement that returns rows generates a result record and an async method that
/// materializes rows. An INSERT, UPDATE, DELETE, or TRUNCATE statement that does not
/// return rows generates a command definition and an async method that returns the
/// affected row count.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class QueryAttribute : Attribute
{
    /// <summary>
    /// Initializes a query declaration.
    /// </summary>
    /// <param name="name">The stable query name used by generated members.</param>
    /// <param name="sql">The SQL text associated with the query.</param>
    public QueryAttribute(string name, string sql)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A query name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL text is required.", nameof(sql));
        }

        Name = name;
        Sql = sql;
    }

    /// <summary>Gets the stable query name.</summary>
    public string Name { get; }

    /// <summary>Gets the SQL text exactly as supplied.</summary>
    public string Sql { get; }
}

/// <summary>
/// Declares a named SQL query whose returned rows are mapped to
/// <typeparamref name="TResult"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class QueryAttribute<TResult> : Attribute
{
    /// <summary>Initializes a typed query declaration.</summary>
    /// <param name="name">The stable query name used by generated members.</param>
    /// <param name="sql">The SQL text associated with the query.</param>
    public QueryAttribute(string name, string sql)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A query name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL text is required.", nameof(sql));
        }

        Name = name;
        Sql = sql;
    }

    /// <summary>Gets the stable query name.</summary>
    public string Name { get; }

    /// <summary>Gets the SQL text exactly as supplied.</summary>
    public string Sql { get; }
}

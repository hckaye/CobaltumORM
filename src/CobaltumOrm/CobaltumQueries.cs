using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace CobaltumOrm;

/// <summary>A generated, strongly typed query with an explicit parameter record.</summary>
public sealed class CobaltumQueryDefinition<TParameters, TResult>
{
    private readonly Action<DbCommand, TParameters> _bind;
    private readonly Func<DbDataReader, TResult> _materialize;

    /// <summary>Initializes a query definition. This constructor is primarily intended for generated code.</summary>
    public CobaltumQueryDefinition(
        string sql,
        Action<DbCommand, TParameters> bind,
        Func<DbDataReader, TResult> materialize)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL text is required.", nameof(sql));
        }

        Sql = sql;
        _bind = bind ?? throw new ArgumentNullException(nameof(bind));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
    }

    /// <summary>Gets the validated SQL text.</summary>
    public string Sql { get; }

    internal void Bind(DbCommand command, TParameters parameters) => _bind(command, parameters);

    internal TResult Materialize(DbDataReader reader) => _materialize(reader);
}

/// <summary>A generated, strongly typed query whose values are already bound.</summary>
public sealed class CobaltumQueryDefinition<TResult>
{
    private readonly Action<DbCommand> _bind;
    private readonly Func<DbDataReader, TResult> _materialize;
    private readonly bool _hasWhereClause;
    private readonly int _nextWhereParameterIndex;

    /// <summary>Initializes a query definition. This constructor is primarily intended for generated code.</summary>
    public CobaltumQueryDefinition(
        string sql,
        Action<DbCommand> bind,
        Func<DbDataReader, TResult> materialize)
        : this(sql, bind, materialize, false, 0)
    {
    }

    internal CobaltumQueryDefinition(
        string sql,
        Action<DbCommand> bind,
        Func<DbDataReader, TResult> materialize,
        bool hasWhereClause,
        int nextWhereParameterIndex)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL text is required.", nameof(sql));
        }

        if (nextWhereParameterIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextWhereParameterIndex));
        }

        Sql = sql;
        _bind = bind ?? throw new ArgumentNullException(nameof(bind));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
        _hasWhereClause = hasWhereClause;
        _nextWhereParameterIndex = nextWhereParameterIndex;
    }

    /// <summary>Gets the validated SQL text.</summary>
    public string Sql { get; }

    /// <summary>
    /// Returns a new query that selects rows matching a generated, parameterized predicate.
    /// Predicates added to the same query are joined with AND.
    /// </summary>
    public CobaltumQueryDefinition<TResult> Where(CobaltumPredicate<TResult> predicate)
    {
        if (predicate is null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        var parameterName = "@__cobaltum_where_" + _nextWhereParameterIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var separator = _hasWhereClause ? " AND " : " WHERE ";
        return new CobaltumQueryDefinition<TResult>(
            Sql + separator + predicate.SqlWithParameter(parameterName),
            command =>
            {
                _bind(command);
                predicate.Bind(command, parameterName);
            },
            _materialize,
            true,
            _nextWhereParameterIndex + 1);
    }

    /// <summary>Returns this query when <paramref name="condition"/> is false; otherwise adds the predicate.</summary>
    public CobaltumQueryDefinition<TResult> WhereIf(
        bool condition,
        CobaltumPredicate<TResult> predicate)
    {
        return condition ? Where(predicate) : this;
    }

    /// <summary>
    /// Returns this query when <paramref name="condition"/> is false; otherwise invokes
    /// the factory and adds its generated predicate.
    /// </summary>
    public CobaltumQueryDefinition<TResult> WhereIf(
        bool condition,
        Func<CobaltumPredicate<TResult>> predicateFactory)
    {
        if (!condition)
        {
            return this;
        }

        if (predicateFactory is null)
        {
            throw new ArgumentNullException(nameof(predicateFactory));
        }

        return Where(predicateFactory());
    }

    internal void Bind(DbCommand command) => _bind(command);

    internal TResult Materialize(DbDataReader reader) => _materialize(reader);
}

/// <summary>
/// A raw SQL command created by <see cref="CobaltumQueryExtensions.Query(DbConnection,string,DbTransaction?)"/>
/// or <see cref="CobaltumQueryExtensions.NoCheckQuery(DbConnection,string,DbTransaction?)"/>.
/// </summary>
public sealed class CobaltumRawQuery
{
    private readonly DbConnection _connection;
    private readonly DbTransaction? _transaction;
    private readonly ReadOnlyCollection<CobaltumRawParameter> _parameters;

    internal CobaltumRawQuery(DbConnection connection, string sql, DbTransaction? transaction)
        : this(connection, sql, transaction, Array.Empty<CobaltumRawParameter>())
    {
    }

    private CobaltumRawQuery(
        DbConnection connection,
        string sql,
        DbTransaction? transaction,
        IEnumerable<CobaltumRawParameter> parameters)
    {
        _connection = connection;
        Sql = sql;
        _transaction = transaction;
        _parameters = new List<CobaltumRawParameter>(parameters).AsReadOnly();
    }

    /// <summary>Gets the SQL supplied by the caller.</summary>
    public string Sql { get; }

    /// <summary>
    /// Returns a new raw query with one provider parameter. The value is never interpolated
    /// into SQL; null is assigned to the provider parameter as <see cref="DBNull.Value"/>.
    /// </summary>
    public CobaltumRawQuery WithParameter(string name, object? value, DbType? dbType = null)
    {
        return WithParameterCore(name, value, dbType, null);
    }

    /// <summary>Returns a new raw query with provider-specific parameter configuration.</summary>
    public CobaltumRawQuery WithConfiguredParameter(
        string name,
        object? value,
        DbType dbType,
        Action<DbParameter> configureParameter)
    {
        return WithParameterCore(
            name,
            value,
            dbType,
            configureParameter ?? throw new ArgumentNullException(nameof(configureParameter)));
    }

    private CobaltumRawQuery WithParameterCore(
        string name,
        object? value,
        DbType? dbType,
        Action<DbParameter>? configureParameter)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A parameter name is required.", nameof(name));
        }

        foreach (var existing in _parameters)
        {
            if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Parameter '{name}' has already been added.", nameof(name));
            }
        }

        var parameters = new List<CobaltumRawParameter>(_parameters)
        {
            new CobaltumRawParameter(name, value, dbType, configureParameter),
        };
        return new CobaltumRawQuery(_connection, Sql, _transaction, parameters);
    }

    /// <summary>
    /// Executes the SQL as a provider-neutral non-query command. A closed connection is
    /// opened asynchronously and closed again; an already-open connection remains open.
    /// </summary>
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var closeWhenFinished = await CobaltumConnection.OpenIfNeededAsync(
            _connection,
            _transaction,
            cancellationToken).ConfigureAwait(false);
        try
        {
            using (var command = BuildCommand())
            {
                return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CobaltumConnection.CloseIfOpened(_connection, closeWhenFinished);
        }
    }

    /// <summary>
    /// Executes the SQL as a provider-neutral query and returns untyped immutable rows.
    /// Database null is represented by null. Duplicate column names remain available by
    /// ordinal and through <see cref="CobaltumRawRow.GetValues(string)"/>.
    /// A closed connection is opened asynchronously and closed again.
    /// </summary>
    public async Task<IReadOnlyList<CobaltumRawRow>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var closeWhenFinished = await CobaltumConnection.OpenIfNeededAsync(
            _connection,
            _transaction,
            cancellationToken).ConfigureAwait(false);
        try
        {
            using (var command = BuildCommand())
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                var names = new string[reader.FieldCount];
                for (var ordinal = 0; ordinal < names.Length; ordinal++)
                {
                    names[ordinal] = reader.GetName(ordinal);
                }

                var rows = new List<CobaltumRawRow>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var values = new object?[reader.FieldCount];
                    for (var ordinal = 0; ordinal < values.Length; ordinal++)
                    {
                        values[ordinal] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
                    }

                    rows.Add(new CobaltumRawRow(names, values));
                }

                return rows.AsReadOnly();
            }
        }
        finally
        {
            CobaltumConnection.CloseIfOpened(_connection, closeWhenFinished);
        }
    }

    private DbCommand BuildCommand()
    {
        var command = _connection.CreateCommand();
        try
        {
            command.CommandText = Sql;
            command.Transaction = _transaction;
            foreach (var rawParameter in _parameters)
            {
                if (rawParameter.DbType.HasValue)
                {
                    if (rawParameter.ConfigureParameter != null)
                    {
                        CobaltumParameter.AddConfigured(
                            command,
                            rawParameter.Name,
                            rawParameter.Value,
                            rawParameter.DbType.Value,
                            rawParameter.ConfigureParameter);
                    }
                    else
                    {
                        CobaltumParameter.Add(
                            command,
                            rawParameter.Name,
                            rawParameter.Value,
                            rawParameter.DbType.Value);
                    }
                }
                else
                {
                    CobaltumParameter.Add(command, rawParameter.Name, rawParameter.Value);
                }
            }

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }
}

internal sealed class CobaltumRawParameter
{
    internal CobaltumRawParameter(
        string name,
        object? value,
        DbType? dbType,
        Action<DbParameter>? configureParameter = null)
    {
        Name = name;
        Value = value;
        DbType = dbType;
        ConfigureParameter = configureParameter;
    }

    internal string Name { get; }
    internal object? Value { get; }
    internal DbType? DbType { get; }
    internal Action<DbParameter>? ConfigureParameter { get; }
}

/// <summary>Describes one named parameter inferred by compile-time SQL analysis.</summary>
public readonly struct CobaltumExpectedParameter
{
    /// <summary>Initializes an expected named parameter. This constructor is intended for generated code.</summary>
    public CobaltumExpectedParameter(string name, DbType dbType)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        DbType = dbType;
        DatabaseTypeName = null;
        ConfigureParameter = null;
    }

    /// <summary>Initializes an expected named parameter with provider-specific configuration.</summary>
    public CobaltumExpectedParameter(
        string name,
        DbType dbType,
        string databaseTypeName,
        Action<DbParameter> configureParameter)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        DbType = dbType;
        DatabaseTypeName = string.IsNullOrWhiteSpace(databaseTypeName)
            ? throw new ArgumentException("A database type name is required.", nameof(databaseTypeName))
            : databaseTypeName;
        ConfigureParameter = configureParameter ?? throw new ArgumentNullException(nameof(configureParameter));
    }

    /// <summary>Gets the provider parameter name.</summary>
    public string Name { get; }

    /// <summary>Gets the database type inferred from the SQL expression.</summary>
    public DbType DbType { get; }

    /// <summary>Gets the database-specific type name when <see cref="DbType"/> is not sufficiently precise.</summary>
    public string? DatabaseTypeName { get; }

    internal Action<DbParameter>? ConfigureParameter { get; }
}

/// <summary>A compile-time-checked statement whose result row is generated from its SQL shape.</summary>
public sealed class CobaltumTypedQuery<TResult>
{
    private readonly DbConnection _connection;
    private readonly DbTransaction? _transaction;
    private readonly CobaltumQueryDefinition<TResult> _definition;
    private readonly ReadOnlyCollection<CobaltumExpectedParameter> _expectedParameters;
    private readonly ReadOnlyCollection<CobaltumRawParameter> _parameters;

    internal CobaltumTypedQuery(
        DbConnection connection,
        CobaltumQueryDefinition<TResult> definition,
        DbTransaction? transaction,
        IEnumerable<CobaltumExpectedParameter> expectedParameters)
        : this(
            connection,
            definition,
            transaction,
            new List<CobaltumExpectedParameter>(expectedParameters).AsReadOnly(),
            new List<CobaltumRawParameter>().AsReadOnly())
    {
    }

    private CobaltumTypedQuery(
        DbConnection connection,
        CobaltumQueryDefinition<TResult> definition,
        DbTransaction? transaction,
        ReadOnlyCollection<CobaltumExpectedParameter> expectedParameters,
        ReadOnlyCollection<CobaltumRawParameter> parameters)
    {
        _connection = connection;
        _definition = definition;
        _transaction = transaction;
        _expectedParameters = expectedParameters;
        _parameters = parameters;
    }

    /// <summary>Gets the validated SQL text.</summary>
    public string Sql => _definition.Sql;

    /// <summary>Adds a named value whose name and database type were inferred at compile time.</summary>
    public CobaltumTypedQuery<TResult> WithParameter(string name, object? value, DbType? dbType = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A parameter name is required.", nameof(name));
        }

        CobaltumExpectedParameter expected = default;
        var found = false;
        foreach (var candidate in _expectedParameters)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                expected = candidate;
                found = true;
                break;
            }
        }

        if (!found)
        {
            throw new ArgumentException(
                $"Parameter '{name}' is not used by this checked query.",
                nameof(name));
        }

        foreach (var existing in _parameters)
        {
            if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Parameter '{name}' has already been added.", nameof(name));
            }
        }

        if (dbType.HasValue && dbType.Value != expected.DbType)
        {
            throw new ArgumentException(
                $"Parameter '{name}' requires DbType.{expected.DbType}, not DbType.{dbType.Value}.",
                nameof(dbType));
        }

        if (!CobaltumParameter.IsCompatibleValue(expected.DbType, value))
        {
            throw new ArgumentException(
                $"The CLR value for parameter '{name}' is not compatible with DbType.{expected.DbType}.",
                nameof(value));
        }

        var parameters = new List<CobaltumRawParameter>(_parameters)
        {
            new CobaltumRawParameter(expected.Name, value, expected.DbType, expected.ConfigureParameter),
        };
        return new CobaltumTypedQuery<TResult>(
            _connection,
            _definition,
            _transaction,
            _expectedParameters,
            parameters.AsReadOnly());
    }

    /// <summary>Executes the checked statement and materializes its generated row type.</summary>
    public async Task<IReadOnlyList<TResult>> ReadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var expected in _expectedParameters)
        {
            var found = false;
            foreach (var parameter in _parameters)
            {
                if (string.Equals(parameter.Name, expected.Name, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new InvalidOperationException(
                    $"Checked query parameter '{expected.Name}' has not been supplied with WithParameter.");
            }
        }

        var closeWhenFinished = await CobaltumConnection.OpenIfNeededAsync(
            _connection,
            _transaction,
            cancellationToken).ConfigureAwait(false);
        try
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = _definition.Sql;
                command.Transaction = _transaction;
                _definition.Bind(command);
                foreach (var rawParameter in _parameters)
                {
                    if (rawParameter.ConfigureParameter != null)
                    {
                        CobaltumParameter.AddConfigured(
                            command,
                            rawParameter.Name,
                            rawParameter.Value,
                            rawParameter.DbType!.Value,
                            rawParameter.ConfigureParameter);
                    }
                    else
                    {
                        CobaltumParameter.Add(
                            command,
                            rawParameter.Name,
                            rawParameter.Value,
                            rawParameter.DbType!.Value);
                    }
                }

                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    var rows = new List<TResult>();
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        rows.Add(_definition.Materialize(reader));
                    }

                    return rows.AsReadOnly();
                }
            }
        }
        finally
        {
            CobaltumConnection.CloseIfOpened(_connection, closeWhenFinished);
        }
    }
}

/// <summary>
/// One immutable row returned by a raw query. Column names and values retain provider
/// ordinal order, including duplicate names.
/// </summary>
public sealed class CobaltumRawRow : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly ReadOnlyCollection<string> _columnNames;
    private readonly ReadOnlyCollection<object?> _values;

    internal CobaltumRawRow(IEnumerable<string> columnNames, IEnumerable<object?> values)
    {
        _columnNames = new List<string>(columnNames).AsReadOnly();
        _values = new List<object?>(values).AsReadOnly();
        if (_columnNames.Count != _values.Count)
        {
            throw new ArgumentException("Column names and values must have the same count.");
        }
    }

    /// <summary>Gets the number of fields in the row.</summary>
    public int FieldCount => _values.Count;

    /// <summary>Gets column names in provider ordinal order. Duplicate names are preserved.</summary>
    public IReadOnlyList<string> ColumnNames => _columnNames;

    /// <summary>Gets a value by ordinal.</summary>
    public object? this[int ordinal] => _values[ordinal];

    /// <summary>
    /// Gets the value for an exact, ordinal-case-sensitive column name. An ambiguous
    /// duplicate name throws; use <see cref="GetValues(string)"/> in that case.
    /// </summary>
    public object? this[string name]
    {
        get
        {
            if (!TryFindUniqueOrdinal(name, out var ordinal, out var ambiguous))
            {
                if (ambiguous)
                {
                    throw new InvalidOperationException(
                        $"Column name '{name}' occurs more than once; use an ordinal or GetValues.");
                }

                throw new KeyNotFoundException($"Column '{name}' was not returned by the query.");
            }

            return _values[ordinal];
        }
    }

    /// <summary>Gets the provider name for one ordinal.</summary>
    public string GetName(int ordinal) => _columnNames[ordinal];

    /// <summary>Gets every value whose column name exactly matches <paramref name="name"/>.</summary>
    public IReadOnlyList<object?> GetValues(string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        var matches = new List<object?>();
        for (var ordinal = 0; ordinal < _columnNames.Count; ordinal++)
        {
            if (string.Equals(_columnNames[ordinal], name, StringComparison.Ordinal))
            {
                matches.Add(_values[ordinal]);
            }
        }

        return matches.AsReadOnly();
    }

    /// <summary>
    /// Tries to get a uniquely named column. Returns false for a missing or duplicate name.
    /// </summary>
    public bool TryGetValue(string name, out object? value)
    {
        if (!TryFindUniqueOrdinal(name, out var ordinal, out _))
        {
            value = null;
            return false;
        }

        value = _values[ordinal];
        return true;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var ordinal = 0; ordinal < _columnNames.Count; ordinal++)
        {
            yield return new KeyValuePair<string, object?>(_columnNames[ordinal], _values[ordinal]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private bool TryFindUniqueOrdinal(string name, out int ordinal, out bool ambiguous)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        ordinal = -1;
        ambiguous = false;
        for (var index = 0; index < _columnNames.Count; index++)
        {
            if (!string.Equals(_columnNames[index], name, StringComparison.Ordinal))
            {
                continue;
            }

            if (ordinal >= 0)
            {
                ambiguous = true;
                return false;
            }

            ordinal = index;
        }

        return ordinal >= 0;
    }
}

/// <summary>Provider-neutral execution methods used by raw and generated queries.</summary>
public static class CobaltumQueryExtensions
{
    /// <summary>
    /// Creates the source-level checked Query call. The build transform replaces SELECT calls
    /// with a shape-specific typed query; literal DML remains a raw executable command.
    /// </summary>
    public static CobaltumRawQuery Query(
        this DbConnection connection,
        [System.Diagnostics.CodeAnalysis.StringSyntax("sql")] string sql,
        DbTransaction? transaction = null)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL text is required.", nameof(sql));
        }

        return new CobaltumRawQuery(connection, sql, transaction);
    }

    /// <summary>
    /// Creates an explicitly untyped command without CobaltumORM compile-time SQL validation.
    /// </summary>
    public static CobaltumRawQuery NoCheckQuery(
        this DbConnection connection,
        [System.Diagnostics.CodeAnalysis.StringSyntax("sql")] string sql,
        DbTransaction? transaction = null) => Query(connection, sql, transaction);

    /// <summary>
    /// Creates an explicitly untyped command for genuinely dynamic SQL.
    /// <see cref="NoCheckQuery(DbConnection,string,DbTransaction?)"/> is the preferred explicit
    /// name when bypassing CobaltumORM compile-time SQL validation.
    /// </summary>
    public static CobaltumRawQuery QueryDynamic(
        this DbConnection connection,
        [System.Diagnostics.CodeAnalysis.StringSyntax("sql")] string sql,
        DbTransaction? transaction = null) => NoCheckQuery(connection, sql, transaction);

    /// <summary>Creates a checked typed query. This method is intended for transformed source.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static CobaltumTypedQuery<TResult> QueryChecked<TResult>(
        this DbConnection connection,
        CobaltumQueryDefinition<TResult> query,
        DbTransaction? transaction = null,
        params CobaltumExpectedParameter[] expectedParameters)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        return new CobaltumTypedQuery<TResult>(
            connection,
            query,
            transaction,
            expectedParameters ?? Array.Empty<CobaltumExpectedParameter>());
    }

    /// <summary>
    /// Executes a generated typed query and materializes all rows. A closed connection
    /// is opened asynchronously and closed again; an already-open connection remains open.
    /// </summary>
    public static async Task<IReadOnlyList<TResult>> Query<TParameters, TResult>(
        this DbConnection connection,
        CobaltumQueryDefinition<TParameters, TResult> query,
        TParameters parameters,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        var closeWhenFinished = await CobaltumConnection.OpenIfNeededAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        try
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = query.Sql;
                command.Transaction = transaction;
                query.Bind(command, parameters);
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    var rows = new List<TResult>();
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        rows.Add(query.Materialize(reader));
                    }

                    return rows.AsReadOnly();
                }
            }
        }
        finally
        {
            CobaltumConnection.CloseIfOpened(connection, closeWhenFinished);
        }
    }

    /// <summary>
    /// Executes a generated typed query whose values are already bound. A closed connection
    /// is opened asynchronously and closed again; an already-open connection remains open.
    /// </summary>
    public static async Task<IReadOnlyList<TResult>> Query<TResult>(
        this DbConnection connection,
        CobaltumQueryDefinition<TResult> query,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        var closeWhenFinished = await CobaltumConnection.OpenIfNeededAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        try
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = query.Sql;
                command.Transaction = transaction;
                query.Bind(command);
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    var rows = new List<TResult>();
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        rows.Add(query.Materialize(reader));
                    }

                    return rows.AsReadOnly();
                }
            }
        }
        finally
        {
            CobaltumConnection.CloseIfOpened(connection, closeWhenFinished);
        }
    }
}

internal static class CobaltumConnection
{
    internal static async Task<bool> OpenIfNeededAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Closed && connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                $"The connection must be closed or open before executing a query; current state is {connection.State}.");
        }

        if (transaction != null)
        {
            if (!ReferenceEquals(transaction.Connection, connection))
            {
                throw new ArgumentException("The transaction does not belong to the query connection.", nameof(transaction));
            }

            if (connection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException("A query connection with a transaction must already be open.");
            }

            return false;
        }

        if (connection.State == ConnectionState.Open)
        {
            return false;
        }

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static void CloseIfOpened(DbConnection connection, bool closeWhenFinished)
    {
        if (closeWhenFinished && connection.State != ConnectionState.Closed)
        {
            connection.Close();
        }
    }
}

/// <summary>Helpers used by generated parameter binders.</summary>
public static class CobaltumParameter
{
    /// <summary>Adds a named provider-neutral input parameter, mapping null to <see cref="DBNull.Value"/>.</summary>
    public static void Add(DbCommand command, string name, object? value)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Adds a named provider-neutral input parameter with an explicit inferred type,
    /// mapping null to <see cref="DBNull.Value"/>.
    /// </summary>
    public static void Add(DbCommand command, string name, object? value, DbType dbType)
    {
        AddCore(command, name, value, dbType, null);
    }

    /// <summary>Adds a named input parameter with provider-specific configuration.</summary>
    public static void AddConfigured(
        DbCommand command,
        string name,
        object? value,
        DbType dbType,
        Action<DbParameter> configureParameter)
    {
        AddCore(
            command,
            name,
            value,
            dbType,
            configureParameter ?? throw new ArgumentNullException(nameof(configureParameter)));
    }

    private static void AddCore(
        DbCommand command,
        string name,
        object? value,
        DbType dbType,
        Action<DbParameter>? configureParameter)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = dbType;
        configureParameter?.Invoke(parameter);
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    internal static bool IsCompatibleValue(DbType dbType, object? value)
    {
        if (value is null || value == DBNull.Value)
        {
            return true;
        }

        switch (dbType)
        {
            case DbType.Boolean: return value is bool;
            case DbType.Int16: return value is short;
            case DbType.Int32: return value is int;
            case DbType.Int64: return value is long;
            case DbType.Single: return value is float;
            case DbType.Double: return value is double;
            case DbType.Decimal: return value is decimal;
            case DbType.String: return value is string;
            case DbType.Guid: return value is Guid;
            case DbType.Date: return value is DateTime || value.GetType().FullName == "System.DateOnly";
            case DbType.Time: return value is TimeSpan || value.GetType().FullName == "System.TimeOnly";
            case DbType.DateTime:
            case DbType.DateTime2: return value is DateTime;
            case DbType.DateTimeOffset: return value is DateTimeOffset;
            case DbType.Binary: return value is byte[];
            case DbType.Object: return true;
            default: return false;
        }
    }
}

/// <summary>A type-safe equality predicate for one generated table record.</summary>
public sealed class CobaltumPredicate<TRecord>
{
    private readonly string _quotedName;
    private readonly object? _value;
    private readonly bool _isNull;
    private readonly DbType? _dbType;
    private readonly Action<DbParameter>? _configureParameter;

    internal CobaltumPredicate(
        string quotedName,
        object? value,
        DbType? dbType = null,
        Action<DbParameter>? configureParameter = null)
    {
        _quotedName = quotedName ?? throw new ArgumentNullException(nameof(quotedName));
        _value = value;
        _isNull = value is null;
        _dbType = dbType;
        _configureParameter = configureParameter;
    }

    internal string Sql => SqlWithParameter("@value");

    internal string SqlWithParameter(string parameterName)
    {
        if (_isNull)
        {
            return _quotedName + " IS NULL";
        }

        return _quotedName + " = " + parameterName;
    }

    internal void Bind(DbCommand command) => Bind(command, "value");

    internal void Bind(DbCommand command, string parameterName)
    {
        if (!_isNull)
        {
            if (_dbType.HasValue)
            {
                if (_configureParameter != null)
                {
                    CobaltumParameter.AddConfigured(
                        command,
                        parameterName,
                        _value,
                        _dbType.Value,
                        _configureParameter);
                }
                else
                {
                    CobaltumParameter.Add(command, parameterName, _value, _dbType.Value);
                }
            }
            else
            {
                CobaltumParameter.Add(command, parameterName, _value);
            }
        }
    }
}

/// <summary>A generated table column that accepts values of <typeparamref name="TValue"/>.</summary>
public sealed class CobaltumColumn<TRecord, TValue>
{
    private readonly string _quotedName;
    private readonly DbType? _dbType;
    private readonly Action<DbParameter>? _configureParameter;

    /// <summary>Initializes a generated column. This constructor is primarily intended for generated code.</summary>
    public CobaltumColumn(string quotedName)
    {
        _quotedName = quotedName ?? throw new ArgumentNullException(nameof(quotedName));
    }

    /// <summary>Initializes a generated column with its database parameter type.</summary>
    public CobaltumColumn(string quotedName, DbType dbType)
    {
        _quotedName = quotedName ?? throw new ArgumentNullException(nameof(quotedName));
        _dbType = dbType;
    }

    /// <summary>Initializes a generated column with provider-specific parameter configuration.</summary>
    public CobaltumColumn(
        string quotedName,
        DbType dbType,
        string databaseTypeName,
        Action<DbParameter> configureParameter)
    {
        _quotedName = quotedName ?? throw new ArgumentNullException(nameof(quotedName));
        _dbType = dbType;
        if (string.IsNullOrWhiteSpace(databaseTypeName))
        {
            throw new ArgumentException("A database type name is required.", nameof(databaseTypeName));
        }

        _configureParameter = configureParameter ?? throw new ArgumentNullException(nameof(configureParameter));
    }

    /// <summary>Builds a parameterized equality predicate.</summary>
    public CobaltumPredicate<TRecord> Equal(TValue value)
    {
        return new CobaltumPredicate<TRecord>(_quotedName, value, _dbType, _configureParameter);
    }
}

/// <summary>A generated, typed entry point for selecting rows from one table.</summary>
public abstract class CobaltumTable<TRecord>
{
    private readonly string _selectSql;
    private readonly Func<DbDataReader, TRecord> _materialize;

    /// <summary>Initializes a generated table entry. This constructor is primarily intended for generated code.</summary>
    protected CobaltumTable(string selectSql, Func<DbDataReader, TRecord> materialize)
    {
        _selectSql = selectSql ?? throw new ArgumentNullException(nameof(selectSql));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
    }

    /// <summary>Starts an immutable typed query for the table.</summary>
    public CobaltumQueryDefinition<TRecord> Query() =>
        new CobaltumQueryDefinition<TRecord>(_selectSql, _ => { }, _materialize);

    /// <summary>Selects every row from the table.</summary>
    public CobaltumQueryDefinition<TRecord> All() => Query();

    /// <summary>Selects rows matching a generated, parameterized predicate.</summary>
    public CobaltumQueryDefinition<TRecord> Where(CobaltumPredicate<TRecord> predicate)
    {
        if (predicate is null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        return new CobaltumQueryDefinition<TRecord>(
            _selectSql + " WHERE " + predicate.Sql,
            predicate.Bind,
            _materialize,
            true,
            1);
    }
}

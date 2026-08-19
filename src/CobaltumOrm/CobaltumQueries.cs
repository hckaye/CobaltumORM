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

/// <summary>
/// A generated, strongly typed command with an explicit parameter record. Commands execute
/// statements that do not return rows and report the affected row count.
/// </summary>
public sealed class CobaltumCommandDefinition<TParameters>
{
    private readonly Action<DbCommand, TParameters> _bind;

    /// <summary>Initializes a command definition. This constructor is primarily intended for generated code.</summary>
    public CobaltumCommandDefinition(
        string sql,
        Action<DbCommand, TParameters> bind)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL text is required.", nameof(sql));
        }

        Sql = sql;
        _bind = bind ?? throw new ArgumentNullException(nameof(bind));
    }

    /// <summary>Gets the validated SQL text.</summary>
    public string Sql { get; }

    internal void Bind(DbCommand command, TParameters parameters) => _bind(command, parameters);
}

/// <summary>
/// A generated command whose values are already bound. Commands execute statements that do
/// not return rows and report the affected row count.
/// </summary>
public sealed class CobaltumCommandDefinition
{
    private readonly Action<DbCommand> _bind;

    /// <summary>Initializes a command definition. This constructor is primarily intended for generated code.</summary>
    public CobaltumCommandDefinition(string sql, Action<DbCommand> bind)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL text is required.", nameof(sql));
        }

        Sql = sql;
        _bind = bind ?? throw new ArgumentNullException(nameof(bind));
    }

    /// <summary>Gets the validated SQL text.</summary>
    public string Sql { get; }

    internal void Bind(DbCommand command) => _bind(command);
}

/// <summary>A generated, strongly typed query whose values are already bound.</summary>
public sealed class CobaltumQueryDefinition<TResult>
{
    private readonly Action<DbCommand> _bind;
    private readonly Func<DbDataReader, TResult> _materialize;
    private readonly bool _hasWhereClause;
    private readonly int _nextWhereParameterIndex;
    private readonly bool _acceptsFilters;

    /// <summary>Initializes a query definition. This constructor is primarily intended for generated code.</summary>
    public CobaltumQueryDefinition(
        string sql,
        Action<DbCommand> bind,
        Func<DbDataReader, TResult> materialize)
        : this(sql, bind, materialize, false, 0)
    {
    }

    /// <summary>
    /// Creates a query definition that rejects <see cref="Where"/> and <see cref="WhereIf(bool,CobaltumPredicate{TResult})"/>.
    /// Statements that cannot carry a trailing WHERE clause, such as an INSERT that reports the
    /// stored row, use this factory. It is primarily intended for generated code.
    /// </summary>
    public static CobaltumQueryDefinition<TResult> WithoutFilters(
        string sql,
        Action<DbCommand> bind,
        Func<DbDataReader, TResult> materialize) =>
        new CobaltumQueryDefinition<TResult>(sql, bind, materialize, false, 0, false);

    internal CobaltumQueryDefinition(
        string sql,
        Action<DbCommand> bind,
        Func<DbDataReader, TResult> materialize,
        bool hasWhereClause,
        int nextWhereParameterIndex)
        : this(sql, bind, materialize, hasWhereClause, nextWhereParameterIndex, true)
    {
    }

    private CobaltumQueryDefinition(
        string sql,
        Action<DbCommand> bind,
        Func<DbDataReader, TResult> materialize,
        bool hasWhereClause,
        int nextWhereParameterIndex,
        bool acceptsFilters)
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
        _acceptsFilters = acceptsFilters;
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

        if (!_acceptsFilters)
        {
            throw new NotSupportedException(
                "This statement does not accept a WHERE clause.");
        }

        var startIndex = _nextWhereParameterIndex;
        var separator = _hasWhereClause ? " AND " : " WHERE ";
        return new CobaltumQueryDefinition<TResult>(
            Sql + separator + predicate.BuildSql(startIndex),
            command =>
            {
                _bind(command);
                predicate.Bind(command, startIndex);
            },
            _materialize,
            true,
            startIndex + predicate.ParameterCount,
            true);
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

        var providerName = CobaltumParameter.ProviderParameterName(name);

        foreach (var existing in _parameters)
        {
            if (string.Equals(
                    CobaltumParameter.ProviderParameterName(existing.Name),
                    providerName,
                    StringComparison.OrdinalIgnoreCase))
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

    internal async Task<IReadOnlyList<TResult>> ReadMappedAsync<TResult>(
        Func<DbDataReader, TResult> materialize,
        CancellationToken cancellationToken)
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
                var rows = new List<TResult>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(materialize(reader));
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

/// <summary>A raw SQL command that maps each returned row to a caller-supplied result type.</summary>
public sealed class MappedQuery<TResult>
{
    private readonly CobaltumRawQuery _query;
    private readonly Func<DbDataReader, TResult> _materialize;

    internal MappedQuery(CobaltumRawQuery query, Func<DbDataReader, TResult> materialize)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
    }

    /// <summary>Gets the SQL supplied by the caller.</summary>
    public string Sql => _query.Sql;

    /// <summary>Returns a new query with one provider parameter.</summary>
    public MappedQuery<TResult> WithParameter(string name, object? value, DbType? dbType = null) =>
        new MappedQuery<TResult>(_query.WithParameter(name, value, dbType), _materialize);

    /// <summary>Returns a new query with provider-specific parameter configuration.</summary>
    public MappedQuery<TResult> WithConfiguredParameter(
        string name,
        object? value,
        DbType dbType,
        Action<DbParameter> configureParameter) =>
        new MappedQuery<TResult>(
            _query.WithConfiguredParameter(name, value, dbType, configureParameter),
            _materialize);

    /// <summary>Executes the SQL as a provider-neutral non-query command.</summary>
    public Task<int> ExecuteAsync(CancellationToken cancellationToken = default) =>
        _query.ExecuteAsync(cancellationToken);

    /// <summary>Executes the SQL and maps every row to <typeparamref name="TResult"/>.</summary>
    public Task<IReadOnlyList<TResult>> ReadAsync(CancellationToken cancellationToken = default) =>
        _query.ReadMappedAsync(_materialize, cancellationToken);
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

/// <summary>
/// A generated query bound to one connection. Values are already supplied by the query
/// definition, so the statement runs as soon as <see cref="ReadAsync"/> is awaited.
/// </summary>
public sealed class CobaltumGeneratedQuery<TResult>
{
    private readonly DbConnection _connection;
    private readonly DbTransaction? _transaction;
    private readonly Action<DbCommand> _bind;
    private readonly Func<DbDataReader, TResult> _materialize;

    internal CobaltumGeneratedQuery(
        DbConnection connection,
        string sql,
        Action<DbCommand> bind,
        Func<DbDataReader, TResult> materialize,
        DbTransaction? transaction)
    {
        _connection = connection;
        Sql = sql;
        _bind = bind;
        _materialize = materialize;
        _transaction = transaction;
    }

    /// <summary>Gets the validated SQL text.</summary>
    public string Sql { get; }

    /// <summary>
    /// Executes the query and materializes every row. A closed connection is opened
    /// asynchronously and closed again; an already-open connection remains open.
    /// </summary>
    public async Task<IReadOnlyList<TResult>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var closeWhenFinished = await CobaltumConnection.OpenIfNeededAsync(
            _connection,
            _transaction,
            cancellationToken).ConfigureAwait(false);
        try
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = Sql;
                command.Transaction = _transaction;
                _bind(command);
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    var rows = new List<TResult>();
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        rows.Add(_materialize(reader));
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
/// A generated command bound to one connection. Values are already supplied by the command
/// definition, so the statement runs as soon as <see cref="ExecuteAsync"/> is awaited.
/// </summary>
public sealed class CobaltumGeneratedCommand
{
    private readonly DbConnection _connection;
    private readonly DbTransaction? _transaction;
    private readonly Action<DbCommand> _bind;

    internal CobaltumGeneratedCommand(
        DbConnection connection,
        string sql,
        Action<DbCommand> bind,
        DbTransaction? transaction)
    {
        _connection = connection;
        Sql = sql;
        _bind = bind;
        _transaction = transaction;
    }

    /// <summary>Gets the validated SQL text.</summary>
    public string Sql { get; }

    /// <summary>
    /// Executes the command and returns the affected row count reported by the provider.
    /// A closed connection is opened asynchronously and closed again; an already-open
    /// connection remains open.
    /// </summary>
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var closeWhenFinished = await CobaltumConnection.OpenIfNeededAsync(
            _connection,
            _transaction,
            cancellationToken).ConfigureAwait(false);
        try
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = Sql;
                command.Transaction = _transaction;
                _bind(command);
                return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CobaltumConnection.CloseIfOpened(_connection, closeWhenFinished);
        }
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
    /// Creates a checked query whose rows are mapped to <typeparamref name="TResult"/>.
    /// The build validates the SQL result shape and replaces this call with generated mapping code.
    /// </summary>
    public static MappedQuery<TResult> Query<TResult>(
        this DbConnection connection,
        [System.Diagnostics.CodeAnalysis.StringSyntax("sql")] string sql,
        DbTransaction? transaction = null) =>
        throw new NotSupportedException(
            "Query<TResult> requires CobaltumORM compile-time query transformation.");

    /// <summary>
    /// Creates an explicitly untyped command without CobaltumORM compile-time SQL validation.
    /// </summary>
    public static CobaltumRawQuery NoCheckQuery(
        this DbConnection connection,
        [System.Diagnostics.CodeAnalysis.StringSyntax("sql")] string sql,
        DbTransaction? transaction = null) => Query(connection, sql, transaction);

    /// <summary>
    /// Creates a query without compile-time SQL validation and maps returned rows to
    /// <typeparamref name="TResult"/> at runtime.
    /// </summary>
    public static MappedQuery<TResult> NoCheckQuery<TResult>(
        this DbConnection connection,
        [System.Diagnostics.CodeAnalysis.StringSyntax("sql")] string sql,
        DbTransaction? transaction = null) =>
        throw new NotSupportedException(
            "NoCheckQuery<TResult> requires CobaltumORM compile-time query transformation.");

    /// <summary>Creates a mapped unchecked query. This method is intended for transformed source.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static MappedQuery<TResult> NoCheckQueryMapped<TResult>(
        this DbConnection connection,
        string sql,
        Func<DbDataReader, TResult> materialize,
        DbTransaction? transaction = null) =>
        new MappedQuery<TResult>(
            NoCheckQuery(connection, sql, transaction),
            materialize);

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
    /// Executes a generated command and returns the affected row count. A closed connection
    /// is opened asynchronously and closed again; an already-open connection remains open.
    /// </summary>
    public static Task<int> ExecuteAsync<TParameters>(
        this DbConnection connection,
        CobaltumCommandDefinition<TParameters> command,
        TParameters parameters,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        Query(connection, command, parameters, transaction).ExecuteAsync(cancellationToken);

    /// <summary>
    /// Binds a generated command whose values are already supplied. Call
    /// <see cref="CobaltumGeneratedCommand.ExecuteAsync"/> to run it.
    /// </summary>
    public static CobaltumGeneratedCommand Query(
        this DbConnection connection,
        CobaltumCommandDefinition command,
        DbTransaction? transaction = null)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        return new CobaltumGeneratedCommand(connection, command.Sql, command.Bind, transaction);
    }

    /// <summary>
    /// Binds a generated command to its parameter record. Call
    /// <see cref="CobaltumGeneratedCommand.ExecuteAsync"/> to run it.
    /// </summary>
    public static CobaltumGeneratedCommand Query<TParameters>(
        this DbConnection connection,
        CobaltumCommandDefinition<TParameters> command,
        TParameters parameters,
        DbTransaction? transaction = null)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        return new CobaltumGeneratedCommand(
            connection,
            command.Sql,
            dbCommand => command.Bind(dbCommand, parameters),
            transaction);
    }

    /// <summary>
    /// Binds a generated typed query to its parameter record. Call
    /// <see cref="CobaltumGeneratedQuery{TResult}.ReadAsync"/> to materialize rows.
    /// </summary>
    public static CobaltumGeneratedQuery<TResult> Query<TParameters, TResult>(
        this DbConnection connection,
        CobaltumQueryDefinition<TParameters, TResult> query,
        TParameters parameters,
        DbTransaction? transaction = null)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        return new CobaltumGeneratedQuery<TResult>(
            connection,
            query.Sql,
            command => query.Bind(command, parameters),
            query.Materialize,
            transaction);
    }

    /// <summary>
    /// Binds a generated typed query whose values are already supplied. Call
    /// <see cref="CobaltumGeneratedQuery{TResult}.ReadAsync"/> to materialize rows.
    /// </summary>
    public static CobaltumGeneratedQuery<TResult> Query<TResult>(
        this DbConnection connection,
        CobaltumQueryDefinition<TResult> query,
        DbTransaction? transaction = null)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        return new CobaltumGeneratedQuery<TResult>(
            connection,
            query.Sql,
            query.Bind,
            query.Materialize,
            transaction);
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

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            CloseIfOpened(connection, true);
            throw;
        }
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
        parameter.ParameterName = ProviderParameterName(name);
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
        parameter.ParameterName = ProviderParameterName(name);
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

    internal static string ProviderParameterName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A parameter name is required.", nameof(name));
        }

        if (name[0] == ':')
        {
            if (name.Length == 1)
            {
                throw new ArgumentException("A parameter name must follow ':'.", nameof(name));
            }

            return name.Substring(1);
        }

        return name;
    }
}

/// <summary>
/// A type-safe predicate for one generated table record. A predicate compares one column with
/// one value, a list of values, or a range; <see cref="And"/>, <see cref="Or"/>, and the
/// <c>&amp;</c> and <c>|</c> operators combine predicates, keeping each combination parenthesized.
/// </summary>
public sealed class CobaltumPredicate<TRecord>
{
    private const string ParameterBaseName = "__cobaltum_where_";

    private enum PredicateKind
    {
        Comparison,
        NullTest,
        InList,
        Range,
        Combination,
    }

    private static readonly object?[] NoValues = new object?[0];

    private readonly PredicateKind _kind;
    private readonly string? _quotedName;
    private readonly string _comparison;
    private readonly object?[] _values;
    private readonly DbType? _dbType;
    private readonly Action<DbParameter>? _configureParameter;
    private readonly char _parameterPrefix;
    private readonly CobaltumPredicate<TRecord>? _left;
    private readonly CobaltumPredicate<TRecord>? _right;
    private readonly bool _isOr;

    private CobaltumPredicate(
        PredicateKind kind,
        string quotedName,
        string comparison,
        object?[] values,
        DbType? dbType,
        Action<DbParameter>? configureParameter,
        char parameterPrefix)
    {
        _kind = kind;
        _quotedName = quotedName ?? throw new ArgumentNullException(nameof(quotedName));
        _comparison = comparison;
        _values = values;
        _dbType = dbType;
        _configureParameter = configureParameter;
        _parameterPrefix = parameterPrefix;
    }

    private CobaltumPredicate(
        CobaltumPredicate<TRecord> left,
        CobaltumPredicate<TRecord> right,
        bool isOr)
    {
        _kind = PredicateKind.Combination;
        _comparison = string.Empty;
        _values = NoValues;
        _left = left;
        _right = right;
        _isOr = isOr;
        _parameterPrefix = left._parameterPrefix;
    }

    /// <summary>Combines this predicate and <paramref name="other"/> with AND.</summary>
    public CobaltumPredicate<TRecord> And(CobaltumPredicate<TRecord> other) =>
        Combine(this, other, false);

    /// <summary>Combines this predicate and <paramref name="other"/> with OR.</summary>
    public CobaltumPredicate<TRecord> Or(CobaltumPredicate<TRecord> other) =>
        Combine(this, other, true);

    /// <summary>Returns this predicate when <paramref name="condition"/> is false; otherwise combines with AND.</summary>
    public CobaltumPredicate<TRecord> AndIf(bool condition, CobaltumPredicate<TRecord> other) =>
        condition ? And(other) : this;

    /// <summary>
    /// Returns this predicate when <paramref name="condition"/> is false; otherwise invokes
    /// the factory and combines its predicate with AND.
    /// </summary>
    public CobaltumPredicate<TRecord> AndIf(
        bool condition,
        Func<CobaltumPredicate<TRecord>> predicateFactory)
    {
        if (!condition)
        {
            return this;
        }

        if (predicateFactory is null)
        {
            throw new ArgumentNullException(nameof(predicateFactory));
        }

        return And(predicateFactory());
    }

    /// <summary>Returns this predicate when <paramref name="condition"/> is false; otherwise combines with OR.</summary>
    public CobaltumPredicate<TRecord> OrIf(bool condition, CobaltumPredicate<TRecord> other) =>
        condition ? Or(other) : this;

    /// <summary>
    /// Returns this predicate when <paramref name="condition"/> is false; otherwise invokes
    /// the factory and combines its predicate with OR.
    /// </summary>
    public CobaltumPredicate<TRecord> OrIf(
        bool condition,
        Func<CobaltumPredicate<TRecord>> predicateFactory)
    {
        if (!condition)
        {
            return this;
        }

        if (predicateFactory is null)
        {
            throw new ArgumentNullException(nameof(predicateFactory));
        }

        return Or(predicateFactory());
    }

    /// <summary>Combines two predicates with AND. <c>&amp;&amp;</c> resolves to this operator.</summary>
    public static CobaltumPredicate<TRecord> operator &(
        CobaltumPredicate<TRecord> left,
        CobaltumPredicate<TRecord> right) =>
        Combine(left, right, false);

    /// <summary>Combines two predicates with OR. <c>||</c> resolves to this operator.</summary>
    public static CobaltumPredicate<TRecord> operator |(
        CobaltumPredicate<TRecord> left,
        CobaltumPredicate<TRecord> right) =>
        Combine(left, right, true);

    /// <summary>
    /// Always false, which makes <c>&amp;&amp;</c> read both sides and combine them with AND.
    /// A predicate describes SQL rather than a C# truth value, so it is never true on its own.
    /// </summary>
    public static bool operator false(CobaltumPredicate<TRecord> predicate) => false;

    /// <summary>
    /// Always false, which makes <c>||</c> read both sides and combine them with OR.
    /// A predicate describes SQL rather than a C# truth value, so it is never true on its own.
    /// </summary>
    public static bool operator true(CobaltumPredicate<TRecord> predicate) => false;

    internal static CobaltumPredicate<TRecord> Comparison(
        string quotedName,
        string comparison,
        object? value,
        DbType? dbType,
        Action<DbParameter>? configureParameter,
        char parameterPrefix)
    {
        if (value is null)
        {
            throw new ArgumentNullException(
                nameof(value),
                "A null value can only be compared with IsNull or IsNotNull.");
        }

        return new CobaltumPredicate<TRecord>(
            PredicateKind.Comparison,
            quotedName,
            comparison,
            new[] { value },
            dbType,
            configureParameter,
            parameterPrefix);
    }

    internal static CobaltumPredicate<TRecord> NullTest(
        string quotedName,
        bool negated,
        char parameterPrefix) =>
        new CobaltumPredicate<TRecord>(
            PredicateKind.NullTest,
            quotedName,
            negated ? "IS NOT NULL" : "IS NULL",
            NoValues,
            null,
            null,
            parameterPrefix);

    internal static CobaltumPredicate<TRecord> InList(
        string quotedName,
        bool negated,
        IEnumerable<object?> values,
        DbType? dbType,
        Action<DbParameter>? configureParameter,
        char parameterPrefix)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        var list = new List<object?>();
        foreach (var value in values)
        {
            if (value is null)
            {
                throw new ArgumentException(
                    "A value list cannot contain null. Combine the predicate with IsNull instead.",
                    nameof(values));
            }

            list.Add(value);
        }

        if (list.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        return new CobaltumPredicate<TRecord>(
            PredicateKind.InList,
            quotedName,
            negated ? "NOT IN" : "IN",
            list.ToArray(),
            dbType,
            configureParameter,
            parameterPrefix);
    }

    internal static CobaltumPredicate<TRecord> Range(
        string quotedName,
        bool negated,
        object? low,
        object? high,
        DbType? dbType,
        Action<DbParameter>? configureParameter,
        char parameterPrefix)
    {
        if (low is null)
        {
            throw new ArgumentNullException(nameof(low));
        }

        if (high is null)
        {
            throw new ArgumentNullException(nameof(high));
        }

        return new CobaltumPredicate<TRecord>(
            PredicateKind.Range,
            quotedName,
            negated ? "NOT BETWEEN" : "BETWEEN",
            new[] { low, high },
            dbType,
            configureParameter,
            parameterPrefix);
    }

    /// <summary>Gets the number of database parameters the predicate binds.</summary>
    internal int ParameterCount =>
        _kind == PredicateKind.Combination
            ? _left!.ParameterCount + _right!.ParameterCount
            : _values.Length;

    /// <summary>
    /// Builds the SQL condition, numbering its parameters from <paramref name="startIndex"/>.
    /// </summary>
    internal string BuildSql(int startIndex)
    {
        var builder = new System.Text.StringBuilder();
        var index = startIndex;
        AppendSql(builder, ref index);
        return builder.ToString();
    }

    /// <summary>
    /// Adds the parameters of the condition, numbered from <paramref name="startIndex"/> in the
    /// order <see cref="BuildSql"/> writes them.
    /// </summary>
    internal void Bind(DbCommand command, int startIndex)
    {
        var index = startIndex;
        AppendParameters(command, ref index);
    }

    private static CobaltumPredicate<TRecord> Combine(
        CobaltumPredicate<TRecord> left,
        CobaltumPredicate<TRecord> right,
        bool isOr)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        return new CobaltumPredicate<TRecord>(left, right, isOr);
    }

    private void AppendSql(System.Text.StringBuilder builder, ref int index)
    {
        switch (_kind)
        {
            case PredicateKind.Combination:
                builder.Append('(');
                _left!.AppendSql(builder, ref index);
                builder.Append(_isOr ? " OR " : " AND ");
                _right!.AppendSql(builder, ref index);
                builder.Append(')');
                return;
            case PredicateKind.NullTest:
                builder.Append(_quotedName).Append(' ').Append(_comparison);
                return;
            case PredicateKind.InList:
                builder.Append(_quotedName).Append(' ').Append(_comparison).Append(" (");
                for (var position = 0; position < _values.Length; position++)
                {
                    if (position != 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(ParameterName(index));
                    index++;
                }

                builder.Append(')');
                return;
            case PredicateKind.Range:
                builder.Append(_quotedName).Append(' ').Append(_comparison).Append(' ')
                    .Append(ParameterName(index));
                index++;
                builder.Append(" AND ").Append(ParameterName(index));
                index++;
                return;
            default:
                builder.Append(_quotedName).Append(' ').Append(_comparison).Append(' ')
                    .Append(ParameterName(index));
                index++;
                return;
        }
    }

    private void AppendParameters(DbCommand command, ref int index)
    {
        if (_kind == PredicateKind.Combination)
        {
            _left!.AppendParameters(command, ref index);
            _right!.AppendParameters(command, ref index);
            return;
        }

        foreach (var value in _values)
        {
            var parameterName = ParameterName(index);
            index++;
            if (_dbType.HasValue)
            {
                if (_configureParameter != null)
                {
                    CobaltumParameter.AddConfigured(
                        command,
                        parameterName,
                        value,
                        _dbType.Value,
                        _configureParameter);
                }
                else
                {
                    CobaltumParameter.Add(command, parameterName, value, _dbType.Value);
                }
            }
            else
            {
                CobaltumParameter.Add(command, parameterName, value);
            }
        }
    }

    private string ParameterName(int index) =>
        _parameterPrefix + ParameterBaseName +
        index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Combines generated predicates that are built as a list.</summary>
public static class CobaltumPredicate
{
    /// <summary>Combines every predicate with AND.</summary>
    public static CobaltumPredicate<TRecord> All<TRecord>(
        params CobaltumPredicate<TRecord>[] predicates) =>
        Combine(predicates, false);

    /// <summary>Combines every predicate with AND.</summary>
    public static CobaltumPredicate<TRecord> All<TRecord>(
        IEnumerable<CobaltumPredicate<TRecord>> predicates) =>
        Combine(predicates, false);

    /// <summary>Combines every predicate with OR.</summary>
    public static CobaltumPredicate<TRecord> Any<TRecord>(
        params CobaltumPredicate<TRecord>[] predicates) =>
        Combine(predicates, true);

    /// <summary>Combines every predicate with OR.</summary>
    public static CobaltumPredicate<TRecord> Any<TRecord>(
        IEnumerable<CobaltumPredicate<TRecord>> predicates) =>
        Combine(predicates, true);

    private static CobaltumPredicate<TRecord> Combine<TRecord>(
        IEnumerable<CobaltumPredicate<TRecord>> predicates,
        bool isOr)
    {
        if (predicates is null)
        {
            throw new ArgumentNullException(nameof(predicates));
        }

        CobaltumPredicate<TRecord>? combined = null;
        foreach (var predicate in predicates)
        {
            if (predicate is null)
            {
                throw new ArgumentException(
                    "A predicate list cannot contain null.",
                    nameof(predicates));
            }

            combined = combined is null
                ? predicate
                : (isOr ? combined.Or(predicate) : combined.And(predicate));
        }

        if (combined is null)
        {
            throw new ArgumentException(
                "At least one predicate is required.",
                nameof(predicates));
        }

        return combined;
    }
}

/// <summary>A generated table column that accepts values of <typeparamref name="TValue"/>.</summary>
public sealed class CobaltumColumn<TRecord, TValue>
{
    private readonly string _quotedName;
    private readonly DbType? _dbType;
    private readonly Action<DbParameter>? _configureParameter;
    private readonly char _parameterPrefix;

    /// <summary>Initializes a generated column. This constructor is primarily intended for generated code.</summary>
    public CobaltumColumn(string quotedName)
    {
        _quotedName = ValidateIdentifier(quotedName);
        _parameterPrefix = '@';
    }

    /// <summary>Initializes a generated column with its database parameter type.</summary>
    public CobaltumColumn(string quotedName, DbType dbType)
        : this(quotedName, dbType, '@')
    {
    }

    /// <summary>Initializes a generated column with its database parameter type and SQL parameter prefix.</summary>
    public CobaltumColumn(string quotedName, DbType dbType, char parameterPrefix)
    {
        _quotedName = ValidateIdentifier(quotedName);
        _dbType = dbType;
        _parameterPrefix = ValidateParameterPrefix(parameterPrefix);
    }

    /// <summary>Initializes a generated column with provider-specific parameter configuration.</summary>
    public CobaltumColumn(
        string quotedName,
        DbType dbType,
        string databaseTypeName,
        Action<DbParameter> configureParameter)
        : this(quotedName, dbType, databaseTypeName, configureParameter, '@')
    {
    }

    /// <summary>Initializes a generated column with provider-specific configuration and SQL parameter prefix.</summary>
    public CobaltumColumn(
        string quotedName,
        DbType dbType,
        string databaseTypeName,
        Action<DbParameter> configureParameter,
        char parameterPrefix)
    {
        _quotedName = ValidateIdentifier(quotedName);
        _dbType = dbType;
        if (string.IsNullOrWhiteSpace(databaseTypeName))
        {
            throw new ArgumentException("A database type name is required.", nameof(databaseTypeName));
        }

        _configureParameter = configureParameter ?? throw new ArgumentNullException(nameof(configureParameter));
        _parameterPrefix = ValidateParameterPrefix(parameterPrefix);
    }

    /// <summary>Builds a parameterized equality predicate. A null value compares with IS NULL.</summary>
    public CobaltumPredicate<TRecord> Equal(TValue value) =>
        value is null ? IsNull() : Compare("=", value);

    /// <summary>Builds a parameterized inequality predicate. A null value compares with IS NOT NULL.</summary>
    public CobaltumPredicate<TRecord> NotEqual(TValue value) =>
        value is null ? IsNotNull() : Compare("<>", value);

    /// <summary>Builds a parameterized <c>&lt;</c> predicate.</summary>
    public CobaltumPredicate<TRecord> LessThan(TValue value) => Compare("<", value);

    /// <summary>Builds a parameterized <c>&lt;=</c> predicate.</summary>
    public CobaltumPredicate<TRecord> LessThanOrEqual(TValue value) => Compare("<=", value);

    /// <summary>Builds a parameterized <c>&gt;</c> predicate.</summary>
    public CobaltumPredicate<TRecord> GreaterThan(TValue value) => Compare(">", value);

    /// <summary>Builds a parameterized <c>&gt;=</c> predicate.</summary>
    public CobaltumPredicate<TRecord> GreaterThanOrEqual(TValue value) => Compare(">=", value);

    /// <summary>Builds an IS NULL predicate.</summary>
    public CobaltumPredicate<TRecord> IsNull() =>
        CobaltumPredicate<TRecord>.NullTest(_quotedName, false, _parameterPrefix);

    /// <summary>Builds an IS NOT NULL predicate.</summary>
    public CobaltumPredicate<TRecord> IsNotNull() =>
        CobaltumPredicate<TRecord>.NullTest(_quotedName, true, _parameterPrefix);

    /// <summary>Builds a parameterized LIKE predicate. The pattern is passed as a database parameter.</summary>
    public CobaltumPredicate<TRecord> Like(TValue pattern) => Compare("LIKE", pattern);

    /// <summary>Builds a parameterized NOT LIKE predicate. The pattern is passed as a database parameter.</summary>
    public CobaltumPredicate<TRecord> NotLike(TValue pattern) => Compare("NOT LIKE", pattern);

    /// <summary>Builds a parameterized IN predicate. Every value is passed as a database parameter.</summary>
    public CobaltumPredicate<TRecord> In(params TValue[] values) => InCore(values, false);

    /// <summary>Builds a parameterized IN predicate. Every value is passed as a database parameter.</summary>
    public CobaltumPredicate<TRecord> In(IEnumerable<TValue> values) => InCore(values, false);

    /// <summary>Builds a parameterized NOT IN predicate. Every value is passed as a database parameter.</summary>
    public CobaltumPredicate<TRecord> NotIn(params TValue[] values) => InCore(values, true);

    /// <summary>Builds a parameterized NOT IN predicate. Every value is passed as a database parameter.</summary>
    public CobaltumPredicate<TRecord> NotIn(IEnumerable<TValue> values) => InCore(values, true);

    /// <summary>Builds a parameterized BETWEEN predicate. Both bounds are included.</summary>
    public CobaltumPredicate<TRecord> Between(TValue low, TValue high) =>
        CobaltumPredicate<TRecord>.Range(
            _quotedName,
            false,
            low,
            high,
            _dbType,
            _configureParameter,
            _parameterPrefix);

    /// <summary>Builds a parameterized NOT BETWEEN predicate. Both bounds are included.</summary>
    public CobaltumPredicate<TRecord> NotBetween(TValue low, TValue high) =>
        CobaltumPredicate<TRecord>.Range(
            _quotedName,
            true,
            low,
            high,
            _dbType,
            _configureParameter,
            _parameterPrefix);

    /// <summary>Builds a parameterized <c>&lt;</c> predicate.</summary>
    public static CobaltumPredicate<TRecord> operator <(
        CobaltumColumn<TRecord, TValue> column,
        TValue value) =>
        Compare(column, "<", value);

    /// <summary>Builds a parameterized <c>&gt;</c> predicate.</summary>
    public static CobaltumPredicate<TRecord> operator >(
        CobaltumColumn<TRecord, TValue> column,
        TValue value) =>
        Compare(column, ">", value);

    /// <summary>Builds a parameterized <c>&lt;=</c> predicate.</summary>
    public static CobaltumPredicate<TRecord> operator <=(
        CobaltumColumn<TRecord, TValue> column,
        TValue value) =>
        Compare(column, "<=", value);

    /// <summary>Builds a parameterized <c>&gt;=</c> predicate.</summary>
    public static CobaltumPredicate<TRecord> operator >=(
        CobaltumColumn<TRecord, TValue> column,
        TValue value) =>
        Compare(column, ">=", value);

    private static CobaltumPredicate<TRecord> Compare(
        CobaltumColumn<TRecord, TValue> column,
        string comparison,
        TValue value)
    {
        if (column is null)
        {
            throw new ArgumentNullException(nameof(column));
        }

        return column.Compare(comparison, value);
    }

    private CobaltumPredicate<TRecord> Compare(string comparison, TValue value) =>
        CobaltumPredicate<TRecord>.Comparison(
            _quotedName,
            comparison,
            value,
            _dbType,
            _configureParameter,
            _parameterPrefix);

    private CobaltumPredicate<TRecord> InCore(IEnumerable<TValue> values, bool negated)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        var boxed = new List<object?>();
        foreach (var value in values)
        {
            boxed.Add(value);
        }

        return CobaltumPredicate<TRecord>.InList(
            _quotedName,
            negated,
            boxed,
            _dbType,
            _configureParameter,
            _parameterPrefix);
    }

    private static char ValidateParameterPrefix(char parameterPrefix)
    {
        if (parameterPrefix != '@' && parameterPrefix != ':')
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameterPrefix),
                "The SQL parameter prefix must be '@' or ':'.");
        }

        return parameterPrefix;
    }

    private static string ValidateIdentifier(string quotedName)
    {
        if (quotedName is null)
        {
            throw new ArgumentNullException(nameof(quotedName));
        }

        if (!IsSingleIdentifier(quotedName))
        {
            throw new ArgumentException(
                "A column name must be one unquoted or provider-quoted SQL identifier.",
                nameof(quotedName));
        }

        return quotedName;
    }

    private static bool IsSingleIdentifier(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var opening = value[0];
        if (opening == '"' || opening == '`' || opening == '[')
        {
            var closing = opening == '[' ? ']' : opening;
            var hasContent = false;
            for (var index = 1; index < value.Length; index++)
            {
                if (value[index] != closing)
                {
                    hasContent = true;
                    continue;
                }

                if (index == value.Length - 1)
                {
                    return hasContent;
                }

                if (value[index + 1] != closing)
                {
                    return false;
                }

                hasContent = true;
                index++;
            }

            return false;
        }

        if (!IsUnquotedIdentifierStart(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsLetterOrDigit(character) && character != '_' && character != '$' && character != '#')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUnquotedIdentifierStart(char character) =>
        char.IsLetter(character) || character == '_';
}

/// <summary>A generated, typed entry point for one table.</summary>
public abstract class CobaltumTable<TRecord>
{
    private readonly string _selectSql;
    private readonly Func<DbDataReader, TRecord> _materialize;
    private readonly string? _deleteSql;

    /// <summary>Initializes a generated table entry. This constructor is primarily intended for generated code.</summary>
    protected CobaltumTable(string selectSql, Func<DbDataReader, TRecord> materialize)
        : this(selectSql, materialize, null)
    {
    }

    /// <summary>
    /// Initializes a generated table entry that also supports <see cref="DeleteWhere"/>.
    /// This constructor is primarily intended for generated code.
    /// </summary>
    protected CobaltumTable(string selectSql, Func<DbDataReader, TRecord> materialize, string? deleteSql)
    {
        _selectSql = selectSql ?? throw new ArgumentNullException(nameof(selectSql));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
        if (deleteSql != null && string.IsNullOrWhiteSpace(deleteSql))
        {
            throw new ArgumentException("SQL text is required.", nameof(deleteSql));
        }

        _deleteSql = deleteSql;
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
            _selectSql + " WHERE " + predicate.BuildSql(0),
            command => predicate.Bind(command, 0),
            _materialize,
            true,
            predicate.ParameterCount);
    }

    /// <summary>
    /// Deletes every row matching a generated, parameterized predicate. Values are passed as
    /// database parameters instead of being concatenated into SQL.
    /// </summary>
    public CobaltumCommandDefinition DeleteWhere(CobaltumPredicate<TRecord> predicate)
    {
        if (predicate is null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        if (_deleteSql is null)
        {
            throw new NotSupportedException(
                "This table entry was created without a DELETE statement.");
        }

        return new CobaltumCommandDefinition(
            _deleteSql + " WHERE " + predicate.BuildSql(0),
            command => predicate.Bind(command, 0));
    }
}

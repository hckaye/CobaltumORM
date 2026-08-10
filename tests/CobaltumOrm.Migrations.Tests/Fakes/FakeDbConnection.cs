#pragma warning disable CS8765

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CobaltumOrm.Migrations.Tests.Fakes;

internal sealed class FakeDbConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;
    private int _nextTransactionId;

    internal List<long> HistoryVersions { get; } = new List<long>();

    internal List<FakeExecution> Executions { get; } = new List<FakeExecution>();

    internal List<FakeDbTransaction> Transactions { get; } = new List<FakeDbTransaction>();

    internal List<CancellationToken> OpenTokens { get; } = new List<CancellationToken>();

    internal List<CancellationToken> BeginTransactionTokens { get; } = new List<CancellationToken>();

    internal string? FailWhenCommandContains { get; set; }

    internal bool HistoryTableExists { get; set; } = true;

    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "1";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName)
    {
    }

    public override void Close()
    {
        _state = ConnectionState.Closed;
    }

    public override void Open()
    {
        _state = ConnectionState.Open;
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenTokens.Add(cancellationToken);
        _state = ConnectionState.Open;
        return Task.CompletedTask;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        var transaction = new FakeDbTransaction(this, ++_nextTransactionId, isolationLevel);
        Transactions.Add(transaction);
        return transaction;
    }

    protected override ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginTransactionTokens.Add(cancellationToken);
        return new ValueTask<DbTransaction>(BeginDbTransaction(isolationLevel));
    }

    protected override DbCommand CreateDbCommand() => new FakeDbCommand(this);

    internal int ExecuteNonQuery(FakeDbCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(command, cancellationToken, false);
        if (FailWhenCommandContains is not null &&
            command.CommandText.IndexOf(FailWhenCommandContains, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException("Configured fake command failure.");
        }

        if (command.CommandText.StartsWith("INSERT INTO", StringComparison.Ordinal))
        {
            var version = Convert.ToInt64(command.Parameters["version"].Value);
            ApplyOrEnlist(command, () => HistoryVersions.Add(version));
        }
        else if (command.CommandText.StartsWith("DELETE FROM", StringComparison.Ordinal))
        {
            var version = Convert.ToInt64(command.Parameters["version"].Value);
            ApplyOrEnlist(command, () => HistoryVersions.Remove(version));
        }
        else if (command.CommandText.StartsWith("CREATE TABLE IF NOT EXISTS", StringComparison.Ordinal))
        {
            HistoryTableExists = true;
        }

        return 1;
    }

    internal object ExecuteScalar(FakeDbCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(command, cancellationToken, false);
        if (command.CommandText.StartsWith("SELECT EXISTS", StringComparison.Ordinal))
        {
            return HistoryTableExists;
        }

        throw new NotSupportedException($"The fake scalar command is not supported: {command.CommandText}");
    }

    internal DbDataReader ExecuteReader(FakeDbCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(command, cancellationToken, true);
        var table = new DataTable();
        table.Columns.Add("version", typeof(long));
        foreach (var version in HistoryVersions)
        {
            table.Rows.Add(version);
        }

        return table.CreateDataReader();
    }

    private void ApplyOrEnlist(FakeDbCommand command, Action action)
    {
        if (command.Transaction is FakeDbTransaction transaction)
        {
            transaction.Enlist(action);
        }
        else
        {
            action();
        }
    }

    private void Record(FakeDbCommand command, CancellationToken cancellationToken, bool isReader)
    {
        var parameters = command.Parameters
            .Cast<DbParameter>()
            .ToDictionary(parameter => parameter.ParameterName, parameter => parameter.Value);
        Executions.Add(new FakeExecution(
            command.CommandText,
            parameters,
            (command.Transaction as FakeDbTransaction)?.Id,
            cancellationToken,
            isReader));
    }
}

internal sealed class FakeDbTransaction : DbTransaction
{
    private readonly FakeDbConnection _connection;
    private readonly List<Action> _pendingActions = new List<Action>();

    internal FakeDbTransaction(FakeDbConnection connection, int id, IsolationLevel isolationLevel)
    {
        _connection = connection;
        Id = id;
        IsolationLevel = isolationLevel;
    }

    internal int Id { get; }

    internal bool WasCommitted { get; private set; }

    internal bool WasRolledBack { get; private set; }

    internal CancellationToken? CommitToken { get; private set; }

    internal CancellationToken? RollbackToken { get; private set; }

    public override IsolationLevel IsolationLevel { get; }

    protected override DbConnection DbConnection => _connection;

    public override void Commit()
    {
        CommitCore();
    }

    public override Task CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitToken = cancellationToken;
        CommitCore();
        return Task.CompletedTask;
    }

    public override void Rollback()
    {
        RollbackCore();
    }

    public override Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        RollbackToken = cancellationToken;
        RollbackCore();
        return Task.CompletedTask;
    }

    internal void Enlist(Action action)
    {
        _pendingActions.Add(action);
    }

    private void CommitCore()
    {
        foreach (var action in _pendingActions)
        {
            action();
        }

        _pendingActions.Clear();
        WasCommitted = true;
    }

    private void RollbackCore()
    {
        _pendingActions.Clear();
        WasRolledBack = true;
    }
}

internal sealed class FakeDbCommand : DbCommand
{
    private readonly FakeDbConnection _connection;
    private readonly FakeDbParameterCollection _parameters = new FakeDbParameterCollection();

    internal FakeDbCommand(FakeDbConnection connection)
    {
        _connection = connection;
    }

    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection DbConnection
    {
        get => _connection;
        set => throw new NotSupportedException();
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery() => _connection.ExecuteNonQuery(this, CancellationToken.None);

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_connection.ExecuteNonQuery(this, cancellationToken));

    public override object ExecuteScalar() => _connection.ExecuteScalar(this, CancellationToken.None);

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
        Task.FromResult<object?>(_connection.ExecuteScalar(this, cancellationToken));

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new FakeDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        _connection.ExecuteReader(this, CancellationToken.None);

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken) =>
        Task.FromResult(_connection.ExecuteReader(this, cancellationToken));
}

internal sealed class FakeDbParameter : DbParameter
{
    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    public override string ParameterName { get; set; } = string.Empty;

    public override int Size { get; set; }

    public override string SourceColumn { get; set; } = string.Empty;

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType()
    {
    }
}

internal sealed class FakeDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = new List<DbParameter>();

    public override int Count => _items.Count;

    public override object SyncRoot => ((ICollection)_items).SyncRoot;

    public override int Add(object value)
    {
        _items.Add((DbParameter)value);
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => _items.Contains((DbParameter)value);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName) =>
        _items.FindIndex(parameter => string.Equals(parameter.ParameterName, parameterName, StringComparison.Ordinal));

    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

    public override void Remove(object value) => _items.Remove((DbParameter)value);

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName)
    {
        var index = IndexOf(parameterName);
        return index < 0 ? throw new IndexOutOfRangeException(parameterName) : _items[index];
    }

    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
        {
            _items.Add(value);
        }
        else
        {
            _items[index] = value;
        }
    }
}

internal sealed class FakeExecution
{
    internal FakeExecution(
        string commandText,
        IReadOnlyDictionary<string, object?> parameters,
        int? transactionId,
        CancellationToken cancellationToken,
        bool isReader)
    {
        CommandText = commandText;
        Parameters = parameters;
        TransactionId = transactionId;
        CancellationToken = cancellationToken;
        IsReader = isReader;
    }

    internal string CommandText { get; }

    internal IReadOnlyDictionary<string, object?> Parameters { get; }

    internal int? TransactionId { get; }

    internal CancellationToken CancellationToken { get; }

    internal bool IsReader { get; }
}

#pragma warning restore CS8765

#pragma warning disable CS8765

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CobaltumOrm.SqlServer.Tests;

internal sealed class SqlServerFakeDbConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;
    private int _nextTransactionId;

    internal List<long> HistoryVersions { get; } = new List<long>();
    internal List<SqlServerFakeExecution> Executions { get; } = new List<SqlServerFakeExecution>();
    internal List<SqlServerFakeTransaction> Transactions { get; } = new List<SqlServerFakeTransaction>();
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
        _state = ConnectionState.Open;
        return Task.CompletedTask;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        var transaction = new SqlServerFakeTransaction(this, ++_nextTransactionId, isolationLevel);
        Transactions.Add(transaction);
        return transaction;
    }

    protected override ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<DbTransaction>(BeginDbTransaction(isolationLevel));
    }

    protected override DbCommand CreateDbCommand() => new SqlServerFakeDbCommand(this);

    internal int ExecuteNonQuery(SqlServerFakeDbCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(command, cancellationToken, false);
        if (command.CommandText.StartsWith("IF NOT EXISTS", StringComparison.Ordinal))
        {
            HistoryTableExists = true;
        }
        else if (command.CommandText.StartsWith("INSERT INTO", StringComparison.Ordinal))
        {
            var version = Convert.ToInt64(command.Parameters["version"].Value);
            EnlistOrApply(command, () => HistoryVersions.Add(version));
        }
        else if (command.CommandText.StartsWith("DELETE FROM", StringComparison.Ordinal))
        {
            var version = Convert.ToInt64(command.Parameters["version"].Value);
            EnlistOrApply(command, () => HistoryVersions.Remove(version));
        }

        return 1;
    }

    internal object ExecuteScalar(SqlServerFakeDbCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(command, cancellationToken, false);
        if (command.CommandText.StartsWith("SELECT CONVERT(bit", StringComparison.Ordinal))
        {
            return HistoryTableExists;
        }

        throw new NotSupportedException("The fake scalar command is not supported.");
    }

    internal DbDataReader ExecuteReader(SqlServerFakeDbCommand command, CancellationToken cancellationToken)
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

    private void EnlistOrApply(SqlServerFakeDbCommand command, Action action)
    {
        if (command.Transaction is SqlServerFakeTransaction transaction)
        {
            transaction.Enlist(action);
        }
        else
        {
            action();
        }
    }

    private void Record(SqlServerFakeDbCommand command, CancellationToken cancellationToken, bool isReader)
    {
        var parameters = command.Parameters
            .Cast<DbParameter>()
            .ToDictionary(parameter => parameter.ParameterName, parameter => parameter.Value);
        Executions.Add(new SqlServerFakeExecution(
            command.CommandText,
            parameters,
            (command.Transaction as SqlServerFakeTransaction)?.Id,
            cancellationToken,
            isReader));
    }
}

internal sealed class SqlServerFakeDbCommand : DbCommand
{
    private readonly SqlServerFakeParameterCollection _parameters = new SqlServerFakeParameterCollection();
    private readonly SqlServerFakeDbConnection _connection;

    internal SqlServerFakeDbCommand(SqlServerFakeDbConnection connection)
    {
        _connection = connection;
    }

    public override string CommandText { get; set; } = string.Empty;
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection DbConnection { get => _connection; set { } }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery() => _connection.ExecuteNonQuery(this, CancellationToken.None);

    public override object ExecuteScalar() => _connection.ExecuteScalar(this, CancellationToken.None);

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new SqlServerFakeParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        _connection.ExecuteReader(this, CancellationToken.None);

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_connection.ExecuteNonQuery(this, cancellationToken));

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
        Task.FromResult<object?>(_connection.ExecuteScalar(this, cancellationToken));

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken) =>
        Task.FromResult(_connection.ExecuteReader(this, cancellationToken));
}

internal sealed class SqlServerFakeParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    public override string ParameterName { get; set; } = string.Empty;
    public override string SourceColumn { get; set; } = string.Empty;
    public override object Value { get; set; } = DBNull.Value;
    public override bool SourceColumnNullMapping { get; set; }
    public override int Size { get; set; }
    public override byte Precision { get; set; }
    public override byte Scale { get; set; }

    public override void ResetDbType()
    {
    }
}

internal sealed class SqlServerFakeParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = new List<DbParameter>();

    public override int Count => _items.Count;
    public override object SyncRoot => ((ICollection)_items).SyncRoot!;

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
    public override bool Contains(string value) => _items.Any(item => item.ParameterName == value);
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) => _items.FindIndex(item => item.ParameterName == parameterName);
    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
    public override void Remove(object value) => _items.Remove((DbParameter)value);
    public override void RemoveAt(int index) => _items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
}

internal sealed class SqlServerFakeTransaction : DbTransaction
{
    private readonly SqlServerFakeDbConnection _connection;
    private readonly List<Action> _pending = new List<Action>();

    internal SqlServerFakeTransaction(SqlServerFakeDbConnection connection, int id, IsolationLevel isolationLevel)
    {
        _connection = connection;
        Id = id;
        IsolationLevel = isolationLevel;
    }

    internal int Id { get; }
    internal bool WasCommitted { get; private set; }
    internal bool WasRolledBack { get; private set; }
    public override IsolationLevel IsolationLevel { get; }
    protected override DbConnection DbConnection => _connection;

    public override void Commit()
    {
        foreach (var action in _pending)
        {
            action();
        }

        WasCommitted = true;
    }

    public override void Rollback()
    {
        WasRolledBack = true;
        _pending.Clear();
    }

    internal void Enlist(Action action) => _pending.Add(action);
}

internal sealed class SqlServerFakeExecution
{
    internal SqlServerFakeExecution(
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

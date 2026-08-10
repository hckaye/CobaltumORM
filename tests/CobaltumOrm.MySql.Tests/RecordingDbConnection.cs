#pragma warning disable CS8765

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CobaltumOrm.MySql.Tests;

internal sealed class RecordingDbConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;
    private int _nextTransactionId;

    internal List<long> HistoryVersions { get; } = new List<long>();
    internal List<RecordedCommand> Commands { get; } = new List<RecordedCommand>();
    internal bool HistoryTableExists { get; set; } = true;

    public override string ConnectionString { get; set; } = string.Empty;
    public override string Database => "recording";
    public override string DataSource => "recording";
    public override string ServerVersion => "8.0";
    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName)
    {
    }

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open() => _state = ConnectionState.Open;

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = ConnectionState.Open;
        return Task.CompletedTask;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        new RecordingDbTransaction(this, ++_nextTransactionId, isolationLevel);

    protected override ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<DbTransaction>(BeginDbTransaction(isolationLevel));
    }

    protected override DbCommand CreateDbCommand() => new RecordingDbCommand(this);

    internal int ExecuteNonQuery(RecordingDbCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(command, false);
        if (command.CommandText.StartsWith("CREATE TABLE IF NOT EXISTS", StringComparison.Ordinal))
        {
            HistoryTableExists = true;
        }
        else if (command.CommandText.StartsWith("INSERT INTO", StringComparison.Ordinal))
        {
            HistoryVersions.Add(Convert.ToInt64(command.Parameters["version"].Value));
        }
        else if (command.CommandText.StartsWith("DELETE FROM", StringComparison.Ordinal))
        {
            HistoryVersions.Remove(Convert.ToInt64(command.Parameters["version"].Value));
        }

        return 1;
    }

    internal object ExecuteScalar(RecordingDbCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(command, false);
        return HistoryTableExists;
    }

    internal DbDataReader ExecuteReader(RecordingDbCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(command, true);
        var table = new DataTable();
        table.Columns.Add("version", typeof(long));
        foreach (var version in HistoryVersions)
        {
            table.Rows.Add(version);
        }

        return table.CreateDataReader();
    }

    private void Record(RecordingDbCommand command, bool isReader)
    {
        Commands.Add(new RecordedCommand(
            command.CommandText,
            command.Parameters.Cast<DbParameter>()
                .ToDictionary(parameter => parameter.ParameterName, parameter => parameter.Value),
            isReader));
    }
}

internal sealed class RecordingDbTransaction : DbTransaction
{
    private readonly RecordingDbConnection _connection;

    internal RecordingDbTransaction(
        RecordingDbConnection connection,
        int id,
        IsolationLevel isolationLevel)
    {
        _connection = connection;
        Id = id;
        IsolationLevel = isolationLevel;
    }

    internal int Id { get; }
    public override IsolationLevel IsolationLevel { get; }
    protected override DbConnection DbConnection => _connection;
    public override void Commit()
    {
    }

    public override void Rollback()
    {
    }
}

internal sealed class RecordingDbCommand : DbCommand
{
    private readonly RecordingDbConnection _connection;
    private readonly RecordingParameterCollection _parameters = new RecordingParameterCollection();

    internal RecordingDbCommand(RecordingDbConnection connection)
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

    protected override DbParameter CreateDbParameter() => new RecordingParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        _connection.ExecuteReader(this, CancellationToken.None);

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken) =>
        Task.FromResult(_connection.ExecuteReader(this, cancellationToken));
}

internal sealed class RecordingParameter : DbParameter
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

internal sealed class RecordingParameterCollection : DbParameterCollection
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

internal sealed class RecordedCommand
{
    internal RecordedCommand(
        string commandText,
        IReadOnlyDictionary<string, object?> parameters,
        bool isReader)
    {
        CommandText = commandText;
        Parameters = parameters;
        IsReader = isReader;
    }

    internal string CommandText { get; }
    internal IReadOnlyDictionary<string, object?> Parameters { get; }
    internal bool IsReader { get; }
}

#pragma warning restore CS8765

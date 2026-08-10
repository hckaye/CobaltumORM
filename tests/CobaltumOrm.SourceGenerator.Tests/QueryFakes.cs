#pragma warning disable CS8765

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CobaltumOrm.SourceGenerator.Tests;

internal sealed class QueryFakeDbConnection : DbConnection
{
    private readonly Queue<object?[]> _rows;
    private readonly IReadOnlyList<string>? _columnNames;
    private ConnectionState _state = ConnectionState.Closed;

    internal QueryFakeDbConnection(params object?[][] rows)
    {
        _rows = new Queue<object?[]>(rows);
    }

    private QueryFakeDbConnection(IReadOnlyList<string> columnNames, IEnumerable<object?[]> rows, bool _)
    {
        _columnNames = columnNames;
        _rows = new Queue<object?[]>(rows);
    }

    internal static QueryFakeDbConnection WithColumns(
        IReadOnlyList<string> columnNames,
        params object?[][] rows) =>
        new QueryFakeDbConnection(columnNames, rows, true);

    internal List<QueryFakeDbCommand> Commands { get; } = new List<QueryFakeDbCommand>();
    internal List<DbDataReader> Readers { get; } = new List<DbDataReader>();
    internal List<CancellationToken> OpenTokens { get; } = new List<CancellationToken>();
    internal int CloseCount { get; private set; }

    public override string ConnectionString { get; set; } = string.Empty;
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "1";
    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }
    public override void Close()
    {
        CloseCount++;
        _state = ConnectionState.Closed;
    }

    public override void Open() => _state = ConnectionState.Open;

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenTokens.Add(cancellationToken);
        _state = ConnectionState.Open;
        return Task.CompletedTask;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        new QueryFakeDbTransaction(this, isolationLevel);

    protected override DbCommand CreateDbCommand()
    {
        var command = new QueryFakeDbCommand(this);
        Commands.Add(command);
        return command;
    }

    internal DbDataReader CreateReader()
    {
        var table = new DataTable();
        if (_rows.Count == 0)
        {
            var emptyReader = table.CreateDataReader();
            Readers.Add(emptyReader);
            return emptyReader;
        }

        var values = _rows.Dequeue();
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            table.Columns.Add("column" + index, value is null || value == DBNull.Value ? typeof(object) : value.GetType());
        }

        table.Rows.Add(values);
        DbDataReader reader = table.CreateDataReader();
        if (_columnNames != null)
        {
            reader = new NamedDbDataReader(reader, _columnNames);
        }

        Readers.Add(reader);
        return reader;
    }
}

internal sealed class QueryFakeDbTransaction : DbTransaction
{
    private readonly DbConnection _connection;

    internal QueryFakeDbTransaction(DbConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
    }

    public override IsolationLevel IsolationLevel { get; }
    protected override DbConnection DbConnection => _connection;
    public override void Commit() { }
    public override void Rollback() { }
}

internal sealed class QueryFakeDbCommand : DbCommand
{
    private readonly QueryFakeDbConnection _connection;
    private readonly QueryFakeDbParameterCollection _parameters = new QueryFakeDbParameterCollection();

    internal QueryFakeDbCommand(QueryFakeDbConnection connection)
    {
        _connection = connection;
    }

    internal bool WasDisposed { get; private set; }
    internal DbTransaction? TransactionSeen => DbTransaction;
    internal CancellationToken CancellationTokenSeen { get; private set; }
    internal IReadOnlyDictionary<string, DbParameter> ParameterValues => _parameters.Items.ToDictionary(item => item.ParameterName);

    public override string CommandText { get; set; } = string.Empty;
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection DbConnection { get => _connection; set => throw new NotSupportedException(); }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }
    public override void Cancel() { }
    public override int ExecuteNonQuery() => 1;

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSeen = cancellationToken;
        return Task.FromResult(1);
    }
    public override object? ExecuteScalar() => null;
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new QueryFakeDbParameter();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => _connection.CreateReader();

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        CancellationTokenSeen = cancellationToken;
        return Task.FromResult(_connection.CreateReader());
    }

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class NamedDbDataReader : DbDataReader
{
    private readonly DbDataReader _inner;
    private readonly IReadOnlyList<string> _names;

    internal NamedDbDataReader(DbDataReader inner, IReadOnlyList<string> names)
    {
        _inner = inner;
        _names = names;
        if (inner.FieldCount != names.Count)
        {
            throw new ArgumentException("Reader field count must match the supplied names.", nameof(names));
        }
    }

    public override object this[int ordinal] => _inner[ordinal];
    public override object this[string name] => _inner[GetOrdinal(name)];
    public override int Depth => _inner.Depth;
    public override int FieldCount => _inner.FieldCount;
    public override bool HasRows => _inner.HasRows;
    public override bool IsClosed => _inner.IsClosed;
    public override int RecordsAffected => _inner.RecordsAffected;
    public override int VisibleFieldCount => _inner.VisibleFieldCount;
    public override bool GetBoolean(int ordinal) => _inner.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => _inner.GetByte(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => _inner.GetChar(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal) => _inner.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => _inner.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => _inner.GetDouble(ordinal);
    public override IEnumerator GetEnumerator() => ((IEnumerable)_inner).GetEnumerator();
    public override Type GetFieldType(int ordinal) => _inner.GetFieldType(ordinal);
    public override T GetFieldValue<T>(int ordinal) => _inner.GetFieldValue<T>(ordinal);
    public override float GetFloat(int ordinal) => _inner.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => _inner.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => _inner.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => _inner.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => _inner.GetInt64(ordinal);
    public override string GetName(int ordinal) => _names[ordinal];

    public override int GetOrdinal(string name)
    {
        for (var ordinal = 0; ordinal < _names.Count; ordinal++)
        {
            if (string.Equals(_names[ordinal], name, StringComparison.Ordinal)) return ordinal;
        }

        throw new IndexOutOfRangeException(name);
    }

    public override string GetString(int ordinal) => _inner.GetString(ordinal);
    public override object GetValue(int ordinal) => _inner.GetValue(ordinal);
    public override int GetValues(object[] values) => _inner.GetValues(values);
    public override bool IsDBNull(int ordinal) => _inner.IsDBNull(ordinal);
    public override bool NextResult() => _inner.NextResult();
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => _inner.NextResultAsync(cancellationToken);
    public override bool Read() => _inner.Read();
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => _inner.ReadAsync(cancellationToken);
    public override DataTable? GetSchemaTable() => _inner.GetSchemaTable();
    public override void Close() => _inner.Close();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class QueryFakeDbParameter : DbParameter
{
    public string? DataTypeName { get; set; }
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    public override string ParameterName { get; set; } = string.Empty;
    public override int Size { get; set; }
    public override string SourceColumn { get; set; } = string.Empty;
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }
    public override void ResetDbType() { }
}

internal sealed class QueryFakeDbParameterCollection : DbParameterCollection
{
    internal List<DbParameter> Items { get; } = new List<DbParameter>();

    public override int Count => Items.Count;
    public override object SyncRoot => ((ICollection)Items).SyncRoot;
    public override int Add(object value) { Items.Add((DbParameter)value); return Items.Count - 1; }
    public override void AddRange(Array values) { foreach (var value in values) Add(value!); }
    public override void Clear() => Items.Clear();
    public override bool Contains(object value) => Items.Contains((DbParameter)value);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)Items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => Items.GetEnumerator();
    public override int IndexOf(object value) => Items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) => Items.FindIndex(item => item.ParameterName == parameterName);
    public override void Insert(int index, object value) => Items.Insert(index, (DbParameter)value);
    public override void Remove(object value) => Items.Remove((DbParameter)value);
    public override void RemoveAt(int index) => Items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => Items.RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index) => Items[index];
    protected override DbParameter GetParameter(string parameterName) => Items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => Items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0) Items.Add(value); else Items[index] = value;
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Linq;

namespace CobaltumOrm.Migrations;

/// <summary>Identifies a database-provided value usable as a column default.</summary>
public enum SystemMethods
{
    /// <summary>Creates a new GUID.</summary>
    NewGuid,
    /// <summary>Creates a new sequential GUID where supported.</summary>
    NewSequentialId,
    /// <summary>Gets the current date and time.</summary>
    CurrentDateTime,
    /// <summary>Gets the current date and time with an offset.</summary>
    CurrentDateTimeOffset,
    /// <summary>Gets the current UTC date and time.</summary>
    CurrentUTCDateTime,
    /// <summary>Gets the current database user.</summary>
    CurrentUser,
}

/// <summary>Marks a value as SQL that should be inserted without quoting.</summary>
public sealed class RawSql
{
    private RawSql(string sql)
    {
        Sql = ExpressionValidation.Sql(sql);
    }

    /// <summary>Gets the SQL fragment.</summary>
    public string Sql { get; }

    /// <summary>Creates a raw SQL value for a data or default-value expression.</summary>
    public static RawSql Insert(string sql) => new RawSql(sql);
}

/// <summary>Describes a foreign key.</summary>
public sealed class ForeignKeyDefinition
{
    private readonly List<string> _foreignColumns = new List<string>();
    private readonly List<string> _primaryColumns = new List<string>();

    internal ForeignKeyDefinition(string? name)
    {
        Name = name;
    }

    /// <summary>Gets the optional constraint name.</summary>
    public string? Name { get; }
    /// <summary>Gets the table containing the foreign-key columns.</summary>
    public string ForeignTableName { get; internal set; } = string.Empty;
    /// <summary>Gets the optional foreign table schema.</summary>
    public string? ForeignTableSchema { get; internal set; }
    /// <summary>Gets the referenced table.</summary>
    public string PrimaryTableName { get; internal set; } = string.Empty;
    /// <summary>Gets the optional referenced table schema.</summary>
    public string? PrimaryTableSchema { get; internal set; }
    /// <summary>Gets the foreign-key columns.</summary>
    public IReadOnlyList<string> ForeignColumns => _foreignColumns.AsReadOnly();
    /// <summary>Gets the referenced columns.</summary>
    public IReadOnlyList<string> PrimaryColumns => _primaryColumns.AsReadOnly();
    /// <summary>Gets the action used when a referenced row is deleted.</summary>
    public Rule OnDelete { get; internal set; }
    /// <summary>Gets the action used when a referenced key is updated.</summary>
    public Rule OnUpdate { get; internal set; }

    internal void AddForeignColumns(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            _foreignColumns.Add(ExpressionValidation.Name(name, nameof(names)));
        }
    }

    internal void AddPrimaryColumns(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            _primaryColumns.Add(ExpressionValidation.Name(name, nameof(names)));
        }
    }
}

/// <summary>Describes one indexed column and its sort direction.</summary>
public sealed class IndexColumnDefinition
{
    internal IndexColumnDefinition(string name)
    {
        Name = name;
    }

    /// <summary>Gets the column name.</summary>
    public string Name { get; }
    /// <summary>Gets whether the column is sorted descending.</summary>
    public bool IsDescending { get; internal set; }
}

/// <summary>Creates a schema.</summary>
public sealed class CreateSchemaOperation : MigrationOperation
{
    internal CreateSchemaOperation(string schemaName) => SchemaName = schemaName;
    /// <summary>Gets the schema name.</summary>
    public string SchemaName { get; }
}

/// <summary>Drops a schema.</summary>
public sealed class DeleteSchemaOperation : MigrationOperation
{
    internal DeleteSchemaOperation(string schemaName) => SchemaName = schemaName;
    /// <summary>Gets the schema name.</summary>
    public string SchemaName { get; }
}

/// <summary>Moves a table to another schema.</summary>
public sealed class MoveTableOperation : MigrationOperation
{
    internal MoveTableOperation(string tableName, string? oldSchemaName, string newSchemaName)
    {
        TableName = tableName;
        OldSchemaName = oldSchemaName;
        NewSchemaName = newSchemaName;
    }

    /// <summary>Gets the table name.</summary>
    public string TableName { get; }
    /// <summary>Gets the optional source schema.</summary>
    public string? OldSchemaName { get; }
    /// <summary>Gets the destination schema.</summary>
    public string NewSchemaName { get; }
}

/// <summary>Adds or replaces a table description.</summary>
public sealed class AlterTableDescriptionOperation : MigrationOperation
{
    internal AlterTableDescriptionOperation(string tableName, string description)
    {
        TableName = tableName;
        Description = description;
    }
    /// <summary>Gets the table name.</summary>
    public string TableName { get; }
    /// <summary>Gets the optional table schema.</summary>
    public string? SchemaName { get; internal set; }
    /// <summary>Gets the description.</summary>
    public string Description { get; }
}

/// <summary>Creates an index.</summary>
public sealed class CreateIndexOperation : MigrationOperation
{
    private readonly List<IndexColumnDefinition> _columns = new List<IndexColumnDefinition>();

    internal CreateIndexOperation(string? indexName) => IndexName = indexName;
    /// <summary>Gets the optional index name.</summary>
    public string? IndexName { get; }
    /// <summary>Gets the table name.</summary>
    public string TableName { get; internal set; } = string.Empty;
    /// <summary>Gets the optional table schema.</summary>
    public string? SchemaName { get; internal set; }
    /// <summary>Gets indexed columns.</summary>
    public IReadOnlyList<IndexColumnDefinition> Columns => _columns.AsReadOnly();
    /// <summary>Gets whether the index is unique.</summary>
    public bool IsUnique { get; internal set; }
    /// <summary>Gets whether a clustered index is requested.</summary>
    public bool? IsClustered { get; internal set; }

    internal IndexColumnDefinition AddColumn(string name)
    {
        var column = new IndexColumnDefinition(ExpressionValidation.Name(name, nameof(name)));
        _columns.Add(column);
        return column;
    }
}

/// <summary>Drops an index.</summary>
public sealed class DeleteIndexOperation : MigrationOperation
{
    private readonly List<string> _columns = new List<string>();

    internal DeleteIndexOperation(string? indexName) => IndexName = indexName;
    /// <summary>Gets the optional index name.</summary>
    public string? IndexName { get; }
    /// <summary>Gets the table name.</summary>
    public string TableName { get; internal set; } = string.Empty;
    /// <summary>Gets the optional table schema.</summary>
    public string? SchemaName { get; internal set; }
    /// <summary>Gets columns used to derive a conventional name.</summary>
    public IReadOnlyList<string> Columns => _columns.AsReadOnly();
    internal void AddColumns(IEnumerable<string> columns)
    {
        foreach (var column in columns) _columns.Add(ExpressionValidation.Name(column, nameof(columns)));
    }
}

/// <summary>Creates a foreign key.</summary>
public sealed class CreateForeignKeyOperation : MigrationOperation
{
    internal CreateForeignKeyOperation(string? name) => ForeignKey = new ForeignKeyDefinition(name);
    /// <summary>Gets the foreign-key definition.</summary>
    public ForeignKeyDefinition ForeignKey { get; }
}

/// <summary>Drops a foreign key.</summary>
public sealed class DeleteForeignKeyOperation : MigrationOperation
{
    internal DeleteForeignKeyOperation(string? name) => ForeignKey = new ForeignKeyDefinition(name);
    /// <summary>Gets the identifying foreign-key fields.</summary>
    public ForeignKeyDefinition ForeignKey { get; }
}

/// <summary>Identifies a table constraint kind.</summary>
public enum MigrationConstraintType
{
    /// <summary>A primary-key constraint.</summary>
    PrimaryKey,
    /// <summary>A unique constraint.</summary>
    Unique,
}

/// <summary>Creates a table constraint.</summary>
public sealed class CreateConstraintOperation : MigrationOperation
{
    private readonly List<string> _columns = new List<string>();
    internal CreateConstraintOperation(MigrationConstraintType type, string? name)
    {
        ConstraintType = type;
        ConstraintName = name;
    }
    /// <summary>Gets the constraint kind.</summary>
    public MigrationConstraintType ConstraintType { get; }
    /// <summary>Gets the optional constraint name.</summary>
    public string? ConstraintName { get; }
    /// <summary>Gets the table name.</summary>
    public string TableName { get; internal set; } = string.Empty;
    /// <summary>Gets the optional table schema.</summary>
    public string? SchemaName { get; internal set; }
    /// <summary>Gets constrained columns.</summary>
    public IReadOnlyList<string> Columns => _columns.AsReadOnly();
    internal void AddColumns(IEnumerable<string> columns)
    {
        foreach (var column in columns) _columns.Add(ExpressionValidation.Name(column, nameof(columns)));
    }
}

/// <summary>Drops a table constraint.</summary>
public sealed class DeleteConstraintOperation : MigrationOperation
{
    private readonly List<string> _columns = new List<string>();
    internal DeleteConstraintOperation(MigrationConstraintType type, string? name)
    {
        ConstraintType = type;
        ConstraintName = name;
    }
    /// <summary>Gets the constraint kind.</summary>
    public MigrationConstraintType ConstraintType { get; }
    /// <summary>Gets the optional constraint name.</summary>
    public string? ConstraintName { get; }
    /// <summary>Gets the table name.</summary>
    public string TableName { get; internal set; } = string.Empty;
    /// <summary>Gets the optional table schema.</summary>
    public string? SchemaName { get; internal set; }
    /// <summary>Gets columns used to derive a conventional name.</summary>
    public IReadOnlyList<string> Columns => _columns.AsReadOnly();
    internal void AddColumns(IEnumerable<string> columns)
    {
        foreach (var column in columns) _columns.Add(ExpressionValidation.Name(column, nameof(columns)));
    }
}

/// <summary>Creates a sequence.</summary>
public sealed class CreateSequenceOperation : MigrationOperation
{
    internal CreateSequenceOperation(string name) => SequenceName = name;
    /// <summary>Gets the sequence name.</summary>
    public string SequenceName { get; }
    /// <summary>Gets the optional schema.</summary>
    public string? SchemaName { get; internal set; }
    /// <summary>Gets the optional increment.</summary>
    public long? Increment { get; internal set; }
    /// <summary>Gets the optional minimum value.</summary>
    public long? MinimumValue { get; internal set; }
    /// <summary>Gets the optional maximum value.</summary>
    public long? MaximumValue { get; internal set; }
    /// <summary>Gets the optional starting value.</summary>
    public long? StartValue { get; internal set; }
    /// <summary>Gets the optional cache size.</summary>
    public long? CacheSize { get; internal set; }
    /// <summary>Gets whether the sequence cycles.</summary>
    public bool IsCyclic { get; internal set; }
}

/// <summary>Drops a sequence.</summary>
public sealed class DeleteSequenceOperation : MigrationOperation
{
    internal DeleteSequenceOperation(string name) => SequenceName = name;
    /// <summary>Gets the sequence name.</summary>
    public string SequenceName { get; }
    /// <summary>Gets the optional schema.</summary>
    public string? SchemaName { get; internal set; }
}

/// <summary>Drops a default constraint from a column.</summary>
public sealed class DeleteDefaultConstraintOperation : MigrationOperation
{
    internal DeleteDefaultConstraintOperation(string tableName, string columnName)
    {
        TableName = tableName;
        ColumnName = columnName;
    }
    /// <summary>Gets the table name.</summary>
    public string TableName { get; }
    /// <summary>Gets the column name.</summary>
    public string ColumnName { get; }
    /// <summary>Gets the optional table schema.</summary>
    public string? SchemaName { get; internal set; }
}

/// <summary>Stores a row as ordered column-value pairs.</summary>
public sealed class MigrationDataRow
{
    private readonly ReadOnlyCollection<KeyValuePair<string, object?>> _values;
    internal MigrationDataRow(IEnumerable<KeyValuePair<string, object?>> values) =>
        _values = new List<KeyValuePair<string, object?>>(values).AsReadOnly();
    /// <summary>Gets values in declaration order.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Values => _values;
}

/// <summary>Inserts rows into a table.</summary>
public sealed class InsertDataOperation : MigrationOperation
{
    private readonly List<MigrationDataRow> _rows = new List<MigrationDataRow>();
    internal InsertDataOperation(string tableName) => TableName = tableName;
    /// <summary>Gets the table name.</summary>
    public string TableName { get; }
    /// <summary>Gets the optional schema.</summary>
    public string? SchemaName { get; internal set; }
    /// <summary>Gets rows to insert.</summary>
    public IReadOnlyList<MigrationDataRow> Rows => _rows.AsReadOnly();
    internal void AddRow(MigrationDataRow row) => _rows.Add(row);
}

/// <summary>Updates rows in a table.</summary>
public sealed class UpdateDataOperation : MigrationOperation
{
    internal UpdateDataOperation(string tableName) => TableName = tableName;
    /// <summary>Gets the table name.</summary>
    public string TableName { get; }
    /// <summary>Gets the optional schema.</summary>
    public string? SchemaName { get; internal set; }
    /// <summary>Gets values assigned by the update.</summary>
    public MigrationDataRow? Values { get; internal set; }
    /// <summary>Gets equality criteria, or null for all rows.</summary>
    public MigrationDataRow? Criteria { get; internal set; }
    /// <summary>Gets whether all rows are explicitly selected.</summary>
    public bool AllRows { get; internal set; }
}

/// <summary>Deletes rows from a table.</summary>
public sealed class DeleteDataOperation : MigrationOperation
{
    private readonly List<MigrationDataRow> _criteria = new List<MigrationDataRow>();
    internal DeleteDataOperation(string tableName) => TableName = tableName;
    /// <summary>Gets the table name.</summary>
    public string TableName { get; }
    /// <summary>Gets the optional schema.</summary>
    public string? SchemaName { get; internal set; }
    /// <summary>Gets equality criteria. Each row produces one delete command.</summary>
    public IReadOnlyList<MigrationDataRow> Criteria => _criteria.AsReadOnly();
    /// <summary>Gets whether all rows are selected.</summary>
    public bool AllRows { get; internal set; }
    internal void AddCriteria(MigrationDataRow row) => _criteria.Add(row);
}

/// <summary>Executes SQL loaded from a file or embedded resource.</summary>
public sealed class ExecuteScriptOperation : MigrationOperation
{
    internal ExecuteScriptOperation(string scriptName, bool embedded, Type migrationType, IReadOnlyDictionary<string, object?> parameters)
    {
        ScriptName = scriptName;
        IsEmbedded = embedded;
        MigrationType = migrationType;
        Parameters = parameters;
    }
    /// <summary>Gets the file path or embedded resource name.</summary>
    public string ScriptName { get; }
    /// <summary>Gets whether the script is an embedded resource.</summary>
    public bool IsEmbedded { get; }
    /// <summary>Gets the migration type used to locate an embedded resource.</summary>
    public Type MigrationType { get; }
    /// <summary>Gets token replacement values.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }
}

/// <summary>Executes code against the current migration connection and transaction.</summary>
public sealed class ExecuteWithConnectionOperation : MigrationOperation
{
    internal ExecuteWithConnectionOperation(Action<IDbConnection, IDbTransaction> callback, string? description)
    {
        Callback = callback;
        Description = description;
    }
    /// <summary>Gets the callback.</summary>
    public Action<IDbConnection, IDbTransaction> Callback { get; }
    /// <summary>Gets the optional description.</summary>
    public string? Description { get; }
}

/// <summary>Wraps an operation that applies only to selected database providers.</summary>
public sealed class ConditionalMigrationOperation : MigrationOperation
{
    private readonly string[] _databaseTypes;
    private readonly Predicate<string>? _predicate;

    internal ConditionalMigrationOperation(
        MigrationOperation operation,
        IEnumerable<string> databaseTypes,
        Predicate<string>? predicate)
    {
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        _databaseTypes = (databaseTypes ?? throw new ArgumentNullException(nameof(databaseTypes))).ToArray();
        _predicate = predicate;
    }

    /// <summary>Gets the wrapped migration operation.</summary>
    public MigrationOperation Operation { get; }

    /// <summary>Gets the configured database type names.</summary>
    public IReadOnlyList<string> DatabaseTypes => _databaseTypes;

    internal bool Matches(string canonicalName, params string[] aliases)
    {
        if (_predicate != null) return _predicate(canonicalName);
        var candidates = new[] { canonicalName }.Concat(aliases).Select(Normalize).ToArray();
        return _databaseTypes.Any(databaseType => candidates.Contains(Normalize(databaseType), StringComparer.Ordinal));
    }

    private static string Normalize(string value) =>
        new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

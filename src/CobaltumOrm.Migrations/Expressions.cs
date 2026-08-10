using System;
using System.Collections.Generic;

namespace CobaltumOrm.Migrations;

internal static class ExpressionValidation
{
    internal static string Name(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A database object name is required.", parameterName);
        }

        if (value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Database object names cannot contain a null character.", parameterName);
        }

        return value;
    }

    internal static string Sql(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("SQL text is required.", nameof(value));
        }

        return value;
    }
}

internal static class ColumnDefinitionMutator
{
    internal static void SetType(ColumnDefinition column, MigrationColumnType type)
    {
        if (column.Type != MigrationColumnType.Unspecified)
        {
            throw new MigrationValidationException($"Column '{column.Name}' already has a type.");
        }

        column.Type = type;
    }

    internal static void SetString(ColumnDefinition column, int? length)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "String length must be positive.");
        }

        SetType(column, MigrationColumnType.String);
        column.Length = length;
    }

    internal static void SetDecimal(ColumnDefinition column, int? precision, int? scale)
    {
        if (precision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(precision), "Decimal precision must be positive.");
        }

        if (scale < 0 || scale > precision)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Decimal scale must be between zero and precision.");
        }

        SetType(column, MigrationColumnType.Decimal);
        column.Precision = precision;
        column.Scale = scale;
    }

    internal static void SetPrimaryKey(ColumnDefinition column)
    {
        if (column.IsAlteration)
        {
            throw new MigrationValidationException("PrimaryKey is only supported when creating or adding a column.");
        }

        column.IsPrimaryKey = true;
        column.IsNullable = false;
    }

    internal static void SetIdentity(ColumnDefinition column)
    {
        if (column.IsAlteration)
        {
            throw new MigrationValidationException("Identity is only supported when creating or adding a column.");
        }

        column.IsIdentity = true;
    }
}

/// <summary>Starts table creation expressions.</summary>
public sealed class CreateExpressionRoot
{
    private readonly Action<MigrationOperation> _add;

    internal CreateExpressionRoot(Action<MigrationOperation> add)
    {
        _add = add;
    }

    /// <summary>Starts a <c>CREATE TABLE</c> operation.</summary>
    public CreateTableExpression Table(string tableName)
    {
        var operation = new CreateTableOperation(ExpressionValidation.Name(tableName, nameof(tableName)));
        _add(operation);
        return new CreateTableExpression(operation);
    }
}

/// <summary>
/// Builds a table and its columns. <c>AsString()</c> maps to unbounded text;
/// <c>AsString(length)</c> maps to a bounded variable-length string.
/// </summary>
public sealed class CreateTableExpression
{
    private readonly CreateTableOperation _operation;
    private ColumnDefinition? _currentColumn;

    internal CreateTableExpression(CreateTableOperation operation)
    {
        _operation = operation;
    }

    /// <summary>Qualifies the table with a schema.</summary>
    public CreateTableExpression InSchema(string schemaName)
    {
        _operation.SchemaName = ExpressionValidation.Name(schemaName, nameof(schemaName));
        return this;
    }

    /// <summary>Adds a column and makes it the target of following type and option calls.</summary>
    public CreateTableExpression WithColumn(string columnName)
    {
        _currentColumn = _operation.AddColumn(ExpressionValidation.Name(columnName, nameof(columnName)));
        return this;
    }

    /// <summary>Uses a 16-bit integer type.</summary>
    public CreateTableExpression AsInt16() => SetType(MigrationColumnType.Int16);

    /// <summary>Uses a 32-bit integer type.</summary>
    public CreateTableExpression AsInt32() => SetType(MigrationColumnType.Int32);

    /// <summary>Uses a 64-bit integer type.</summary>
    public CreateTableExpression AsInt64() => SetType(MigrationColumnType.Int64);

    /// <summary>Uses a Boolean type.</summary>
    public CreateTableExpression AsBoolean() => SetType(MigrationColumnType.Boolean);

    /// <summary>Uses an unconstrained decimal type.</summary>
    public CreateTableExpression AsDecimal()
    {
        ColumnDefinitionMutator.SetDecimal(Current(), null, null);
        return this;
    }

    /// <summary>Uses a decimal type with precision and scale.</summary>
    public CreateTableExpression AsDecimal(int precision, int scale)
    {
        ColumnDefinitionMutator.SetDecimal(Current(), precision, scale);
        return this;
    }

    /// <summary>Uses a single-precision floating-point type.</summary>
    public CreateTableExpression AsFloat() => SetType(MigrationColumnType.Single);

    /// <summary>Uses a double-precision floating-point type.</summary>
    public CreateTableExpression AsDouble() => SetType(MigrationColumnType.Double);

    /// <summary>Uses an unbounded string type.</summary>
    public CreateTableExpression AsString()
    {
        ColumnDefinitionMutator.SetString(Current(), null);
        return this;
    }

    /// <summary>Uses a variable-length string with the specified maximum length.</summary>
    public CreateTableExpression AsString(int length)
    {
        ColumnDefinitionMutator.SetString(Current(), length);
        return this;
    }

    /// <summary>Uses an unbounded text type.</summary>
    public CreateTableExpression AsText() => SetType(MigrationColumnType.Text);

    /// <summary>Uses a date type.</summary>
    public CreateTableExpression AsDate() => SetType(MigrationColumnType.Date);

    /// <summary>Uses a date-time type without a UTC offset.</summary>
    public CreateTableExpression AsDateTime() => SetType(MigrationColumnType.DateTime);

    /// <summary>Uses a date-time type representing an instant in time.</summary>
    public CreateTableExpression AsDateTimeOffset() => SetType(MigrationColumnType.DateTimeOffset);

    /// <summary>Uses a time-of-day type.</summary>
    public CreateTableExpression AsTime() => SetType(MigrationColumnType.Time);

    /// <summary>Uses a GUID type.</summary>
    public CreateTableExpression AsGuid() => SetType(MigrationColumnType.Guid);

    /// <summary>Uses a binary data type.</summary>
    public CreateTableExpression AsBinary() => SetType(MigrationColumnType.Binary);

    /// <summary>Uses a JSON text type.</summary>
    public CreateTableExpression AsJson() => SetType(MigrationColumnType.Json);

    /// <summary>Uses a binary JSON type where supported.</summary>
    public CreateTableExpression AsJsonb() => SetType(MigrationColumnType.JsonBinary);

    /// <summary>Allows database null values.</summary>
    public CreateTableExpression Nullable()
    {
        var column = Current();
        column.IsNullable = column.IsPrimaryKey ? false : true;
        return this;
    }

    /// <summary>Disallows database null values.</summary>
    public CreateTableExpression NotNullable()
    {
        Current().IsNullable = false;
        return this;
    }

    /// <summary>Makes the current column an inline primary key.</summary>
    public CreateTableExpression PrimaryKey()
    {
        ColumnDefinitionMutator.SetPrimaryKey(Current());
        return this;
    }

    /// <summary>Makes the database generate values for the current integer column.</summary>
    public CreateTableExpression Identity()
    {
        ColumnDefinitionMutator.SetIdentity(Current());
        return this;
    }

    private CreateTableExpression SetType(MigrationColumnType type)
    {
        ColumnDefinitionMutator.SetType(Current(), type);
        return this;
    }

    private ColumnDefinition Current()
    {
        return _currentColumn ?? throw new MigrationValidationException("WithColumn must be called before configuring a column.");
    }
}

/// <summary>Starts table alteration expressions.</summary>
public sealed class AlterExpressionRoot
{
    private readonly Action<MigrationOperation> _add;

    internal AlterExpressionRoot(Action<MigrationOperation> add)
    {
        _add = add;
    }

    /// <summary>Targets a table for column additions or changes.</summary>
    public AlterTableExpression Table(string tableName) =>
        new AlterTableExpression(ExpressionValidation.Name(tableName, nameof(tableName)), _add);
}

/// <summary>Builds add-column and alter-column operations for one table.</summary>
public sealed class AlterTableExpression
{
    private readonly string _tableName;
    private readonly Action<MigrationOperation> _add;
    private readonly List<MigrationOperation> _createdOperations = new List<MigrationOperation>();
    private string? _schemaName;
    private ColumnDefinition? _currentColumn;

    internal AlterTableExpression(string tableName, Action<MigrationOperation> add)
    {
        _tableName = tableName;
        _add = add;
    }

    /// <summary>Qualifies this table and all operations in the expression with a schema.</summary>
    public AlterTableExpression InSchema(string schemaName)
    {
        _schemaName = ExpressionValidation.Name(schemaName, nameof(schemaName));
        foreach (var operation in _createdOperations)
        {
            SetSchema(operation, _schemaName);
        }

        return this;
    }

    /// <summary>Adds a column and makes it the target of following type and option calls.</summary>
    public AlterTableExpression AddColumn(string columnName)
    {
        var column = new ColumnDefinition(ExpressionValidation.Name(columnName, nameof(columnName)), false);
        var operation = new AddColumnOperation(_tableName, column) { SchemaName = _schemaName };
        Add(operation, column);
        return this;
    }

    /// <summary>Changes a column and makes it the target of following type and nullability calls.</summary>
    public AlterTableExpression AlterColumn(string columnName)
    {
        var column = new ColumnDefinition(ExpressionValidation.Name(columnName, nameof(columnName)), true);
        var operation = new AlterColumnOperation(_tableName, column) { SchemaName = _schemaName };
        Add(operation, column);
        return this;
    }

    /// <summary>Uses a 16-bit integer type.</summary>
    public AlterTableExpression AsInt16() => SetType(MigrationColumnType.Int16);

    /// <summary>Uses a 32-bit integer type.</summary>
    public AlterTableExpression AsInt32() => SetType(MigrationColumnType.Int32);

    /// <summary>Uses a 64-bit integer type.</summary>
    public AlterTableExpression AsInt64() => SetType(MigrationColumnType.Int64);

    /// <summary>Uses a Boolean type.</summary>
    public AlterTableExpression AsBoolean() => SetType(MigrationColumnType.Boolean);

    /// <summary>Uses an unconstrained decimal type.</summary>
    public AlterTableExpression AsDecimal()
    {
        ColumnDefinitionMutator.SetDecimal(Current(), null, null);
        return this;
    }

    /// <summary>Uses a decimal type with precision and scale.</summary>
    public AlterTableExpression AsDecimal(int precision, int scale)
    {
        ColumnDefinitionMutator.SetDecimal(Current(), precision, scale);
        return this;
    }

    /// <summary>Uses a single-precision floating-point type.</summary>
    public AlterTableExpression AsFloat() => SetType(MigrationColumnType.Single);

    /// <summary>Uses a double-precision floating-point type.</summary>
    public AlterTableExpression AsDouble() => SetType(MigrationColumnType.Double);

    /// <summary>Uses an unbounded string type.</summary>
    public AlterTableExpression AsString()
    {
        ColumnDefinitionMutator.SetString(Current(), null);
        return this;
    }

    /// <summary>Uses a variable-length string with the specified maximum length.</summary>
    public AlterTableExpression AsString(int length)
    {
        ColumnDefinitionMutator.SetString(Current(), length);
        return this;
    }

    /// <summary>Uses an unbounded text type.</summary>
    public AlterTableExpression AsText() => SetType(MigrationColumnType.Text);

    /// <summary>Uses a date type.</summary>
    public AlterTableExpression AsDate() => SetType(MigrationColumnType.Date);

    /// <summary>Uses a date-time type without a UTC offset.</summary>
    public AlterTableExpression AsDateTime() => SetType(MigrationColumnType.DateTime);

    /// <summary>Uses a date-time type representing an instant in time.</summary>
    public AlterTableExpression AsDateTimeOffset() => SetType(MigrationColumnType.DateTimeOffset);

    /// <summary>Uses a time-of-day type.</summary>
    public AlterTableExpression AsTime() => SetType(MigrationColumnType.Time);

    /// <summary>Uses a GUID type.</summary>
    public AlterTableExpression AsGuid() => SetType(MigrationColumnType.Guid);

    /// <summary>Uses a binary data type.</summary>
    public AlterTableExpression AsBinary() => SetType(MigrationColumnType.Binary);

    /// <summary>Uses a JSON text type.</summary>
    public AlterTableExpression AsJson() => SetType(MigrationColumnType.Json);

    /// <summary>Uses a binary JSON type where supported.</summary>
    public AlterTableExpression AsJsonb() => SetType(MigrationColumnType.JsonBinary);

    /// <summary>Allows database null values.</summary>
    public AlterTableExpression Nullable()
    {
        var column = Current();
        column.IsNullable = column.IsPrimaryKey ? false : true;
        return this;
    }

    /// <summary>Disallows database null values.</summary>
    public AlterTableExpression NotNullable()
    {
        Current().IsNullable = false;
        return this;
    }

    /// <summary>Makes an added column an inline primary key.</summary>
    public AlterTableExpression PrimaryKey()
    {
        ColumnDefinitionMutator.SetPrimaryKey(Current());
        return this;
    }

    /// <summary>Makes the database generate values for an added integer column.</summary>
    public AlterTableExpression Identity()
    {
        ColumnDefinitionMutator.SetIdentity(Current());
        return this;
    }

    private void Add(MigrationOperation operation, ColumnDefinition column)
    {
        _createdOperations.Add(operation);
        _currentColumn = column;
        _add(operation);
    }

    private AlterTableExpression SetType(MigrationColumnType type)
    {
        ColumnDefinitionMutator.SetType(Current(), type);
        return this;
    }

    private ColumnDefinition Current()
    {
        return _currentColumn ?? throw new MigrationValidationException("AddColumn or AlterColumn must be called before configuring a column.");
    }

    private static void SetSchema(MigrationOperation operation, string schemaName)
    {
        if (operation is AddColumnOperation addColumn)
        {
            addColumn.SchemaName = schemaName;
        }
        else if (operation is AlterColumnOperation alterColumn)
        {
            alterColumn.SchemaName = schemaName;
        }
    }
}

/// <summary>Starts table and column deletion expressions.</summary>
public sealed class DeleteExpressionRoot
{
    private readonly Action<MigrationOperation> _add;

    internal DeleteExpressionRoot(Action<MigrationOperation> add)
    {
        _add = add;
    }

    /// <summary>Creates a table deletion operation.</summary>
    public DeleteTableExpression Table(string tableName)
    {
        var operation = new DeleteTableOperation(ExpressionValidation.Name(tableName, nameof(tableName)));
        _add(operation);
        return new DeleteTableExpression(operation);
    }

    /// <summary>Starts a column deletion operation completed by <c>FromTable</c>.</summary>
    public DeleteColumnFromExpression Column(string columnName) =>
        new DeleteColumnFromExpression(ExpressionValidation.Name(columnName, nameof(columnName)), _add);
}

/// <summary>Qualifies a table deletion operation.</summary>
public sealed class DeleteTableExpression
{
    private readonly DeleteTableOperation _operation;

    internal DeleteTableExpression(DeleteTableOperation operation)
    {
        _operation = operation;
    }

    /// <summary>Qualifies the table with a schema.</summary>
    public DeleteTableExpression InSchema(string schemaName)
    {
        _operation.SchemaName = ExpressionValidation.Name(schemaName, nameof(schemaName));
        return this;
    }
}

/// <summary>Completes a column deletion by selecting its table.</summary>
public sealed class DeleteColumnFromExpression
{
    private readonly string _columnName;
    private readonly Action<MigrationOperation> _add;

    internal DeleteColumnFromExpression(string columnName, Action<MigrationOperation> add)
    {
        _columnName = columnName;
        _add = add;
    }

    /// <summary>Selects the table containing the column.</summary>
    public DeleteColumnExpression FromTable(string tableName)
    {
        var operation = new DeleteColumnOperation(
            ExpressionValidation.Name(tableName, nameof(tableName)),
            _columnName);
        _add(operation);
        return new DeleteColumnExpression(operation);
    }
}

/// <summary>Qualifies a column deletion operation.</summary>
public sealed class DeleteColumnExpression
{
    private readonly DeleteColumnOperation _operation;

    internal DeleteColumnExpression(DeleteColumnOperation operation)
    {
        _operation = operation;
    }

    /// <summary>Qualifies the table with a schema.</summary>
    public DeleteColumnExpression InSchema(string schemaName)
    {
        _operation.SchemaName = ExpressionValidation.Name(schemaName, nameof(schemaName));
        return this;
    }
}

/// <summary>Starts table and column rename expressions.</summary>
public sealed class RenameExpressionRoot
{
    private readonly Action<MigrationOperation> _add;

    internal RenameExpressionRoot(Action<MigrationOperation> add)
    {
        _add = add;
    }

    /// <summary>Starts a table rename completed by <c>To</c>.</summary>
    public RenameTableToExpression Table(string tableName) =>
        new RenameTableToExpression(ExpressionValidation.Name(tableName, nameof(tableName)), _add);

    /// <summary>Starts a column rename completed by <c>OnTable(...).To(...)</c>.</summary>
    public RenameColumnOnExpression Column(string columnName) =>
        new RenameColumnOnExpression(ExpressionValidation.Name(columnName, nameof(columnName)), _add);
}

/// <summary>Selects the new table name and optional schema.</summary>
public sealed class RenameTableToExpression
{
    private readonly string _oldName;
    private readonly Action<MigrationOperation> _add;
    private string? _schemaName;

    internal RenameTableToExpression(string oldName, Action<MigrationOperation> add)
    {
        _oldName = oldName;
        _add = add;
    }

    /// <summary>Qualifies the current table name with a schema.</summary>
    public RenameTableToExpression InSchema(string schemaName)
    {
        _schemaName = ExpressionValidation.Name(schemaName, nameof(schemaName));
        return this;
    }

    /// <summary>Sets the new unqualified table name.</summary>
    public void To(string newName)
    {
        var operation = new RenameTableOperation(
            _oldName,
            ExpressionValidation.Name(newName, nameof(newName)))
        {
            SchemaName = _schemaName,
        };
        _add(operation);
    }
}

/// <summary>Selects the table containing a column to rename.</summary>
public sealed class RenameColumnOnExpression
{
    private readonly string _oldName;
    private readonly Action<MigrationOperation> _add;

    internal RenameColumnOnExpression(string oldName, Action<MigrationOperation> add)
    {
        _oldName = oldName;
        _add = add;
    }

    /// <summary>Selects the table containing the column.</summary>
    public RenameColumnToExpression OnTable(string tableName) =>
        new RenameColumnToExpression(
            ExpressionValidation.Name(tableName, nameof(tableName)),
            _oldName,
            _add);
}

/// <summary>Selects the new column name and optional table schema.</summary>
public sealed class RenameColumnToExpression
{
    private readonly string _tableName;
    private readonly string _oldName;
    private readonly Action<MigrationOperation> _add;
    private string? _schemaName;

    internal RenameColumnToExpression(string tableName, string oldName, Action<MigrationOperation> add)
    {
        _tableName = tableName;
        _oldName = oldName;
        _add = add;
    }

    /// <summary>Qualifies the table with a schema.</summary>
    public RenameColumnToExpression InSchema(string schemaName)
    {
        _schemaName = ExpressionValidation.Name(schemaName, nameof(schemaName));
        return this;
    }

    /// <summary>Sets the new unqualified column name.</summary>
    public void To(string newName)
    {
        var operation = new RenameColumnOperation(
            _tableName,
            _oldName,
            ExpressionValidation.Name(newName, nameof(newName)))
        {
            SchemaName = _schemaName,
        };
        _add(operation);
    }
}

/// <summary>Starts raw SQL operations.</summary>
public sealed class ExecuteExpressionRoot
{
    private readonly Action<MigrationOperation> _add;

    internal ExecuteExpressionRoot(Action<MigrationOperation> add)
    {
        _add = add;
    }

    /// <summary>Adds SQL to the operation stream without changing its text.</summary>
    public void Sql(string sql) => _add(new ExecuteSqlOperation(ExpressionValidation.Sql(sql)));
}

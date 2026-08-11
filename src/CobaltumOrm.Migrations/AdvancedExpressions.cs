using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CobaltumOrm.Migrations;

internal static class MigrationDataRowFactory
{
    internal static MigrationDataRow FromDictionary(IEnumerable<KeyValuePair<string, object?>> values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        var row = new List<KeyValuePair<string, object?>>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var name = ExpressionValidation.Name(value.Key, nameof(values));
            if (!names.Add(name))
            {
                throw new ArgumentException($"Column '{name}' appears more than once.", nameof(values));
            }

            row.Add(new KeyValuePair<string, object?>(name, value.Value));
        }

        if (row.Count == 0) throw new ArgumentException("At least one column value is required.", nameof(values));
        return new MigrationDataRow(row);
    }

    internal static MigrationDataRow FromObject<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (value is IEnumerable<KeyValuePair<string, object?>> nullableValues)
        {
            return FromDictionary(nullableValues);
        }

        if (value is IEnumerable<KeyValuePair<string, object>> values)
        {
            return FromDictionary(values.Select(item =>
                new KeyValuePair<string, object?>(item.Key, item.Value)));
        }

        var properties = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0 && property.CanRead)
            .OrderBy(property => property.MetadataToken);
        return FromDictionary(properties.Select(property =>
            new KeyValuePair<string, object?>(property.Name, property.GetValue(value, null))));
    }

    [RequiresUnreferencedCode("Runtime row types must preserve their public properties.")]
    internal static MigrationDataRow FromRuntimeObject(object value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (value is IEnumerable<KeyValuePair<string, object?>> nullableValues)
            return FromDictionary(nullableValues);
        if (value is IEnumerable<KeyValuePair<string, object>> values)
            return FromDictionary(values.Select(item =>
                new KeyValuePair<string, object?>(item.Key, item.Value)));

        var properties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0 && property.CanRead)
            .OrderBy(property => property.MetadataToken);
        return FromDictionary(properties.Select(property =>
            new KeyValuePair<string, object?>(property.Name, property.GetValue(value, null))));
    }
}

internal static class AdvancedColumnDefinitionMutator
{
    internal static void SetSizedType(ColumnDefinition column, MigrationColumnType type, int? length, string? collation)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "Length must be positive.");
        ColumnDefinitionMutator.SetType(column, type);
        column.Length = length;
        column.CollationName = collation is null ? null : ExpressionValidation.Name(collation, nameof(collation));
    }

    internal static void SetDateTimePrecision(ColumnDefinition column, MigrationColumnType type, int precision)
    {
        if (precision < 0 || precision > 9)
            throw new ArgumentOutOfRangeException(nameof(precision), "Date and time precision must be between zero and nine.");
        ColumnDefinitionMutator.SetType(column, type);
        column.DateTimePrecision = precision;
    }

    internal static void SetCustom(ColumnDefinition column, string customType)
    {
        ColumnDefinitionMutator.SetType(column, MigrationColumnType.Custom);
        column.CustomType = ExpressionValidation.Name(customType, nameof(customType));
    }

    internal static void SetDefault(ColumnDefinition column, object? value)
    {
        if (column.ComputedExpression != null)
            throw new MigrationValidationException("A computed column cannot also have a default value.");
        column.HasDefaultValue = true;
        column.DefaultValue = value;
    }

    internal static void SetComputed(ColumnDefinition column, string expression, bool stored)
    {
        if (column.HasDefaultValue)
            throw new MigrationValidationException("A computed column cannot also have a default value.");
        column.ComputedExpression = ExpressionValidation.Sql(expression);
        column.IsComputedStored = stored;
    }
}

/// <summary>Provides migration roots whose operations apply only to selected databases.</summary>
public sealed class IfDatabaseExpressionRoot
{
    private readonly Action<Action> _delegate;

    internal IfDatabaseExpressionRoot(
        Action<MigrationOperation> add,
        Func<Type> migrationType,
        Action<Action> delegation)
    {
        _delegate = delegation;
        Alter = new AlterExpressionRoot(add);
        Create = new CreateExpressionRoot(add);
        Delete = new DeleteExpressionRoot(add);
        Rename = new RenameExpressionRoot(add);
        Insert = new InsertExpressionRoot(add);
        Execute = new ExecuteExpressionRoot(add, migrationType);
        Update = new UpdateExpressionRoot(add);
    }

    /// <summary>Gets the conditional alter root.</summary>
    public AlterExpressionRoot Alter { get; }
    /// <summary>Gets the conditional create root.</summary>
    public CreateExpressionRoot Create { get; }
    /// <summary>Gets the conditional delete root.</summary>
    public DeleteExpressionRoot Delete { get; }
    /// <summary>Gets the conditional rename root.</summary>
    public RenameExpressionRoot Rename { get; }
    /// <summary>Gets the conditional insert root.</summary>
    public InsertExpressionRoot Insert { get; }
    /// <summary>Gets the conditional SQL execution root.</summary>
    public ExecuteExpressionRoot Execute { get; }
    /// <summary>Gets the conditional update root.</summary>
    public UpdateExpressionRoot Update { get; }

    /// <summary>Collects operations produced by a database-specific delegate.</summary>
    public void Delegate(Action delegation)
    {
        if (delegation is null) throw new ArgumentNullException(nameof(delegation));
        _delegate(delegation);
    }
}

public sealed partial class CreateExpressionRoot
{
    /// <summary>Creates a schema.</summary>
    public CreateSchemaExpression Schema(string schemaName)
    {
        var operation = new CreateSchemaOperation(ExpressionValidation.Name(schemaName, nameof(schemaName)));
        _add(operation);
        return new CreateSchemaExpression(operation);
    }

    /// <summary>Creates a column on an existing table.</summary>
    public CreateColumnOnExpression Column(string columnName) =>
        new CreateColumnOnExpression(ExpressionValidation.Name(columnName, nameof(columnName)), _add);

    /// <summary>Creates a foreign key using a generated name.</summary>
    public CreateForeignKeyExpression ForeignKey() => new CreateForeignKeyExpression(null, _add);

    /// <summary>Creates a named foreign key.</summary>
    public CreateForeignKeyExpression ForeignKey(string foreignKeyName) =>
        new CreateForeignKeyExpression(ExpressionValidation.Name(foreignKeyName, nameof(foreignKeyName)), _add);

    /// <summary>Creates an index using a generated name.</summary>
    public CreateIndexExpression Index() => new CreateIndexExpression(null, _add);

    /// <summary>Creates a named index.</summary>
    public CreateIndexExpression Index(string indexName) =>
        new CreateIndexExpression(ExpressionValidation.Name(indexName, nameof(indexName)), _add);

    /// <summary>Creates a sequence.</summary>
    public CreateSequenceExpression Sequence(string sequenceName)
    {
        var operation = new CreateSequenceOperation(ExpressionValidation.Name(sequenceName, nameof(sequenceName)));
        _add(operation);
        return new CreateSequenceExpression(operation);
    }

    /// <summary>Creates a primary key using a generated name.</summary>
    public CreateConstraintExpression PrimaryKey() =>
        new CreateConstraintExpression(MigrationConstraintType.PrimaryKey, null, _add);

    /// <summary>Creates a named primary key.</summary>
    public CreateConstraintExpression PrimaryKey(string primaryKeyName) =>
        new CreateConstraintExpression(
            MigrationConstraintType.PrimaryKey,
            ExpressionValidation.Name(primaryKeyName, nameof(primaryKeyName)),
            _add);

    /// <summary>Creates a unique constraint using a generated name.</summary>
    public CreateConstraintExpression UniqueConstraint() =>
        new CreateConstraintExpression(MigrationConstraintType.Unique, null, _add);

    /// <summary>Creates a named unique constraint.</summary>
    public CreateConstraintExpression UniqueConstraint(string constraintName) =>
        new CreateConstraintExpression(
            MigrationConstraintType.Unique,
            ExpressionValidation.Name(constraintName, nameof(constraintName)),
            _add);
}

/// <summary>Represents a completed schema creation.</summary>
public sealed class CreateSchemaExpression
{
    internal CreateSchemaExpression(CreateSchemaOperation operation) => Operation = operation;
    internal CreateSchemaOperation Operation { get; }
}

/// <summary>Selects the table for a standalone column creation.</summary>
public sealed class CreateColumnOnExpression
{
    private readonly string _columnName;
    private readonly Action<MigrationOperation> _add;
    internal CreateColumnOnExpression(string columnName, Action<MigrationOperation> add)
    {
        _columnName = columnName;
        _add = add;
    }

    /// <summary>Selects the table receiving the column.</summary>
    public AlterTableExpression OnTable(string tableName) =>
        new AlterTableExpression(ExpressionValidation.Name(tableName, nameof(tableName)), _add)
            .AddColumn(_columnName);
}

public sealed partial class CreateTableExpression
{
    /// <summary>Skips creation when the table already exists.</summary>
    public CreateTableExpression IfNotExists()
    {
        _operation.IfNotExists = true;
        return this;
    }

    /// <summary>Adds a description to the table where the provider supports it.</summary>
    public CreateTableExpression WithDescription(string description)
    {
        _operation.Description = ExpressionValidation.Sql(description);
        return this;
    }

    /// <summary>Uses an unsigned 8-bit integer type.</summary>
    public CreateTableExpression AsByte() => SetType(MigrationColumnType.Byte);
    /// <summary>Uses a currency type.</summary>
    public CreateTableExpression AsCurrency() => SetType(MigrationColumnType.Currency);
    /// <summary>Uses an unbounded non-Unicode string.</summary>
    public CreateTableExpression AsAnsiString() => SetSized(MigrationColumnType.AnsiString, null, null);
    /// <summary>Uses a bounded non-Unicode string.</summary>
    public CreateTableExpression AsAnsiString(int size) => SetSized(MigrationColumnType.AnsiString, size, null);
    /// <summary>Uses a collated unbounded non-Unicode string.</summary>
    public CreateTableExpression AsAnsiString(string collationName) => SetSized(MigrationColumnType.AnsiString, null, collationName);
    /// <summary>Uses a collated bounded non-Unicode string.</summary>
    public CreateTableExpression AsAnsiString(int size, string collationName) => SetSized(MigrationColumnType.AnsiString, size, collationName);
    /// <summary>Uses a fixed-length Unicode string.</summary>
    public CreateTableExpression AsFixedLengthString(int size) => SetSized(MigrationColumnType.FixedString, size, null);
    /// <summary>Uses a collated fixed-length Unicode string.</summary>
    public CreateTableExpression AsFixedLengthString(int size, string collationName) => SetSized(MigrationColumnType.FixedString, size, collationName);
    /// <summary>Uses a fixed-length non-Unicode string.</summary>
    public CreateTableExpression AsFixedLengthAnsiString(int size) => SetSized(MigrationColumnType.FixedAnsiString, size, null);
    /// <summary>Uses a collated fixed-length non-Unicode string.</summary>
    public CreateTableExpression AsFixedLengthAnsiString(int size, string collationName) => SetSized(MigrationColumnType.FixedAnsiString, size, collationName);
    /// <summary>Uses a bounded binary type.</summary>
    public CreateTableExpression AsBinary(int size) => SetSized(MigrationColumnType.Binary, size, null);
    /// <summary>Uses the provider's second-generation date-time type.</summary>
    public CreateTableExpression AsDateTime2() => SetType(MigrationColumnType.DateTime);
    /// <summary>Uses a date-time-with-offset type with fractional-second precision.</summary>
    public CreateTableExpression AsDateTimeOffset(int precision) => SetDateTimePrecision(MigrationColumnType.DateTimeOffset, precision);
    /// <summary>Uses an XML type.</summary>
    public CreateTableExpression AsXml() => SetType(MigrationColumnType.Xml);
    /// <summary>Uses a bounded XML type where supported.</summary>
    public CreateTableExpression AsXml(int size) => SetSized(MigrationColumnType.Xml, size, null);
    /// <summary>Uses a provider-specific type.</summary>
    public CreateTableExpression AsCustom(string customType)
    {
        AdvancedColumnDefinitionMutator.SetCustom(Current(), customType);
        return this;
    }
    /// <summary>Uses a collation with an unbounded string.</summary>
    public CreateTableExpression AsString(string collationName) => SetString(null, collationName);
    /// <summary>Uses a collation with a bounded string.</summary>
    public CreateTableExpression AsString(int size, string collationName) => SetString(size, collationName);
    /// <summary>Sets a database system method as the default.</summary>
    public CreateTableExpression WithDefault(SystemMethods method) => WithDefaultValue(method);
    /// <summary>Sets a column default value.</summary>
    public CreateTableExpression WithDefaultValue(object? value)
    {
        AdvancedColumnDefinitionMutator.SetDefault(Current(), value);
        return this;
    }
    /// <summary>Adds a column description where supported.</summary>
    public CreateTableExpression WithColumnDescription(string description)
    {
        Current().Description = ExpressionValidation.Sql(description);
        return this;
    }
    /// <summary>Adds a named column description.</summary>
    public CreateTableExpression WithColumnAdditionalDescription(string descriptionName, string description)
    {
        AddDescription(Current(), descriptionName, description);
        return this;
    }
    /// <summary>Adds named column descriptions.</summary>
    public CreateTableExpression WithColumnAdditionalDescriptions(Dictionary<string, string> columnDescriptions)
    {
        if (columnDescriptions is null) throw new ArgumentNullException(nameof(columnDescriptions));
        if (columnDescriptions.Count == 0) throw new ArgumentException("At least one column description is required.", nameof(columnDescriptions));
        foreach (var item in columnDescriptions) AddDescription(Current(), item.Key, item.Value);
        return this;
    }
    /// <summary>Requests a non-unique index for the current column.</summary>
    public CreateTableExpression Indexed() => IndexedCore(null);
    /// <summary>Requests a named non-unique index for the current column.</summary>
    public CreateTableExpression Indexed(string indexName) => IndexedCore(ExpressionValidation.Name(indexName, nameof(indexName)));
    /// <summary>Requests a unique index for the current column.</summary>
    public CreateTableExpression Unique() => UniqueCore(null);
    /// <summary>Requests a named unique index for the current column.</summary>
    public CreateTableExpression Unique(string indexName) => UniqueCore(ExpressionValidation.Name(indexName, nameof(indexName)));
    /// <summary>Uses a named primary-key constraint for the current column.</summary>
    public CreateTableExpression PrimaryKey(string primaryKeyName)
    {
        PrimaryKey();
        Current().PrimaryKeyName = ExpressionValidation.Name(primaryKeyName, nameof(primaryKeyName));
        return this;
    }
    /// <summary>Defines a generated column.</summary>
    public CreateTableExpression Computed(string expression, bool stored = false)
    {
        AdvancedColumnDefinitionMutator.SetComputed(Current(), expression, stored);
        return this;
    }
    /// <summary>Creates a foreign key from the current column.</summary>
    public CreateTableExpression ForeignKey(string primaryTableName, string primaryColumnName) =>
        ForeignKeyCore(null, null, primaryTableName, primaryColumnName);
    /// <summary>Creates a named foreign key from the current column.</summary>
    public CreateTableExpression ForeignKey(string foreignKeyName, string primaryTableName, string primaryColumnName) =>
        ForeignKeyCore(ExpressionValidation.Name(foreignKeyName, nameof(foreignKeyName)), null, primaryTableName, primaryColumnName);
    /// <summary>Creates a named foreign key from the current column to a table in a schema.</summary>
    public CreateTableExpression ForeignKey(string foreignKeyName, string primaryTableSchema, string primaryTableName, string primaryColumnName) =>
        ForeignKeyCore(
            ExpressionValidation.Name(foreignKeyName, nameof(foreignKeyName)),
            ExpressionValidation.Name(primaryTableSchema, nameof(primaryTableSchema)),
            primaryTableName,
            primaryColumnName);
    /// <summary>Marks the current column as a foreign key for provider naming conventions.</summary>
    public CreateTableExpression ForeignKey() => this;
    /// <summary>Creates a foreign key in another table that references the current column.</summary>
    public CreateTableExpression ReferencedBy(string foreignTableName, string foreignColumnName) =>
        ReferencedByCore(null, null, foreignTableName, foreignColumnName);
    /// <summary>Creates a named foreign key in another table that references the current column.</summary>
    public CreateTableExpression ReferencedBy(string foreignKeyName, string foreignTableName, string foreignColumnName) =>
        ReferencedByCore(ExpressionValidation.Name(foreignKeyName, nameof(foreignKeyName)), null, foreignTableName, foreignColumnName);
    /// <summary>Creates a named foreign key in another schema that references the current column.</summary>
    public CreateTableExpression ReferencedBy(string foreignKeyName, string foreignTableSchema, string foreignTableName, string foreignColumnName) =>
        ReferencedByCore(
            ExpressionValidation.Name(foreignKeyName, nameof(foreignKeyName)),
            ExpressionValidation.Name(foreignTableSchema, nameof(foreignTableSchema)),
            foreignTableName,
            foreignColumnName);
    /// <summary>Sets the foreign-key delete action.</summary>
    public CreateTableExpression OnDelete(Rule rule)
    {
        CurrentForeignKey().OnDelete = rule;
        return this;
    }
    /// <summary>Sets the foreign-key update action.</summary>
    public CreateTableExpression OnUpdate(Rule rule)
    {
        CurrentForeignKey().OnUpdate = rule;
        return this;
    }
    /// <summary>Sets both foreign-key actions.</summary>
    public CreateTableExpression OnDeleteOrUpdate(Rule rule)
    {
        OnDelete(rule);
        return OnUpdate(rule);
    }

    private CreateTableExpression SetSized(MigrationColumnType type, int? size, string? collation)
    {
        AdvancedColumnDefinitionMutator.SetSizedType(Current(), type, size, collation);
        return this;
    }
    private CreateTableExpression SetString(int? size, string? collation)
    {
        AdvancedColumnDefinitionMutator.SetSizedType(Current(), MigrationColumnType.String, size, collation);
        return this;
    }
    private CreateTableExpression SetDateTimePrecision(MigrationColumnType type, int precision)
    {
        AdvancedColumnDefinitionMutator.SetDateTimePrecision(Current(), type, precision);
        return this;
    }
    private CreateTableExpression IndexedCore(string? name)
    {
        Current().IsIndexed = true;
        Current().IndexName = name;
        return this;
    }
    private CreateTableExpression UniqueCore(string? name)
    {
        Current().IsUnique = true;
        Current().UniqueIndexName = name;
        return this;
    }
    private CreateTableExpression ForeignKeyCore(string? name, string? schema, string table, string column)
    {
        var definition = new ForeignKeyDefinition(name)
        {
            PrimaryTableSchema = schema,
            PrimaryTableName = ExpressionValidation.Name(table, nameof(table)),
            ForeignTableName = _operation.TableName,
            ForeignTableSchema = _operation.SchemaName,
        };
        definition.AddForeignColumns(new[] { Current().Name });
        definition.AddPrimaryColumns(new[] { column });
        Current().ForeignKey = definition;
        Current().ActiveForeignKey = definition;
        return this;
    }
    private CreateTableExpression ReferencedByCore(string? name, string? schema, string table, string column)
    {
        var definition = new ForeignKeyDefinition(name)
        {
            ForeignTableSchema = schema,
            ForeignTableName = ExpressionValidation.Name(table, nameof(table)),
            PrimaryTableName = _operation.TableName,
            PrimaryTableSchema = _operation.SchemaName,
        };
        definition.AddForeignColumns(new[] { column });
        definition.AddPrimaryColumns(new[] { Current().Name });
        Current().AddReferencedBy(definition);
        Current().ActiveForeignKey = definition;
        return this;
    }
    private ForeignKeyDefinition CurrentForeignKey() =>
        Current().ActiveForeignKey ?? throw new MigrationValidationException("OnDelete or OnUpdate must follow ForeignKey or ReferencedBy.");
    private static void AddDescription(ColumnDefinition column, string descriptionName, string description)
    {
        var name = ExpressionValidation.Name(descriptionName, nameof(descriptionName));
        if (string.Equals(name, "Description", StringComparison.Ordinal))
            throw new ArgumentException("'Description' is reserved for WithColumnDescription.", nameof(descriptionName));
        column.AddDescription(name, ExpressionValidation.Sql(description));
    }
}

public sealed partial class AlterExpressionRoot
{
    /// <summary>Targets one column for alteration.</summary>
    public AlterColumnOnExpression Column(string columnName) =>
        new AlterColumnOnExpression(ExpressionValidation.Name(columnName, nameof(columnName)), _add);
}

/// <summary>Selects the table containing a column to alter.</summary>
public sealed class AlterColumnOnExpression
{
    private readonly string _columnName;
    private readonly Action<MigrationOperation> _add;
    internal AlterColumnOnExpression(string columnName, Action<MigrationOperation> add)
    {
        _columnName = columnName;
        _add = add;
    }
    /// <summary>Selects the table containing the column.</summary>
    public AlterTableExpression OnTable(string tableName) =>
        new AlterTableExpression(ExpressionValidation.Name(tableName, nameof(tableName)), _add)
            .AlterColumn(_columnName);
}

public sealed partial class AlterTableExpression
{
    private bool _ifExists;

    /// <summary>Skips the table alteration when the table does not exist.</summary>
    public AlterTableExpression IfExists()
    {
        _ifExists = true;
        foreach (var operation in _createdOperations)
        {
            if (operation is AddColumnOperation add) add.IfTableExists = true;
            if (operation is AlterColumnOperation alter) alter.IfTableExists = true;
        }
        return this;
    }

    /// <summary>Moves the table to another schema.</summary>
    public void ToSchema(string schemaName) => _add(new MoveTableOperation(
        _tableName,
        _schemaName,
        ExpressionValidation.Name(schemaName, nameof(schemaName))));

    /// <summary>Adds or replaces the table description where supported.</summary>
    public AlterTableExpression WithDescription(string description)
    {
        var operation = new AlterTableDescriptionOperation(_tableName, ExpressionValidation.Sql(description))
        {
            SchemaName = _schemaName,
        };
        _createdOperations.Add(operation);
        _add(operation);
        return this;
    }

    /// <summary>Uses an unsigned 8-bit integer type.</summary>
    public AlterTableExpression AsByte() => SetType(MigrationColumnType.Byte);
    /// <summary>Uses a currency type.</summary>
    public AlterTableExpression AsCurrency() => SetType(MigrationColumnType.Currency);
    /// <summary>Uses an unbounded non-Unicode string.</summary>
    public AlterTableExpression AsAnsiString() => SetSized(MigrationColumnType.AnsiString, null, null);
    /// <summary>Uses a bounded non-Unicode string.</summary>
    public AlterTableExpression AsAnsiString(int size) => SetSized(MigrationColumnType.AnsiString, size, null);
    /// <summary>Uses a collated unbounded non-Unicode string.</summary>
    public AlterTableExpression AsAnsiString(string collationName) => SetSized(MigrationColumnType.AnsiString, null, collationName);
    /// <summary>Uses a collated bounded non-Unicode string.</summary>
    public AlterTableExpression AsAnsiString(int size, string collationName) => SetSized(MigrationColumnType.AnsiString, size, collationName);
    /// <summary>Uses a fixed-length Unicode string.</summary>
    public AlterTableExpression AsFixedLengthString(int size) => SetSized(MigrationColumnType.FixedString, size, null);
    /// <summary>Uses a collated fixed-length Unicode string.</summary>
    public AlterTableExpression AsFixedLengthString(int size, string collationName) => SetSized(MigrationColumnType.FixedString, size, collationName);
    /// <summary>Uses a fixed-length non-Unicode string.</summary>
    public AlterTableExpression AsFixedLengthAnsiString(int size) => SetSized(MigrationColumnType.FixedAnsiString, size, null);
    /// <summary>Uses a collated fixed-length non-Unicode string.</summary>
    public AlterTableExpression AsFixedLengthAnsiString(int size, string collationName) => SetSized(MigrationColumnType.FixedAnsiString, size, collationName);
    /// <summary>Uses a bounded binary type.</summary>
    public AlterTableExpression AsBinary(int size) => SetSized(MigrationColumnType.Binary, size, null);
    /// <summary>Uses the provider's second-generation date-time type.</summary>
    public AlterTableExpression AsDateTime2() => SetType(MigrationColumnType.DateTime);
    /// <summary>Uses a date-time-with-offset type with fractional-second precision.</summary>
    public AlterTableExpression AsDateTimeOffset(int precision) => SetDateTimePrecision(MigrationColumnType.DateTimeOffset, precision);
    /// <summary>Uses an XML type.</summary>
    public AlterTableExpression AsXml() => SetType(MigrationColumnType.Xml);
    /// <summary>Uses a bounded XML type where supported.</summary>
    public AlterTableExpression AsXml(int size) => SetSized(MigrationColumnType.Xml, size, null);
    /// <summary>Uses a provider-specific type.</summary>
    public AlterTableExpression AsCustom(string customType)
    {
        AdvancedColumnDefinitionMutator.SetCustom(Current(), customType);
        return this;
    }
    /// <summary>Uses a collation with an unbounded string.</summary>
    public AlterTableExpression AsString(string collationName) => SetString(null, collationName);
    /// <summary>Uses a collation with a bounded string.</summary>
    public AlterTableExpression AsString(int size, string collationName) => SetString(size, collationName);
    /// <summary>Sets a database system method as the default.</summary>
    public AlterTableExpression WithDefault(SystemMethods method) => WithDefaultValue(method);
    /// <summary>Sets a column default value.</summary>
    public AlterTableExpression WithDefaultValue(object? value)
    {
        AdvancedColumnDefinitionMutator.SetDefault(Current(), value);
        return this;
    }
    /// <summary>Sets the value for rows that exist before an added column is created.</summary>
    public AlterTableExpression SetExistingRowsTo(object? value)
    {
        var column = Current();
        if (column.IsAlteration)
            throw new MigrationValidationException("SetExistingRowsTo can only be used with AddColumn or Create.Column.");
        if (column.HasExistingRowsValue)
            throw new MigrationValidationException($"SetExistingRowsTo was already configured for column '{column.Name}'.");
        column.HasExistingRowsValue = true;
        column.ExistingRowsValue = value;
        var finalNotNullable = column.IsNullable == false;
        if (finalNotNullable && (value is null || value == DBNull.Value))
            throw new MigrationValidationException(
                $"SetExistingRowsTo for non-null column '{column.Name}' requires a non-null value.");
        if (finalNotNullable) column.IsNullable = true;

        var update = new UpdateDataOperation(_tableName)
        {
            SchemaName = _schemaName,
            AllRows = true,
            Values = MigrationDataRowFactory.FromDictionary(new[]
            {
                new KeyValuePair<string, object?>(column.Name, value),
            }),
        };
        _createdOperations.Add(update);
        _add(update);

        if (finalNotNullable)
        {
            EnsureExistingRowsFinalColumn(column).IsNullable = false;
        }
        return this;
    }
    /// <summary>Adds a column description where supported.</summary>
    public AlterTableExpression WithColumnDescription(string description)
    {
        Current().Description = ExpressionValidation.Sql(description);
        return this;
    }
    /// <summary>Adds a named column description.</summary>
    public AlterTableExpression WithColumnAdditionalDescription(string descriptionName, string description)
    {
        AddDescription(Current(), descriptionName, description);
        return this;
    }
    /// <summary>Adds named column descriptions.</summary>
    public AlterTableExpression WithColumnAdditionalDescriptions(Dictionary<string, string> columnDescriptions)
    {
        if (columnDescriptions is null) throw new ArgumentNullException(nameof(columnDescriptions));
        if (columnDescriptions.Count == 0) throw new ArgumentException("At least one column description is required.", nameof(columnDescriptions));
        foreach (var item in columnDescriptions) AddDescription(Current(), item.Key, item.Value);
        return this;
    }
    /// <summary>Requests a non-unique index for the current column.</summary>
    public AlterTableExpression Indexed() => IndexedCore(null);
    /// <summary>Requests a named non-unique index for the current column.</summary>
    public AlterTableExpression Indexed(string indexName) => IndexedCore(ExpressionValidation.Name(indexName, nameof(indexName)));
    /// <summary>Requests a unique index for the current column.</summary>
    public AlterTableExpression Unique() => UniqueCore(null);
    /// <summary>Requests a named unique index for the current column.</summary>
    public AlterTableExpression Unique(string indexName) => UniqueCore(ExpressionValidation.Name(indexName, nameof(indexName)));
    /// <summary>Uses a named primary-key constraint for an added column.</summary>
    public AlterTableExpression PrimaryKey(string primaryKeyName)
    {
        PrimaryKey();
        Current().PrimaryKeyName = ExpressionValidation.Name(primaryKeyName, nameof(primaryKeyName));
        return this;
    }
    /// <summary>Defines a generated column.</summary>
    public AlterTableExpression Computed(string expression, bool stored = false)
    {
        AdvancedColumnDefinitionMutator.SetComputed(Current(), expression, stored);
        return this;
    }
    /// <summary>Creates a foreign key from the current column.</summary>
    public AlterTableExpression ForeignKey(string primaryTableName, string primaryColumnName) =>
        ForeignKeyCore(null, null, primaryTableName, primaryColumnName);
    /// <summary>Creates a named foreign key from the current column.</summary>
    public AlterTableExpression ForeignKey(string foreignKeyName, string primaryTableName, string primaryColumnName) =>
        ForeignKeyCore(ExpressionValidation.Name(foreignKeyName, nameof(foreignKeyName)), null, primaryTableName, primaryColumnName);
    /// <summary>Creates a named foreign key to a table in a schema.</summary>
    public AlterTableExpression ForeignKey(string foreignKeyName, string primaryTableSchema, string primaryTableName, string primaryColumnName) =>
        ForeignKeyCore(
            ExpressionValidation.Name(foreignKeyName, nameof(foreignKeyName)),
            ExpressionValidation.Name(primaryTableSchema, nameof(primaryTableSchema)),
            primaryTableName,
            primaryColumnName);
    /// <summary>Marks the current column as a foreign key for provider naming conventions.</summary>
    public AlterTableExpression ForeignKey() => this;
    /// <summary>Creates a foreign key in another table that references the current column.</summary>
    public AlterTableExpression ReferencedBy(string foreignTableName, string foreignColumnName) =>
        ReferencedByCore(null, null, foreignTableName, foreignColumnName);
    /// <summary>Creates a named foreign key in another table that references the current column.</summary>
    public AlterTableExpression ReferencedBy(string foreignKeyName, string foreignTableName, string foreignColumnName) =>
        ReferencedByCore(ExpressionValidation.Name(foreignKeyName, nameof(foreignKeyName)), null, foreignTableName, foreignColumnName);
    /// <summary>Creates a named foreign key in another schema that references the current column.</summary>
    public AlterTableExpression ReferencedBy(string foreignKeyName, string foreignTableSchema, string foreignTableName, string foreignColumnName) =>
        ReferencedByCore(
            ExpressionValidation.Name(foreignKeyName, nameof(foreignKeyName)),
            ExpressionValidation.Name(foreignTableSchema, nameof(foreignTableSchema)),
            foreignTableName,
            foreignColumnName);
    /// <summary>Sets the foreign-key delete action.</summary>
    public AlterTableExpression OnDelete(Rule rule)
    {
        CurrentForeignKey().OnDelete = rule;
        return this;
    }
    /// <summary>Sets the foreign-key update action.</summary>
    public AlterTableExpression OnUpdate(Rule rule)
    {
        CurrentForeignKey().OnUpdate = rule;
        return this;
    }
    /// <summary>Sets both foreign-key actions.</summary>
    public AlterTableExpression OnDeleteOrUpdate(Rule rule)
    {
        OnDelete(rule);
        return OnUpdate(rule);
    }

    private AlterTableExpression SetSized(MigrationColumnType type, int? size, string? collation)
    {
        AdvancedColumnDefinitionMutator.SetSizedType(Current(), type, size, collation);
        return this;
    }
    private AlterTableExpression SetString(int? size, string? collation)
    {
        AdvancedColumnDefinitionMutator.SetSizedType(Current(), MigrationColumnType.String, size, collation);
        return this;
    }
    private AlterTableExpression SetDateTimePrecision(MigrationColumnType type, int precision)
    {
        AdvancedColumnDefinitionMutator.SetDateTimePrecision(Current(), type, precision);
        return this;
    }
    private AlterTableExpression IndexedCore(string? name)
    {
        Current().IsIndexed = true;
        Current().IndexName = name;
        return this;
    }
    private AlterTableExpression UniqueCore(string? name)
    {
        Current().IsUnique = true;
        Current().UniqueIndexName = name;
        return this;
    }
    private AlterTableExpression ForeignKeyCore(string? name, string? schema, string table, string column)
    {
        var definition = new ForeignKeyDefinition(name)
        {
            PrimaryTableSchema = schema,
            PrimaryTableName = ExpressionValidation.Name(table, nameof(table)),
            ForeignTableName = _tableName,
            ForeignTableSchema = _schemaName,
        };
        definition.AddForeignColumns(new[] { Current().Name });
        definition.AddPrimaryColumns(new[] { column });
        Current().ForeignKey = definition;
        Current().ActiveForeignKey = definition;
        return this;
    }
    private AlterTableExpression ReferencedByCore(string? name, string? schema, string table, string column)
    {
        var definition = new ForeignKeyDefinition(name)
        {
            ForeignTableSchema = schema,
            ForeignTableName = ExpressionValidation.Name(table, nameof(table)),
            PrimaryTableName = _tableName,
            PrimaryTableSchema = _schemaName,
        };
        definition.AddForeignColumns(new[] { column });
        definition.AddPrimaryColumns(new[] { Current().Name });
        Current().AddReferencedBy(definition);
        Current().ActiveForeignKey = definition;
        return this;
    }
    private ForeignKeyDefinition CurrentForeignKey() =>
        Current().ActiveForeignKey ?? throw new MigrationValidationException("OnDelete or OnUpdate must follow ForeignKey or ReferencedBy.");
    private ColumnDefinition EnsureExistingRowsFinalColumn(ColumnDefinition column)
    {
        if (column.ExistingRowsFinalColumn != null) return column.ExistingRowsFinalColumn;
        var finalColumn = new ColumnDefinition(column.Name, true)
        {
            Type = column.Type,
            Length = column.Length,
            Precision = column.Precision,
            Scale = column.Scale,
            DateTimePrecision = column.DateTimePrecision,
            CollationName = column.CollationName,
            CustomType = column.CustomType,
            IsNullable = false,
        };
        var finalAlter = new AlterColumnOperation(_tableName, finalColumn) { SchemaName = _schemaName };
        column.ExistingRowsFinalColumn = finalColumn;
        _createdOperations.Add(finalAlter);
        _add(finalAlter);
        return finalColumn;
    }
    private static void AddDescription(ColumnDefinition column, string descriptionName, string description)
    {
        var name = ExpressionValidation.Name(descriptionName, nameof(descriptionName));
        if (string.Equals(name, "Description", StringComparison.Ordinal))
            throw new ArgumentException("'Description' is reserved for WithColumnDescription.", nameof(descriptionName));
        column.AddDescription(name, ExpressionValidation.Sql(description));
    }
}

/// <summary>Builds a foreign-key creation.</summary>
public sealed class CreateForeignKeyExpression
{
    private readonly CreateForeignKeyOperation _operation;
    private readonly Action<MigrationOperation> _add;
    private bool _added;
    internal CreateForeignKeyExpression(string? name, Action<MigrationOperation> add)
    {
        _operation = new CreateForeignKeyOperation(name);
        _add = add;
    }
    /// <summary>Selects the table containing the foreign-key columns.</summary>
    public CreateForeignKeyExpression FromTable(string table)
    {
        _operation.ForeignKey.ForeignTableName = ExpressionValidation.Name(table, nameof(table));
        return this;
    }
    /// <summary>Selects the schema containing the most recently selected table.</summary>
    public CreateForeignKeyExpression InSchema(string schema)
    {
        var name = ExpressionValidation.Name(schema, nameof(schema));
        if (string.IsNullOrEmpty(_operation.ForeignKey.PrimaryTableName))
            _operation.ForeignKey.ForeignTableSchema = name;
        else
            _operation.ForeignKey.PrimaryTableSchema = name;
        return this;
    }
    /// <summary>Adds one foreign-key column.</summary>
    public CreateForeignKeyExpression ForeignColumn(string column) => ForeignColumns(column);
    /// <summary>Adds foreign-key columns.</summary>
    public CreateForeignKeyExpression ForeignColumns(params string[] columns)
    {
        _operation.ForeignKey.AddForeignColumns(columns ?? throw new ArgumentNullException(nameof(columns)));
        return this;
    }
    /// <summary>Selects the referenced table.</summary>
    public CreateForeignKeyExpression ToTable(string table)
    {
        _operation.ForeignKey.PrimaryTableName = ExpressionValidation.Name(table, nameof(table));
        return this;
    }
    /// <summary>Adds one referenced column and completes the operation.</summary>
    public CreateForeignKeyExpression PrimaryColumn(string column) => PrimaryColumns(column);
    /// <summary>Adds referenced columns and completes the operation.</summary>
    public CreateForeignKeyExpression PrimaryColumns(params string[] columns)
    {
        _operation.ForeignKey.AddPrimaryColumns(columns ?? throw new ArgumentNullException(nameof(columns)));
        AddOnce();
        return this;
    }
    /// <summary>Sets the delete action.</summary>
    public CreateForeignKeyExpression OnDelete(Rule rule) { _operation.ForeignKey.OnDelete = rule; return this; }
    /// <summary>Sets the update action.</summary>
    public CreateForeignKeyExpression OnUpdate(Rule rule) { _operation.ForeignKey.OnUpdate = rule; return this; }
    /// <summary>Sets both actions.</summary>
    public void OnDeleteOrUpdate(Rule rule) { OnDelete(rule); OnUpdate(rule); }
    private void AddOnce() { if (!_added) { _add(_operation); _added = true; } }
}

/// <summary>Builds an index creation.</summary>
public sealed class CreateIndexExpression
{
    private readonly CreateIndexOperation _operation;
    private readonly Action<MigrationOperation> _add;
    private IndexColumnDefinition? _current;
    private bool _added;
    internal CreateIndexExpression(string? name, Action<MigrationOperation> add)
    {
        _operation = new CreateIndexOperation(name);
        _add = add;
    }
    /// <summary>Selects the indexed table.</summary>
    public CreateIndexExpression OnTable(string tableName)
    {
        _operation.TableName = ExpressionValidation.Name(tableName, nameof(tableName));
        return this;
    }
    /// <summary>Selects the table schema.</summary>
    public CreateIndexExpression InSchema(string schemaName)
    {
        _operation.SchemaName = ExpressionValidation.Name(schemaName, nameof(schemaName));
        return this;
    }
    /// <summary>Adds an indexed column.</summary>
    public CreateIndexExpression OnColumn(string columnName)
    {
        _current = _operation.AddColumn(columnName);
        AddOnce();
        return this;
    }
    /// <summary>Uses ascending order for the current column.</summary>
    public CreateIndexExpression Ascending() { Current().IsDescending = false; return this; }
    /// <summary>Uses descending order for the current column.</summary>
    public CreateIndexExpression Descending() { Current().IsDescending = true; return this; }
    /// <summary>Makes the index unique.</summary>
    public CreateIndexExpression Unique() { _operation.IsUnique = true; return this; }
    /// <summary>Requests a non-clustered index.</summary>
    public CreateIndexExpression NonClustered() { _operation.IsClustered = false; return this; }
    /// <summary>Requests a clustered index.</summary>
    public CreateIndexExpression Clustered() { _operation.IsClustered = true; return this; }
    /// <summary>Continues to index options.</summary>
    public CreateIndexExpression WithOptions() => this;
    private IndexColumnDefinition Current() => _current ?? throw new MigrationValidationException("OnColumn must be called first.");
    private void AddOnce() { if (!_added) { _add(_operation); _added = true; } }
}

/// <summary>Builds a sequence creation.</summary>
public sealed class CreateSequenceExpression
{
    private readonly CreateSequenceOperation _operation;
    internal CreateSequenceExpression(CreateSequenceOperation operation) => _operation = operation;
    /// <summary>Selects the sequence schema.</summary>
    public CreateSequenceExpression InSchema(string schemaName) { _operation.SchemaName = ExpressionValidation.Name(schemaName, nameof(schemaName)); return this; }
    /// <summary>Sets the increment.</summary>
    public CreateSequenceExpression IncrementBy(long increment) { if (increment == 0) throw new ArgumentOutOfRangeException(nameof(increment)); _operation.Increment = increment; return this; }
    /// <summary>Sets the minimum value.</summary>
    public CreateSequenceExpression MinValue(long value) { _operation.MinimumValue = value; return this; }
    /// <summary>Sets the maximum value.</summary>
    public CreateSequenceExpression MaxValue(long value) { _operation.MaximumValue = value; return this; }
    /// <summary>Sets the starting value.</summary>
    public CreateSequenceExpression StartWith(long value) { _operation.StartValue = value; return this; }
    /// <summary>Sets the cache size.</summary>
    public CreateSequenceExpression Cache(long value) { if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); _operation.CacheSize = value; return this; }
    /// <summary>Makes the sequence cycle.</summary>
    public CreateSequenceExpression Cycle() { _operation.IsCyclic = true; return this; }
}

/// <summary>Builds a primary-key or unique constraint.</summary>
public sealed class CreateConstraintExpression
{
    private readonly CreateConstraintOperation _operation;
    private readonly Action<MigrationOperation> _add;
    private bool _added;
    internal CreateConstraintExpression(MigrationConstraintType type, string? name, Action<MigrationOperation> add)
    {
        _operation = new CreateConstraintOperation(type, name);
        _add = add;
    }
    /// <summary>Selects the constrained table.</summary>
    public CreateConstraintExpression OnTable(string tableName) { _operation.TableName = ExpressionValidation.Name(tableName, nameof(tableName)); return this; }
    /// <summary>Selects the table schema.</summary>
    public CreateConstraintExpression WithSchema(string schemaName) { _operation.SchemaName = ExpressionValidation.Name(schemaName, nameof(schemaName)); return this; }
    /// <summary>Adds one constrained column.</summary>
    public CreateConstraintExpression Column(string columnName) => Columns(columnName);
    /// <summary>Adds constrained columns and completes the operation.</summary>
    public CreateConstraintExpression Columns(params string[] columnNames)
    {
        _operation.AddColumns(columnNames ?? throw new ArgumentNullException(nameof(columnNames)));
        if (!_added) { _add(_operation); _added = true; }
        return this;
    }
}

public sealed partial class DeleteExpressionRoot
{
    /// <summary>Drops a schema.</summary>
    public void Schema(string schemaName) => _add(new DeleteSchemaOperation(ExpressionValidation.Name(schemaName, nameof(schemaName))));
    /// <summary>Starts a foreign-key deletion using a generated name.</summary>
    public DeleteForeignKeyExpression ForeignKey() => new DeleteForeignKeyExpression(null, _add);
    /// <summary>Starts a named foreign-key deletion.</summary>
    public DeleteForeignKeyExpression ForeignKey(string foreignKeyName) => new DeleteForeignKeyExpression(ExpressionValidation.Name(foreignKeyName, nameof(foreignKeyName)), _add);
    /// <summary>Starts deleting data from a table.</summary>
    public DeleteDataExpression FromTable(string tableName)
    {
        var operation = new DeleteDataOperation(ExpressionValidation.Name(tableName, nameof(tableName)));
        _add(operation);
        return new DeleteDataExpression(operation);
    }
    /// <summary>Starts a named index deletion.</summary>
    public DeleteIndexExpression Index(string indexName) => new DeleteIndexExpression(ExpressionValidation.Name(indexName, nameof(indexName)), _add);
    /// <summary>Starts an index deletion using a generated name.</summary>
    public DeleteIndexExpression Index() => new DeleteIndexExpression(null, _add);
    /// <summary>Drops a sequence.</summary>
    public DeleteSequenceExpression Sequence(string sequenceName)
    {
        var operation = new DeleteSequenceOperation(ExpressionValidation.Name(sequenceName, nameof(sequenceName)));
        _add(operation);
        return new DeleteSequenceExpression(operation);
    }
    /// <summary>Starts deleting a named primary key.</summary>
    public DeleteConstraintExpression PrimaryKey(string primaryKeyName) => new DeleteConstraintExpression(MigrationConstraintType.PrimaryKey, ExpressionValidation.Name(primaryKeyName, nameof(primaryKeyName)), _add);
    /// <summary>Starts deleting a named unique constraint.</summary>
    public DeleteConstraintExpression UniqueConstraint(string constraintName) => new DeleteConstraintExpression(MigrationConstraintType.Unique, ExpressionValidation.Name(constraintName, nameof(constraintName)), _add);
    /// <summary>Starts deleting a unique constraint using a generated name.</summary>
    public DeleteConstraintExpression UniqueConstraint() => new DeleteConstraintExpression(MigrationConstraintType.Unique, null, _add);
    /// <summary>Starts deleting a column default.</summary>
    public DeleteDefaultConstraintExpression DefaultConstraint() => new DeleteDefaultConstraintExpression(_add);
}

/// <summary>Builds a multi-column deletion.</summary>
public sealed class DeleteColumnsFromExpression
{
    private readonly List<string> _columns;
    private readonly Action<MigrationOperation> _add;
    internal DeleteColumnsFromExpression(IEnumerable<string> columns, Action<MigrationOperation> add)
    {
        _columns = new List<string>(columns);
        _add = add;
    }
    /// <summary>Adds another column.</summary>
    public DeleteColumnsFromExpression Column(string columnName) { _columns.Add(ExpressionValidation.Name(columnName, nameof(columnName))); return this; }
    /// <summary>Selects the table containing the columns.</summary>
    public DeleteColumnsExpression FromTable(string tableName)
    {
        var operations = _columns.Select(column => new DeleteColumnOperation(ExpressionValidation.Name(tableName, nameof(tableName)), column)).ToList();
        foreach (var operation in operations) _add(operation);
        return new DeleteColumnsExpression(operations);
    }
}

/// <summary>Qualifies a multi-column deletion.</summary>
public sealed class DeleteColumnsExpression
{
    private readonly IReadOnlyList<DeleteColumnOperation> _operations;
    internal DeleteColumnsExpression(IReadOnlyList<DeleteColumnOperation> operations) => _operations = operations;
    /// <summary>Selects the table schema.</summary>
    public DeleteColumnsExpression InSchema(string schemaName)
    {
        var schema = ExpressionValidation.Name(schemaName, nameof(schemaName));
        foreach (var operation in _operations) operation.SchemaName = schema;
        return this;
    }
}

/// <summary>Builds a foreign-key deletion.</summary>
public sealed class DeleteForeignKeyExpression
{
    private readonly DeleteForeignKeyOperation _operation;
    private readonly Action<MigrationOperation> _add;
    private bool _added;
    internal DeleteForeignKeyExpression(string? name, Action<MigrationOperation> add) { _operation = new DeleteForeignKeyOperation(name); _add = add; }
    /// <summary>Selects the foreign table.</summary>
    public DeleteForeignKeyExpression FromTable(string table) { _operation.ForeignKey.ForeignTableName = ExpressionValidation.Name(table, nameof(table)); return this; }
    /// <summary>Selects a named foreign key's table and completes the operation.</summary>
    public DeleteForeignKeyExpression OnTable(string table) { FromTable(table); AddOnce(); return this; }
    /// <summary>Selects the most recently named table schema.</summary>
    public DeleteForeignKeyExpression InSchema(string schema) { var name = ExpressionValidation.Name(schema, nameof(schema)); if (string.IsNullOrEmpty(_operation.ForeignKey.PrimaryTableName)) _operation.ForeignKey.ForeignTableSchema = name; else _operation.ForeignKey.PrimaryTableSchema = name; return this; }
    /// <summary>Adds one foreign column.</summary>
    public DeleteForeignKeyExpression ForeignColumn(string column) => ForeignColumns(column);
    /// <summary>Adds foreign columns.</summary>
    public DeleteForeignKeyExpression ForeignColumns(params string[] columns) { _operation.ForeignKey.AddForeignColumns(columns ?? throw new ArgumentNullException(nameof(columns))); return this; }
    /// <summary>Selects the referenced table.</summary>
    public DeleteForeignKeyExpression ToTable(string table) { _operation.ForeignKey.PrimaryTableName = ExpressionValidation.Name(table, nameof(table)); return this; }
    /// <summary>Adds one referenced column and completes the operation.</summary>
    public void PrimaryColumn(string column) => PrimaryColumns(column);
    /// <summary>Adds referenced columns and completes the operation.</summary>
    public void PrimaryColumns(params string[] columns) { _operation.ForeignKey.AddPrimaryColumns(columns ?? throw new ArgumentNullException(nameof(columns))); AddOnce(); }
    private void AddOnce() { if (!_added) { _add(_operation); _added = true; } }
}

/// <summary>Builds an index deletion.</summary>
public sealed class DeleteIndexExpression
{
    private readonly DeleteIndexOperation _operation;
    private readonly Action<MigrationOperation> _add;
    private bool _added;
    internal DeleteIndexExpression(string? name, Action<MigrationOperation> add) { _operation = new DeleteIndexOperation(name); _add = add; }
    /// <summary>Selects the indexed table and completes a named-index deletion.</summary>
    public DeleteIndexExpression OnTable(string tableName) { _operation.TableName = ExpressionValidation.Name(tableName, nameof(tableName)); AddOnceIfNamed(); return this; }
    /// <summary>Selects the table schema.</summary>
    public DeleteIndexExpression InSchema(string schemaName) { _operation.SchemaName = ExpressionValidation.Name(schemaName, nameof(schemaName)); return this; }
    /// <summary>Adds one indexed column.</summary>
    public DeleteIndexExpression OnColumn(string columnName) => OnColumns(columnName);
    /// <summary>Adds indexed columns and completes the deletion.</summary>
    public DeleteIndexExpression OnColumns(params string[] columnNames) { _operation.AddColumns(columnNames ?? throw new ArgumentNullException(nameof(columnNames))); AddOnce(); return this; }
    /// <summary>Continues to provider index options.</summary>
    public DeleteIndexExpression WithOptions() { AddOnce(); return this; }
    private void AddOnceIfNamed() { if (_operation.IndexName != null) AddOnce(); }
    private void AddOnce() { if (!_added) { _add(_operation); _added = true; } }
}

/// <summary>Qualifies a sequence deletion.</summary>
public sealed class DeleteSequenceExpression
{
    private readonly DeleteSequenceOperation _operation;
    internal DeleteSequenceExpression(DeleteSequenceOperation operation) => _operation = operation;
    /// <summary>Selects the sequence schema.</summary>
    public void InSchema(string schemaName) => _operation.SchemaName = ExpressionValidation.Name(schemaName, nameof(schemaName));
}

/// <summary>Builds a constraint deletion.</summary>
public sealed class DeleteConstraintExpression
{
    private readonly DeleteConstraintOperation _operation;
    private readonly Action<MigrationOperation> _add;
    private bool _added;
    internal DeleteConstraintExpression(MigrationConstraintType type, string? name, Action<MigrationOperation> add) { _operation = new DeleteConstraintOperation(type, name); _add = add; }
    /// <summary>Selects the constrained table and completes a named deletion.</summary>
    public DeleteConstraintExpression FromTable(string tableName) { _operation.TableName = ExpressionValidation.Name(tableName, nameof(tableName)); if (_operation.ConstraintName != null) AddOnce(); return this; }
    /// <summary>Selects the table schema.</summary>
    public DeleteConstraintExpression InSchema(string schemaName) { _operation.SchemaName = ExpressionValidation.Name(schemaName, nameof(schemaName)); return this; }
    /// <summary>Adds one constrained column.</summary>
    public void Column(string columnName) => Columns(columnName);
    /// <summary>Adds constrained columns and completes the deletion.</summary>
    public void Columns(params string[] columnNames) { _operation.AddColumns(columnNames ?? throw new ArgumentNullException(nameof(columnNames))); AddOnce(); }
    private void AddOnce() { if (!_added) { _add(_operation); _added = true; } }
}

/// <summary>Builds a default-constraint deletion.</summary>
public sealed class DeleteDefaultConstraintExpression
{
    private readonly Action<MigrationOperation> _add;
    private string? _tableName;
    private string? _schemaName;
    internal DeleteDefaultConstraintExpression(Action<MigrationOperation> add) => _add = add;
    /// <summary>Selects the table.</summary>
    public DeleteDefaultConstraintExpression OnTable(string tableName) { _tableName = ExpressionValidation.Name(tableName, nameof(tableName)); return this; }
    /// <summary>Selects the table schema.</summary>
    public DeleteDefaultConstraintExpression InSchema(string schemaName) { _schemaName = ExpressionValidation.Name(schemaName, nameof(schemaName)); return this; }
    /// <summary>Selects the column and completes the operation.</summary>
    public void OnColumn(string columnName)
    {
        if (_tableName is null) throw new MigrationValidationException("OnTable must be called before OnColumn.");
        _add(new DeleteDefaultConstraintOperation(_tableName, ExpressionValidation.Name(columnName, nameof(columnName))) { SchemaName = _schemaName });
    }
}

/// <summary>Builds a data deletion.</summary>
public sealed class DeleteDataExpression
{
    private readonly DeleteDataOperation _operation;
    internal DeleteDataExpression(DeleteDataOperation operation) => _operation = operation;
    /// <summary>Selects the table schema.</summary>
    public DeleteDataExpression InSchema(string schemaName) { _operation.SchemaName = ExpressionValidation.Name(schemaName, nameof(schemaName)); return this; }
    /// <summary>Adds equality criteria from an anonymous object.</summary>
    public DeleteDataExpression Row<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T values) { _operation.AddCriteria(MigrationDataRowFactory.FromObject(values)); return this; }
    /// <summary>Adds equality criteria from a dictionary.</summary>
    public DeleteDataExpression Row(IDictionary<string, object?> values) { _operation.AddCriteria(MigrationDataRowFactory.FromDictionary(values)); return this; }
    /// <summary>Adds equality criteria.</summary>
    public DeleteDataExpression Where<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T values) => Row(values);
    /// <summary>Deletes rows where one column is null.</summary>
    public void IsNull(string columnName) => Row(new Dictionary<string, object?> { [ExpressionValidation.Name(columnName, nameof(columnName))] = null });
    /// <summary>Deletes every row.</summary>
    public void AllRows() => _operation.AllRows = true;
}

/// <summary>Starts row insertion expressions.</summary>
public sealed class InsertExpressionRoot
{
    private readonly Action<MigrationOperation> _add;
    internal InsertExpressionRoot(Action<MigrationOperation> add) => _add = add;
    /// <summary>Selects the destination table.</summary>
    public InsertDataExpression IntoTable(string tableName)
    {
        var operation = new InsertDataOperation(ExpressionValidation.Name(tableName, nameof(tableName)));
        _add(operation);
        return new InsertDataExpression(operation);
    }
}

/// <summary>Builds row insertion.</summary>
public sealed class InsertDataExpression
{
    private readonly InsertDataOperation _operation;
    internal InsertDataExpression(InsertDataOperation operation) => _operation = operation;
    /// <summary>Selects the table schema.</summary>
    public InsertDataExpression InSchema(string schemaName) { _operation.SchemaName = ExpressionValidation.Name(schemaName, nameof(schemaName)); return this; }
    /// <summary>Adds one row from an anonymous object.</summary>
    public InsertDataExpression Row<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T values) { _operation.AddRow(MigrationDataRowFactory.FromObject(values)); return this; }
    /// <summary>Adds one row from a dictionary.</summary>
    public InsertDataExpression Row(IDictionary<string, object?> values) { _operation.AddRow(MigrationDataRowFactory.FromDictionary(values)); return this; }
    /// <summary>Adds rows from anonymous objects.</summary>
    public InsertDataExpression Rows<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(params T[] rows)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        foreach (var row in rows) Row(row);
        return this;
    }
    /// <summary>Adds rows whose anonymous-object shapes differ.</summary>
    [RequiresUnreferencedCode("Runtime row types must preserve their public properties. Use the generic Rows overload for trimming and Native AOT.")]
    public InsertDataExpression Rows(params object[] rows)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        foreach (var row in rows) _operation.AddRow(MigrationDataRowFactory.FromRuntimeObject(row));
        return this;
    }
    /// <summary>Adds rows from dictionaries.</summary>
    public InsertDataExpression Rows(params IDictionary<string, object?>[] rows)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        foreach (var row in rows) Row(row);
        return this;
    }
}

/// <summary>Starts row update expressions.</summary>
public sealed class UpdateExpressionRoot
{
    private readonly Action<MigrationOperation> _add;
    internal UpdateExpressionRoot(Action<MigrationOperation> add) => _add = add;
    /// <summary>Selects the table to update.</summary>
    public UpdateDataExpression Table(string tableName)
    {
        var operation = new UpdateDataOperation(ExpressionValidation.Name(tableName, nameof(tableName)));
        _add(operation);
        return new UpdateDataExpression(operation);
    }
}

/// <summary>Builds a row update.</summary>
public sealed class UpdateDataExpression
{
    private readonly UpdateDataOperation _operation;
    internal UpdateDataExpression(UpdateDataOperation operation) => _operation = operation;
    /// <summary>Selects the table schema.</summary>
    public UpdateDataExpression InSchema(string schemaName) { _operation.SchemaName = ExpressionValidation.Name(schemaName, nameof(schemaName)); return this; }
    /// <summary>Sets values from an anonymous object.</summary>
    public UpdateDataExpression Set<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T values) { _operation.Values = MigrationDataRowFactory.FromObject(values); return this; }
    /// <summary>Sets values from a dictionary.</summary>
    public UpdateDataExpression Set(IDictionary<string, object?> values) { _operation.Values = MigrationDataRowFactory.FromDictionary(values); return this; }
    /// <summary>Selects rows by equality criteria from an anonymous object.</summary>
    public void Where<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T values) { _operation.Criteria = MigrationDataRowFactory.FromObject(values); }
    /// <summary>Selects rows by equality criteria from a dictionary.</summary>
    public void Where(IDictionary<string, object?> values) { _operation.Criteria = MigrationDataRowFactory.FromDictionary(values); }
    /// <summary>Selects every row.</summary>
    public void AllRows() => _operation.AllRows = true;
}

public sealed partial class ExecuteExpressionRoot
{
    /// <summary>Executes SQL after replacing <c>$(name)</c> tokens.</summary>
    public void Sql(string sql, IDictionary<string, object?> parameters) =>
        Sql(ReplaceTokens(sql, parameters));
    /// <summary>Executes SQL after replacing string-valued <c>$(name)</c> tokens.</summary>
    public void Sql(string sql, IDictionary<string, string> parameters) =>
        Sql(ReplaceTokens(sql, ConvertParameters(parameters)));
    /// <summary>Executes SQL and records a description for dry-run output.</summary>
    public void Sql(string sql, string description)
    {
        ExpressionValidation.Sql(description);
        Sql(sql);
    }
    /// <summary>Executes described SQL after replacing <c>$(name)</c> tokens.</summary>
    public void Sql(string sql, string description, IDictionary<string, object?> parameters)
    {
        ExpressionValidation.Sql(description);
        Sql(sql, parameters);
    }
    /// <summary>Executes described SQL after replacing string-valued <c>$(name)</c> tokens.</summary>
    public void Sql(string sql, string description, IDictionary<string, string> parameters)
    {
        ExpressionValidation.Sql(description);
        Sql(sql, parameters);
    }
    /// <summary>Executes a SQL file.</summary>
    public void Script(string pathToSqlScript) => Script(pathToSqlScript, new Dictionary<string, object?>());
    /// <summary>Executes a SQL file after replacing <c>$(name)</c> tokens.</summary>
    public void Script(string pathToSqlScript, IDictionary<string, object?> parameters) =>
        _add(new ExecuteScriptOperation(
            ExpressionValidation.Name(pathToSqlScript, nameof(pathToSqlScript)),
            false,
            _migrationType(),
            CopyParameters(parameters)));
    /// <summary>Executes a SQL file after replacing string-valued <c>$(name)</c> tokens.</summary>
    public void Script(string pathToSqlScript, IDictionary<string, string> parameters) =>
        Script(pathToSqlScript, ConvertParameters(parameters));
    /// <summary>Executes an embedded SQL resource.</summary>
    public void EmbeddedScript(string embeddedSqlScriptName) => EmbeddedScript(embeddedSqlScriptName, new Dictionary<string, object?>());
    /// <summary>Executes an embedded SQL resource after replacing <c>$(name)</c> tokens.</summary>
    public void EmbeddedScript(string embeddedSqlScriptName, IDictionary<string, object?> parameters) =>
        _add(new ExecuteScriptOperation(
            ExpressionValidation.Name(embeddedSqlScriptName, nameof(embeddedSqlScriptName)),
            true,
            _migrationType(),
            CopyParameters(parameters)));
    /// <summary>Executes an embedded resource after replacing string-valued <c>$(name)</c> tokens.</summary>
    public void EmbeddedScript(string embeddedSqlScriptName, IDictionary<string, string> parameters) =>
        EmbeddedScript(embeddedSqlScriptName, ConvertParameters(parameters));
    /// <summary>Executes code against the current connection and transaction.</summary>
    public void WithConnection(Action<IDbConnection, IDbTransaction> operation) => WithConnection(operation, null);
    /// <summary>Executes described code against the current connection and transaction.</summary>
    public void WithConnection(Action<IDbConnection, IDbTransaction> operation, string? description) =>
        _add(new ExecuteWithConnectionOperation(operation ?? throw new ArgumentNullException(nameof(operation)), description));

    private static IReadOnlyDictionary<string, object?> CopyParameters(IDictionary<string, object?> parameters)
    {
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));
        return new Dictionary<string, object?>(parameters, StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> ConvertParameters(IDictionary<string, string> parameters)
    {
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));
        return parameters.ToDictionary(item => item.Key, item => (object?)item.Value, StringComparer.Ordinal);
    }

    private static string ReplaceTokens(string sql, IDictionary<string, object?> parameters)
    {
        var result = ExpressionValidation.Sql(sql);
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));
        foreach (var parameter in parameters)
        {
            result = result.Replace(
                "$(" + parameter.Key + ")",
                Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
        }
        return result;
    }
}

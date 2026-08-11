using System;
using System.Collections.Generic;

namespace CobaltumOrm.Migrations;

/// <summary>Associates a positive, monotonically increasing version with a migration class.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MigrationAttribute : Attribute
{
    /// <summary>Initializes a migration using a description derived from its class name.</summary>
    /// <param name="version">A positive version unique within the discovered migration set.</param>
    public MigrationAttribute(long version)
        : this(version, null)
    {
    }

    /// <summary>Initializes a migration with an explicit history description.</summary>
    /// <param name="version">A positive version unique within the discovered migration set.</param>
    /// <param name="description">A readable description stored in migration history.</param>
    public MigrationAttribute(long version, string? description)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Migration versions must be positive.");
        }

        Version = version;
        Description = description;
    }

    /// <summary>Gets the migration version.</summary>
    public long Version { get; }

    /// <summary>Gets the optional explicit history description.</summary>
    public string? Description { get; }
}

/// <summary>
/// Base class for migrations. Override <see cref="Up"/> and <see cref="Down"/>
/// and use the protected expression roots to describe ordered operations.
/// </summary>
public abstract class Migration
{
    private readonly List<MigrationOperation> _operations = new List<MigrationOperation>();
    private bool _collecting;
    private ConditionalFilter? _activeCondition;

    /// <summary>Initializes the FluentMigrator-style expression roots.</summary>
    protected Migration()
    {
        Create = new CreateExpressionRoot(AddOperation);
        Alter = new AlterExpressionRoot(AddOperation);
        Delete = new DeleteExpressionRoot(AddOperation);
        Rename = new RenameExpressionRoot(AddOperation);
        Execute = new ExecuteExpressionRoot(AddOperation, GetType);
        Insert = new InsertExpressionRoot(AddOperation);
        Update = new UpdateExpressionRoot(AddOperation);
    }

    /// <summary>Gets the root for <c>Create.Table(...)</c> expressions.</summary>
    protected CreateExpressionRoot Create { get; }

    /// <summary>Gets the root for <c>Alter.Table(...)</c> expressions.</summary>
    protected AlterExpressionRoot Alter { get; }

    /// <summary>Gets the root for <c>Delete.Table(...)</c> and <c>Delete.Column(...)</c>.</summary>
    protected DeleteExpressionRoot Delete { get; }

    /// <summary>Gets the root for table and column rename expressions.</summary>
    protected RenameExpressionRoot Rename { get; }

    /// <summary>Gets the root for <c>Execute.Sql(...)</c> expressions.</summary>
    protected ExecuteExpressionRoot Execute { get; }

    /// <summary>Gets the root for inserting migration data.</summary>
    protected InsertExpressionRoot Insert { get; }

    /// <summary>Gets the root for updating migration data.</summary>
    protected UpdateExpressionRoot Update { get; }

    /// <summary>Creates migration roots that apply only to named database providers.</summary>
    protected IfDatabaseExpressionRoot IfDatabase(params string[] databaseTypes)
    {
        if (databaseTypes is null) throw new ArgumentNullException(nameof(databaseTypes));
        if (databaseTypes.Length == 0) throw new ArgumentException("At least one database type is required.", nameof(databaseTypes));
        var names = new string[databaseTypes.Length];
        for (var index = 0; index < databaseTypes.Length; index++)
            names[index] = ExpressionValidation.Name(databaseTypes[index], nameof(databaseTypes));
        return CreateConditionalRoot(new ConditionalFilter(names, null));
    }

    /// <summary>Creates migration roots selected by a database-provider predicate.</summary>
    protected IfDatabaseExpressionRoot IfDatabase(Predicate<string> databaseTypePredicate)
    {
        if (databaseTypePredicate is null) throw new ArgumentNullException(nameof(databaseTypePredicate));
        return CreateConditionalRoot(new ConditionalFilter(Array.Empty<string>(), databaseTypePredicate));
    }

    /// <summary>Declares operations that apply the migration.</summary>
    public abstract void Up();

    /// <summary>Declares operations that reverse the migration.</summary>
    public abstract void Down();

    internal IReadOnlyList<MigrationOperation> CollectOperations(bool up)
    {
        if (_collecting)
        {
            throw new InvalidOperationException("Migration operation collection cannot be re-entered.");
        }

        _operations.Clear();
        _collecting = true;
        try
        {
            if (up)
            {
                Up();
            }
            else
            {
                Down();
            }

            return _operations.ToArray();
        }
        finally
        {
            _collecting = false;
        }
    }

    private void AddOperation(MigrationOperation operation)
    {
        if (!_collecting)
        {
            throw new InvalidOperationException("Migration expressions may only be used from Up or Down.");
        }

        _operations.Add(_activeCondition is null
            ? operation
            : new ConditionalMigrationOperation(operation, _activeCondition.DatabaseTypes, _activeCondition.Predicate));
    }

    private IfDatabaseExpressionRoot CreateConditionalRoot(ConditionalFilter filter) =>
        new IfDatabaseExpressionRoot(
            operation => AddConditionalOperation(operation, filter),
            GetType,
            delegation => ExecuteConditional(filter, delegation));

    private void AddConditionalOperation(MigrationOperation operation, ConditionalFilter filter)
    {
        if (!_collecting)
            throw new InvalidOperationException("Migration expressions may only be used from Up or Down.");
        _operations.Add(new ConditionalMigrationOperation(
            operation, filter.DatabaseTypes, filter.Predicate));
    }

    private void ExecuteConditional(ConditionalFilter filter, Action delegation)
    {
        if (!_collecting)
            throw new InvalidOperationException("Migration expressions may only be used from Up or Down.");
        if (_activeCondition != null)
            throw new InvalidOperationException("Nested IfDatabase delegates are not supported.");
        _activeCondition = filter;
        try { delegation(); }
        finally { _activeCondition = null; }
    }

    private sealed class ConditionalFilter
    {
        internal ConditionalFilter(string[] databaseTypes, Predicate<string>? predicate)
        {
            DatabaseTypes = databaseTypes;
            Predicate = predicate;
        }
        internal string[] DatabaseTypes { get; }
        internal Predicate<string>? Predicate { get; }
    }
}

/// <summary>
/// Base class for migrations, such as imported Flyway scripts, that can only move
/// a database forward. The runner rejects a rollback containing one of these
/// migrations before it starts any rollback transaction.
/// </summary>
public abstract class ForwardOnlyMigration : Migration
{
    /// <summary>
    /// This member is sealed because a forward-only migration has no reverse operation.
    /// Callers should use <see cref="MigrationRunner"/>, which validates rollback plans first.
    /// </summary>
    public sealed override void Down()
    {
        throw new MigrationValidationException(
            $"Migration '{GetType().FullName}' is forward-only and cannot be rolled back.");
    }
}

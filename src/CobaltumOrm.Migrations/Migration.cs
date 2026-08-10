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

    /// <summary>Initializes the FluentMigrator-style expression roots.</summary>
    protected Migration()
    {
        Create = new CreateExpressionRoot(AddOperation);
        Alter = new AlterExpressionRoot(AddOperation);
        Delete = new DeleteExpressionRoot(AddOperation);
        Rename = new RenameExpressionRoot(AddOperation);
        Execute = new ExecuteExpressionRoot(AddOperation);
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

        _operations.Add(operation);
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

using System;
using System.Collections.Generic;

namespace CobaltumOrm.Analysis;

/// <summary>Provides the MySQL 8 implementation of the compile-time dialect services.</summary>
public sealed class MySqlDatabaseDialect : IDatabaseDialect
{
    public MySqlDatabaseDialect()
    {
        QueryAnalyzer = new MySqlQueryAnalyzer();
        SchemaMigrationAnalyzer = new MySqlSchemaMigrationAnalyzer();
        ScriptClassifier = new MySqlScriptClassifierService();
        IdentifierQuoter = new MySqlIdentifierQuoter();
        TypeMapper = new MySqlTypeMapper();
        MigrationSqlWriter = new MySqlMigrationSqlWriter();
        SchemaRules = new MySqlSchemaRules();
    }

    public DatabaseProvider Provider => DatabaseProvider.MySql;
    public string Name => "MySql";
    public IQueryAnalyzer QueryAnalyzer { get; }
    public ISchemaMigrationAnalyzer SchemaMigrationAnalyzer { get; }
    public ISqlScriptClassifier ScriptClassifier { get; }
    public ISqlIdentifierQuoter IdentifierQuoter { get; }
    public ISqlTypeMapper TypeMapper { get; }
    public ISqlMigrationWriter MigrationSqlWriter { get; }
    public ISchemaRules SchemaRules { get; }
}

/// <summary>Quotes MySQL identifiers with backticks.</summary>
public sealed class MySqlIdentifierQuoter : ISqlIdentifierQuoter
{
    public string QuoteIdentifier(string identifier)
    {
        if (identifier is null)
        {
            throw new ArgumentNullException(nameof(identifier));
        }

        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("A MySQL identifier is required.", nameof(identifier));
        }

        if (identifier.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A MySQL identifier cannot contain a null character.", nameof(identifier));
        }

        return "`" + identifier.Replace("`", "``") + "`";
    }

    public string QuoteQualifiedName(string? schema, string name)
    {
        return string.IsNullOrEmpty(schema)
            ? QuoteIdentifier(name)
            : QuoteIdentifier(schema!) + "." + QuoteIdentifier(name);
    }
}

/// <summary>Applies MySQL database and identifier comparison rules used by analysis.</summary>
public sealed class MySqlSchemaRules : ISchemaRules
{
    public bool SupportsSchemas => true;

    // MySQL calls a schema a database. The selected database is connection-specific.
    public string? DefaultSchema => null;

    public bool IsDefaultSchema(string? schema) => string.IsNullOrEmpty(schema);

    public string NormalizeUnquotedIdentifier(string identifier) =>
        (identifier ?? throw new ArgumentNullException(nameof(identifier))).ToLowerInvariant();

    public string NormalizeQuotedIdentifier(string identifier) =>
        identifier ?? throw new ArgumentNullException(nameof(identifier));

    public bool AreIdentifiersEqual(string reference, bool referenceIsQuoted, string declared)
    {
        if (reference is null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

        if (declared is null)
        {
            throw new ArgumentNullException(nameof(declared));
        }

        return referenceIsQuoted
            ? string.Equals(reference, declared, StringComparison.Ordinal)
            : string.Equals(reference, declared, StringComparison.OrdinalIgnoreCase);
    }
}

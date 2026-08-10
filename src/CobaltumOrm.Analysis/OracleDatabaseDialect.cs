using System;

namespace CobaltumOrm.Analysis;

/// <summary>Provides the Oracle 19c and later compile-time dialect services.</summary>
public sealed class OracleDatabaseDialect : IDatabaseDialect
{
    public OracleDatabaseDialect()
    {
        var typeMapper = new OracleTypeMapper();
        QueryAnalyzer = new OracleQueryAnalyzer(typeMapper);
        SchemaMigrationAnalyzer = new OracleSchemaMigrationAnalyzer();
        ScriptClassifier = new OracleScriptClassifierService();
        IdentifierQuoter = new OracleIdentifierQuoter();
        TypeMapper = typeMapper;
        MigrationSqlWriter = new OracleMigrationSqlWriter(typeMapper);
        SchemaRules = new OracleSchemaRules();
    }

    public DatabaseProvider Provider => DatabaseProvider.Oracle;
    public string Name => "Oracle";
    public IQueryAnalyzer QueryAnalyzer { get; }
    public ISchemaMigrationAnalyzer SchemaMigrationAnalyzer { get; }
    public ISqlScriptClassifier ScriptClassifier { get; }
    public ISqlIdentifierQuoter IdentifierQuoter { get; }
    public ISqlTypeMapper TypeMapper { get; }
    public ISqlMigrationWriter MigrationSqlWriter { get; }
    public ISchemaRules SchemaRules { get; }
}

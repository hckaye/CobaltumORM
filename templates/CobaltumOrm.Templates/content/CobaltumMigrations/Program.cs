using System.Data.Common;
using CobaltumOrm.Migrations;
#if (provider == "PostgreSql")
using CobaltumOrm.Migrations.PostgreSql;
using Npgsql;
#elseif (provider == "MySql")
using CobaltumOrm.Migrations.MySql;
using MySqlConnector;
#elseif (provider == "Sqlite")
using CobaltumOrm.Migrations.Sqlite;
using Microsoft.Data.Sqlite;
#elseif (provider == "SqlServer")
using CobaltumOrm.Migrations.SqlServer;
using Microsoft.Data.SqlClient;
#elseif (provider == "Oracle")
using CobaltumOrm.Migrations.Oracle;
using Oracle.ManagedDataAccess.Client;
#endif

namespace CobaltumMigrations;

public static class Program
{
    public static Task<int> Main(string[] args) =>
        MigrationProjectHost.RunAsync<DatabaseMigrationProject>(
            args,
            global::CobaltumMigrations.Generated.CobaltumMigrationCatalog.All);
}

public sealed class DatabaseMigrationProject : MigrationProject
{
#if (provider == "PostgreSql")
    public override DbConnection CreateConnection(MigrationProjectContext context) =>
        new NpgsqlConnection(context.ConnectionString);

    public override IMigrationDatabaseAdapter CreateAdapter() =>
        new PostgreSqlMigrationAdapter();
#elseif (provider == "MySql")
    public override DbConnection CreateConnection(MigrationProjectContext context) =>
        new MySqlConnection(context.ConnectionString);

    public override IMigrationDatabaseAdapter CreateAdapter() =>
        new MySqlMigrationAdapter();
#elseif (provider == "Sqlite")
    public override DbConnection CreateConnection(MigrationProjectContext context) =>
        new SqliteConnection(context.ConnectionString);

    public override IMigrationDatabaseAdapter CreateAdapter() =>
        new SqliteMigrationAdapter();
#elseif (provider == "SqlServer")
    public override DbConnection CreateConnection(MigrationProjectContext context) =>
        new SqlConnection(context.ConnectionString);

    public override IMigrationDatabaseAdapter CreateAdapter() =>
        new SqlServerMigrationAdapter();
#elseif (provider == "Oracle")
    public override DbConnection CreateConnection(MigrationProjectContext context) =>
        new OracleConnection(context.ConnectionString);

    public override IMigrationDatabaseAdapter CreateAdapter() =>
        new OracleMigrationAdapter();
#endif
}

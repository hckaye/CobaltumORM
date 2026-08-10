using System;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.MySql;
using MySqlConnector;
using Xunit;

namespace CobaltumOrm.MySql.Tests;

public sealed class MySqlConnectorParameterTests
{
    [Fact]
    public void GeneratedHistoryParametersCanBeBoundByMySqlConnectorWithoutOpeningAConnection()
    {
        var adapter = new MySqlMigrationAdapter();
        var commandDefinition = adapter.CreateInsertHistoryCommand(
            "meta",
            "history",
            12,
            "Create accounts",
            new DateTimeOffset(2026, 8, 10, 1, 2, 3, TimeSpan.FromHours(9)));

        using var connection = new MySqlConnection();
        using var command = connection.CreateCommand();
        command.CommandText = commandDefinition.CommandText;
        foreach (var definition in commandDefinition.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = definition.Name;
            parameter.Value = definition.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        Assert.Equal("version", command.Parameters[0].ParameterName);
        Assert.Equal(12L, command.Parameters[0].Value);
        Assert.Equal("Create accounts", command.Parameters[1].Value);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(command.Parameters[2].Value).Offset);
        Assert.Equal(3, command.Parameters.Count);
    }
}

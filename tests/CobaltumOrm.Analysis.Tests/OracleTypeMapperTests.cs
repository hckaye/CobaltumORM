using System;
using CobaltumOrm.Analysis;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class OracleTypeMapperTests
{
    [Theory]
    [InlineData("NUMBER(1,0)", SqlValueKind.Bool)]
    [InlineData("NUMBER(5,0)", SqlValueKind.Int16)]
    [InlineData("NUMBER(10)", SqlValueKind.Int32)]
    [InlineData("NUMBER(19,0)", SqlValueKind.Int64)]
    [InlineData("NUMBER(18,4)", SqlValueKind.Decimal)]
    [InlineData("NUMBER", SqlValueKind.Decimal)]
    [InlineData("BINARY_FLOAT", SqlValueKind.Float)]
    [InlineData("BINARY_DOUBLE", SqlValueKind.Double)]
    [InlineData("VARCHAR2(50 CHAR)", SqlValueKind.String)]
    [InlineData("NVARCHAR2(50)", SqlValueKind.String)]
    [InlineData("CHAR(1)", SqlValueKind.String)]
    [InlineData("CLOB", SqlValueKind.String)]
    [InlineData("NCLOB", SqlValueKind.String)]
    [InlineData("DATE", SqlValueKind.DateTime)]
    [InlineData("TIMESTAMP(6)", SqlValueKind.DateTime)]
    [InlineData("TIMESTAMP WITH TIME ZONE", SqlValueKind.DateTimeOffset)]
    [InlineData("TIMESTAMP(3) WITH LOCAL TIME ZONE", SqlValueKind.DateTime)]
    [InlineData("RAW(16)", SqlValueKind.Guid)]
    [InlineData("RAW(15)", SqlValueKind.Bytes)]
    [InlineData("RAW(32)", SqlValueKind.Bytes)]
    [InlineData("LONG RAW", SqlValueKind.Bytes)]
    [InlineData("BLOB", SqlValueKind.Bytes)]
    [InlineData("JSON", SqlValueKind.Json)]
    public void MapsOracleStorageTypes(string sqlType, SqlValueKind expected)
    {
        var mapper = new OracleTypeMapper();

        Assert.True(mapper.TryMap(sqlType, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("NUMBER(0)")]
    [InlineData("NUMBER(39,0)")]
    [InlineData("NUMBER(10,128)")]
    [InlineData("VARCHAR2(0)")]
    [InlineData("RAW(2001)")]
    [InlineData("TIMESTAMP(10)")]
    [InlineData("INTERVAL DAY TO SECOND")]
    [InlineData("INTERVAL HOUR TO MINUTE")]
    [InlineData("INTERVAL YEAR TO MONTH")]
    [InlineData("BINARY_FLOAT(2)")]
    [InlineData("JSONB")]
    [InlineData("BOOLEAN")]
    public void RejectsTypesThatCannotBeRepresentedByTheCommonModel(string sqlType)
    {
        var mapper = new OracleTypeMapper();

        Assert.False(mapper.TryMap(sqlType, out _));
    }

    [Fact]
    public void MapsMigrationLogicalTypesLikeTheOracleRuntimeAdapter()
    {
        var mapper = new OracleTypeMapper();

        Assert.Equal("NUMBER(5,0)", mapper.MapMigrationType("int16"));
        Assert.Equal("NUMBER(10,0)", mapper.MapMigrationType("int32"));
        Assert.Equal("NUMBER(19,0)", mapper.MapMigrationType("int64"));
        Assert.Equal("NUMBER(1,0)", mapper.MapMigrationType("boolean"));
        Assert.Equal("BINARY_FLOAT", mapper.MapMigrationType("float"));
        Assert.Equal("BINARY_DOUBLE", mapper.MapMigrationType("double"));
        Assert.Equal("VARCHAR2(80)", mapper.MapMigrationType("string", length: 80));
        Assert.Equal("CLOB", mapper.MapMigrationType("string"));
        Assert.Equal("CLOB", mapper.MapMigrationType("text"));
        Assert.Equal("DATE", mapper.MapMigrationType("date"));
        Assert.Equal("TIMESTAMP", mapper.MapMigrationType("datetime"));
        Assert.Equal("TIMESTAMP WITH TIME ZONE", mapper.MapMigrationType("datetimeoffset"));
        Assert.Equal("TIMESTAMP", mapper.MapMigrationType("time"));
        Assert.Equal("RAW(16)", mapper.MapMigrationType("guid"));
        Assert.Equal("BLOB", mapper.MapMigrationType("binary"));
        Assert.Equal("CLOB", mapper.MapMigrationType("json"));
        Assert.Equal("BLOB", mapper.MapMigrationType("jsonb"));
        Assert.Equal("NUMBER(18,4)", mapper.MapMigrationType("decimal", precision: 18, scale: 4));
        Assert.Equal("CLOB", mapper.ToDatabaseTypeName(SqlValueKind.Json));
        Assert.Equal("BLOB", mapper.ToDatabaseTypeName(SqlValueKind.JsonBinary));
        Assert.Throws<ArgumentException>(() => mapper.MapMigrationType("not_a_type"));
    }
}

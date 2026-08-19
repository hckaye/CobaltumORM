using System;
using Xunit;

namespace CobaltumOrm.Tests;

public sealed class ColumnIdentifierSecurityTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\"\"")]
    [InlineData("``")]
    [InlineData("[]")]
    [InlineData("1id")]
    [InlineData("id.name")]
    [InlineData("id; DROP TABLE users; --")]
    [InlineData("\"id\"; DROP TABLE users; --")]
    [InlineData("`id`; DROP TABLE users; --")]
    [InlineData("[id]; DROP TABLE users; --")]
    [InlineData("\"unterminated")]
    [InlineData("`unterminated")]
    [InlineData("[unterminated")]
    public void RejectsSqlFragmentsAsColumnNames(string name)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new CobaltumColumn<TestRecord, int>(name));

        Assert.Equal("quotedName", exception.ParamName);
    }

    [Fact]
    public void RejectsNullColumnNames()
    {
        Assert.Throws<ArgumentNullException>(
            () => new CobaltumColumn<TestRecord, int>(null!));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("_id2")]
    [InlineData("識別子")]
    [InlineData("\"id\"")]
    [InlineData("`id`")]
    [InlineData("[id]")]
    [InlineData("\"id\"\"; DROP TABLE users; --\"")]
    [InlineData("`id``; DROP TABLE users; --`")]
    [InlineData("[id]]; DROP TABLE users; --]")]
    public void AcceptsOneUnquotedOrProviderQuotedIdentifier(string name)
    {
        var table = new TestTable(name);

        var sql = table.Where(table.Column.Equal(1)).Sql;

        Assert.Equal("SELECT 1 FROM test_rows WHERE " + name + " = @__cobaltum_where_0", sql);
    }

    [Fact]
    public void SupportsOracleParameterPrefixes()
    {
        var column = new CobaltumColumn<TestRecord, int>("\"ID\"", System.Data.DbType.Int32, ':');
        var table = new TestTable(column);

        var sql = table.Query().Where(column.Equal(1)).Where(column.Equal(2)).Sql;

        Assert.Equal(
            "SELECT 1 FROM test_rows WHERE \"ID\" = :__cobaltum_where_0 AND \"ID\" = :__cobaltum_where_1",
            sql);
    }

    private sealed record TestRecord(int Id);

    private sealed class TestTable : CobaltumTable<TestRecord>
    {
        internal TestTable(string columnName)
            : this(new CobaltumColumn<TestRecord, int>(columnName))
        {
        }

        internal TestTable(CobaltumColumn<TestRecord, int> column)
            : base("SELECT 1 FROM test_rows", static _ => new TestRecord(1))
        {
            Column = column;
        }

        internal CobaltumColumn<TestRecord, int> Column { get; }
    }
}

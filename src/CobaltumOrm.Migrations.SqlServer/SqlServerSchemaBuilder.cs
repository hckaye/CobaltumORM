using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CobaltumOrm.Migrations;

namespace CobaltumOrm.Migrations.SqlServer;

internal static class SqlServerSchemaBuilder
{
    internal static SqlServerSchemaState CreateEmpty() => new SqlServerSchemaState();

    internal static SqlServerSchemaState ApplyScript(
        SqlServerSchemaState schema,
        MigrationCommand command)
    {
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in command.Parameters)
        {
            var name = SqlServerToken.NormalizeParameterName(parameter.Name);
            if (parameters.ContainsKey(name))
            {
                throw new MigrationValidationException(
                    $"The schema preview command contains duplicate parameter '{parameter.Name}'.");
            }

            parameters.Add(name, parameter.Value);
        }

        var tokens = new SqlServerDdlLexer(command.CommandText).Lex();
        var parser = new SqlServerSchemaParser(command.CommandText, tokens, parameters, schema);
        parser.Apply();
        return schema;
    }

    internal static MigrationSchema ToMigrationSchema(SqlServerSchemaState schema)
    {
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        return new MigrationSchema(schema.Tables.Select(table =>
            new MigrationSchemaTable(
                table.Schema,
                table.Name,
                table.Columns.Select(column =>
                    new MigrationSchemaColumn(
                        column.Name,
                        column.SqlType,
                        column.IsNullable,
                        column.IsPrimaryKey,
                        column.DefaultExpression,
                        column.IsIdentity)))));
    }
}

internal sealed class SqlServerSchemaState
{
    internal List<SqlServerTableState> Tables { get; } = new List<SqlServerTableState>();

    internal SqlServerTableState FindTable(string schema, string name)
    {
        var table = Tables.FirstOrDefault(item =>
            string.Equals(item.Schema, schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        return table ?? throw new MigrationValidationException(
            $"The schema preview refers to table '{schema}.{name}', but that table is not present.");
    }

    internal bool ContainsTable(string schema, string name)
    {
        return Tables.Any(item =>
            string.Equals(item.Schema, schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    internal SqlServerTableState? FindTableOrNull(string schema, string name)
    {
        return Tables.FirstOrDefault(item =>
            string.Equals(item.Schema, schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class SqlServerTableState
{
    internal SqlServerTableState(string schema, string name)
    {
        Schema = schema;
        Name = name;
    }

    internal string Schema { get; }
    internal string Name { get; set; }
    internal List<SqlServerColumnState> Columns { get; } = new List<SqlServerColumnState>();

    internal SqlServerColumnState FindColumn(string name)
    {
        var column = Columns.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        return column ?? throw new MigrationValidationException(
            $"The schema preview refers to column '{name}' on table '{Schema}.{Name}', but that column is not present.");
    }

    internal bool ContainsColumn(string name)
    {
        return Columns.Any(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class SqlServerColumnState
{
    internal SqlServerColumnState(
        string name,
        string sqlType,
        bool isNullable,
        bool isPrimaryKey,
        string? defaultExpression,
        bool isIdentity)
    {
        Name = name;
        SqlType = sqlType;
        IsNullable = isNullable;
        IsPrimaryKey = isPrimaryKey;
        DefaultExpression = defaultExpression;
        IsIdentity = isIdentity;
    }

    internal string Name { get; set; }
    internal string SqlType { get; set; }
    internal bool IsNullable { get; set; }
    internal bool IsPrimaryKey { get; set; }
    internal string? DefaultExpression { get; set; }
    internal bool IsIdentity { get; }
}

internal enum SqlServerTokenKind
{
    End,
    Identifier,
    BracketIdentifier,
    QuotedIdentifier,
    String,
    Parameter,
    Number,
    Symbol,
}

internal sealed class SqlServerToken
{
    internal SqlServerToken(
        SqlServerTokenKind kind,
        string text,
        string? value,
        int start,
        int end)
    {
        Kind = kind;
        Text = text;
        Value = value;
        Start = start;
        End = end;
    }

    internal SqlServerTokenKind Kind { get; }
    internal string Text { get; }
    internal string? Value { get; }
    internal int Start { get; }
    internal int End { get; }

    internal bool Is(string keyword)
    {
        return (Kind == SqlServerTokenKind.Identifier ||
                Kind == SqlServerTokenKind.BracketIdentifier ||
                Kind == SqlServerTokenKind.QuotedIdentifier) &&
            string.Equals(Value, keyword, StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeParameterName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new MigrationValidationException("A SQL Server command parameter name is required.");
        }

        return name[0] == '@' ? name.Substring(1) : name;
    }
}

internal sealed class SqlServerDdlLexer
{
    private readonly string _sql;
    private int _position;

    internal SqlServerDdlLexer(string sql)
    {
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
    }

    internal IReadOnlyList<SqlServerToken> Lex()
    {
        var tokens = new List<SqlServerToken>();
        while (true)
        {
            SkipTrivia();
            if (_position >= _sql.Length)
            {
                tokens.Add(new SqlServerToken(
                    SqlServerTokenKind.End,
                    string.Empty,
                    null,
                    _position,
                    _position));
                return tokens;
            }

            var start = _position;
            var current = _sql[_position];
            if (current == '[')
            {
                tokens.Add(ReadBracketIdentifier());
            }
            else if (current == '"')
            {
                tokens.Add(ReadQuotedIdentifier());
            }
            else if (current == '\'')
            {
                tokens.Add(ReadString());
            }
            else if (current == '@')
            {
                tokens.Add(ReadParameter());
            }
            else if (char.IsLetter(current) || current == '_' || current == '#')
            {
                tokens.Add(ReadIdentifier());
            }
            else if (char.IsDigit(current))
            {
                tokens.Add(ReadNumber());
            }
            else
            {
                _position++;
                tokens.Add(new SqlServerToken(
                    SqlServerTokenKind.Symbol,
                    _sql.Substring(start, 1),
                    _sql.Substring(start, 1),
                    start,
                    _position));
            }
        }
    }

    private void SkipTrivia()
    {
        while (_position < _sql.Length)
        {
            if (char.IsWhiteSpace(_sql[_position]))
            {
                _position++;
                continue;
            }

            if (_position + 1 < _sql.Length &&
                _sql[_position] == '-' && _sql[_position + 1] == '-')
            {
                _position += 2;
                while (_position < _sql.Length && _sql[_position] != '\r' && _sql[_position] != '\n')
                {
                    _position++;
                }

                continue;
            }

            if (_position + 1 < _sql.Length &&
                _sql[_position] == '/' && _sql[_position + 1] == '*')
            {
                SkipBlockComment();
                continue;
            }

            break;
        }
    }

    private void SkipBlockComment()
    {
        var start = _position;
        var depth = 0;
        while (_position + 1 < _sql.Length)
        {
            if (_sql[_position] == '/' && _sql[_position + 1] == '*')
            {
                depth++;
                _position += 2;
                continue;
            }

            if (_sql[_position] == '*' && _sql[_position + 1] == '/')
            {
                depth--;
                _position += 2;
                if (depth == 0)
                {
                    return;
                }

                continue;
            }

            _position++;
        }

        throw new MigrationValidationException(
            $"The SQL Server dry-run encountered an unterminated block comment at offset {start}.");
    }

    private SqlServerToken ReadBracketIdentifier()
    {
        var start = _position++;
        var value = new StringBuilder();
        while (_position < _sql.Length)
        {
            var current = _sql[_position++];
            if (current == ']')
            {
                if (_position < _sql.Length && _sql[_position] == ']')
                {
                    _position++;
                    value.Append(']');
                    continue;
                }

                return new SqlServerToken(
                    SqlServerTokenKind.BracketIdentifier,
                    _sql.Substring(start, _position - start),
                    value.ToString(),
                    start,
                    _position);
            }

            value.Append(current);
        }

        throw new MigrationValidationException(
            $"The SQL Server dry-run encountered an unterminated bracket identifier at offset {start}.");
    }

    private SqlServerToken ReadQuotedIdentifier()
    {
        var start = _position++;
        var value = new StringBuilder();
        while (_position < _sql.Length)
        {
            var current = _sql[_position++];
            if (current == '"')
            {
                if (_position < _sql.Length && _sql[_position] == '"')
                {
                    _position++;
                    value.Append('"');
                    continue;
                }

                return new SqlServerToken(
                    SqlServerTokenKind.QuotedIdentifier,
                    _sql.Substring(start, _position - start),
                    value.ToString(),
                    start,
                    _position);
            }

            value.Append(current);
        }

        throw new MigrationValidationException(
            $"The SQL Server dry-run encountered an unterminated quoted identifier at offset {start}.");
    }

    private SqlServerToken ReadString()
    {
        var start = _position++;
        var value = new StringBuilder();
        while (_position < _sql.Length)
        {
            var current = _sql[_position++];
            if (current == '\'')
            {
                if (_position < _sql.Length && _sql[_position] == '\'')
                {
                    _position++;
                    value.Append('\'');
                    continue;
                }

                return new SqlServerToken(
                    SqlServerTokenKind.String,
                    _sql.Substring(start, _position - start),
                    value.ToString(),
                    start,
                    _position);
            }

            value.Append(current);
        }

        throw new MigrationValidationException(
            $"The SQL Server dry-run encountered an unterminated string literal at offset {start}.");
    }

    private SqlServerToken ReadParameter()
    {
        var start = _position++;
        while (_position < _sql.Length && IsIdentifierPart(_sql[_position]))
        {
            _position++;
        }

        var text = _sql.Substring(start, _position - start);
        return new SqlServerToken(
            SqlServerTokenKind.Parameter,
            text,
            text.Substring(1),
            start,
            _position);
    }

    private SqlServerToken ReadIdentifier()
    {
        var start = _position++;
        while (_position < _sql.Length && IsIdentifierPart(_sql[_position]))
        {
            _position++;
        }

        var text = _sql.Substring(start, _position - start);
        return new SqlServerToken(
            SqlServerTokenKind.Identifier,
            text,
            text,
            start,
            _position);
    }

    private SqlServerToken ReadNumber()
    {
        var start = _position++;
        while (_position < _sql.Length &&
               (char.IsDigit(_sql[_position]) || _sql[_position] == '.' || _sql[_position] == 'e' ||
                _sql[_position] == 'E' || _sql[_position] == '+' || _sql[_position] == '-'))
        {
            _position++;
        }

        var text = _sql.Substring(start, _position - start);
        return new SqlServerToken(
            SqlServerTokenKind.Number,
            text,
            text,
            start,
            _position);
    }

    private static bool IsIdentifierPart(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_' || value == '$' || value == '#' || value == '@';
    }
}

internal sealed class SqlServerSchemaParser
{
    private static readonly HashSet<string> ColumnConstraintKeywords = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "COLLATE",
        "CONSTRAINT",
        "DEFAULT",
        "FOR",
        "GENERATED",
        "IDENTITY",
        "NOT",
        "NULL",
        "PRIMARY",
        "REFERENCES",
        "UNIQUE",
        "CHECK",
        "WITH",
    };

    private readonly string _sql;
    private readonly IReadOnlyList<SqlServerToken> _tokens;
    private readonly IReadOnlyDictionary<string, object?> _parameters;
    private readonly SqlServerSchemaState _schema;
    private int _position;

    internal SqlServerSchemaParser(
        string sql,
        IReadOnlyList<SqlServerToken> tokens,
        IReadOnlyDictionary<string, object?> parameters,
        SqlServerSchemaState schema)
    {
        _sql = sql;
        _tokens = tokens;
        _parameters = parameters;
        _schema = schema;
    }

    internal void Apply()
    {
        while (!AtEnd)
        {
            if (MatchSymbol(";"))
            {
                continue;
            }

            var start = _position;
            var end = start;
            var depth = 0;
            while (end < _tokens.Count && _tokens[end].Kind != SqlServerTokenKind.End)
            {
                if (_tokens[end].Is("GO") && depth == 0 && end == start)
                {
                    end++;
                    break;
                }

                if (_tokens[end].Text == "(")
                {
                    depth++;
                }
                else if (_tokens[end].Text == ")" && depth > 0)
                {
                    depth--;
                }

                if (_tokens[end].Text == ";" && depth == 0)
                {
                    break;
                }

                end++;
            }

            if (end == start)
            {
                _position++;
                continue;
            }

            ApplyStatement(start, end);
            _position = end;
            MatchSymbol(";");
        }
    }

    private bool AtEnd => _tokens[_position].Kind == SqlServerTokenKind.End;

    private void ApplyStatement(int start, int end)
    {
        _position = start;
        if (_tokens[start].Is("GO"))
        {
            return;
        }

        if (_tokens[start].Is("CREATE"))
        {
            ParseCreate(end);
            return;
        }

        if (_tokens[start].Is("ALTER"))
        {
            ParseAlter(end);
            return;
        }

        if (_tokens[start].Is("DROP"))
        {
            ParseDrop(end);
            return;
        }

        if (_tokens[start].Is("EXEC") || _tokens[start].Is("EXECUTE"))
        {
            ParseExecute(end);
            return;
        }

        if (IsSchemaNeutralStatement(_tokens[start]))
        {
            return;
        }

        ThrowUnsupported(start, end);
    }

    private void ParseCreate(int end)
    {
        Advance();
        if (Match("TABLE"))
        {
            ParseCreateTable(end);
            return;
        }

        if (Match("UNIQUE"))
        {
            Match("CLUSTERED");
            Match("NONCLUSTERED");
            if (!Match("INDEX"))
            {
                ThrowUnsupported(_position - 2, end);
            }

            return;
        }

        if (Match("CLUSTERED") || Match("NONCLUSTERED"))
        {
            if (!Match("INDEX"))
            {
                ThrowUnsupported(_position - 2, end);
            }

            return;
        }

        if (Match("INDEX"))
        {
            return;
        }

        ThrowUnsupported(_position - 1, end);
    }

    private void ParseCreateTable(int end)
    {
        var tableName = ReadQualifiedName("Expected a table name after CREATE TABLE.");
        RequireSymbol("(", "Expected '(' after the CREATE TABLE name.");
        var table = new SqlServerTableState(tableName.Schema, tableName.Name);
        var primaryKeys = new List<string>();

        while (!AtEnd && !IsSymbol(")"))
        {
            if (MatchSymbol(","))
            {
                continue;
            }

            if (Is("CONSTRAINT") || Is("PRIMARY") || Is("UNIQUE") || Is("FOREIGN") || Is("CHECK") || Is("DEFAULT"))
            {
                ParseTableConstraint(end, primaryKeys);
            }
            else
            {
                var column = ParseColumn(end);
                if (table.ContainsColumn(column.Name))
                {
                    throw new MigrationValidationException(
                        $"The CREATE TABLE statement declares column '{column.Name}' more than once.");
                }

                table.Columns.Add(column);
            }

            if (!MatchSymbol(",") && !IsSymbol(")"))
            {
                ThrowUnsupported(_position, end);
            }
        }

        RequireSymbol(")", "Expected ')' after CREATE TABLE definitions.");
        if (table.Columns.Count == 0)
        {
            throw new MigrationValidationException("A CREATE TABLE statement must declare at least one column.");
        }

        foreach (var primaryKey in primaryKeys)
        {
            table.FindColumn(primaryKey).IsPrimaryKey = true;
        }

        ConsumeCreateTableOptions(end);
        if (_schema.ContainsTable(table.Schema, table.Name))
        {
            throw new MigrationValidationException(
                $"The schema preview creates table '{table.Schema}.{table.Name}' more than once.");
        }

        _schema.Tables.Add(table);
    }

    private void ConsumeCreateTableOptions(int end)
    {
        while (_position < end)
        {
            if (Match("ON"))
            {
                ReadAnyIdentifier("Expected a filegroup name after ON.");
                continue;
            }

            if (Match("TEXTIMAGE_ON"))
            {
                ReadAnyIdentifier("Expected a filegroup name after TEXTIMAGE_ON.");
                continue;
            }

            if (Match("WITH"))
            {
                SkipBalancedParenthesesIfPresent();
                continue;
            }

            ThrowUnsupported(_position, end);
        }
    }

    private SqlServerColumnState ParseColumn(int end)
    {
        var name = ReadAnyIdentifier("Expected a column name.");
        var type = ParseType(end);
        var nullable = true;
        var primaryKey = false;
        var identity = false;
        string? defaultExpression = null;

        while (_position < end && !IsSymbol(",") && !IsSymbol(")"))
        {
            if (Match("CONSTRAINT"))
            {
                ReadAnyIdentifier("Expected a constraint name.");
                continue;
            }

            if (Match("IDENTITY"))
            {
                identity = true;
                SkipBalancedParenthesesIfPresent();
                if (Is("NOT") && _position + 1 < end && _tokens[_position + 1].Is("FOR"))
                {
                    Advance();
                    Require("FOR", "Expected FOR after NOT in an IDENTITY definition.");
                    Require("REPLICATION", "Expected REPLICATION after NOT FOR in an IDENTITY definition.");
                }
                continue;
            }

            if (Match("NOT"))
            {
                Require("NULL", "Expected NULL after NOT.");
                nullable = false;
                continue;
            }

            if (Match("NULL"))
            {
                nullable = true;
                continue;
            }

            if (Match("PRIMARY"))
            {
                Require("KEY", "Expected KEY after PRIMARY.");
                primaryKey = true;
                SkipOptionalIndexKind();
                continue;
            }

            if (Match("DEFAULT"))
            {
                defaultExpression = ReadDefaultExpression(end);
                continue;
            }

            if (Match("COLLATE"))
            {
                ReadAnyIdentifier("Expected a collation name after COLLATE.");
                continue;
            }

            if (Match("UNIQUE"))
            {
                SkipOptionalIndexKind();
                continue;
            }

            if (Match("REFERENCES"))
            {
                ReadQualifiedName("Expected a referenced table after REFERENCES.");
                if (MatchSymbol("("))
                {
                    SkipUntilMatchingCloseParen();
                }

                continue;
            }

            if (Match("ON"))
            {
                if (Is("DELETE") || Is("UPDATE"))
                {
                    Advance();
                }

                if (_position < end)
                {
                    Advance();
                }

                continue;
            }

            if (Match("WITH"))
            {
                if (Match("VALUES"))
                {
                    continue;
                }

                SkipBalancedParenthesesIfPresent();
                continue;
            }

            if (Match("CHECK"))
            {
                SkipBalancedParenthesesIfPresent();
                continue;
            }

            if (Is("GENERATED"))
            {
                ThrowUnsupported(_position, end);
            }

            ThrowUnsupported(_position, end);
        }

        return new SqlServerColumnState(name, type, nullable, primaryKey, defaultExpression, identity);
    }

    private string ParseType(int end)
    {
        var first = ReadAnyIdentifier("Expected a SQL Server column type.");
        var typeName = first.ToLowerInvariant();
        if (typeName == "double" && Match("PRECISION"))
        {
            typeName = "float";
        }
        else if (typeName == "character" && Match("VARYING"))
        {
            typeName = "varchar";
        }

        if (!IsSupportedTypeName(typeName))
        {
            throw new MigrationValidationException(
                $"The SQL Server dry-run does not support column type '{first}'.");
        }

        if (MatchSymbol("("))
        {
            var modifier = new StringBuilder();
            var depth = 1;
            while (_position < end && depth > 0)
            {
                var token = _tokens[_position++];
                if (token.Text == "(")
                {
                    depth++;
                }
                else if (token.Text == ")")
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                modifier.Append(RenderTypeToken(token));
            }

            if (depth != 0)
            {
                throw new MigrationValidationException("The SQL Server dry-run encountered an unterminated type modifier.");
            }

            if (modifier.Length == 0)
            {
                throw new MigrationValidationException("A SQL Server type modifier cannot be empty.");
            }

            typeName += "(" + modifier + ")";
        }

        ValidateTypeModifier(typeName);
        return typeName;
    }

    private void ParseTableConstraint(int end, List<string> primaryKeys)
    {
        if (Match("CONSTRAINT"))
        {
            ReadAnyIdentifier("Expected a table constraint name.");
        }

        if (Match("PRIMARY"))
        {
            Require("KEY", "Expected KEY after PRIMARY.");
            SkipOptionalIndexKind();
            ReadColumnList(primaryKeys, end);
            SkipConstraintTail(end);
            return;
        }

        if (Match("UNIQUE"))
        {
            SkipOptionalIndexKind();
            SkipConstraintColumnList(end);
            return;
        }

        if (Match("FOREIGN"))
        {
            Require("KEY", "Expected KEY after FOREIGN.");
            SkipConstraintColumnList(end);
            if (Match("REFERENCES"))
            {
                ReadQualifiedName("Expected a referenced table after REFERENCES.");
                SkipConstraintColumnList(end);
            }

            SkipConstraintTail(end);
            return;
        }

        if (Match("CHECK"))
        {
            SkipBalancedParenthesesIfPresent();
            return;
        }

        if (Match("DEFAULT"))
        {
            ReadDefaultExpression(end);
            if (Match("FOR"))
            {
                ReadAnyIdentifier("Expected a column name after FOR.");
            }

            return;
        }

        ThrowUnsupported(_position, end);
    }

    private void ParseAlter(int end)
    {
        Advance();
        if (Match("INDEX"))
        {
            return;
        }

        if (!Match("TABLE"))
        {
            ThrowUnsupported(_position - 1, end);
        }

        var tableName = ReadQualifiedName("Expected a table name after ALTER TABLE.");
        if (!Is("ADD") && !Is("ALTER") && !Is("DROP"))
        {
            ThrowUnsupported(_position, end);
        }

        var table = _schema.FindTable(tableName.Schema, tableName.Name);
        var parsedAction = false;
        while (_position < end)
        {
            if (MatchSymbol(","))
            {
                continue;
            }

            parsedAction = true;
            if (Match("ADD"))
            {
                ParseAlterAdd(table, end);
            }
            else if (Match("ALTER"))
            {
                ParseAlterColumn(table, end);
            }
            else if (Match("DROP"))
            {
                ParseAlterDrop(table, end);
            }
            else
            {
                ThrowUnsupported(_position, end);
            }

            if (_position < end && !IsSymbol(","))
            {
                ThrowUnsupported(_position, end);
            }
        }

        if (!parsedAction)
        {
            ThrowUnsupported(_position, end);
        }
    }

    private void ParseAlterAdd(SqlServerTableState table, int end)
    {
        if (Match("CONSTRAINT") || Is("PRIMARY") || Is("UNIQUE") || Is("FOREIGN") || Is("CHECK"))
        {
            if (_tokens[_position - 1].Is("CONSTRAINT"))
            {
                ReadAnyIdentifier("Expected a constraint name.");
            }

            var primaryKeys = new List<string>();
            ParseTableConstraint(end, primaryKeys);
            foreach (var primaryKey in primaryKeys)
            {
                table.FindColumn(primaryKey).IsPrimaryKey = true;
            }

            return;
        }

        Match("COLUMN");
        var column = ParseColumn(end);
        if (table.ContainsColumn(column.Name))
        {
            throw new MigrationValidationException(
                $"The ALTER TABLE statement adds column '{column.Name}' more than once to '{table.Schema}.{table.Name}'.");
        }

        table.Columns.Add(column);
    }

    private void ParseAlterColumn(SqlServerTableState table, int end)
    {
        Require("COLUMN", "Expected COLUMN after ALTER in an ALTER TABLE statement.");
        var name = ReadAnyIdentifier("Expected a column name after ALTER COLUMN.");
        var type = ParseType(end);
        var nullable = table.FindColumn(name).IsNullable;
        var hasNullability = false;
        while (_position < end && !IsSymbol(","))
        {
            if (Match("NOT"))
            {
                Require("NULL", "Expected NULL after NOT.");
                nullable = false;
                hasNullability = true;
            }
            else if (Match("NULL"))
            {
                nullable = true;
                hasNullability = true;
            }
            else
            {
                ThrowUnsupported(_position, end);
            }
        }

        var column = table.FindColumn(name);
        column.SqlType = type;
        if (hasNullability)
        {
            column.IsNullable = nullable;
        }
    }

    private void ParseAlterDrop(SqlServerTableState table, int end)
    {
        if (Match("COLUMN"))
        {
            while (_position < end)
            {
                var name = ReadAnyIdentifier("Expected a column name after DROP COLUMN.");
                var column = table.FindColumn(name);
                table.Columns.Remove(column);
                if (!MatchSymbol(","))
                {
                    break;
                }
            }

            return;
        }

        ThrowUnsupported(_position, end);
    }

    private void ParseDrop(int end)
    {
        Advance();
        if (Match("INDEX"))
        {
            return;
        }

        if (!Match("TABLE"))
        {
            ThrowUnsupported(_position - 1, end);
        }

        var dropIfExists = false;
        if (Match("IF"))
        {
            Require("EXISTS", "Expected EXISTS after IF in DROP TABLE.");
            dropIfExists = true;
        }

        while (_position < end)
        {
            var tableName = ReadQualifiedName("Expected a table name after DROP TABLE.");
            var table = _schema.FindTableOrNull(tableName.Schema, tableName.Name);
            if (table is null)
            {
                if (!dropIfExists)
                {
                    _schema.FindTable(tableName.Schema, tableName.Name);
                }
            }
            else
            {
                _schema.Tables.Remove(table);
            }
            if (!MatchSymbol(","))
            {
                break;
            }
        }
    }

    private void ParseExecute(int end)
    {
        Advance();
        var procedure = ReadAnyIdentifier("Expected a procedure name after EXEC.");
        if (MatchSymbol("."))
        {
            procedure += "." + ReadAnyIdentifier("Expected a procedure name after '.'.");
        }

        if (string.Equals(procedure, "sp_addextendedproperty", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(procedure, "sys.sp_addextendedproperty", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(procedure, "sp_dropextendedproperty", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(procedure, "sys.sp_dropextendedproperty", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(procedure, "sp_rename", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(procedure, "sys.sp_rename", StringComparison.OrdinalIgnoreCase))
        {
            ThrowUnsupported(_position - 1, end);
        }

        string? objectName = null;
        string? newName = null;
        string? objectType = null;
        var positional = 0;
        while (_position < end)
        {
            if (MatchSymbol(","))
            {
                continue;
            }

            string? parameterName = null;
            if (_tokens[_position].Kind == SqlServerTokenKind.Parameter)
            {
                parameterName = SqlServerToken.NormalizeParameterName(_tokens[_position].Value!);
                Advance();
                if (MatchSymbol("="))
                {
                    // Named procedure argument.
                }
                else
                {
                    positional++;
                    parameterName = PositionalRenameParameter(positional);
                }
            }

            var value = ReadExecuteValue();
            if (parameterName is null)
            {
                positional++;
                parameterName = PositionalRenameParameter(positional);
            }

            if (string.Equals(parameterName, "objname", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parameterName, "old_name", StringComparison.OrdinalIgnoreCase))
            {
                objectName = value;
            }
            else if (string.Equals(parameterName, "newname", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(parameterName, "new_name", StringComparison.OrdinalIgnoreCase))
            {
                newName = value;
            }
            else if (string.Equals(parameterName, "objtype", StringComparison.OrdinalIgnoreCase))
            {
                objectType = value;
            }
            else
            {
                ThrowUnsupported(_position - 1, end);
            }
        }

        if (objectName is null || newName is null)
        {
            ThrowUnsupported(0, end);
        }

        ApplyRename(objectName!, newName!, objectType);
    }

    private string ReadExecuteValue()
    {
        if (_tokens[_position].Kind == SqlServerTokenKind.Parameter)
        {
            var name = SqlServerToken.NormalizeParameterName(_tokens[_position].Value!);
            Advance();
            if (!_parameters.TryGetValue(name, out var value) || value is null || value == DBNull.Value)
            {
                throw new MigrationValidationException(
                    $"The SQL Server schema preview is missing a value for parameter '@{name}'.");
            }

            if (!(value is string))
            {
                throw new MigrationValidationException(
                    $"The SQL Server schema preview parameter '@{name}' must contain text.");
            }

            return (string)value;
        }

        if (_tokens[_position].Kind == SqlServerTokenKind.Identifier &&
            string.Equals(_tokens[_position].Value, "N", StringComparison.OrdinalIgnoreCase) &&
            _position + 1 < _tokens.Count && _tokens[_position + 1].Kind == SqlServerTokenKind.String)
        {
            Advance();
        }

        if (_tokens[_position].Kind == SqlServerTokenKind.String)
        {
            return Advance().Value!;
        }

        if (_tokens[_position].Kind == SqlServerTokenKind.Identifier ||
            _tokens[_position].Kind == SqlServerTokenKind.BracketIdentifier ||
            _tokens[_position].Kind == SqlServerTokenKind.QuotedIdentifier)
        {
            return Advance().Value!;
        }

        ThrowUnsupported(_position, _tokens.Count - 1);
        return string.Empty;
    }

    private void ApplyRename(string objectName, string newName, string? objectType)
    {
        var parts = ParseRenameName(objectName);
        var kind = objectType ?? (parts.Count == 3 ? "COLUMN" : "OBJECT");
        if (string.Equals(kind, "OBJECT", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Count == 1)
            {
                parts.Insert(0, "dbo");
            }

            if (parts.Count != 2)
            {
                throw new MigrationValidationException(
                    "SQL Server sp_rename object names must contain a schema and object name.");
            }

            var table = _schema.FindTable(parts[0], parts[1]);
            if (_schema.ContainsTable(table.Schema, newName))
            {
                throw new MigrationValidationException(
                    $"The schema preview renames table '{table.Schema}.{table.Name}' to an existing table '{newName}'.");
            }

            table.Name = newName;
            return;
        }

        if (string.Equals(kind, "COLUMN", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Count == 2)
            {
                parts.Insert(0, "dbo");
            }

            if (parts.Count != 3)
            {
                throw new MigrationValidationException(
                    "SQL Server sp_rename column names must contain a schema, table, and column name.");
            }

            var table = _schema.FindTable(parts[0], parts[1]);
            var column = table.FindColumn(parts[2]);
            if (table.ContainsColumn(newName))
            {
                throw new MigrationValidationException(
                    $"The schema preview renames column '{parts[2]}' to an existing column '{newName}'.");
            }

            column.Name = newName;
            return;
        }

        throw new MigrationValidationException(
            $"SQL Server sp_rename object type '{objectType}' is not supported by schema preview.");
    }

    private List<string> ParseRenameName(string value)
    {
        var tokens = new SqlServerDdlLexer(value).Lex();
        var parts = new List<string>();
        var position = 0;
        while (tokens[position].Kind != SqlServerTokenKind.End)
        {
            if (tokens[position].Kind != SqlServerTokenKind.Identifier &&
                tokens[position].Kind != SqlServerTokenKind.BracketIdentifier &&
                tokens[position].Kind != SqlServerTokenKind.QuotedIdentifier)
            {
                throw new MigrationValidationException(
                    $"SQL Server sp_rename object name '{value}' is not a qualified identifier.");
            }

            parts.Add(tokens[position].Value!);
            position++;
            if (tokens[position].Kind == SqlServerTokenKind.End)
            {
                break;
            }

            if (tokens[position].Text != ".")
            {
                throw new MigrationValidationException(
                    $"SQL Server sp_rename object name '{value}' is not a qualified identifier.");
            }

            position++;
        }

        if (parts.Count == 0)
        {
            throw new MigrationValidationException("SQL Server sp_rename requires a non-empty object name.");
        }

        return parts;
    }

    private string ReadDefaultExpression(int end)
    {
        var start = _position;
        var depth = 0;
        while (_position < end)
        {
            var token = _tokens[_position];
            if (depth == 0 &&
                (token.Text == "," || token.Text == ")" ||
                 (token.Kind == SqlServerTokenKind.Identifier && ColumnConstraintKeywords.Contains(token.Value!))))
            {
                break;
            }

            if (token.Text == "(")
            {
                depth++;
            }
            else if (token.Text == ")" && depth > 0)
            {
                depth--;
            }

            _position++;
        }

        if (_position == start)
        {
            throw new MigrationValidationException("A SQL Server DEFAULT constraint requires an expression.");
        }

        var sourceStart = _tokens[start].Start;
        var sourceEnd = _tokens[_position - 1].End;
        return _sql.Substring(sourceStart, sourceEnd - sourceStart).Trim();
    }

    private void ReadColumnList(List<string> columns, int end)
    {
        RequireSymbol("(", "Expected a column list in a table constraint.");
        while (_position < end && !IsSymbol(")"))
        {
            if (MatchSymbol(","))
            {
                continue;
            }

            columns.Add(ReadAnyIdentifier("Expected a column name in a table constraint."));
            if (Is("ASC") || Is("DESC"))
            {
                Advance();
            }
        }

        RequireSymbol(")", "Expected ')' after a table constraint column list.");
    }

    private void SkipConstraintColumnList(int end)
    {
        if (MatchSymbol("("))
        {
            SkipUntilMatchingCloseParen();
        }

        SkipConstraintTail(end);
    }

    private void SkipConstraintTail(int end)
    {
        var depth = 0;
        while (_position < end)
        {
            if (depth == 0 && (IsSymbol(",") || IsSymbol(")")))
            {
                return;
            }

            var token = Advance();
            if (token.Text == "(")
            {
                depth++;
            }
            else if (token.Text == ")" && depth > 0)
            {
                depth--;
            }
        }
    }

    private void SkipOptionalIndexKind()
    {
        if (Is("CLUSTERED") || Is("NONCLUSTERED"))
        {
            Advance();
        }
    }

    private void SkipBalancedParenthesesIfPresent()
    {
        if (!MatchSymbol("("))
        {
            return;
        }

        SkipUntilMatchingCloseParen();
    }

    private void SkipUntilMatchingCloseParen()
    {
        var depth = 1;
        while (!AtEnd && depth > 0)
        {
            var token = Advance();
            if (token.Text == "(")
            {
                depth++;
            }
            else if (token.Text == ")")
            {
                depth--;
            }
        }

        if (depth != 0)
        {
            throw new MigrationValidationException(
                "The SQL Server dry-run encountered an unterminated parenthesized expression.");
        }
    }

    private SqlServerQualifiedName ReadQualifiedName(string message)
    {
        var first = ReadAnyIdentifier(message);
        var schema = "dbo";
        var name = first;
        if (MatchSymbol("."))
        {
            schema = first;
            name = ReadAnyIdentifier("Expected an object name after '.'.");
            if (IsSymbol("."))
            {
                throw new MigrationValidationException("Four-part SQL Server names are not supported by migration schema preview.");
            }
        }

        return new SqlServerQualifiedName(schema, name);
    }

    private string ReadAnyIdentifier(string message)
    {
        var token = _tokens[_position];
        if (token.Kind != SqlServerTokenKind.Identifier &&
            token.Kind != SqlServerTokenKind.BracketIdentifier &&
            token.Kind != SqlServerTokenKind.QuotedIdentifier)
        {
            throw new MigrationValidationException(message);
        }

        Advance();
        return token.Value!;
    }

    private void Require(string keyword, string message)
    {
        if (!Match(keyword))
        {
            throw new MigrationValidationException(message);
        }
    }

    private void RequireSymbol(string symbol, string message)
    {
        if (!MatchSymbol(symbol))
        {
            throw new MigrationValidationException(message);
        }
    }

    private bool Match(string keyword)
    {
        if (!Is(keyword))
        {
            return false;
        }

        _position++;
        return true;
    }

    private bool MatchSymbol(string symbol)
    {
        if (!IsSymbol(symbol))
        {
            return false;
        }

        _position++;
        return true;
    }

    private bool Is(string keyword)
    {
        return _tokens[_position].Is(keyword);
    }

    private bool IsSymbol(string symbol)
    {
        return _tokens[_position].Kind == SqlServerTokenKind.Symbol &&
            string.Equals(_tokens[_position].Text, symbol, StringComparison.Ordinal);
    }

    private SqlServerToken Advance()
    {
        return _tokens[_position++];
    }

    private void ThrowUnsupported(int start, int end)
    {
        var first = Math.Max(0, Math.Min(start, _tokens.Count - 1));
        var last = Math.Max(first, Math.Min(end, _tokens.Count - 1));
        var text = string.Join(" ", _tokens.Skip(first).Take(last - first).Select(token => token.Text));
        throw new MigrationValidationException(
            "The SQL Server dry-run cannot determine the final schema from statement: " + text);
    }

    private static bool IsSchemaNeutralStatement(SqlServerToken token)
    {
        if (token.Kind != SqlServerTokenKind.Identifier)
        {
            return false;
        }

        switch (token.Value!.ToUpperInvariant())
        {
            case "BEGIN":
            case "COMMIT":
            case "DELETE":
            case "DECLARE":
            case "DENY":
            case "GRANT":
            case "INSERT":
            case "MERGE":
            case "PRINT":
            case "ROLLBACK":
            case "SAVE":
            case "SELECT":
            case "SET":
            case "TRUNCATE":
            case "UPDATE":
            case "USE":
            case "WAITFOR":
            case "WITH":
                return true;
            default:
                return false;
        }
    }

    private static string PositionalRenameParameter(int position)
    {
        switch (position)
        {
            case 1: return "objname";
            case 2: return "newname";
            case 3: return "objtype";
            default: return "argument" + position.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string RenderTypeToken(SqlServerToken token)
    {
        if (token.Kind == SqlServerTokenKind.Identifier || token.Kind == SqlServerTokenKind.Number)
        {
            return token.Text.ToLowerInvariant();
        }

        return token.Text;
    }

    private static bool IsSupportedTypeName(string typeName)
    {
        switch (typeName)
        {
            case "bigint":
            case "binary":
            case "bit":
            case "char":
            case "date":
            case "datetime":
            case "datetime2":
            case "datetimeoffset":
            case "decimal":
            case "float":
            case "image":
            case "int":
            case "integer":
            case "money":
            case "nchar":
            case "ntext":
            case "nvarchar":
            case "numeric":
            case "real":
            case "rowversion":
            case "smalldatetime":
            case "smallint":
            case "smallmoney":
            case "text":
            case "time":
            case "timestamp":
            case "tinyint":
            case "uniqueidentifier":
            case "varbinary":
            case "varchar":
            case "xml":
                return true;
            default:
                return false;
        }
    }

    private static void ValidateTypeModifier(string typeName)
    {
        var open = typeName.IndexOf('(');
        if (open < 0)
        {
            return;
        }

        var close = typeName.LastIndexOf(')');
        if (close <= open)
        {
            throw new MigrationValidationException($"The SQL Server type '{typeName}' has an invalid modifier.");
        }

        var modifier = typeName.Substring(open + 1, close - open - 1);
        var pieces = modifier.Split(',');
        if (pieces.Any(piece => piece.Length == 0))
        {
            throw new MigrationValidationException($"The SQL Server type '{typeName}' has an invalid modifier.");
        }

        if (typeName.StartsWith("nvarchar(", StringComparison.OrdinalIgnoreCase) ||
            typeName.StartsWith("nchar(", StringComparison.OrdinalIgnoreCase) ||
            typeName.StartsWith("varchar(", StringComparison.OrdinalIgnoreCase) ||
            typeName.StartsWith("char(", StringComparison.OrdinalIgnoreCase) ||
            typeName.StartsWith("varbinary(", StringComparison.OrdinalIgnoreCase) ||
            typeName.StartsWith("binary(", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(modifier, "max", StringComparison.OrdinalIgnoreCase) &&
                (!int.TryParse(modifier, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length <= 0))
            {
                throw new MigrationValidationException($"The SQL Server type '{typeName}' has an invalid length.");
            }
        }

        if (typeName.StartsWith("decimal(", StringComparison.OrdinalIgnoreCase) ||
            typeName.StartsWith("numeric(", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var precision) ||
                precision < 1 || precision > 38)
            {
                throw new MigrationValidationException($"The SQL Server type '{typeName}' has an invalid precision.");
            }

            if (pieces.Length > 2 ||
                (pieces.Length == 2 &&
                 (!int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var scale) ||
                  scale < 0 || scale > precision)))
            {
                throw new MigrationValidationException($"The SQL Server type '{typeName}' has an invalid scale.");
            }
        }
    }

    private sealed class SqlServerQualifiedName
    {
        internal SqlServerQualifiedName(string schema, string name)
        {
            Schema = schema;
            Name = name;
        }

        internal string Schema { get; }
        internal string Name { get; }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CobaltumOrm.Migrations;

namespace CobaltumOrm.Migrations.Sqlite;

/// <summary>Reconstructs the table portion of a SQLite schema from migration SQL.</summary>
internal sealed class SqliteSchemaBuilder
{
    private readonly List<SqliteMutableTable> _tables = new List<SqliteMutableTable>();

    internal void Apply(string sql)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        foreach (var statementText in SqliteSqlTokenizer.SplitStatements(sql))
        {
            var tokens = SqliteSqlTokenizer.Tokenize(statementText);
            if (tokens.Count == 0)
            {
                continue;
            }

            new SqliteStatementParser(tokens, this).Apply();
        }
    }

    internal MigrationSchema ToMigrationSchema()
    {
        return new MigrationSchema(_tables.Select(table =>
            new MigrationSchemaTable(
                null,
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

    internal SqliteMutableTable FindTable(string name)
    {
        return _tables.FirstOrDefault(table =>
                   string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new MigrationValidationException($"SQLite schema does not contain table '{name}'.");
    }

    internal bool HasTable(string name) => _tables.Any(table =>
        string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase));

    internal void AddTable(SqliteMutableTable table, bool ifNotExists)
    {
        if (HasTable(table.Name))
        {
            if (ifNotExists)
            {
                return;
            }

            throw new MigrationValidationException(
                $"SQLite schema already contains table '{table.Name}'.");
        }

        _tables.Add(table);
    }

    internal void RemoveTable(string name, bool ifExists)
    {
        var index = _tables.FindIndex(table =>
            string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            if (ifExists)
            {
                return;
            }

            throw new MigrationValidationException(
                $"SQLite schema does not contain table '{name}'.");
        }

        _tables.RemoveAt(index);
    }

    internal void RenameTable(string oldName, string newName)
    {
        var table = FindTable(oldName);
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) && HasTable(newName))
        {
            throw new MigrationValidationException(
                $"SQLite schema already contains table '{newName}'.");
        }

        table.Name = newName;
    }
}

internal sealed class SqliteMutableTable
{
    internal SqliteMutableTable(string name, IEnumerable<SqliteMutableColumn> columns)
    {
        Name = name;
        Columns = new List<SqliteMutableColumn>(columns);
    }

    internal string Name { get; set; }
    internal List<SqliteMutableColumn> Columns { get; }

    internal SqliteMutableColumn FindColumn(string name)
    {
        return Columns.FirstOrDefault(column =>
                   string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new MigrationValidationException(
                   $"SQLite table '{Name}' does not contain column '{name}'.");
    }

    internal bool HasColumn(string name) => Columns.Any(column =>
        string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed class SqliteMutableColumn
{
    internal SqliteMutableColumn(
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
    internal string SqlType { get; }
    internal bool IsNullable { get; set; }
    internal bool IsPrimaryKey { get; set; }
    internal string? DefaultExpression { get; }
    internal bool IsIdentity { get; }
}

internal enum SqliteTokenKind
{
    Identifier,
    QuotedIdentifier,
    String,
    Symbol,
}

internal sealed class SqliteToken
{
    internal SqliteToken(SqliteTokenKind kind, string text, string raw)
    {
        Kind = kind;
        Text = text;
        Raw = raw;
    }

    internal SqliteTokenKind Kind { get; }
    internal string Text { get; }
    internal string Raw { get; }

    internal bool IsWord(string word) =>
        Kind == SqliteTokenKind.Identifier &&
        string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
}

internal static class SqliteSqlTokenizer
{
    internal static IReadOnlyList<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var start = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inBacktickQuote = false;
        var inBracketQuote = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\n' || current == '\r')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }

                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && !inBacktickQuote && !inBracketQuote)
            {
                if (current == '-' && next == '-')
                {
                    inLineComment = true;
                    index++;
                    continue;
                }

                if (current == '/' && next == '*')
                {
                    inBlockComment = true;
                    index++;
                    continue;
                }
            }

            if (inSingleQuote)
            {
                if (current == '\'' && next == '\'')
                {
                    index++;
                }
                else if (current == '\'')
                {
                    inSingleQuote = false;
                }

                continue;
            }

            if (inDoubleQuote)
            {
                if (current == '"' && next == '"')
                {
                    index++;
                }
                else if (current == '"')
                {
                    inDoubleQuote = false;
                }

                continue;
            }

            if (inBacktickQuote)
            {
                if (current == '`' && next == '`')
                {
                    index++;
                }
                else if (current == '`')
                {
                    inBacktickQuote = false;
                }

                continue;
            }

            if (inBracketQuote)
            {
                if (current == ']')
                {
                    inBracketQuote = false;
                }

                continue;
            }

            switch (current)
            {
                case '\'':
                    inSingleQuote = true;
                    break;
                case '"':
                    inDoubleQuote = true;
                    break;
                case '`':
                    inBacktickQuote = true;
                    break;
                case '[':
                    inBracketQuote = true;
                    break;
                case ';':
                    var statement = sql.Substring(start, index - start).Trim();
                    if (statement.Length != 0)
                    {
                        statements.Add(statement);
                    }

                    start = index + 1;
                    break;
            }
        }

        if (inSingleQuote || inDoubleQuote || inBacktickQuote || inBracketQuote || inBlockComment)
        {
            throw new MigrationValidationException(
                "SQLite dry-run could not parse SQL containing an unterminated quote or comment.");
        }

        var trailing = sql.Substring(start).Trim();
        if (trailing.Length != 0)
        {
            statements.Add(trailing);
        }

        return statements;
    }

    internal static IReadOnlyList<SqliteToken> Tokenize(string sql)
    {
        var tokens = new List<SqliteToken>();
        for (var index = 0; index < sql.Length;)
        {
            var current = sql[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] != '\n' && sql[index] != '\r')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new MigrationValidationException(
                        "SQLite dry-run could not parse an unterminated block comment.");
                }

                index = end + 2;
                continue;
            }

            if (current == '\'' || current == '"' || current == '`' || current == '[')
            {
                tokens.Add(ReadQuoted(sql, ref index));
                continue;
            }

            if (IsIdentifierStart(current) || char.IsDigit(current))
            {
                var start = index++;
                while (index < sql.Length && IsIdentifierPart(sql[index]))
                {
                    index++;
                }

                var text = sql.Substring(start, index - start);
                tokens.Add(new SqliteToken(SqliteTokenKind.Identifier, text, text));
                continue;
            }

            var symbolStart = index++;
            if (index < sql.Length && IsTwoCharacterOperator(current, sql[index]))
            {
                index++;
            }

            var symbol = sql.Substring(symbolStart, index - symbolStart);
            tokens.Add(new SqliteToken(SqliteTokenKind.Symbol, symbol, symbol));
        }

        return tokens;
    }

    private static SqliteToken ReadQuoted(string sql, ref int index)
    {
        var start = index;
        var opener = sql[index++];
        var closer = opener == '[' ? ']' : opener;
        var isString = opener == '\'';
        var builder = new StringBuilder();
        while (index < sql.Length)
        {
            var current = sql[index++];
            if (current == closer)
            {
                if (index < sql.Length && sql[index] == closer && opener != '[')
                {
                    builder.Append(closer);
                    index++;
                    continue;
                }

                var raw = sql.Substring(start, index - start);
                return new SqliteToken(
                    isString ? SqliteTokenKind.String : SqliteTokenKind.QuotedIdentifier,
                    builder.ToString(),
                    raw);
            }

            builder.Append(current);
        }

        throw new MigrationValidationException(
            "SQLite dry-run could not parse an unterminated quoted identifier or string.");
    }

    private static bool IsIdentifierStart(char value) =>
        value == '_' || value == '$' || char.IsLetter(value);

    private static bool IsIdentifierPart(char value) =>
        IsIdentifierStart(value) || char.IsDigit(value);

    private static bool IsTwoCharacterOperator(char first, char second) =>
        (first == '<' && (second == '=' || second == '>')) ||
        (first == '>' && second == '=') ||
        (first == '!' && second == '=') ||
        (first == '|' && second == '|') ||
        (first == '-' && second == '>');
}

internal sealed class SqliteStatementParser
{
    private static readonly HashSet<string> ColumnConstraintWords = new HashSet<string>(
        new[] { "CONSTRAINT", "PRIMARY", "NOT", "NULL", "UNIQUE", "CHECK", "DEFAULT", "COLLATE", "REFERENCES", "GENERATED" },
        StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<SqliteToken> _tokens;
    private readonly SqliteSchemaBuilder _schema;
    private int _position;

    internal SqliteStatementParser(IReadOnlyList<SqliteToken> tokens, SqliteSchemaBuilder schema)
    {
        _tokens = tokens;
        _schema = schema;
    }

    internal void Apply()
    {
        if (MatchWord("CREATE"))
        {
            ApplyCreate();
            return;
        }

        if (MatchWord("DROP"))
        {
            ApplyDrop();
            return;
        }

        if (MatchWord("ALTER"))
        {
            ApplyAlter();
            return;
        }

        if (IsSchemaNeutral())
        {
            return;
        }

        throw UnsupportedSchemaChange("This SQLite statement is not supported by dry-run schema reconstruction.");
    }

    private void ApplyCreate()
    {
        if (MatchWord("TEMP") || MatchWord("TEMPORARY"))
        {
            throw UnsupportedSchemaChange(
                "SQLite dry-run does not reconstruct temporary tables.");
        }

        if (!MatchWord("TABLE"))
        {
            throw UnsupportedSchemaChange(
                "SQLite dry-run only reconstructs CREATE TABLE statements.");
        }

        var ifNotExists = MatchIfNotExists();
        var tableName = ReadQualifiedName("CREATE TABLE");
        if (!MatchSymbol("("))
        {
            throw UnsupportedSchemaChange(
                "SQLite dry-run cannot reconstruct CREATE TABLE AS SELECT statements.");
        }

        var body = ReadBalancedBody();
        SkipCreateTableOptions();
        EnsureEnd("CREATE TABLE");

        var table = ParseTable(tableName, body);
        _schema.AddTable(table, ifNotExists);
    }

    private void ApplyDrop()
    {
        if (!MatchWord("TABLE"))
        {
            throw UnsupportedSchemaChange(
                "SQLite dry-run only reconstructs DROP TABLE statements.");
        }

        var ifExists = MatchIfExists();
        var tableName = ReadQualifiedName("DROP TABLE");
        EnsureEnd("DROP TABLE");
        _schema.RemoveTable(tableName, ifExists);
    }

    private void ApplyAlter()
    {
        if (!MatchWord("TABLE"))
        {
            throw UnsupportedSchemaChange(
                "SQLite dry-run only reconstructs ALTER TABLE statements.");
        }

        var ifExists = MatchIfExists();
        var tableName = ReadQualifiedName("ALTER TABLE");
        if (MatchWord("ADD"))
        {
            MatchWord("COLUMN");
            var ifNotExists = MatchIfNotExists();
            var columnTokens = RemainingTokens();
            if (columnTokens.Count == 0)
            {
                throw InvalidDdl("ALTER TABLE ADD COLUMN requires a column definition.");
            }

            var column = ParseColumn(columnTokens);
            if (column.IsPrimaryKey || column.IsIdentity)
            {
                throw UnsupportedSchemaChange(
                    "SQLite dry-run cannot apply a primary-key or identity ADD COLUMN definition.");
            }

            SqliteMutableTable table;
            try
            {
                table = _schema.FindTable(tableName);
            }
            catch (MigrationValidationException) when (ifExists)
            {
                return;
            }

            if (table.HasColumn(column.Name))
            {
                if (!ifNotExists)
                {
                    throw new MigrationValidationException(
                        $"SQLite table '{tableName}' already contains column '{column.Name}'.");
                }

                return;
            }

            table.Columns.Add(column);
            return;
        }

        if (MatchWord("DROP"))
        {
            MatchWord("COLUMN");
            var ifColumnExists = MatchIfExists();
            var columnName = ReadName("ALTER TABLE DROP COLUMN");
            EnsureEnd("ALTER TABLE DROP COLUMN");
            SqliteMutableTable table;
            try
            {
                table = _schema.FindTable(tableName);
            }
            catch (MigrationValidationException) when (ifExists)
            {
                return;
            }

            var columnIndex = table.Columns.FindIndex(column =>
                string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase));
            if (columnIndex < 0)
            {
                if (ifColumnExists)
                {
                    return;
                }

                throw new MigrationValidationException(
                    $"SQLite table '{tableName}' does not contain column '{columnName}'.");
            }

            if (table.Columns.Count == 1)
            {
                throw UnsupportedSchemaChange(
                    "SQLite cannot drop the only column from a table.");
            }

            table.Columns.RemoveAt(columnIndex);
            return;
        }

        if (MatchWord("RENAME"))
        {
            if (MatchWord("COLUMN"))
            {
                var oldName = ReadName("ALTER TABLE RENAME COLUMN");
                ExpectWord("TO", "ALTER TABLE RENAME COLUMN");
                var newName = ReadName("ALTER TABLE RENAME COLUMN");
                EnsureEnd("ALTER TABLE RENAME COLUMN");
                SqliteMutableTable table;
                try
                {
                    table = _schema.FindTable(tableName);
                }
                catch (MigrationValidationException) when (ifExists)
                {
                    return;
                }

                var column = table.FindColumn(oldName);
                if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) &&
                    table.HasColumn(newName))
                {
                    throw new MigrationValidationException(
                        $"SQLite table '{tableName}' already contains column '{newName}'.");
                }

                column.Name = newName;
                return;
            }

            ExpectWord("TO", "ALTER TABLE RENAME");
            var newTableName = ReadName("ALTER TABLE RENAME");
            EnsureEnd("ALTER TABLE RENAME");
            if (ifExists && !_schema.HasTable(tableName))
            {
                return;
            }

            _schema.RenameTable(tableName, newTableName);
            return;
        }

        throw UnsupportedSchemaChange(
            "SQLite dry-run only reconstructs ADD COLUMN, DROP COLUMN, RENAME TO, and RENAME COLUMN.");
    }

    private SqliteMutableTable ParseTable(string tableName, IReadOnlyList<SqliteToken> body)
    {
        var items = SplitTopLevel(body, ",");
        if (items.Count == 0 || items.All(item => item.Count == 0))
        {
            throw InvalidDdl("CREATE TABLE must declare at least one column.");
        }

        var columns = new List<SqliteMutableColumn>();
        var primaryKeys = new List<IReadOnlyList<string>>();
        foreach (var item in items)
        {
            if (item.Count == 0)
            {
                continue;
            }

            if (IsTableConstraint(item))
            {
                var primaryKey = ParseTablePrimaryKey(item);
                if (primaryKey != null)
                {
                    primaryKeys.Add(primaryKey);
                }

                continue;
            }

            var column = ParseColumn(item);
            if (columns.Any(existing =>
                    string.Equals(existing.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new MigrationValidationException(
                    $"SQLite table '{tableName}' declares column '{column.Name}' more than once.");
            }

            columns.Add(column);
        }

        if (columns.Count == 0)
        {
            throw InvalidDdl("CREATE TABLE must declare at least one column.");
        }

        if (primaryKeys.Count > 1)
        {
            throw InvalidDdl("CREATE TABLE cannot declare more than one PRIMARY KEY constraint.");
        }

        if (primaryKeys.Count == 1)
        {
            var primaryKey = primaryKeys[0];
            if (primaryKey.Count == 0)
            {
                throw InvalidDdl("PRIMARY KEY must contain at least one column.");
            }

            foreach (var primaryKeyName in primaryKey)
            {
                var column = columns.FirstOrDefault(item =>
                    string.Equals(item.Name, primaryKeyName, StringComparison.OrdinalIgnoreCase));
                if (column is null)
                {
                    throw InvalidDdl(
                        $"PRIMARY KEY refers to unknown column '{primaryKeyName}'.");
                }

                if (column.IsPrimaryKey)
                {
                    throw InvalidDdl(
                        $"Column '{column.Name}' appears in more than one PRIMARY KEY constraint.");
                }

                column.IsPrimaryKey = true;
                column.IsNullable = false;
            }
        }

        return new SqliteMutableTable(tableName, columns);
    }

    private static bool IsTableConstraint(IReadOnlyList<SqliteToken> item)
    {
        var index = 0;
        if (item[index].IsWord("CONSTRAINT"))
        {
            index += 2;
            if (index >= item.Count)
            {
                throw InvalidDdl("CONSTRAINT requires a constraint definition.");
            }
        }

        return item[index].IsWord("PRIMARY") ||
               item[index].IsWord("UNIQUE") ||
               item[index].IsWord("CHECK") ||
               item[index].IsWord("FOREIGN");
    }

    private static IReadOnlyList<string>? ParseTablePrimaryKey(IReadOnlyList<SqliteToken> item)
    {
        var index = 0;
        if (item[index].IsWord("CONSTRAINT"))
        {
            if (item.Count < 3)
            {
                throw InvalidDdl("CONSTRAINT requires a constraint definition.");
            }

            index += 2;
        }

        if (!item[index].IsWord("PRIMARY"))
        {
            return null;
        }

        index++;
        if (index >= item.Count || !item[index].IsWord("KEY"))
        {
            throw InvalidDdl("Expected KEY after PRIMARY.");
        }

        index++;
        if (index >= item.Count || item[index].Text != "(")
        {
            throw InvalidDdl("A table PRIMARY KEY must list its columns.");
        }

        index++;
        var names = new List<string>();
        while (index < item.Count && item[index].Text != ")")
        {
            if (item[index].Text == ",")
            {
                index++;
                continue;
            }

            var name = ReadName(item, ref index, "PRIMARY KEY");
            names.Add(name);
            if (index < item.Count && item[index].Text != "," && item[index].Text != ")")
            {
                if (item[index].IsWord("ASC") || item[index].IsWord("DESC"))
                {
                    index++;
                }

                if (index < item.Count && item[index].Text != "," && item[index].Text != ")")
                {
                    throw InvalidDdl("Unexpected text in PRIMARY KEY column list.");
                }
            }
        }

        if (index >= item.Count || item[index].Text != ")")
        {
            throw InvalidDdl("A table PRIMARY KEY must close its column list.");
        }

        return names;
    }

    private static SqliteMutableColumn ParseColumn(IReadOnlyList<SqliteToken> item)
    {
        var index = 0;
        var name = ReadName(item, ref index, "column definition");
        var typeStart = index;
        var depth = 0;
        while (index < item.Count)
        {
            var token = item[index];
            if (token.Text == "(")
            {
                depth++;
            }
            else if (token.Text == ")")
            {
                depth--;
            }

            if (depth == 0 && token.Kind == SqliteTokenKind.Identifier &&
                ColumnConstraintWords.Contains(token.Text))
            {
                break;
            }

            index++;
        }

        var typeTokens = item.Skip(typeStart).Take(index - typeStart).ToArray();
        var sqlType = typeTokens.Length == 0 ? "BLOB" : RenderType(typeTokens);
        var isNullable = true;
        var isPrimaryKey = false;
        var isIdentity = false;
        string? defaultExpression = null;
        while (index < item.Count)
        {
            if (item[index].IsWord("CONSTRAINT"))
            {
                index += 1;
                if (index < item.Count)
                {
                    index++;
                }

                continue;
            }

            if (item[index].IsWord("PRIMARY"))
            {
                index++;
                if (index >= item.Count || !item[index].IsWord("KEY"))
                {
                    throw InvalidDdl("Expected KEY after PRIMARY in a column definition.");
                }

                index++;
                isPrimaryKey = true;
                isNullable = false;
                if (index < item.Count && (item[index].IsWord("ASC") || item[index].IsWord("DESC")))
                {
                    index++;
                }

                if (index < item.Count && item[index].IsWord("AUTOINCREMENT"))
                {
                    isIdentity = true;
                    index++;
                }

                continue;
            }

            if (item[index].IsWord("NOT"))
            {
                index++;
                if (index >= item.Count || !item[index].IsWord("NULL"))
                {
                    throw InvalidDdl("Expected NULL after NOT in a column definition.");
                }

                index++;
                isNullable = false;
                continue;
            }

            if (item[index].IsWord("NULL"))
            {
                index++;
                isNullable = true;
                continue;
            }

            if (item[index].IsWord("DEFAULT"))
            {
                index++;
                var expressionStart = index;
                index = ConsumeExpression(item, index);
                if (index == expressionStart)
                {
                    throw InvalidDdl("DEFAULT requires an expression.");
                }

                defaultExpression = RenderExpression(item.Skip(expressionStart).Take(index - expressionStart));
                continue;
            }

            if (item[index].IsWord("CHECK"))
            {
                index++;
                ConsumeOptionalParenthesized(item, ref index, "CHECK");
                continue;
            }

            if (item[index].IsWord("COLLATE"))
            {
                index += 1;
                ReadName(item, ref index, "COLLATE");
                continue;
            }

            if (item[index].IsWord("REFERENCES"))
            {
                index++;
                if (index < item.Count)
                {
                    ReadName(item, ref index, "REFERENCES");
                    if (index < item.Count && item[index].Text == ".")
                    {
                        throw InvalidDdl("Qualified REFERENCES names are not supported by SQLite dry-run.");
                    }
                }

                if (index < item.Count && item[index].Text == "(")
                {
                    ConsumeOptionalParenthesized(item, ref index, "REFERENCES");
                }

                ConsumeReferenceTail(item, ref index);

                continue;
            }

            if (item[index].IsWord("GENERATED"))
            {
                // Generated expressions affect value production, not the table/column
                // shape represented by MigrationSchema. Consume the complete clause.
                index++;
                while (index < item.Count &&
                       !item[index].IsWord("STORED") &&
                       !item[index].IsWord("VIRTUAL"))
                {
                    index++;
                }

                if (index < item.Count)
                {
                    index++;
                }

                continue;
            }

            if (item[index].IsWord("UNIQUE"))
            {
                index++;
                continue;
            }

            if (item[index].IsWord("AUTOINCREMENT"))
            {
                isIdentity = true;
                index++;
                continue;
            }

            if (item[index].IsWord("ON"))
            {
                // ON CONFLICT is a valid tail for several SQLite constraints.
                index++;
                if (index < item.Count && item[index].IsWord("CONFLICT"))
                {
                    index++;
                    if (index < item.Count)
                    {
                        index++;
                    }

                    continue;
                }
            }

            throw InvalidDdl(
                $"Unsupported column constraint token '{item[index].Raw}'.");
        }

        if (isIdentity && (!isPrimaryKey || !string.Equals(sqlType, "INTEGER", StringComparison.OrdinalIgnoreCase)))
        {
            throw InvalidDdl("AUTOINCREMENT requires an INTEGER PRIMARY KEY column.");
        }

        if (isPrimaryKey)
        {
            isNullable = false;
        }

        return new SqliteMutableColumn(name, sqlType, isNullable, isPrimaryKey, defaultExpression, isIdentity);
    }

    private static int ConsumeExpression(IReadOnlyList<SqliteToken> tokens, int index)
    {
        var expressionStart = index;
        var depth = 0;
        while (index < tokens.Count)
        {
            var token = tokens[index];
            if (token.Text == "(")
            {
                depth++;
            }
            else if (token.Text == ")")
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }

            if (depth == 0 && index != expressionStart && IsConstraintStart(tokens[index]))
            {
                break;
            }

            index++;
        }

        return index;
    }

    private static bool IsConstraintStart(SqliteToken token) =>
        token.Kind == SqliteTokenKind.Identifier && ColumnConstraintWords.Contains(token.Text);

    private static void ConsumeOptionalParenthesized(
        IReadOnlyList<SqliteToken> tokens,
        ref int index,
        string context)
    {
        if (index >= tokens.Count || tokens[index].Text != "(")
        {
            throw InvalidDdl($"{context} requires a parenthesized expression.");
        }

        var depth = 0;
        while (index < tokens.Count)
        {
            if (tokens[index].Text == "(")
            {
                depth++;
            }
            else if (tokens[index].Text == ")")
            {
                depth--;
                if (depth == 0)
                {
                    index++;
                    return;
                }
            }

            index++;
        }

        throw InvalidDdl($"{context} has an unterminated parenthesized expression.");
    }

    private static void ConsumeReferenceTail(IReadOnlyList<SqliteToken> tokens, ref int index)
    {
        var depth = 0;
        while (index < tokens.Count)
        {
            if (tokens[index].Text == "(")
            {
                depth++;
            }
            else if (tokens[index].Text == ")")
            {
                if (depth == 0)
                {
                    return;
                }

                depth--;
            }

            if (depth == 0 && IsConstraintStart(tokens[index]))
            {
                return;
            }

            index++;
        }
    }

    private void SkipCreateTableOptions()
    {
        while (_position < _tokens.Count)
        {
            if (MatchWord("WITHOUT"))
            {
                ExpectWord("ROWID", "CREATE TABLE WITHOUT");
                continue;
            }

            if (MatchWord("STRICT"))
            {
                continue;
            }

            throw UnsupportedSchemaChange(
                $"Unsupported CREATE TABLE option '{_tokens[_position].Raw}'.");
        }
    }

    private IReadOnlyList<SqliteToken> ReadBalancedBody()
    {
        var body = new List<SqliteToken>();
        var depth = 1;
        while (_position < _tokens.Count)
        {
            var token = _tokens[_position++];
            if (token.Text == "(")
            {
                depth++;
                body.Add(token);
                continue;
            }

            if (token.Text == ")")
            {
                depth--;
                if (depth == 0)
                {
                    return body;
                }

                body.Add(token);
                continue;
            }

            body.Add(token);
        }

        throw InvalidDdl("CREATE TABLE has an unterminated column list.");
    }

    private IReadOnlyList<SqliteToken> RemainingTokens()
    {
        var result = _tokens.Skip(_position).ToArray();
        _position = _tokens.Count;
        return result;
    }

    private string ReadQualifiedName(string context)
    {
        var first = ReadName(context);
        if (!MatchSymbol("."))
        {
            return first;
        }

        ReadName(context);
        throw new NotSupportedException(
            $"SQLite migrations do not support non-empty schema name '{first}' in {context}.");
    }

    private string ReadName(string context)
    {
        return ReadName(_tokens, ref _position, context);
    }

    private static string ReadName(
        IReadOnlyList<SqliteToken> tokens,
        ref int position,
        string context)
    {
        if (position >= tokens.Count ||
            (tokens[position].Kind != SqliteTokenKind.Identifier &&
             tokens[position].Kind != SqliteTokenKind.QuotedIdentifier))
        {
            throw InvalidDdl($"{context} requires an identifier.");
        }

        return tokens[position++].Text;
    }

    private bool MatchIfNotExists()
    {
        if (!MatchWord("IF"))
        {
            return false;
        }

        ExpectWord("NOT", "IF NOT EXISTS");
        ExpectWord("EXISTS", "IF NOT EXISTS");
        return true;
    }

    private bool MatchIfExists()
    {
        if (!MatchWord("IF"))
        {
            return false;
        }

        ExpectWord("EXISTS", "IF EXISTS");
        return true;
    }

    private bool MatchWord(string word)
    {
        if (_position < _tokens.Count && _tokens[_position].IsWord(word))
        {
            _position++;
            return true;
        }

        return false;
    }

    private void ExpectWord(string word, string context)
    {
        if (!MatchWord(word))
        {
            throw InvalidDdl($"Expected {word} in {context}.");
        }
    }

    private bool MatchSymbol(string symbol)
    {
        if (_position < _tokens.Count && _tokens[_position].Kind == SqliteTokenKind.Symbol &&
            _tokens[_position].Text == symbol)
        {
            _position++;
            return true;
        }

        return false;
    }

    private void EnsureEnd(string context)
    {
        if (_position != _tokens.Count)
        {
            throw InvalidDdl(
                $"Unexpected token '{_tokens[_position].Raw}' after {context}.");
        }
    }

    private bool IsSchemaNeutral()
    {
        if (_tokens[0].Kind != SqliteTokenKind.Identifier)
        {
            return false;
        }

        var word = _tokens[0].Text;
        return string.Equals(word, "SELECT", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "INSERT", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "UPDATE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "DELETE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "REPLACE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "WITH", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "PRAGMA", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "BEGIN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "COMMIT", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "END", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "ROLLBACK", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "SAVEPOINT", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "RELEASE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "VACUUM", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "ANALYZE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "REINDEX", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "ATTACH", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "DETACH", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(word, "EXPLAIN", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IReadOnlyList<SqliteToken>> SplitTopLevel(
        IReadOnlyList<SqliteToken> tokens,
        string separator)
    {
        var result = new List<IReadOnlyList<SqliteToken>>();
        var current = new List<SqliteToken>();
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.Text == "(")
            {
                depth++;
            }
            else if (token.Text == ")")
            {
                depth--;
                if (depth < 0)
                {
                    throw InvalidDdl("Unexpected ')' in a table definition.");
                }
            }

            if (depth == 0 && token.Text == separator)
            {
                result.Add(current);
                current = new List<SqliteToken>();
            }
            else
            {
                current.Add(token);
            }
        }

        if (depth != 0)
        {
            throw InvalidDdl("An expression in a table definition has unbalanced parentheses.");
        }

        result.Add(current);
        return result;
    }

    private static string RenderType(IEnumerable<SqliteToken> tokens)
    {
        var result = RenderTokens(tokens, true);
        return result.Length == 0 ? "BLOB" : result;
    }

    private static string RenderExpression(IEnumerable<SqliteToken> tokens) =>
        RenderTokens(tokens, false);

    private static string RenderTokens(IEnumerable<SqliteToken> tokens, bool upperCaseWords)
    {
        var builder = new StringBuilder();
        SqliteToken? previous = null;
        foreach (var token in tokens)
        {
            var text = upperCaseWords && token.Kind == SqliteTokenKind.Identifier
                ? token.Text.ToUpperInvariant()
                : token.Raw;
            var noSpaceBefore = text == ")" || text == "," || text == "." ||
                                (previous != null && (previous.Text == "(" || previous.Text == "."));
            var noSpaceAfterPrevious = previous != null && previous.Text == ",";
            if (builder.Length != 0 && !noSpaceBefore && !noSpaceAfterPrevious && text != "(")
            {
                builder.Append(' ');
            }

            builder.Append(text);
            previous = token;
        }

        return builder.ToString().Trim();
    }

    private static MigrationValidationException InvalidDdl(string message) =>
        new MigrationValidationException("SQLite dry-run could not reconstruct schema: " + message);

    private static MigrationValidationException UnsupportedSchemaChange(string message) =>
        new MigrationValidationException("SQLite dry-run rejected a schema-changing statement: " + message);
}

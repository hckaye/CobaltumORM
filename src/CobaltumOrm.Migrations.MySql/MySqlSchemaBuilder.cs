using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CobaltumOrm.Analysis;
using CobaltumOrm.Migrations;

namespace CobaltumOrm.Migrations.MySql;

internal static class MySqlSchemaBuilder
{
    private static readonly HashSet<string> SchemaNeutralStatements = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "ANALYZE",
        "BEGIN",
        "CALL",
        "CHECK",
        "COMMIT",
        "DELETE",
        "DESC",
        "DESCRIBE",
        "DO",
        "EXPLAIN",
        "FLUSH",
        "GRANT",
        "HANDLER",
        "INSERT",
        "KILL",
        "LOAD",
        "LOCK",
        "OPTIMIZE",
        "REPAIR",
        "RESET",
        "REPLACE",
        "REVOKE",
        "ROLLBACK",
        "SELECT",
        "SET",
        "SHOW",
        "START",
        "TRUNCATE",
        "UPDATE",
        "UNLOCK",
        "WITH",
    };

    private static readonly HashSet<string> IgnoredTableItems = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "CHECK",
        "CONSTRAINT",
        "FOREIGN",
        "FULLTEXT",
        "INDEX",
        "KEY",
        "SPATIAL",
        "UNIQUE",
    };

    private static readonly HashSet<string> ColumnConstraintWords = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "AUTO_INCREMENT",
        "CHECK",
        "COLLATE",
        "COMMENT",
        "CONSTRAINT",
        "DEFAULT",
        "GENERATED",
        "KEY",
        "NOT",
        "NULL",
        "ON",
        "PRIMARY",
        "REFERENCES",
        "UNIQUE",
        "VISIBLE",
        "INVISIBLE",
        "COLUMN_FORMAT",
        "STORAGE",
        "SRID",
    };

    private static readonly HashSet<string> IgnoredAlterActions = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "ALGORITHM",
        "DISABLE",
        "ENABLE",
        "FORCE",
        "LOCK",
        "ORDER",
        "REORGANIZE",
        "VALIDATE",
    };

    internal static MigrationSchema Build(IReadOnlyList<MigrationCommand> commands)
    {
        if (commands is null)
        {
            throw new ArgumentNullException(nameof(commands));
        }

        var state = new SchemaState();
        foreach (var command in commands)
        {
            if (command is null)
            {
                throw new MigrationValidationException("The schema preview command collection contains null.");
            }

            foreach (var statement in SplitStatements(command.CommandText))
            {
                ApplyStatement(state, statement);
            }
        }

        return new MigrationSchema(state.Tables.Select(table =>
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

    private static void ApplyStatement(SchemaState state, string sql)
    {
        var parser = new StatementParser(sql);
        if (parser.IsAtEnd)
        {
            return;
        }

        var keyword = parser.PeekWord();
        if (string.Equals(keyword, "CREATE", StringComparison.OrdinalIgnoreCase))
        {
            var target = string.Equals(parser.PeekWord(1), "TEMPORARY", StringComparison.OrdinalIgnoreCase)
                ? parser.PeekWord(2)
                : parser.PeekWord(1);
            if (!string.Equals(target, "TABLE", StringComparison.OrdinalIgnoreCase))
            {
                throw UnsupportedSchemaChange(sql, target ?? "CREATE");
            }

            ApplyCreateTable(state, parser);
            return;
        }

        if (string.Equals(keyword, "ALTER", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(parser.PeekWord(1), "TABLE", StringComparison.OrdinalIgnoreCase))
            {
                throw UnsupportedSchemaChange(sql, parser.PeekWord(1) ?? "ALTER");
            }

            ApplyAlterTable(state, parser);
            return;
        }

        if (string.Equals(keyword, "DROP", StringComparison.OrdinalIgnoreCase))
        {
            var target = string.Equals(parser.PeekWord(1), "TEMPORARY", StringComparison.OrdinalIgnoreCase)
                ? parser.PeekWord(2)
                : parser.PeekWord(1);
            if (!string.Equals(target, "TABLE", StringComparison.OrdinalIgnoreCase))
            {
                throw UnsupportedSchemaChange(sql, target ?? "DROP");
            }

            ApplyDropTable(state, parser);
            return;
        }

        if (string.Equals(keyword, "RENAME", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(parser.PeekWord(1), "TABLE", StringComparison.OrdinalIgnoreCase))
            {
                throw UnsupportedSchemaChange(sql, parser.PeekWord(1) ?? "RENAME");
            }

            ApplyRenameTable(state, parser);
            return;
        }

        if (string.Equals(keyword, "USE", StringComparison.OrdinalIgnoreCase))
        {
            ApplyUse(state, parser);
            return;
        }

        if (keyword is not null && SchemaNeutralStatements.Contains(keyword))
        {
            return;
        }

        throw UnsupportedSchemaChange(sql, keyword ?? "");
    }

    private static void ApplyUse(SchemaState state, StatementParser parser)
    {
        parser.ExpectWord("USE");
        state.DefaultDatabase = parser.ReadIdentifier("Expected a database name after USE.");
        parser.ExpectEnd("USE does not accept additional statements in a schema preview.");
    }

    private static void ApplyCreateTable(SchemaState state, StatementParser parser)
    {
        parser.ExpectWord("CREATE");
        parser.MatchWord("TEMPORARY");
        parser.ExpectWord("TABLE");
        var ifNotExists = parser.MatchIfNotExists();
        var tableName = parser.ReadTableName(state.DefaultDatabase, "CREATE TABLE");
        if (parser.IsAtEnd || !parser.MatchSymbol('('))
        {
            throw InvalidSchemaSql("CREATE TABLE must declare its columns in parentheses.");
        }

        var items = parser.ReadParenthesizedItems();
        var columns = new List<Column>();
        var primaryKeys = new List<IReadOnlyList<string>>();
        foreach (var item in items)
        {
            if (item.Count == 0)
            {
                throw InvalidSchemaSql("CREATE TABLE contains an empty table item.");
            }

            ParseCreateTableItem(item, columns, primaryKeys);
        }

        parser.SkipCreateTableOptions();
        parser.ExpectEnd("CREATE TABLE contains unsupported trailing SQL.");

        var existingIndex = FindTableIndex(state.Tables, tableName);
        if (existingIndex >= 0)
        {
            if (ifNotExists)
            {
                return;
            }

            throw InvalidSchemaSql($"Table '{tableName.DisplayName}' already exists.");
        }

        if (columns.Count == 0)
        {
            throw InvalidSchemaSql("CREATE TABLE must declare at least one column.");
        }

        if (primaryKeys.Count > 1)
        {
            throw InvalidSchemaSql("A table can have only one PRIMARY KEY constraint.");
        }

        if (primaryKeys.Count == 1)
        {
            MarkPrimaryKey(columns, primaryKeys[0], tableName.DisplayName);
        }

        state.Tables.Add(new Table(tableName.Name, columns, tableName.Schema));
    }

    private static void ParseCreateTableItem(
        IReadOnlyList<Token> item,
        List<Column> columns,
        List<IReadOnlyList<string>> primaryKeys)
    {
        var first = Word(item, 0);
        if (string.Equals(first, "PRIMARY", StringComparison.OrdinalIgnoreCase))
        {
            primaryKeys.Add(ParseConstraintColumns(item, "PRIMARY KEY"));
            return;
        }

        if (string.Equals(first, "CONSTRAINT", StringComparison.OrdinalIgnoreCase))
        {
            var primaryIndex = IndexOfWord(item, "PRIMARY");
            if (primaryIndex >= 0)
            {
                primaryKeys.Add(ParseConstraintColumns(item.Skip(primaryIndex).ToArray(), "PRIMARY KEY"));
            }

            return;
        }

        if (first is not null && IgnoredTableItems.Contains(first))
        {
            return;
        }

        var column = ParseColumnDefinition(item);
        if (FindColumnIndex(columns, column.Name) >= 0)
        {
            throw InvalidSchemaSql($"Column '{column.Name}' is defined more than once.");
        }

        if (column.IsPrimaryKey)
        {
            primaryKeys.Add(new[] { column.Name });
        }

        columns.Add(column);
    }

    private static IReadOnlyList<string> ParseConstraintColumns(
        IReadOnlyList<Token> tokens,
        string constraintName)
    {
        var openIndex = IndexOfSymbol(tokens, '(');
        if (openIndex < 0)
        {
            throw InvalidSchemaSql($"{constraintName} must list its columns in parentheses.");
        }

        var groups = SplitTokenGroups(tokens, openIndex + 1, tokens.Count - 1, ')');
        var names = new List<string>();
        foreach (var group in groups)
        {
            if (group.Count == 0 || !IsIdentifier(group[0]))
            {
                throw InvalidSchemaSql($"{constraintName} contains an invalid column name.");
            }

            names.Add(group[0].Value);
        }

        if (names.Count == 0)
        {
            throw InvalidSchemaSql($"{constraintName} must contain at least one column.");
        }

        return names;
    }

    private static void ApplyAlterTable(SchemaState state, StatementParser parser)
    {
        parser.ExpectWord("ALTER");
        parser.ExpectWord("TABLE");
        var ifExists = parser.MatchIfExists();
        var tableName = parser.ReadTableName(state.DefaultDatabase, "ALTER TABLE");
        var tableIndex = FindTableIndex(state.Tables, tableName);
        if (tableIndex < 0)
        {
            if (ifExists)
            {
                return;
            }

            throw InvalidSchemaSql($"Cannot alter unknown table '{tableName.DisplayName}'.");
        }

        var actionTokens = parser.ReadRemainingTokens();
        var actionGroups = SplitTokenGroups(actionTokens, 0, actionTokens.Count, null);
        if (actionGroups.Count == 0)
        {
            throw InvalidSchemaSql("ALTER TABLE requires a supported table change.");
        }

        foreach (var action in actionGroups)
        {
            ApplyAlterAction(state, tableIndex, tableName, action);
        }
    }

    private static void ApplyAlterAction(
        SchemaState state,
        int tableIndex,
        TableName tableName,
        IReadOnlyList<Token> action)
    {
        if (action.Count == 0)
        {
            throw InvalidSchemaSql("ALTER TABLE contains an empty action.");
        }

        var first = Word(action, 0);
        if (first is not null && IgnoredAlterActions.Contains(first))
        {
            return;
        }

        if (string.Equals(first, "ADD", StringComparison.OrdinalIgnoreCase))
        {
            ApplyAddAction(state, tableIndex, tableName, action);
            return;
        }

        if (string.Equals(first, "DROP", StringComparison.OrdinalIgnoreCase))
        {
            ApplyDropAction(state, tableIndex, tableName, action);
            return;
        }

        if (string.Equals(first, "MODIFY", StringComparison.OrdinalIgnoreCase))
        {
            ApplyModifyAction(state, tableIndex, tableName, action);
            return;
        }

        if (string.Equals(first, "CHANGE", StringComparison.OrdinalIgnoreCase))
        {
            ApplyChangeAction(state, tableIndex, tableName, action);
            return;
        }

        if (string.Equals(first, "RENAME", StringComparison.OrdinalIgnoreCase))
        {
            ApplyAlterRenameAction(state, tableIndex, tableName, action);
            return;
        }

        if (string.Equals(first, "ALTER", StringComparison.OrdinalIgnoreCase))
        {
            ApplyAlterColumnAction(state, tableIndex, tableName, action);
            return;
        }

        throw UnsupportedSchemaChange(
            JoinTokens(action, preserveCase: true),
            first ?? "ALTER TABLE action");
    }

    private static void ApplyAddAction(
        SchemaState state,
        int tableIndex,
        TableName tableName,
        IReadOnlyList<Token> action)
    {
        var index = 1;
        if (string.Equals(Word(action, index), "COLUMN", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        var ifNotExists = MatchIfNotExists(action, ref index);
        if (index >= action.Count)
        {
            throw InvalidSchemaSql("ALTER TABLE ADD requires a column or constraint.");
        }

        var next = Word(action, index);
        if (string.Equals(next, "PRIMARY", StringComparison.OrdinalIgnoreCase))
        {
            var names = ParseConstraintColumns(action.Skip(index).ToArray(), "PRIMARY KEY");
            var currentTable = GetTable(state, tableIndex);
            if (currentTable.Columns.Any(column => column.IsPrimaryKey &&
                                                   !names.Any(name => string.Equals(
                                                       name,
                                                       column.Name,
                                                       StringComparison.OrdinalIgnoreCase))))
            {
                throw InvalidSchemaSql(
                    $"Table '{tableName.DisplayName}' already has a different PRIMARY KEY.");
            }

            SetTableColumns(
                state,
                tableIndex,
                MarkedPrimaryKeyColumns(currentTable, names));
            return;
        }

        if (string.Equals(next, "CONSTRAINT", StringComparison.OrdinalIgnoreCase))
        {
            var primaryIndex = IndexOfWord(action, "PRIMARY", index);
            if (primaryIndex >= 0)
            {
                var names = ParseConstraintColumns(action.Skip(primaryIndex).ToArray(), "PRIMARY KEY");
                var currentTable = GetTable(state, tableIndex);
                if (currentTable.Columns.Any(column => column.IsPrimaryKey &&
                                                       !names.Any(name => string.Equals(
                                                           name,
                                                           column.Name,
                                                           StringComparison.OrdinalIgnoreCase))))
                {
                    throw InvalidSchemaSql(
                        $"Table '{tableName.DisplayName}' already has a different PRIMARY KEY.");
                }

                SetTableColumns(state, tableIndex, MarkedPrimaryKeyColumns(currentTable, names));
                return;
            }

            return;
        }

        if (next is not null &&
            string.Equals(next, "GENERATED", StringComparison.OrdinalIgnoreCase))
        {
            throw UnsupportedSchemaChange(JoinTokens(action, preserveCase: true), "GENERATED");
        }

        if (next is not null &&
            (string.Equals(next, "UNIQUE", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(next, "INDEX", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(next, "KEY", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(next, "FULLTEXT", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(next, "SPATIAL", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(next, "FOREIGN", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var columnTokens = action.Skip(index).ToArray();
        var position = ReadColumnPosition(columnTokens);
        var column = ParseColumnDefinition(columnTokens);
        var table = GetTable(state, tableIndex);
        if (FindColumnIndex(table.Columns, column.Name) >= 0)
        {
            if (ifNotExists)
            {
                return;
            }

            throw InvalidSchemaSql(
                $"Column '{column.Name}' already exists on table '{tableName.DisplayName}'.");
        }

        if (column.IsPrimaryKey && table.Columns.Any(existing => existing.IsPrimaryKey))
        {
            throw InvalidSchemaSql($"Table '{tableName.DisplayName}' already has a PRIMARY KEY.");
        }

        var columns = table.Columns.ToList();
        InsertColumn(columns, column, position, tableName.DisplayName);
        SetTableColumns(state, tableIndex, columns);
    }

    private static void ApplyDropAction(
        SchemaState state,
        int tableIndex,
        TableName tableName,
        IReadOnlyList<Token> action)
    {
        var index = 1;
        if (string.Equals(Word(action, index), "COLUMN", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        var ifExists = MatchIfExists(action, ref index);
        var name = index < action.Count && IsIdentifier(action[index])
            ? action[index].Value
            : string.Empty;
        if (string.Equals(name, "PRIMARY", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Word(action, index + 1), "KEY", StringComparison.OrdinalIgnoreCase))
        {
            var table = GetTable(state, tableIndex);
            SetTableColumns(
                state,
                tableIndex,
                table.Columns.Select(column =>
                    new Column(
                        column.Name,
                        column.SqlType,
                        column.IsNullable,
                        false,
                        column.DefaultExpression,
                        column.IsIdentity)).ToArray());
            return;
        }

        if (string.Equals(name, "INDEX", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "KEY", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "FOREIGN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "CONSTRAINT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (name is null || index + 1 != action.Count)
        {
            throw InvalidSchemaSql("ALTER TABLE DROP COLUMN requires one column name.");
        }

        var tableForColumn = GetTable(state, tableIndex);
        var columnIndex = FindColumnIndex(tableForColumn.Columns, name);
        if (columnIndex < 0)
        {
            if (ifExists)
            {
                return;
            }

            throw InvalidSchemaSql(
                $"Cannot drop unknown column '{name}' from table '{tableName.DisplayName}'.");
        }

        SetTableColumns(
            state,
            tableIndex,
            tableForColumn.Columns.Where((_, current) => current != columnIndex).ToArray());
    }

    private static void ApplyModifyAction(
        SchemaState state,
        int tableIndex,
        TableName tableName,
        IReadOnlyList<Token> action)
    {
        var index = 1;
        if (string.Equals(Word(action, index), "COLUMN", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        if (index >= action.Count)
        {
            throw InvalidSchemaSql("ALTER TABLE MODIFY requires a complete column definition.");
        }

        var definitionTokens = action.Skip(index).ToArray();
        var position = ReadColumnPosition(definitionTokens);
        var specification = ParseColumnDefinition(definitionTokens);
        var table = GetTable(state, tableIndex);
        var columnIndex = FindColumnIndex(table.Columns, specification.Name);
        if (columnIndex < 0)
        {
            throw InvalidSchemaSql(
                $"Cannot modify unknown column '{specification.Name}' on table '{tableName.DisplayName}'.");
        }

        var oldColumn = table.Columns[columnIndex];
        var replacement = new Column(
            specification.Name,
            specification.SqlType,
            oldColumn.IsPrimaryKey ? false : specification.IsNullable,
            oldColumn.IsPrimaryKey || specification.IsPrimaryKey,
            specification.DefaultExpression,
            specification.IsIdentity);
        var columns = table.Columns.ToList();
        columns.RemoveAt(columnIndex);
        InsertColumn(columns, replacement, position, tableName.DisplayName, columnIndex);
        SetTableColumns(state, tableIndex, columns);
    }

    private static void ApplyChangeAction(
        SchemaState state,
        int tableIndex,
        TableName tableName,
        IReadOnlyList<Token> action)
    {
        var index = 1;
        if (string.Equals(Word(action, index), "COLUMN", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        if (index + 1 >= action.Count ||
            !IsIdentifier(action[index]) ||
            !IsIdentifier(action[index + 1]))
        {
            throw InvalidSchemaSql("ALTER TABLE CHANGE requires an old name, a new name, and a complete definition.");
        }

        var oldName = action[index].Value;
        index++;
        var newName = action[index].Value;
        index++;
        var definitionTokens = action.Skip(index - 1).ToArray();
        var position = ReadColumnPosition(definitionTokens);
        var definition = ParseColumnDefinition(definitionTokens);
        if (!string.Equals(definition.Name, newName, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidSchemaSql("ALTER TABLE CHANGE contains inconsistent column names.");
        }

        var table = GetTable(state, tableIndex);
        var oldIndex = FindColumnIndex(table.Columns, oldName);
        if (oldIndex < 0)
        {
            throw InvalidSchemaSql(
                $"Cannot change unknown column '{oldName}' on table '{tableName.DisplayName}'.");
        }

        var duplicateIndex = FindColumnIndex(table.Columns, newName);
        if (duplicateIndex >= 0 && duplicateIndex != oldIndex)
        {
            throw InvalidSchemaSql(
                $"Column '{newName}' already exists on table '{tableName.DisplayName}'.");
        }

        var oldColumn = table.Columns[oldIndex];
        var replacement = new Column(
            newName,
            definition.SqlType,
            oldColumn.IsPrimaryKey ? false : definition.IsNullable,
            oldColumn.IsPrimaryKey || definition.IsPrimaryKey,
            definition.DefaultExpression,
            definition.IsIdentity);
        var columns = table.Columns.ToList();
        columns.RemoveAt(oldIndex);
        InsertColumn(columns, replacement, position, tableName.DisplayName, oldIndex);
        SetTableColumns(state, tableIndex, columns);
    }

    private static void ApplyAlterRenameAction(
        SchemaState state,
        int tableIndex,
        TableName tableName,
        IReadOnlyList<Token> action)
    {
        var index = 1;
        if (string.Equals(Word(action, index), "COLUMN", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            if (index + 2 >= action.Count ||
                !IsIdentifier(action[index]) ||
                !string.Equals(Word(action, index + 1), "TO", StringComparison.OrdinalIgnoreCase) ||
                !IsIdentifier(action[index + 2]))
            {
                throw InvalidSchemaSql("ALTER TABLE RENAME COLUMN requires old and new names.");
            }

            RenameColumn(state, tableIndex, tableName, action[index].Value, action[index + 2].Value);
            return;
        }

        if (string.Equals(Word(action, index), "TO", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            var newName = ParseTableName(action, ref index, state.DefaultDatabase);
            EnsureEnd(action, index);
            RenameTable(state, tableIndex, tableName, newName);
            return;
        }

        throw UnsupportedSchemaChange(JoinTokens(action, preserveCase: true), "RENAME");
    }

    private static void ApplyAlterColumnAction(
        SchemaState state,
        int tableIndex,
        TableName tableName,
        IReadOnlyList<Token> action)
    {
        var index = 1;
        if (string.Equals(Word(action, index), "COLUMN", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        if (index >= action.Count || !IsIdentifier(action[index]))
        {
            throw InvalidSchemaSql("ALTER TABLE ALTER COLUMN requires a column name.");
        }

        var columnName = action[index].Value;
        index++;
        var table = GetTable(state, tableIndex);
        var columnIndex = FindColumnIndex(table.Columns, columnName);
        if (columnIndex < 0)
        {
            throw InvalidSchemaSql(
                $"Cannot alter unknown column '{columnName}' on table '{tableName.DisplayName}'.");
        }

        var operation = Word(action, index);
        if (string.Equals(operation, "SET", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Word(action, index + 1), "DEFAULT", StringComparison.OrdinalIgnoreCase))
        {
            var defaultTokens = action.Skip(index + 2).ToArray();
            if (defaultTokens.Length == 0)
            {
                throw InvalidSchemaSql("ALTER TABLE ALTER COLUMN SET DEFAULT requires an expression.");
            }

            ReplaceDefault(state, tableIndex, columnIndex, JoinTokens(defaultTokens, preserveCase: true));
            return;
        }

        if (string.Equals(operation, "DROP", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Word(action, index + 1), "DEFAULT", StringComparison.OrdinalIgnoreCase) &&
            index + 2 == action.Count)
        {
            ReplaceDefault(state, tableIndex, columnIndex, null);
            return;
        }

        throw UnsupportedSchemaChange(JoinTokens(action, preserveCase: true), "ALTER COLUMN");
    }

    private static void ApplyDropTable(SchemaState state, StatementParser parser)
    {
        parser.ExpectWord("DROP");
        parser.MatchWord("TEMPORARY");
        parser.ExpectWord("TABLE");
        var ifExists = parser.MatchIfExists();
        var names = parser.ReadRemainingTokenGroups();
        if (names.Count == 0)
        {
            throw InvalidSchemaSql("DROP TABLE requires a table name.");
        }

        foreach (var tokens in names)
        {
            var index = 0;
            var tableName = ParseTableName(tokens, ref index, state.DefaultDatabase);
            EnsureEnd(tokens, index);
            var tableIndex = FindTableIndex(state.Tables, tableName);
            if (tableIndex < 0)
            {
                if (ifExists)
                {
                    continue;
                }

                throw InvalidSchemaSql($"Cannot drop unknown table '{tableName.DisplayName}'.");
            }

            state.Tables.RemoveAt(tableIndex);
        }
    }

    private static void ApplyRenameTable(SchemaState state, StatementParser parser)
    {
        parser.ExpectWord("RENAME");
        parser.ExpectWord("TABLE");
        var pairs = parser.ReadRemainingTokenGroups();
        if (pairs.Count == 0)
        {
            throw InvalidSchemaSql("RENAME TABLE requires a source and destination table.");
        }

        foreach (var pair in pairs)
        {
            var index = 0;
            var oldName = ParseTableName(pair, ref index, state.DefaultDatabase);
            if (!string.Equals(Word(pair, index), "TO", StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidSchemaSql("RENAME TABLE requires TO between table names.");
            }

            index++;
            var newName = ParseTableName(pair, ref index, state.DefaultDatabase);
            EnsureEnd(pair, index);
            var oldIndex = FindTableIndex(state.Tables, oldName);
            if (oldIndex < 0)
            {
                throw InvalidSchemaSql($"Cannot rename unknown table '{oldName.DisplayName}'.");
            }

            RenameTable(state, oldIndex, oldName, newName);
        }
    }

    private static void RenameTable(
        SchemaState state,
        int tableIndex,
        TableName oldName,
        TableName requestedName)
    {
        var newName = requestedName.Schema is null
            ? new TableName(oldName.Schema, requestedName.Name)
            : requestedName;
        var existing = FindTableIndex(state.Tables, newName);
        if (existing >= 0 && existing != tableIndex)
        {
            throw InvalidSchemaSql($"Table '{newName.DisplayName}' already exists.");
        }

        var table = state.Tables[tableIndex];
        state.Tables[tableIndex] = new Table(newName.Name, table.Columns, newName.Schema);
    }

    private static void RenameColumn(
        SchemaState state,
        int tableIndex,
        TableName tableName,
        string oldName,
        string newName)
    {
        var table = GetTable(state, tableIndex);
        var oldIndex = FindColumnIndex(table.Columns, oldName);
        if (oldIndex < 0)
        {
            throw InvalidSchemaSql(
                $"Cannot rename unknown column '{oldName}' on table '{tableName.DisplayName}'.");
        }

        var duplicateIndex = FindColumnIndex(table.Columns, newName);
        if (duplicateIndex >= 0 && duplicateIndex != oldIndex)
        {
            throw InvalidSchemaSql(
                $"Column '{newName}' already exists on table '{tableName.DisplayName}'.");
        }

        var oldColumn = table.Columns[oldIndex];
        var columns = table.Columns.ToList();
        columns[oldIndex] = new Column(
            newName,
            oldColumn.SqlType,
            oldColumn.IsNullable,
            oldColumn.IsPrimaryKey,
            oldColumn.DefaultExpression,
            oldColumn.IsIdentity);
        SetTableColumns(state, tableIndex, columns);
    }

    private static void ReplaceDefault(
        SchemaState state,
        int tableIndex,
        int columnIndex,
        string? defaultExpression)
    {
        var table = GetTable(state, tableIndex);
        var oldColumn = table.Columns[columnIndex];
        var columns = table.Columns.ToList();
        columns[columnIndex] = new Column(
            oldColumn.Name,
            oldColumn.SqlType,
            oldColumn.IsNullable,
            oldColumn.IsPrimaryKey,
            defaultExpression,
            oldColumn.IsIdentity);
        SetTableColumns(state, tableIndex, columns);
    }

    private static IReadOnlyList<Column> MarkedPrimaryKeyColumns(Table table, IReadOnlyList<string> names)
    {
        var columns = table.Columns.ToList();
        MarkPrimaryKey(columns, names, table.Schema is null ? table.Name : table.Schema + "." + table.Name);
        return columns;
    }

    private static void MarkPrimaryKey(
        List<Column> columns,
        IReadOnlyList<string> names,
        string tableDisplayName)
    {
        if (names.Count == 0)
        {
            throw InvalidSchemaSql("PRIMARY KEY must contain at least one column.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (!seen.Add(name))
            {
                throw InvalidSchemaSql(
                    $"PRIMARY KEY cannot list column '{name}' more than once on table '{tableDisplayName}'.");
            }
        }

        foreach (var name in names)
        {
            var index = FindColumnIndex(columns, name);
            if (index < 0)
            {
                throw InvalidSchemaSql($"PRIMARY KEY refers to unknown column '{name}' on table '{tableDisplayName}'.");
            }

            var oldColumn = columns[index];
            columns[index] = new Column(
                oldColumn.Name,
                oldColumn.SqlType,
                false,
                true,
                oldColumn.DefaultExpression,
                oldColumn.IsIdentity);
        }
    }

    private static Column ParseColumnDefinition(IReadOnlyList<Token> originalTokens)
    {
        var tokens = TrimColumnPosition(originalTokens);
        if (tokens.Count < 2 || !IsIdentifier(tokens[0]))
        {
            throw InvalidSchemaSql("A column definition requires a name and a type.");
        }

        var typeEnd = FindTypeEnd(tokens);
        if (typeEnd <= 1)
        {
            throw InvalidSchemaSql($"Column '{tokens[0].Value}' must declare a type.");
        }

        var sqlType = NormalizeType(tokens, 1, typeEnd);
        ValidateType(sqlType, tokens[0].Value);

        var isNullable = true;
        var isPrimaryKey = false;
        var isIdentity = false;
        string? defaultExpression = null;
        var index = typeEnd;
        while (index < tokens.Count)
        {
            var word = Word(tokens, index);
            if (string.Equals(word, "NULL", StringComparison.OrdinalIgnoreCase))
            {
                isNullable = true;
                index++;
                continue;
            }

            if (string.Equals(word, "NOT", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(Word(tokens, index + 1), "NULL", StringComparison.OrdinalIgnoreCase))
                {
                    throw InvalidSchemaSql($"Column '{tokens[0].Value}' has an invalid NOT constraint.");
                }

                isNullable = false;
                index += 2;
                continue;
            }

            if (string.Equals(word, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(Word(tokens, index + 1), "KEY", StringComparison.OrdinalIgnoreCase))
                {
                    throw InvalidSchemaSql($"Column '{tokens[0].Value}' has an invalid PRIMARY constraint.");
                }

                isPrimaryKey = true;
                isNullable = false;
                index += 2;
                continue;
            }

            if (string.Equals(word, "AUTO_INCREMENT", StringComparison.OrdinalIgnoreCase))
            {
                isIdentity = true;
                index++;
                continue;
            }

            if (string.Equals(word, "DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                var defaultStart = index + 1;
                var defaultEnd = FindNextColumnConstraint(tokens, defaultStart);
                if (defaultEnd == defaultStart)
                {
                    throw InvalidSchemaSql($"Column '{tokens[0].Value}' has an empty DEFAULT expression.");
                }

                defaultExpression = JoinTokens(tokens, defaultStart, defaultEnd, preserveCase: true);
                index = defaultEnd;
                continue;
            }

            if (string.Equals(word, "CONSTRAINT", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                if (index < tokens.Count && IsIdentifier(tokens[index]))
                {
                    index++;
                }

                if (string.Equals(Word(tokens, index), "PRIMARY", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Word(tokens, index + 1), "KEY", StringComparison.OrdinalIgnoreCase))
                {
                    isPrimaryKey = true;
                    isNullable = false;
                    index += 2;
                    continue;
                }

                throw InvalidSchemaSql(
                    $"Column '{tokens[0].Value}' contains an unsupported CONSTRAINT definition.");
            }

            if (string.Equals(word, "UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(word, "KEY", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                if (index < tokens.Count && IsIdentifier(tokens[index]))
                {
                    index++;
                }

                continue;
            }

            if (string.Equals(word, "REFERENCES", StringComparison.OrdinalIgnoreCase))
            {
                index = tokens.Count;
                continue;
            }

            if (string.Equals(word, "CHECK", StringComparison.OrdinalIgnoreCase))
            {
                index = SkipBalancedClause(tokens, index + 1);
                continue;
            }

            if (string.Equals(word, "COLLATE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(word, "COMMENT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(word, "SRID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(word, "COLUMN_FORMAT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(word, "STORAGE", StringComparison.OrdinalIgnoreCase))
            {
                index += 2;
                continue;
            }

            if (string.Equals(word, "ON", StringComparison.OrdinalIgnoreCase))
            {
                index = FindNextColumnConstraint(tokens, index + 1);
                continue;
            }

            if (string.Equals(word, "VISIBLE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(word, "INVISIBLE", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (string.Equals(word, "GENERATED", StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidSchemaSql(
                    $"Generated column '{tokens[0].Value}' is not supported by the schema preview.");
            }

            throw UnsupportedSchemaChange(
                JoinTokens(tokens, preserveCase: true),
                word ?? "column constraint");
        }

        if (isIdentity && !IsIntegerType(sqlType))
        {
            throw InvalidSchemaSql(
                $"AUTO_INCREMENT column '{tokens[0].Value}' must use an integer type.");
        }

        if (string.Equals(sqlType, "serial", StringComparison.OrdinalIgnoreCase))
        {
            isIdentity = true;
        }

        return new Column(tokens[0].Value, sqlType, isNullable, isPrimaryKey, defaultExpression, isIdentity);
    }

    private static ColumnPosition ReadColumnPosition(IReadOnlyList<Token> tokens)
    {
        var depth = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.IsSymbol('('))
            {
                depth++;
                continue;
            }

            if (token.IsSymbol(')'))
            {
                depth--;
                continue;
            }

            if (depth != 0)
            {
                continue;
            }

            if (string.Equals(Word(token), "FIRST", StringComparison.OrdinalIgnoreCase))
            {
                return new ColumnPosition(true, true, null);
            }

            if (string.Equals(Word(token), "AFTER", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= tokens.Count || !IsIdentifier(tokens[index + 1]))
                {
                    throw InvalidSchemaSql("AFTER must be followed by an existing column name.");
                }

                return new ColumnPosition(true, false, tokens[index + 1].Value);
            }
        }

        return new ColumnPosition(false, false, null);
    }

    private static void InsertColumn(
        List<Column> columns,
        Column column,
        ColumnPosition position,
        string tableDisplayName,
        int? originalIndex = null)
    {
        if (!position.IsSpecified)
        {
            if (originalIndex.HasValue)
            {
                columns.Insert(Math.Min(originalIndex.Value, columns.Count), column);
            }
            else
            {
                columns.Add(column);
            }

            return;
        }

        if (position.IsFirst)
        {
            columns.Insert(0, column);
            return;
        }

        var afterIndex = FindColumnIndex(columns, position.AfterName!);
        if (afterIndex < 0)
        {
            throw InvalidSchemaSql(
                $"AFTER refers to unknown column '{position.AfterName}' on table '{tableDisplayName}'.");
        }

        columns.Insert(afterIndex + 1, column);
    }

    private static IReadOnlyList<Token> TrimColumnPosition(IReadOnlyList<Token> tokens)
    {
        var depth = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.IsSymbol('('))
            {
                depth++;
            }
            else if (token.IsSymbol(')'))
            {
                depth--;
            }
            else if (depth == 0 &&
                     (string.Equals(token.Value, "FIRST", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(token.Value, "AFTER", StringComparison.OrdinalIgnoreCase)))
            {
                return tokens.Take(index).ToArray();
            }
        }

        return tokens;
    }

    private static int FindTypeEnd(IReadOnlyList<Token> tokens)
    {
        var depth = 0;
        for (var index = 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.IsSymbol('('))
            {
                depth++;
                continue;
            }

            if (token.IsSymbol(')'))
            {
                depth--;
                if (depth < 0)
                {
                    throw InvalidSchemaSql("A column type contains an unmatched closing parenthesis.");
                }

                continue;
            }

            if (depth == 0 && index > 1 && Word(token) is string word && ColumnConstraintWords.Contains(word))
            {
                return index;
            }
        }

        if (depth != 0)
        {
            throw InvalidSchemaSql("A column type contains an unmatched opening parenthesis.");
        }

        return tokens.Count;
    }

    private static int FindNextColumnConstraint(IReadOnlyList<Token> tokens, int start)
    {
        var depth = 0;
        for (var index = start; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.IsSymbol('('))
            {
                depth++;
                continue;
            }

            if (token.IsSymbol(')'))
            {
                depth--;
                continue;
            }

            if (depth == 0 && Word(token) is string word && ColumnConstraintWords.Contains(word))
            {
                return index;
            }
        }

        return tokens.Count;
    }

    private static int SkipBalancedClause(IReadOnlyList<Token> tokens, int start)
    {
        var openIndex = start < tokens.Count && tokens[start].IsSymbol('(') ? start : -1;
        if (openIndex < 0)
        {
            return FindNextColumnConstraint(tokens, start);
        }

        var depth = 0;
        for (var index = openIndex; index < tokens.Count; index++)
        {
            if (tokens[index].IsSymbol('('))
            {
                depth++;
            }
            else if (tokens[index].IsSymbol(')'))
            {
                depth--;
                if (depth == 0)
                {
                    return index + 1;
                }
            }
        }

        throw InvalidSchemaSql("A CHECK expression contains an unmatched parenthesis.");
    }

    private static string NormalizeType(IReadOnlyList<Token> tokens, int start, int end)
    {
        var text = JoinTokens(tokens, start, end, preserveCase: false);
        var lowerText = text.ToLowerInvariant();
        text = lowerText.Replace("character varying", "varchar")
            .Replace("double precision", "double")
            .Replace("numeric", "decimal")
            .Replace("integer", "int");
        if (string.Equals(text, "boolean", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "bool", StringComparison.OrdinalIgnoreCase))
        {
            return "tinyint(1)";
        }

        return text.ToLowerInvariant();
    }

    private static void ValidateType(string sqlType, string columnName)
    {
        var firstPart = sqlType;
        var parenthesis = firstPart.IndexOf('(');
        if (parenthesis >= 0)
        {
            firstPart = firstPart.Substring(0, parenthesis);
        }

        var space = firstPart.IndexOf(' ');
        if (space >= 0)
        {
            firstPart = firstPart.Substring(0, space);
        }

        switch (firstPart.ToLowerInvariant())
        {
            case "bigint":
            case "binary":
            case "bit":
            case "blob":
            case "char":
            case "date":
            case "datetime":
            case "decimal":
            case "double":
            case "enum":
            case "float":
            case "int":
            case "json":
            case "longblob":
            case "longtext":
            case "mediumblob":
            case "mediumint":
            case "mediumtext":
            case "numeric":
            case "real":
            case "serial":
            case "set":
            case "smallint":
            case "text":
            case "time":
            case "timestamp":
            case "tinyblob":
            case "tinyint":
            case "tinytext":
            case "varbinary":
            case "varchar":
            case "year":
            case "uuid":
                return;
            default:
                throw InvalidSchemaSql($"Unsupported MySQL column type '{sqlType}' on column '{columnName}'.");
        }
    }

    private static bool IsIntegerType(string sqlType)
    {
        var firstPart = sqlType;
        var parenthesis = firstPart.IndexOf('(');
        if (parenthesis >= 0)
        {
            firstPart = firstPart.Substring(0, parenthesis);
        }

        var space = firstPart.IndexOf(' ');
        if (space >= 0)
        {
            firstPart = firstPart.Substring(0, space);
        }

        switch (firstPart)
        {
            case "tinyint":
            case "smallint":
            case "mediumint":
            case "int":
            case "bigint":
            case "serial":
                return true;
            default:
                return false;
        }
    }

    private static int FindTableIndex(IReadOnlyList<Table> tables, TableName tableName)
    {
        for (var index = 0; index < tables.Count; index++)
        {
            if (string.Equals(tables[index].Name, tableName.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tables[index].Schema, tableName.Schema, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindColumnIndex(IReadOnlyList<Column> columns, string name)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (string.Equals(columns[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static Table GetTable(SchemaState state, int index)
    {
        if (index < 0 || index >= state.Tables.Count)
        {
            throw InvalidSchemaSql("The schema preview table index is invalid.");
        }

        return state.Tables[index];
    }

    private static void SetTableColumns(
        SchemaState state,
        int tableIndex,
        IEnumerable<Column> columns)
    {
        var table = GetTable(state, tableIndex);
        state.Tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private static string Word(IReadOnlyList<Token> tokens, int index)
    {
        return index >= 0 && index < tokens.Count ? Word(tokens[index]) : string.Empty;
    }

    private static string Word(Token token)
    {
        return token.Kind == TokenKind.Word ? token.Value : string.Empty;
    }

    private static int IndexOfWord(IReadOnlyList<Token> tokens, string word, int start = 0)
    {
        for (var index = start; index < tokens.Count; index++)
        {
            if (string.Equals(Word(tokens, index), word, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfSymbol(IReadOnlyList<Token> tokens, char symbol)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].IsSymbol(symbol))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsIdentifier(Token token) =>
        token.Kind == TokenKind.Word || token.Kind == TokenKind.QuotedIdentifier;

    private static IReadOnlyList<List<Token>> SplitTokenGroups(
        IReadOnlyList<Token> tokens,
        int start,
        int end,
        char? closingSymbol)
    {
        var groups = new List<List<Token>>();
        var current = new List<Token>();
        var depth = 0;
        var limit = Math.Min(end, tokens.Count);
        for (var index = start; index < limit; index++)
        {
            var token = tokens[index];
            if (closingSymbol.HasValue && depth == 0 && token.IsSymbol(closingSymbol.Value))
            {
                break;
            }

            if (token.IsSymbol('('))
            {
                depth++;
                current.Add(token);
                continue;
            }

            if (token.IsSymbol(')'))
            {
                depth--;
                if (depth < 0)
                {
                    throw InvalidSchemaSql("A statement contains an unmatched closing parenthesis.");
                }

                current.Add(token);
                continue;
            }

            if (token.IsSymbol(',') && depth == 0)
            {
                groups.Add(current);
                current = new List<Token>();
                continue;
            }

            current.Add(token);
        }

        if (depth != 0)
        {
            throw InvalidSchemaSql("A statement contains an unmatched opening parenthesis.");
        }

        if (current.Count != 0 || groups.Count != 0)
        {
            groups.Add(current);
        }

        return groups;
    }

    private static string JoinTokens(IReadOnlyList<Token> tokens, bool preserveCase) =>
        JoinTokens(tokens, 0, tokens.Count, preserveCase);

    private static string JoinTokens(
        IReadOnlyList<Token> tokens,
        int start,
        int end,
        bool preserveCase)
    {
        var builder = new StringBuilder();
        Token? previous = null;
        var limit = Math.Min(end, tokens.Count);
        for (var index = start; index < limit; index++)
        {
            var token = tokens[index];
            var value = token.Kind == TokenKind.Word && !preserveCase
                ? token.Value.ToLowerInvariant()
                : token.Raw;
            if (builder.Length != 0 && NeedsSpace(previous!, token))
            {
                builder.Append(' ');
            }

            builder.Append(value);
            previous = token;
        }

        return builder.ToString();
    }

    private static bool NeedsSpace(Token previous, Token current)
    {
        if (current.IsSymbol(')') || current.IsSymbol(',') || current.IsSymbol('.'))
        {
            return false;
        }

        if (previous.IsSymbol('(') || previous.IsSymbol('.') || previous.IsSymbol(','))
        {
            return false;
        }

        if (current.IsSymbol('('))
        {
            return false;
        }

        return true;
    }

    private static bool MatchIfNotExists(IReadOnlyList<Token> tokens, ref int index)
    {
        if (string.Equals(Word(tokens, index), "IF", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Word(tokens, index + 1), "NOT", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Word(tokens, index + 2), "EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            index += 3;
            return true;
        }

        return false;
    }

    private static bool MatchIfExists(IReadOnlyList<Token> tokens, ref int index)
    {
        if (string.Equals(Word(tokens, index), "IF", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Word(tokens, index + 1), "EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            index += 2;
            return true;
        }

        return false;
    }

    private static TableName ParseTableName(
        IReadOnlyList<Token> tokens,
        ref int index,
        string? defaultSchema)
    {
        if (index >= tokens.Count || !IsIdentifier(tokens[index]))
        {
            throw InvalidSchemaSql("Expected a database or table name.");
        }

        var first = tokens[index].Value;
        index++;
        if (index < tokens.Count && tokens[index].IsSymbol('.'))
        {
            index++;
            if (index >= tokens.Count || !IsIdentifier(tokens[index]))
            {
                throw InvalidSchemaSql("A qualified table name must contain a table name after '.'.");
            }

            var second = tokens[index].Value;
            index++;
            if (index < tokens.Count && tokens[index].IsSymbol('.'))
            {
                throw InvalidSchemaSql("MySQL schema preview accepts database.table qualification only.");
            }

            return new TableName(first, second);
        }

        return new TableName(defaultSchema, first);
    }

    private static void EnsureEnd(IReadOnlyList<Token> tokens, int index)
    {
        if (index != tokens.Count)
        {
            throw InvalidSchemaSql("The statement contains unsupported trailing SQL.");
        }
    }

    private static MigrationValidationException InvalidSchemaSql(string message) =>
        new MigrationValidationException("MySQL schema preview could not determine the final schema: " + message);

    private static MigrationValidationException UnsupportedSchemaChange(string sql, string statementKind) =>
        InvalidSchemaSql(
            $"schema-changing SQL beginning with '{statementKind}' is not supported. SQL: {sql}");

    private static IReadOnlyList<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var start = 0;
        var quote = '\0';
        var lineComment = false;
        var blockComment = false;
        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            if (lineComment)
            {
                if (current == '\n' || current == '\r')
                {
                    lineComment = false;
                }

                continue;
            }

            if (blockComment)
            {
                if (current == '*' && next == '/')
                {
                    blockComment = false;
                    index++;
                }

                continue;
            }

            if (quote != '\0')
            {
                if (current == '\\')
                {
                    index++;
                    continue;
                }

                if (current == quote)
                {
                    if (next == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (current == '-' && next == '-' &&
                (index + 2 >= sql.Length || char.IsWhiteSpace(sql[index + 2])))
            {
                lineComment = true;
                index++;
                continue;
            }

            if (current == '#')
            {
                lineComment = true;
                continue;
            }

            if (current == '/' && next == '*')
            {
                blockComment = true;
                index++;
                continue;
            }

            if (current == '\'' || current == '"' || current == '`')
            {
                quote = current;
                continue;
            }

            if (current == ';')
            {
                statements.Add(sql.Substring(start, index - start));
                start = index + 1;
            }
        }

        if (quote != '\0' || blockComment)
        {
            throw InvalidSchemaSql("SQL contains an unterminated string, identifier, or comment.");
        }

        if (start < sql.Length)
        {
            statements.Add(sql.Substring(start));
        }

        return statements;
    }

    private sealed class SchemaState
    {
        internal List<Table> Tables { get; } = new List<Table>();
        internal string? DefaultDatabase { get; set; }
    }

    private readonly struct TableName
    {
        internal TableName(string? schema, string name)
        {
            Schema = schema;
            Name = name;
        }

        internal string? Schema { get; }
        internal string Name { get; }
        internal string DisplayName => Schema is null ? Name : Schema + "." + Name;
    }

    private readonly struct ColumnPosition
    {
        internal ColumnPosition(bool isSpecified, bool isFirst, string? afterName)
        {
            IsSpecified = isSpecified;
            IsFirst = isFirst;
            AfterName = afterName;
        }

        internal bool IsSpecified { get; }
        internal bool IsFirst { get; }
        internal string? AfterName { get; }
    }

    private enum TokenKind
    {
        Word,
        QuotedIdentifier,
        String,
        Symbol,
    }

    private sealed class Token
    {
        internal Token(TokenKind kind, string value, string raw)
        {
            Kind = kind;
            Value = value;
            Raw = raw;
        }

        internal TokenKind Kind { get; }
        internal string Value { get; }
        internal string Raw { get; }

        internal bool IsSymbol(char value) => Kind == TokenKind.Symbol && Value.Length == 1 && Value[0] == value;
    }

    private sealed class StatementParser
    {
        private readonly IReadOnlyList<Token> _tokens;
        private int _index;

        internal StatementParser(string sql)
        {
            _tokens = Tokenize(sql);
        }

        internal bool IsAtEnd => _index >= _tokens.Count;

        internal string PeekWord(int offset = 0) => Word(_tokens, _index + offset);

        internal void ExpectWord(string word)
        {
            if (!MatchWord(word))
            {
                throw InvalidSchemaSql($"Expected {word} in migration SQL.");
            }
        }

        internal bool MatchWord(string word)
        {
            if (string.Equals(Word(_tokens, _index), word, StringComparison.OrdinalIgnoreCase))
            {
                _index++;
                return true;
            }

            return false;
        }

        internal bool MatchSymbol(char symbol)
        {
            if (_index < _tokens.Count && _tokens[_index].IsSymbol(symbol))
            {
                _index++;
                return true;
            }

            return false;
        }

        internal bool MatchIfNotExists()
        {
            var index = _index;
            if (MatchWord("IF") && MatchWord("NOT") && MatchWord("EXISTS"))
            {
                return true;
            }

            _index = index;
            return false;
        }

        internal bool MatchIfExists()
        {
            var index = _index;
            if (MatchWord("IF") && MatchWord("EXISTS"))
            {
                return true;
            }

            _index = index;
            return false;
        }

        internal string ReadIdentifier(string error)
        {
            if (_index >= _tokens.Count || !IsIdentifier(_tokens[_index]))
            {
                throw InvalidSchemaSql(error);
            }

            return _tokens[_index++].Value;
        }

        internal TableName ReadTableName(string? defaultSchema, string operation)
        {
            var tableName = ParseTableName(_tokens, ref _index, defaultSchema);
            if (string.IsNullOrWhiteSpace(tableName.Name))
            {
                throw InvalidSchemaSql($"{operation} requires a table name.");
            }

            return tableName;
        }

        internal List<List<Token>> ReadParenthesizedItems()
        {
            var items = new List<List<Token>>();
            var current = new List<Token>();
            var depth = 1;
            while (_index < _tokens.Count)
            {
                var token = _tokens[_index++];
                if (token.IsSymbol('('))
                {
                    depth++;
                    current.Add(token);
                    continue;
                }

                if (token.IsSymbol(')'))
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (current.Count != 0 || items.Count != 0)
                        {
                            items.Add(current);
                        }

                        return items;
                    }

                    if (depth < 0)
                    {
                        throw InvalidSchemaSql("A parenthesized SQL list is not balanced.");
                    }

                    current.Add(token);
                    continue;
                }

                if (token.IsSymbol(',') && depth == 1)
                {
                    items.Add(current);
                    current = new List<Token>();
                    continue;
                }

                current.Add(token);
            }

            throw InvalidSchemaSql("A parenthesized SQL list is not closed.");
        }

        internal void SkipCreateTableOptions()
        {
            while (!IsAtEnd)
            {
                _index++;
            }
        }

        internal List<Token> ReadRemainingTokens()
        {
            var tokens = new List<Token>();
            while (!IsAtEnd)
            {
                tokens.Add(_tokens[_index++]);
            }

            return tokens;
        }

        internal IReadOnlyList<List<Token>> ReadRemainingTokenGroups()
        {
            return SplitTokenGroups(ReadRemainingTokens(), 0, int.MaxValue, null);
        }

        internal void ExpectEnd(string message)
        {
            if (!IsAtEnd)
            {
                throw InvalidSchemaSql(message);
            }
        }
    }

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < sql.Length)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '-' && next == '-' &&
                (index + 2 >= sql.Length || char.IsWhiteSpace(sql[index + 2])))
            {
                index += 2;
                while (index < sql.Length && sql[index] != '\n' && sql[index] != '\r')
                {
                    index++;
                }

                continue;
            }

            if (current == '#')
            {
                index++;
                while (index < sql.Length && sql[index] != '\n' && sql[index] != '\r')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && next == '*')
            {
                index += 2;
                while (index + 1 < sql.Length && !(sql[index] == '*' && sql[index + 1] == '/'))
                {
                    index++;
                }

                if (index + 1 >= sql.Length)
                {
                    throw InvalidSchemaSql("SQL contains an unterminated block comment.");
                }

                index += 2;
                continue;
            }

            if (current == '`')
            {
                var start = index++;
                var value = new StringBuilder();
                var closed = false;
                while (index < sql.Length)
                {
                    if (sql[index] == '`')
                    {
                        if (index + 1 < sql.Length && sql[index + 1] == '`')
                        {
                            value.Append('`');
                            index += 2;
                            continue;
                        }

                        index++;
                        closed = true;
                        break;
                    }

                    if (sql[index] == '\\' && index + 1 < sql.Length)
                    {
                        value.Append(sql[index + 1]);
                        index += 2;
                        continue;
                    }

                    value.Append(sql[index++]);
                }

                if (!closed)
                {
                    throw InvalidSchemaSql("A backtick-quoted identifier is not closed.");
                }

                tokens.Add(new Token(TokenKind.QuotedIdentifier, value.ToString(), sql.Substring(start, index - start)));
                continue;
            }

            if (current == '\'' || current == '"')
            {
                var quote = current;
                var start = index++;
                var closed = false;
                while (index < sql.Length)
                {
                    if (sql[index] == '\\' && index + 1 < sql.Length)
                    {
                        index += 2;
                        continue;
                    }

                    if (sql[index] == quote)
                    {
                        if (index + 1 < sql.Length && sql[index + 1] == quote)
                        {
                            index += 2;
                            continue;
                        }

                        index++;
                        closed = true;
                        break;
                    }

                    index++;
                }

                if (!closed)
                {
                    throw InvalidSchemaSql("A quoted SQL string is not closed.");
                }

                var raw = sql.Substring(start, index - start);
                tokens.Add(new Token(TokenKind.String, raw, raw));
                continue;
            }

            if (IsSymbol(current))
            {
                tokens.Add(new Token(TokenKind.Symbol, current.ToString(), current.ToString()));
                index++;
                continue;
            }

            var wordStart = index;
            while (index < sql.Length &&
                   !char.IsWhiteSpace(sql[index]) &&
                   !IsSymbol(sql[index]) &&
                   sql[index] != '`' && sql[index] != '\'' && sql[index] != '"')
            {
                index++;
            }

            if (wordStart == index)
            {
                throw InvalidSchemaSql($"Unsupported character '{sql[index]}'.");
            }

            var word = sql.Substring(wordStart, index - wordStart);
            tokens.Add(new Token(TokenKind.Word, word, word));
        }

        return tokens;
    }

    private static bool IsSymbol(char value)
    {
        switch (value)
        {
            case '(':
            case ')':
            case ',':
            case '.':
            case '=':
            case '+':
            case '-':
            case '*':
            case '/':
            case '%':
            case '<':
            case '>':
            case '!':
            case '?':
                return true;
            default:
                return false;
        }
    }
}

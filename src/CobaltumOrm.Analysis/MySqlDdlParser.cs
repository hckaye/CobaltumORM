using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CobaltumOrm.Analysis;

internal abstract class MySqlDdlStatement
{
    protected MySqlDdlStatement(SourceSpan span, bool isValid)
    {
        Span = span;
        IsValid = isValid;
    }

    internal SourceSpan Span { get; }
    internal bool IsValid { get; }
}

internal sealed class MySqlCreateTableStatement : MySqlDdlStatement
{
    internal MySqlCreateTableStatement(
        SqlQualifiedName table,
        bool ifNotExists,
        IReadOnlyList<MySqlDdlColumnDefinition> columns,
        IReadOnlyList<IReadOnlyList<SqlIdentifier>> primaryKeys,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Table = table;
        IfNotExists = ifNotExists;
        Columns = columns;
        PrimaryKeys = primaryKeys;
    }

    internal SqlQualifiedName Table { get; }
    internal bool IfNotExists { get; }
    internal IReadOnlyList<MySqlDdlColumnDefinition> Columns { get; }
    internal IReadOnlyList<IReadOnlyList<SqlIdentifier>> PrimaryKeys { get; }
}

internal sealed class MySqlDropTableStatement : MySqlDdlStatement
{
    internal MySqlDropTableStatement(
        IReadOnlyList<SqlQualifiedName> tables,
        bool ifExists,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Tables = tables;
        IfExists = ifExists;
    }

    internal IReadOnlyList<SqlQualifiedName> Tables { get; }
    internal bool IfExists { get; }
}

internal sealed class MySqlAlterTableStatement : MySqlDdlStatement
{
    internal MySqlAlterTableStatement(
        SqlQualifiedName table,
        bool ifExists,
        IReadOnlyList<MySqlDdlAlterAction> actions,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Table = table;
        IfExists = ifExists;
        Actions = actions;
    }

    internal SqlQualifiedName Table { get; }
    internal bool IfExists { get; }
    internal IReadOnlyList<MySqlDdlAlterAction> Actions { get; }
}

internal sealed class MySqlRenameTableStatement : MySqlDdlStatement
{
    internal MySqlRenameTableStatement(
        IReadOnlyList<MySqlRenameTablePair> pairs,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Pairs = pairs;
    }

    internal IReadOnlyList<MySqlRenameTablePair> Pairs { get; }
}

internal sealed class MySqlUseStatement : MySqlDdlStatement
{
    internal MySqlUseStatement(SqlIdentifier database, SourceSpan span, bool isValid)
        : base(span, isValid)
    {
        Database = database;
    }

    internal SqlIdentifier Database { get; }
}

internal readonly struct MySqlRenameTablePair
{
    internal MySqlRenameTablePair(SqlQualifiedName oldName, SqlQualifiedName newName, SourceSpan span)
    {
        OldName = oldName;
        NewName = newName;
        Span = span;
    }

    internal SqlQualifiedName OldName { get; }
    internal SqlQualifiedName NewName { get; }
    internal SourceSpan Span { get; }
}

internal sealed class MySqlDdlColumnDefinition
{
    internal MySqlDdlColumnDefinition(
        SqlIdentifier name,
        string sqlType,
        bool isNullable,
        bool isPrimaryKey,
        string? defaultExpression,
        bool isIdentity,
        MySqlColumnPosition position,
        SourceSpan span)
    {
        Name = name;
        SqlType = sqlType;
        IsNullable = isNullable;
        IsPrimaryKey = isPrimaryKey;
        DefaultExpression = defaultExpression;
        IsIdentity = isIdentity;
        Position = position;
        Span = span;
    }

    internal SqlIdentifier Name { get; }
    internal string SqlType { get; }
    internal bool IsNullable { get; }
    internal bool IsPrimaryKey { get; }
    internal string? DefaultExpression { get; }
    internal bool IsIdentity { get; }
    internal MySqlColumnPosition Position { get; }
    internal SourceSpan Span { get; }
}

internal abstract class MySqlDdlAlterAction
{
    protected MySqlDdlAlterAction(SourceSpan span)
    {
        Span = span;
    }

    internal SourceSpan Span { get; }
}

internal sealed class MySqlAddColumnAction : MySqlDdlAlterAction
{
    internal MySqlAddColumnAction(MySqlDdlColumnDefinition column, bool ifNotExists, SourceSpan span)
        : base(span)
    {
        Column = column;
        IfNotExists = ifNotExists;
    }

    internal MySqlDdlColumnDefinition Column { get; }
    internal bool IfNotExists { get; }
}

internal sealed class MySqlDropColumnAction : MySqlDdlAlterAction
{
    internal MySqlDropColumnAction(SqlIdentifier column, bool ifExists, SourceSpan span)
        : base(span)
    {
        Column = column;
        IfExists = ifExists;
    }

    internal SqlIdentifier Column { get; }
    internal bool IfExists { get; }
}

internal sealed class MySqlModifyColumnAction : MySqlDdlAlterAction
{
    internal MySqlModifyColumnAction(MySqlDdlColumnDefinition column, SourceSpan span)
        : base(span)
    {
        Column = column;
    }

    internal MySqlDdlColumnDefinition Column { get; }
}

internal sealed class MySqlChangeColumnAction : MySqlDdlAlterAction
{
    internal MySqlChangeColumnAction(
        SqlIdentifier oldName,
        MySqlDdlColumnDefinition column,
        SourceSpan span)
        : base(span)
    {
        OldName = oldName;
        Column = column;
    }

    internal SqlIdentifier OldName { get; }
    internal MySqlDdlColumnDefinition Column { get; }
}

internal sealed class MySqlRenameColumnAction : MySqlDdlAlterAction
{
    internal MySqlRenameColumnAction(SqlIdentifier oldName, SqlIdentifier newName, SourceSpan span)
        : base(span)
    {
        OldName = oldName;
        NewName = newName;
    }

    internal SqlIdentifier OldName { get; }
    internal SqlIdentifier NewName { get; }
}

internal sealed class MySqlRenameTableAction : MySqlDdlAlterAction
{
    internal MySqlRenameTableAction(SqlQualifiedName newName, SourceSpan span)
        : base(span)
    {
        NewName = newName;
    }

    internal SqlQualifiedName NewName { get; }
}

internal sealed class MySqlAlterDefaultAction : MySqlDdlAlterAction
{
    internal MySqlAlterDefaultAction(
        SqlIdentifier column,
        string? defaultExpression,
        bool drop,
        SourceSpan span)
        : base(span)
    {
        Column = column;
        DefaultExpression = defaultExpression;
        Drop = drop;
    }

    internal SqlIdentifier Column { get; }
    internal string? DefaultExpression { get; }
    internal bool Drop { get; }
}

internal sealed class MySqlPrimaryKeyAction : MySqlDdlAlterAction
{
    internal MySqlPrimaryKeyAction(IReadOnlyList<SqlIdentifier> columns, bool drop, SourceSpan span)
        : base(span)
    {
        Columns = columns;
        Drop = drop;
    }

    internal IReadOnlyList<SqlIdentifier> Columns { get; }
    internal bool Drop { get; }
}

internal sealed class MySqlSchemaNeutralAlterAction : MySqlDdlAlterAction
{
    internal MySqlSchemaNeutralAlterAction(SourceSpan span)
        : base(span)
    {
    }
}

internal readonly struct MySqlColumnPosition
{
    internal MySqlColumnPosition(bool isSpecified, bool isFirst, SqlIdentifier? after)
    {
        IsSpecified = isSpecified;
        IsFirst = isFirst;
        After = after;
    }

    internal bool IsSpecified { get; }
    internal bool IsFirst { get; }
    internal SqlIdentifier? After { get; }
}

internal sealed class MySqlDdlParser
{
    private static readonly HashSet<string> MySqlTableConstraintWords = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "CHECK",
        "CONSTRAINT",
        "FOREIGN",
        "FULLTEXT",
        "INDEX",
        "KEY",
        "PARTITION",
        "PRIMARY",
        "SPATIAL",
        "UNIQUE",
    };

    private static readonly HashSet<string> MySqlColumnConstraintWords = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "AFTER",
        "AUTO_INCREMENT",
        "CHECK",
        "COLLATE",
        "COMMENT",
        "CONSTRAINT",
        "DEFAULT",
        "FIRST",
        "GENERATED",
        "KEY",
        "NOT",
        "NULL",
        "ON",
        "PRIMARY",
        "REFERENCES",
        "SRID",
        "UNIQUE",
        "VISIBLE",
        "INVISIBLE",
        "COLUMN_FORMAT",
        "STORAGE",
        "CHARACTER",
    };

    private static readonly HashSet<string> MySqlIgnoredAlterWords = new HashSet<string>(
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

    private readonly IReadOnlyList<MySqlDdlToken> _tokens;
    private readonly List<Diagnostic> _diagnostics;
    private readonly string _sql;
    private int _position;

    internal MySqlDdlParser(
        IReadOnlyList<MySqlDdlToken> tokens,
        string sql,
        List<Diagnostic> diagnostics)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    internal IReadOnlyList<MySqlDdlStatement> Parse()
    {
        var statements = new List<MySqlDdlStatement>();
        while (Current.Kind != MySqlDdlTokenKind.End)
        {
            if (MySqlMatch(MySqlDdlTokenKind.Semicolon))
            {
                continue;
            }

            var start = Current.Span.Start;
            var diagnosticCount = _diagnostics.Count;
            MySqlDdlStatement? statement;
            if (MySqlIsKeyword("CREATE"))
            {
                statement = MySqlParseCreateTable(start);
            }
            else if (MySqlIsKeyword("DROP"))
            {
                statement = MySqlParseDropTable(start);
            }
            else if (MySqlIsKeyword("ALTER"))
            {
                statement = MySqlParseAlterTable(start);
            }
            else if (MySqlIsKeyword("RENAME"))
            {
                statement = MySqlParseRenameTable(start);
            }
            else if (MySqlIsKeyword("USE"))
            {
                statement = MySqlParseUse(start);
            }
            else
            {
                MySqlReport(
                    "DDL101",
                    "This MySQL statement is not supported by schema analysis.",
                    Current.Span);
                MySqlSkipToStatementEnd();
                statement = null;
            }

            if (Current.Kind == MySqlDdlTokenKind.Semicolon)
            {
                MySqlAdvance();
            }
            else if (Current.Kind != MySqlDdlTokenKind.End)
            {
                MySqlReport("DDL100", "Expected a semicolon between migration statements.", Current.Span);
                MySqlSkipToStatementEnd();
            }

            if (statement != null && _diagnostics.Count == diagnosticCount)
            {
                statements.Add(statement);
            }
        }

        return statements;
    }

    private MySqlDdlStatement? MySqlParseCreateTable(int start)
    {
        var diagnosticCount = _diagnostics.Count;
        MySqlExpectKeyword("CREATE", "Expected CREATE.");
        MySqlMatchKeyword("TEMPORARY");
        if (!MySqlMatchKeyword("TABLE"))
        {
            MySqlReport("DDL101", "Only CREATE TABLE is supported by MySQL schema analysis.", Current.Span);
            MySqlSkipToStatementEnd();
            return null;
        }

        var ifNotExists = MySqlMatchIfNotExists();
        var table = MySqlParseTableName("Expected a table name after CREATE TABLE.");
        if (!MySqlMatch(MySqlDdlTokenKind.OpenParen))
        {
            MySqlReport(
                "DDL101",
                "CREATE TABLE must declare columns in parentheses; CREATE TABLE AS SELECT is not supported.",
                Current.Span);
            MySqlSkipToStatementEnd();
            return null;
        }

        var columns = new List<MySqlDdlColumnDefinition>();
        var primaryKeys = new List<IReadOnlyList<SqlIdentifier>>();
        while (Current.Kind != MySqlDdlTokenKind.CloseParen && Current.Kind != MySqlDdlTokenKind.End)
        {
            if (MySqlMatch(MySqlDdlTokenKind.Comma))
            {
                continue;
            }

            if (MySqlIsKeyword("PRIMARY"))
            {
                var primary = MySqlParsePrimaryKey();
                if (primary != null)
                {
                    primaryKeys.Add(primary);
                }
            }
            else if (MySqlIsKeyword("CONSTRAINT"))
            {
                MySqlAdvance();
                MySqlParseIdentifier("Expected a constraint name after CONSTRAINT.");
                if (MySqlIsKeyword("PRIMARY"))
                {
                    var primary = MySqlParsePrimaryKey();
                    if (primary != null)
                    {
                        primaryKeys.Add(primary);
                    }
                }
                else
                {
                    MySqlSkipToTableItemEnd();
                }
            }
            else if (MySqlCurrentWord() is string tableConstraintWord &&
                     MySqlTableConstraintWords.Contains(tableConstraintWord))
            {
                MySqlSkipToTableItemEnd();
            }
            else
            {
                var column = MySqlParseColumnDefinition();
                if (column != null)
                {
                    columns.Add(column);
                }
            }

            if (Current.Kind != MySqlDdlTokenKind.Comma &&
                Current.Kind != MySqlDdlTokenKind.CloseParen &&
                Current.Kind != MySqlDdlTokenKind.End)
            {
                MySqlReport("DDL100", "Expected a comma between CREATE TABLE definitions.", Current.Span);
                MySqlSkipToTableItemEnd();
            }

            MySqlMatch(MySqlDdlTokenKind.Comma);
        }

        var close = MySqlExpect(
            MySqlDdlTokenKind.CloseParen,
            "Expected ')' after CREATE TABLE definitions.");
        MySqlSkipCreateTableOptions();
        var span = MySqlFromBounds(start, MySqlEndOf(close));
        return new MySqlCreateTableStatement(
            table,
            ifNotExists,
            columns,
            primaryKeys,
            span,
            _diagnostics.Count == diagnosticCount);
    }

    private MySqlDdlStatement? MySqlParseDropTable(int start)
    {
        var diagnosticCount = _diagnostics.Count;
        MySqlExpectKeyword("DROP", "Expected DROP.");
        MySqlMatchKeyword("TEMPORARY");
        if (!MySqlMatchKeyword("TABLE"))
        {
            MySqlReport("DDL101", "Only DROP TABLE is supported by MySQL schema analysis.", Current.Span);
            MySqlSkipToStatementEnd();
            return null;
        }

        var ifExists = MySqlMatchIfExists();
        var tables = new List<SqlQualifiedName>
        {
            MySqlParseTableName("Expected a table name after DROP TABLE."),
        };
        while (MySqlMatch(MySqlDdlTokenKind.Comma))
        {
            tables.Add(MySqlParseTableName("Expected another table name after ','."));
        }

        if (MySqlIsKeyword("CASCADE") || MySqlIsKeyword("RESTRICT"))
        {
            MySqlAdvance();
        }

        if (Current.Kind != MySqlDdlTokenKind.Semicolon && Current.Kind != MySqlDdlTokenKind.End)
        {
            MySqlReport("DDL101", "DROP TABLE contains unsupported trailing SQL.", Current.Span);
            MySqlSkipToStatementEnd();
        }

        var end = tables.Count == 0 ? Current.Span.Start : MySqlEndOf(tables[tables.Count - 1].Span);
        return new MySqlDropTableStatement(
            tables,
            ifExists,
            MySqlFromBounds(start, end),
            _diagnostics.Count == diagnosticCount);
    }

    private MySqlDdlStatement? MySqlParseAlterTable(int start)
    {
        var diagnosticCount = _diagnostics.Count;
        MySqlExpectKeyword("ALTER", "Expected ALTER.");
        if (!MySqlMatchKeyword("TABLE"))
        {
            MySqlReport("DDL101", "Only ALTER TABLE is supported by MySQL schema analysis.", Current.Span);
            MySqlSkipToStatementEnd();
            return null;
        }

        var ifExists = MySqlMatchIfExists();
        var table = MySqlParseTableName("Expected a table name after ALTER TABLE.");
        var actions = new List<MySqlDdlAlterAction>();
        while (Current.Kind != MySqlDdlTokenKind.Semicolon && Current.Kind != MySqlDdlTokenKind.End)
        {
            var action = MySqlParseAlterAction();
            if (action != null)
            {
                actions.Add(action);
            }

            if (!MySqlMatch(MySqlDdlTokenKind.Comma))
            {
                break;
            }
        }

        if (actions.Count == 0 && _diagnostics.Count == diagnosticCount)
        {
            MySqlReport("DDL100", "ALTER TABLE requires a supported table change.", Current.Span);
        }

        var end = actions.Count == 0
            ? MySqlEndOf(table.Span)
            : MySqlEndOf(actions[actions.Count - 1].Span);
        return new MySqlAlterTableStatement(
            table,
            ifExists,
            actions,
            MySqlFromBounds(start, end),
            _diagnostics.Count == diagnosticCount);
    }

    private MySqlDdlStatement? MySqlParseRenameTable(int start)
    {
        var diagnosticCount = _diagnostics.Count;
        MySqlExpectKeyword("RENAME", "Expected RENAME.");
        if (!MySqlMatchKeyword("TABLE"))
        {
            MySqlReport("DDL101", "Only RENAME TABLE is supported by MySQL schema analysis.", Current.Span);
            MySqlSkipToStatementEnd();
            return null;
        }

        var pairs = new List<MySqlRenameTablePair>();
        while (Current.Kind != MySqlDdlTokenKind.Semicolon && Current.Kind != MySqlDdlTokenKind.End)
        {
            var oldName = MySqlParseTableName("Expected a source table name after RENAME TABLE.");
            MySqlExpectKeyword("TO", "Expected TO between RENAME TABLE names.");
            var newName = MySqlParseTableName("Expected a destination table name after RENAME TABLE ... TO.");
            pairs.Add(new MySqlRenameTablePair(
                oldName,
                newName,
                MySqlFromBounds(oldName.Span.Start, MySqlEndOf(newName.Span))));
            if (!MySqlMatch(MySqlDdlTokenKind.Comma))
            {
                break;
            }
        }

        if (pairs.Count == 0 && _diagnostics.Count == diagnosticCount)
        {
            MySqlReport("DDL100", "RENAME TABLE requires at least one source and destination pair.", Current.Span);
        }

        var end = pairs.Count == 0 ? Current.Span.Start : MySqlEndOf(pairs[pairs.Count - 1].Span);
        return new MySqlRenameTableStatement(
            pairs,
            MySqlFromBounds(start, end),
            _diagnostics.Count == diagnosticCount);
    }

    private MySqlDdlStatement? MySqlParseUse(int start)
    {
        var diagnosticCount = _diagnostics.Count;
        MySqlExpectKeyword("USE", "Expected USE.");
        var database = MySqlParseIdentifier("Expected a database name after USE.");
        if (Current.Kind != MySqlDdlTokenKind.Semicolon && Current.Kind != MySqlDdlTokenKind.End)
        {
            MySqlReport("DDL101", "USE contains unsupported trailing SQL.", Current.Span);
            MySqlSkipToStatementEnd();
        }

        return new MySqlUseStatement(
            database,
            MySqlFromBounds(start, MySqlEndOf(database.Span)),
            _diagnostics.Count == diagnosticCount);
    }

    private MySqlDdlAlterAction? MySqlParseAlterAction()
    {
        var start = Current.Span.Start;
        var word = MySqlCurrentWord();
        if (word != null && MySqlIgnoredAlterWords.Contains(word))
        {
            MySqlSkipToActionEnd();
            return new MySqlSchemaNeutralAlterAction(MySqlFromBounds(start, Current.Span.Start));
        }

        if (MySqlMatchKeyword("ADD"))
        {
            MySqlMatchKeyword("COLUMN");
            var ifNotExists = MySqlMatchIfNotExists();
            if (MySqlIsKeyword("PRIMARY"))
            {
                var primary = MySqlParsePrimaryKey();
                return primary == null
                    ? null
                    : new MySqlPrimaryKeyAction(
                        primary,
                        false,
                        MySqlFromBounds(
                            start,
                            primary.Count == 0 ? Current.Span.Start : MySqlEndOf(primary[primary.Count - 1].Span)));
            }

            if (MySqlIsKeyword("CONSTRAINT"))
            {
                MySqlAdvance();
                MySqlParseIdentifier("Expected a constraint name after ADD CONSTRAINT.");
                if (MySqlIsKeyword("PRIMARY"))
                {
                    var primary = MySqlParsePrimaryKey();
                    return primary == null
                        ? null
                        : new MySqlPrimaryKeyAction(
                            primary,
                            false,
                            MySqlFromBounds(
                                start,
                                primary.Count == 0 ? Current.Span.Start : MySqlEndOf(primary[primary.Count - 1].Span)));
                }

                MySqlSkipToActionEnd();
                return new MySqlSchemaNeutralAlterAction(MySqlFromBounds(start, Current.Span.Start));
            }

            if (MySqlIsKeyword("UNIQUE") || MySqlIsKeyword("INDEX") || MySqlIsKeyword("KEY") ||
                MySqlIsKeyword("FULLTEXT") || MySqlIsKeyword("SPATIAL") || MySqlIsKeyword("FOREIGN") ||
                MySqlIsKeyword("CHECK"))
            {
                MySqlSkipToActionEnd();
                return new MySqlSchemaNeutralAlterAction(MySqlFromBounds(start, Current.Span.Start));
            }

            var column = MySqlParseColumnDefinition();
            return column == null
                ? null
                : new MySqlAddColumnAction(column, ifNotExists, MySqlFromBounds(start, MySqlEndOf(column.Span)));
        }

        if (MySqlMatchKeyword("DROP"))
        {
            MySqlMatchKeyword("COLUMN");
            var ifExists = MySqlMatchIfExists();
            if (MySqlMatchKeyword("PRIMARY"))
            {
                MySqlExpectKeyword("KEY", "Expected KEY after DROP PRIMARY.");
                return new MySqlPrimaryKeyAction(
                    Array.Empty<SqlIdentifier>(),
                    true,
                    MySqlFromBounds(start, Current.Span.Start));
            }

            if (MySqlIsKeyword("INDEX") || MySqlIsKeyword("KEY") || MySqlIsKeyword("FOREIGN") ||
                MySqlIsKeyword("CONSTRAINT") || MySqlIsKeyword("CHECK"))
            {
                MySqlSkipToActionEnd();
                return new MySqlSchemaNeutralAlterAction(MySqlFromBounds(start, Current.Span.Start));
            }

            var column = MySqlParseIdentifier("Expected a column name after ALTER TABLE DROP COLUMN.");
            return new MySqlDropColumnAction(column, ifExists, MySqlFromBounds(start, MySqlEndOf(column.Span)));
        }

        if (MySqlMatchKeyword("MODIFY"))
        {
            MySqlMatchKeyword("COLUMN");
            var column = MySqlParseColumnDefinition();
            return column == null
                ? null
                : new MySqlModifyColumnAction(column, MySqlFromBounds(start, MySqlEndOf(column.Span)));
        }

        if (MySqlMatchKeyword("CHANGE"))
        {
            MySqlMatchKeyword("COLUMN");
            var oldName = MySqlParseIdentifier("Expected the old column name after ALTER TABLE CHANGE.");
            var column = MySqlParseColumnDefinition();
            return column == null
                ? null
                : new MySqlChangeColumnAction(oldName, column, MySqlFromBounds(start, MySqlEndOf(column.Span)));
        }

        if (MySqlMatchKeyword("RENAME"))
        {
            if (MySqlMatchKeyword("COLUMN"))
            {
                var oldName = MySqlParseIdentifier("Expected the old column name after RENAME COLUMN.");
                MySqlExpectKeyword("TO", "Expected TO in RENAME COLUMN.");
                var newName = MySqlParseIdentifier("Expected the new column name in RENAME COLUMN.");
                return new MySqlRenameColumnAction(oldName, newName, MySqlFromBounds(start, MySqlEndOf(newName.Span)));
            }

            if (MySqlMatchKeyword("TO"))
            {
                var newName = MySqlParseTableName("Expected the new table name after RENAME TO.");
                return new MySqlRenameTableAction(newName, MySqlFromBounds(start, MySqlEndOf(newName.Span)));
            }

            MySqlReport("DDL101", "ALTER TABLE RENAME supports COLUMN old TO new or TO new_table.", Current.Span);
            MySqlSkipToActionEnd();
            return null;
        }

        if (MySqlMatchKeyword("ALTER"))
        {
            MySqlMatchKeyword("COLUMN");
            var column = MySqlParseIdentifier("Expected a column name after ALTER TABLE ALTER COLUMN.");
            if (MySqlMatchKeyword("SET"))
            {
                if (!MySqlMatchKeyword("DEFAULT"))
                {
                    MySqlReport("DDL101", "Only SET DEFAULT is supported for ALTER TABLE ALTER COLUMN.", Current.Span);
                    MySqlSkipToActionEnd();
                    return null;
                }

                var expression = MySqlReadDefaultExpression();
                return new MySqlAlterDefaultAction(
                    column,
                    expression,
                    false,
                    MySqlFromBounds(start, Current.Span.Start));
            }

            if (MySqlMatchKeyword("DROP"))
            {
                if (!MySqlMatchKeyword("DEFAULT"))
                {
                    MySqlReport("DDL101", "Only DROP DEFAULT is supported for ALTER TABLE ALTER COLUMN.", Current.Span);
                    MySqlSkipToActionEnd();
                    return null;
                }

                return new MySqlAlterDefaultAction(
                    column,
                    null,
                    true,
                    MySqlFromBounds(start, Current.Span.Start));
            }

            MySqlReport("DDL101", "Only SET DEFAULT and DROP DEFAULT are supported for ALTER TABLE ALTER COLUMN.", Current.Span);
            MySqlSkipToActionEnd();
            return null;
        }

        MySqlReport("DDL101", "ALTER TABLE contains an unsupported schema-changing action.", Current.Span);
        MySqlSkipToActionEnd();
        return null;
    }

    private MySqlDdlColumnDefinition? MySqlParseColumnDefinition()
    {
        var start = Current.Span.Start;
        var name = MySqlParseIdentifier("Expected a column name.");
        var sqlType = MySqlParseTypeName();
        if (sqlType.Length == 0)
        {
            MySqlSkipToTableItemEnd();
            return null;
        }

        var isNullable = true;
        var isPrimaryKey = false;
        var isIdentity = false;
        string? defaultExpression = null;
        var position = new MySqlColumnPosition(false, false, null);
        while (!MySqlIsColumnDefinitionEnd())
        {
            if (MySqlMatchKeyword("NULL"))
            {
                isNullable = true;
                continue;
            }

            if (MySqlMatchKeyword("NOT"))
            {
                if (MySqlMatchKeyword("NULL"))
                {
                    isNullable = false;
                }
                else
                {
                    MySqlReport("DDL101", "Expected NULL after NOT in a column definition.", Current.Span);
                    MySqlSkipToTableItemEnd();
                }

                continue;
            }

            if (MySqlMatchKeyword("PRIMARY"))
            {
                MySqlExpectKeyword("KEY", "Expected KEY after PRIMARY in a column definition.");
                isNullable = false;
                isPrimaryKey = true;
                continue;
            }

            if (MySqlMatchKeyword("AUTO_INCREMENT"))
            {
                isIdentity = true;
                continue;
            }

            if (MySqlMatchKeyword("CONSTRAINT"))
            {
                MySqlParseIdentifier("Expected a constraint name after CONSTRAINT.");
                if (MySqlMatchKeyword("PRIMARY"))
                {
                    MySqlExpectKeyword("KEY", "Expected KEY after PRIMARY in a column constraint.");
                    isNullable = false;
                    isPrimaryKey = true;
                }
                else
                {
                    MySqlReport("DDL101", "Only PRIMARY KEY column constraints are supported by schema analysis.", Current.Span);
                    MySqlSkipToTableItemEnd();
                }

                continue;
            }

            if (MySqlMatchKeyword("DEFAULT"))
            {
                defaultExpression = MySqlReadDefaultExpression();
                continue;
            }

            if (MySqlMatchKeyword("UNIQUE") || MySqlMatchKeyword("KEY"))
            {
                if (MySqlIsIdentifier(Current))
                {
                    MySqlAdvance();
                }

                continue;
            }

            if (MySqlMatchKeyword("REFERENCES"))
            {
                MySqlSkipToTableItemEnd();
                continue;
            }

            if (MySqlMatchKeyword("CHECK"))
            {
                MySqlSkipBalancedClause();
                continue;
            }

            if (MySqlMatchKeyword("COLLATE") || MySqlMatchKeyword("COMMENT") ||
                MySqlMatchKeyword("SRID") || MySqlMatchKeyword("COLUMN_FORMAT") ||
                MySqlMatchKeyword("STORAGE"))
            {
                if (!MySqlIsColumnDefinitionEnd())
                {
                    MySqlAdvance();
                }

                continue;
            }

            if (MySqlMatchKeyword("CHARACTER"))
            {
                if (MySqlMatchKeyword("SET") && MySqlIsIdentifier(Current))
                {
                    MySqlAdvance();
                }
                else
                {
                    MySqlReport("DDL101", "Expected CHARACTER SET in a column definition.", Current.Span);
                    MySqlSkipToTableItemEnd();
                }

                continue;
            }

            if (MySqlMatchKeyword("ON"))
            {
                if (MySqlMatchKeyword("UPDATE"))
                {
                    MySqlSkipToNextColumnConstraint();
                }
                else
                {
                    MySqlReport("DDL101", "Expected UPDATE after ON in a column definition.", Current.Span);
                    MySqlSkipToTableItemEnd();
                }

                continue;
            }

            if (MySqlMatchKeyword("VISIBLE") || MySqlMatchKeyword("INVISIBLE"))
            {
                continue;
            }

            if (MySqlMatchKeyword("GENERATED"))
            {
                MySqlReport("DDL101", "Generated columns are not supported by MySQL schema analysis.", Current.Span);
                MySqlSkipToTableItemEnd();
                continue;
            }

            if (MySqlMatchKeyword("FIRST"))
            {
                position = new MySqlColumnPosition(true, true, null);
                continue;
            }

            if (MySqlMatchKeyword("AFTER"))
            {
                var after = MySqlParseIdentifier("Expected a column name after AFTER.");
                position = new MySqlColumnPosition(true, false, after);
                continue;
            }

            MySqlReport("DDL101", "This MySQL column constraint is not supported by schema analysis.", Current.Span);
            MySqlSkipToTableItemEnd();
        }

        if (string.Equals(sqlType, "serial", StringComparison.OrdinalIgnoreCase))
        {
            isIdentity = true;
        }

        return new MySqlDdlColumnDefinition(
            name,
            sqlType,
            isNullable,
            isPrimaryKey,
            defaultExpression,
            isIdentity,
            position,
            MySqlFromBounds(start, Current.Span.Start));
    }

    private string MySqlParseTypeName()
    {
        if (!MySqlIsIdentifier(Current))
        {
            MySqlReport("DDL100", "Expected a MySQL column type.", Current.Span);
            return string.Empty;
        }

        var typeTokens = new List<MySqlDdlToken> { MySqlAdvance() };
        if (MySqlCurrentWord() == "PRECISION" || MySqlCurrentWord() == "VARYING")
        {
            typeTokens.Add(MySqlAdvance());
        }

        if (Current.Kind == MySqlDdlTokenKind.OpenParen)
        {
            MySqlAppendBalancedTypeTokens(typeTokens);
        }

        while (MySqlCurrentWord() == "UNSIGNED" || MySqlCurrentWord() == "SIGNED" ||
               MySqlCurrentWord() == "ZEROFILL")
        {
            typeTokens.Add(MySqlAdvance());
        }

        var normalized = MySqlJoinTokens(typeTokens, false);
        normalized = normalized.Replace("double precision", "double")
            .Replace("character varying", "varchar")
            .Replace("numeric", "decimal")
            .Replace("integer", "int")
            .Replace("character", "char")
            .Replace(" signed", string.Empty)
            .Replace("boolean", "tinyint(1)")
            .Replace("bool", "tinyint(1)");
        return normalized;
    }

    private void MySqlAppendBalancedTypeTokens(List<MySqlDdlToken> tokens)
    {
        var depth = 0;
        while (Current.Kind != MySqlDdlTokenKind.End)
        {
            var token = MySqlAdvance();
            tokens.Add(token);
            if (token.Kind == MySqlDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == MySqlDdlTokenKind.CloseParen)
            {
                depth--;
                if (depth == 0)
                {
                    return;
                }
            }
        }

        MySqlReport("DDL100", "A MySQL column type contains an unmatched opening parenthesis.", Current.Span);
    }

    private IReadOnlyList<SqlIdentifier>? MySqlParsePrimaryKey()
    {
        MySqlMatchKeyword("PRIMARY");
        MySqlExpectKeyword("KEY", "Expected KEY after PRIMARY.");
        while (Current.Kind != MySqlDdlTokenKind.OpenParen &&
               Current.Kind != MySqlDdlTokenKind.Comma &&
               Current.Kind != MySqlDdlTokenKind.CloseParen &&
               Current.Kind != MySqlDdlTokenKind.Semicolon &&
               Current.Kind != MySqlDdlTokenKind.End)
        {
            MySqlAdvance();
        }

        if (!MySqlMatch(MySqlDdlTokenKind.OpenParen))
        {
            MySqlReport("DDL100", "PRIMARY KEY must list its columns in parentheses.", Current.Span);
            return null;
        }

        var result = new List<SqlIdentifier>();
        if (Current.Kind != MySqlDdlTokenKind.CloseParen)
        {
            do
            {
                var identifier = MySqlParseIdentifier("Expected a column name in PRIMARY KEY.");
                result.Add(identifier);
                if (Current.Kind == MySqlDdlTokenKind.OpenParen)
                {
                    MySqlSkipBalancedClause();
                }
            }
            while (MySqlMatch(MySqlDdlTokenKind.Comma));
        }

        MySqlExpect(MySqlDdlTokenKind.CloseParen, "Expected ')' after PRIMARY KEY columns.");
        MySqlSkipToTableItemEnd();
        return result;
    }

    private string? MySqlReadDefaultExpression()
    {
        if (MySqlIsColumnDefinitionEnd() || MySqlIsDefaultConstraintBoundary())
        {
            MySqlReport("DDL100", "DEFAULT requires an expression.", Current.Span);
            return null;
        }

        var start = Current.Span.Start;
        var end = start;
        var depth = 0;
        while (Current.Kind != MySqlDdlTokenKind.End)
        {
            if (depth == 0 &&
                (Current.Kind == MySqlDdlTokenKind.Comma ||
                 Current.Kind == MySqlDdlTokenKind.CloseParen ||
                 Current.Kind == MySqlDdlTokenKind.Semicolon ||
                 MySqlIsDefaultConstraintBoundary()))
            {
                break;
            }

            var token = MySqlAdvance();
            if (token.Kind == MySqlDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == MySqlDdlTokenKind.CloseParen && depth > 0)
            {
                depth--;
            }

            end = MySqlEndOf(token);
        }

        return end <= start ? null : _sql.Substring(start, end - start).Trim();
    }

    private bool MySqlIsDefaultConstraintBoundary()
    {
        if (MySqlIsKeyword("NOT"))
        {
            return MySqlIsKeyword(MySqlPeek(1), "NULL");
        }

        if (MySqlIsKeyword("PRIMARY"))
        {
            return MySqlIsKeyword(MySqlPeek(1), "KEY");
        }

        return MySqlIsKeyword("AUTO_INCREMENT") || MySqlIsKeyword("CONSTRAINT") ||
            MySqlIsKeyword("UNIQUE") || MySqlIsKeyword("REFERENCES") || MySqlIsKeyword("CHECK") ||
            MySqlIsKeyword("GENERATED") || MySqlIsKeyword("COLLATE") || MySqlIsKeyword("COMMENT") ||
            MySqlIsKeyword("ON") || MySqlIsKeyword("VISIBLE") || MySqlIsKeyword("INVISIBLE") ||
            MySqlIsKeyword("FIRST") || MySqlIsKeyword("AFTER");
    }

    private void MySqlSkipToNextColumnConstraint()
    {
        var depth = 0;
        while (Current.Kind != MySqlDdlTokenKind.End)
        {
            if (depth == 0 &&
                (Current.Kind == MySqlDdlTokenKind.Comma ||
                 Current.Kind == MySqlDdlTokenKind.CloseParen ||
                 Current.Kind == MySqlDdlTokenKind.Semicolon ||
                 MySqlCurrentWord() is string word && MySqlColumnConstraintWords.Contains(word)))
            {
                return;
            }

            var token = MySqlAdvance();
            if (token.Kind == MySqlDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == MySqlDdlTokenKind.CloseParen && depth > 0)
            {
                depth--;
            }
        }
    }

    private void MySqlSkipBalancedClause()
    {
        if (!MySqlMatch(MySqlDdlTokenKind.OpenParen))
        {
            MySqlReport("DDL101", "Expected a parenthesized clause.", Current.Span);
            MySqlSkipToTableItemEnd();
            return;
        }

        var depth = 1;
        while (Current.Kind != MySqlDdlTokenKind.End && depth > 0)
        {
            var token = MySqlAdvance();
            if (token.Kind == MySqlDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == MySqlDdlTokenKind.CloseParen)
            {
                depth--;
            }
        }

        if (depth != 0)
        {
            MySqlReport("DDL100", "A MySQL parenthesized clause is not balanced.", Current.Span);
        }
    }

    private bool MySqlIsColumnDefinitionEnd() =>
        Current.Kind == MySqlDdlTokenKind.Comma ||
        Current.Kind == MySqlDdlTokenKind.CloseParen ||
        Current.Kind == MySqlDdlTokenKind.Semicolon ||
        Current.Kind == MySqlDdlTokenKind.End;

    private void MySqlSkipCreateTableOptions()
    {
        while (Current.Kind != MySqlDdlTokenKind.Semicolon && Current.Kind != MySqlDdlTokenKind.End)
        {
            MySqlAdvance();
        }
    }

    private void MySqlSkipToTableItemEnd()
    {
        var depth = 0;
        while (Current.Kind != MySqlDdlTokenKind.End)
        {
            if (depth == 0 &&
                (Current.Kind == MySqlDdlTokenKind.Comma ||
                 Current.Kind == MySqlDdlTokenKind.CloseParen ||
                 Current.Kind == MySqlDdlTokenKind.Semicolon))
            {
                return;
            }

            var token = MySqlAdvance();
            if (token.Kind == MySqlDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == MySqlDdlTokenKind.CloseParen && depth > 0)
            {
                depth--;
            }
        }
    }

    private void MySqlSkipToActionEnd()
    {
        var depth = 0;
        while (Current.Kind != MySqlDdlTokenKind.End && Current.Kind != MySqlDdlTokenKind.Semicolon)
        {
            if (depth == 0 && Current.Kind == MySqlDdlTokenKind.Comma)
            {
                return;
            }

            var token = MySqlAdvance();
            if (token.Kind == MySqlDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == MySqlDdlTokenKind.CloseParen && depth > 0)
            {
                depth--;
            }
        }
    }

    private void MySqlSkipToStatementEnd()
    {
        while (Current.Kind != MySqlDdlTokenKind.Semicolon && Current.Kind != MySqlDdlTokenKind.End)
        {
            MySqlAdvance();
        }
    }

    private SqlQualifiedName MySqlParseTableName(string message)
    {
        var first = MySqlParseIdentifier(message);
        if (!MySqlMatch(MySqlDdlTokenKind.Dot))
        {
            return new SqlQualifiedName(null, first, first.Span);
        }

        var name = MySqlParseIdentifier("Expected a table name after '.'.");
        return new SqlQualifiedName(
            first,
            name,
            MySqlFromBounds(first.Span.Start, MySqlEndOf(name.Span)));
    }

    private SqlIdentifier MySqlParseIdentifier(string message)
    {
        if (MySqlIsIdentifier(Current))
        {
            var token = MySqlAdvance();
            return new SqlIdentifier(
                token.Value ?? token.Text,
                token.Kind == MySqlDdlTokenKind.QuotedIdentifier,
                token.Span);
        }

        MySqlReport("DDL100", message, Current.Span);
        var fallback = Current;
        if (Current.Kind != MySqlDdlTokenKind.End)
        {
            MySqlAdvance();
        }

        return new SqlIdentifier(fallback.Text, false, fallback.Span);
    }

    private void MySqlExpectKeyword(string keyword, string message)
    {
        if (!MySqlMatchKeyword(keyword))
        {
            MySqlReport("DDL100", message, Current.Span);
        }
    }

    private MySqlDdlToken MySqlExpect(MySqlDdlTokenKind kind, string message)
    {
        if (Current.Kind == kind)
        {
            return MySqlAdvance();
        }

        MySqlReport("DDL100", message, Current.Span);
        return new MySqlDdlToken(kind, string.Empty, null, new SourceSpan(Current.Span.Start, 0));
    }

    private bool MySqlMatchIfNotExists()
    {
        var start = _position;
        if (MySqlMatchKeyword("IF") && MySqlMatchKeyword("NOT") && MySqlMatchKeyword("EXISTS"))
        {
            return true;
        }

        _position = start;
        return false;
    }

    private bool MySqlMatchIfExists()
    {
        var start = _position;
        if (MySqlMatchKeyword("IF") && MySqlMatchKeyword("EXISTS"))
        {
            return true;
        }

        _position = start;
        return false;
    }

    private bool MySqlMatchKeyword(string keyword)
    {
        if (!MySqlIsKeyword(keyword))
        {
            return false;
        }

        MySqlAdvance();
        return true;
    }

    private bool MySqlIsKeyword(string keyword) => MySqlIsKeyword(Current, keyword);

    private static bool MySqlIsKeyword(MySqlDdlToken token, string keyword) =>
        token.Kind == MySqlDdlTokenKind.Identifier &&
        string.Equals(token.Text, keyword, StringComparison.OrdinalIgnoreCase);

    private bool MySqlIsIdentifier(MySqlDdlToken token) =>
        token.Kind == MySqlDdlTokenKind.Identifier || token.Kind == MySqlDdlTokenKind.QuotedIdentifier;

    private bool MySqlMatch(MySqlDdlTokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        MySqlAdvance();
        return true;
    }

    private MySqlDdlToken MySqlAdvance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }

        return token;
    }

    private MySqlDdlToken MySqlPeek(int offset)
    {
        var index = _position + offset;
        return index < _tokens.Count ? _tokens[index] : _tokens[_tokens.Count - 1];
    }

    private string? MySqlCurrentWord() =>
        Current.Kind == MySqlDdlTokenKind.Identifier ? Current.Text.ToUpperInvariant() : null;

    private MySqlDdlToken Current => _tokens[_position];

    private void MySqlReport(string code, string message, SourceSpan span) =>
        _diagnostics.Add(new Diagnostic(code, message, span));

    private static int MySqlEndOf(SourceSpan span) => span.Start + span.Length;

    private static int MySqlEndOf(MySqlDdlToken token) => token.Span.Start + token.Span.Length;

    private static SourceSpan MySqlFromBounds(int start, int end) =>
        new SourceSpan(start, Math.Max(0, end - start));

    private static string MySqlJoinTokens(IReadOnlyList<MySqlDdlToken> tokens, bool preserveCase)
    {
        var builder = new StringBuilder();
        MySqlDdlToken? previous = null;
        foreach (var token in tokens)
        {
            var value = token.Kind == MySqlDdlTokenKind.Identifier && !preserveCase
                ? token.Text.ToLowerInvariant()
                : token.Text;
            if (builder.Length != 0 && previous.HasValue && MySqlNeedsSpace(previous.Value, token))
            {
                builder.Append(' ');
            }

            builder.Append(value);
            previous = token;
        }

        return builder.ToString();
    }

    private static bool MySqlNeedsSpace(MySqlDdlToken previous, MySqlDdlToken current)
    {
        if (current.Kind == MySqlDdlTokenKind.CloseParen ||
            current.Kind == MySqlDdlTokenKind.Comma ||
            current.Kind == MySqlDdlTokenKind.Dot)
        {
            return false;
        }

        if (previous.Kind == MySqlDdlTokenKind.OpenParen ||
            previous.Kind == MySqlDdlTokenKind.Dot ||
            previous.Kind == MySqlDdlTokenKind.Comma)
        {
            return false;
        }

        if (current.Kind == MySqlDdlTokenKind.OpenParen)
        {
            return false;
        }

        return true;
    }
}

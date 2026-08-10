using System;
using System.Collections.Generic;
using System.Text;

namespace CobaltumOrm.Analysis;

/// <summary>Formats MySQL 8 migration DDL for compile-time migration analysis.</summary>
public sealed class MySqlMigrationSqlWriter : ISqlMigrationWriter
{
    private readonly MySqlTypeMapper _typeMapper = new MySqlTypeMapper();

    public string FormatColumn(string quotedName, string sqlType, bool? nullable, bool primaryKey, bool identity)
    {
        if (string.IsNullOrWhiteSpace(quotedName))
        {
            throw new ArgumentException("A quoted column name is required.", nameof(quotedName));
        }

        if (string.IsNullOrWhiteSpace(sqlType))
        {
            throw new ArgumentException("A MySQL column type is required.", nameof(sqlType));
        }

        if (identity && (!_typeMapper.TryMap(sqlType, out var kind) || !SqlTypeMapper.IsInteger(kind)))
        {
            throw new ArgumentException(
                "MySQL AUTO_INCREMENT columns must use a signed integer type.",
                nameof(sqlType));
        }

        var builder = new StringBuilder();
        builder.Append(quotedName).Append(' ').Append(sqlType);
        if (identity)
        {
            builder.Append(" AUTO_INCREMENT");
        }

        if (nullable == false || primaryKey)
        {
            builder.Append(" NOT NULL");
        }

        if (primaryKey)
        {
            builder.Append(" PRIMARY KEY");
        }

        return builder.ToString();
    }

    public string CreateTable(string qualifiedTable, IReadOnlyList<string> columns)
    {
        if (string.IsNullOrWhiteSpace(qualifiedTable))
        {
            throw new ArgumentException("A qualified table name is required.", nameof(qualifiedTable));
        }

        if (columns is null)
        {
            throw new ArgumentNullException(nameof(columns));
        }

        return "CREATE TABLE " + qualifiedTable + " (" + string.Join(", ", columns) + ");";
    }

    public string AddColumn(string qualifiedTable, string column) =>
        "ALTER TABLE " + qualifiedTable + " ADD COLUMN " + column + ";";

    public bool TryAlterColumn(
        string qualifiedTable,
        string quotedColumn,
        string? sqlType,
        bool? nullable,
        out string? sql,
        out string? error)
    {
        sql = null;
        error = null;
        if (string.IsNullOrWhiteSpace(qualifiedTable))
        {
            error = "MySQL ALTER COLUMN requires a qualified table name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(quotedColumn))
        {
            error = "MySQL ALTER COLUMN requires a quoted column name.";
            return false;
        }

        if (sqlType is null)
        {
            error = "MySQL ALTER COLUMN requires a complete target SQL type and explicit nullability; MODIFY COLUMN replaces the complete definition.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(sqlType))
        {
            error = "MySQL ALTER COLUMN target SQL type cannot be empty.";
            return false;
        }

        if (!nullable.HasValue)
        {
            error = "MySQL ALTER COLUMN requires explicit nullability; MODIFY COLUMN replaces the complete definition and cannot safely omit NULL or NOT NULL.";
            return false;
        }

        sql = "ALTER TABLE " + qualifiedTable + " MODIFY COLUMN " + quotedColumn + " " +
            sqlType + (nullable.Value ? " NULL;" : " NOT NULL;");
        return true;
    }

    public string DropTable(string qualifiedTable) => "DROP TABLE " + qualifiedTable + ";";

    public string DropColumn(string qualifiedTable, string quotedColumn) =>
        "ALTER TABLE " + qualifiedTable + " DROP COLUMN " + quotedColumn + ";";

    public string RenameTable(string qualifiedTable, string quotedNewName) =>
        "RENAME TABLE " + qualifiedTable + " TO " + quotedNewName + ";";

    public string RenameColumn(string qualifiedTable, string quotedOldName, string quotedNewName) =>
        "ALTER TABLE " + qualifiedTable + " RENAME COLUMN " + quotedOldName + " TO " + quotedNewName + ";";
}

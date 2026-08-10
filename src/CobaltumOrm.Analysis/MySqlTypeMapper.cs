using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CobaltumOrm.Analysis;

/// <summary>Maps signed MySQL 8 scalar types and CobaltumORM migration types.</summary>
public sealed class MySqlTypeMapper : ISqlTypeMapper
{
    public bool TryMap(string sqlType, out SqlValueKind kind)
    {
        var normalized = MySqlNormalizeType(sqlType);
        if (normalized.Length == 0 || !MySqlAreTypeModifiersValid(normalized))
        {
            kind = SqlValueKind.Error;
            return false;
        }

        if (string.Equals(normalized, "char(36)", StringComparison.Ordinal))
        {
            kind = SqlValueKind.Guid;
            return true;
        }

        var baseType = MySqlRemoveTypeModifiers(normalized);
        switch (baseType)
        {
            case "bool":
            case "boolean":
                kind = SqlValueKind.Bool;
                return true;
            case "tinyint":
                kind = MySqlHasSingleModifier(normalized, 1)
                    ? SqlValueKind.Bool
                    : SqlValueKind.Int16;
                return true;
            case "smallint":
                kind = SqlValueKind.Int16;
                return true;
            case "mediumint":
            case "int":
            case "integer":
                kind = SqlValueKind.Int32;
                return true;
            case "bigint":
            case "serial":
                kind = SqlValueKind.Int64;
                return true;
            case "year":
                kind = SqlValueKind.Int32;
                return true;
            case "float":
                kind = SqlValueKind.Float;
                return true;
            case "double":
            case "real":
                kind = SqlValueKind.Double;
                return true;
            case "decimal":
            case "numeric":
                kind = SqlValueKind.Decimal;
                return true;
            case "char":
            case "character":
            case "nchar":
            case "varchar":
            case "nvarchar":
            case "tinytext":
            case "text":
            case "mediumtext":
            case "longtext":
            case "enum":
            case "set":
                kind = SqlValueKind.String;
                return true;
            case "binary":
            case "varbinary":
            case "tinyblob":
            case "blob":
            case "mediumblob":
            case "longblob":
                kind = SqlValueKind.Bytes;
                return true;
            case "json":
                kind = SqlValueKind.Json;
                return true;
            case "date":
                kind = SqlValueKind.DateOnly;
                return true;
            case "time":
                kind = SqlValueKind.TimeOnly;
                return true;
            case "datetime":
                kind = SqlValueKind.DateTime;
                return true;
            case "timestamp":
                kind = SqlValueKind.DateTime;
                return true;
            case "uuid":
                kind = SqlValueKind.Guid;
                return true;
            case "bit":
                kind = MySqlHasSingleModifier(normalized, 1)
                    ? SqlValueKind.Bool
                    : SqlValueKind.Bytes;
                return true;
            default:
                kind = SqlValueKind.Error;
                return false;
        }
    }

    public string ToClrTypeName(SqlValueKind kind, bool nullable) =>
        SqlTypeMapper.ToClrName(kind, nullable);

    public string? ToDatabaseTypeName(SqlValueKind kind)
    {
        switch (kind)
        {
            case SqlValueKind.Bool: return "tinyint(1)";
            case SqlValueKind.Int16: return "smallint";
            case SqlValueKind.Int32: return "int";
            case SqlValueKind.Int64: return "bigint";
            case SqlValueKind.Float: return "float";
            case SqlValueKind.Double: return "double";
            case SqlValueKind.Decimal: return "decimal";
            case SqlValueKind.String: return "text";
            case SqlValueKind.Json:
            case SqlValueKind.JsonBinary: return "json";
            case SqlValueKind.Guid: return "char(36)";
            case SqlValueKind.DateOnly: return "date";
            case SqlValueKind.TimeOnly: return "time";
            case SqlValueKind.DateTime:
            case SqlValueKind.DateTimeOffset: return "datetime";
            case SqlValueKind.Bytes: return "longblob";
            default: return null;
        }
    }

    public string MapMigrationType(
        string logicalType,
        int? length = null,
        int? precision = null,
        int? scale = null)
    {
        if (logicalType is null)
        {
            throw new ArgumentNullException(nameof(logicalType));
        }

        var name = logicalType.Trim().ToLowerInvariant();
        if (name.Length == 0)
        {
            throw new ArgumentException("A MySQL migration type is required.", nameof(logicalType));
        }

        if (length.HasValue && length.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "A MySQL string length must be positive.");
        }

        switch (name)
        {
            case "int16": return "smallint";
            case "int32": return "int";
            case "int64": return "bigint";
            case "boolean":
            case "bool": return "tinyint(1)";
            case "float": return "float";
            case "double": return "double";
            case "text": return "text";
            case "date": return "date";
            case "datetime": return "datetime";
            case "datetimeoffset": return "datetime";
            case "time": return "time";
            case "guid": return "char(36)";
            case "binary": return "longblob";
            case "json":
            case "jsonb": return "json";
            case "string":
                return length.HasValue
                    ? "varchar(" + length.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "text";
            case "decimal":
                if (precision.HasValue != scale.HasValue)
                {
                    throw new ArgumentException(
                        "A MySQL decimal migration type requires both precision and scale, or neither.",
                        nameof(precision));
                }

                if (!precision.HasValue)
                {
                    return "decimal";
                }

                MySqlValidateDecimalModifiers(precision.Value, scale!.Value);
                return "decimal(" + precision.Value.ToString(CultureInfo.InvariantCulture) + "," +
                    scale.Value.ToString(CultureInfo.InvariantCulture) + ")";
            default:
                throw new ArgumentException(
                    "Unknown MySQL migration type '" + logicalType + "'.",
                    nameof(logicalType));
        }
    }

    private static string MySqlNormalizeType(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var parts = value.Trim().ToLowerInvariant().Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        var normalized = string.Join(" ", parts);
        normalized = normalized.Replace("character varying", "varchar")
            .Replace("double precision", "double")
            .Replace("numeric", "decimal")
            .Replace("integer", "int")
            .Replace("character", "char")
            .Replace(" signed", string.Empty)
            .Replace("boolean", "bool");
        return normalized;
    }

    private static string MySqlRemoveTypeModifiers(string value)
    {
        var builder = new StringBuilder();
        var depth = 0;
        foreach (var character in value)
        {
            if (character == '(')
            {
                depth++;
                continue;
            }

            if (character == ')')
            {
                if (depth > 0)
                {
                    depth--;
                }

                continue;
            }

            if (depth == 0)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }

    private static bool MySqlAreTypeModifiersValid(string value)
    {
        var open = value.IndexOf('(');
        if (open < 0)
        {
            return value.IndexOf(" unsigned", StringComparison.Ordinal) < 0 &&
                value.IndexOf(" zerofill", StringComparison.Ordinal) < 0 &&
                !value.EndsWith(" unsigned", StringComparison.Ordinal);
        }

        var close = value.LastIndexOf(')');
        if (close <= open || value.IndexOf('(', close + 1) >= 0 || value.IndexOf(')', close + 1) >= 0)
        {
            return false;
        }

        var baseType = MySqlRemoveTypeModifiers(value);
        if (baseType.IndexOf(" unsigned", StringComparison.Ordinal) >= 0 ||
            baseType.IndexOf(" zerofill", StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        var modifiers = value.Substring(open + 1, close - open - 1).Split(',');
        if (modifiers.Length == 0)
        {
            return false;
        }

        var baseName = baseType.Split(' ')[0];
        if (baseName == "enum" || baseName == "set")
        {
            return value.Substring(open + 1, close - open - 1).Trim().Length != 0;
        }

        var numbers = new List<int>();
        foreach (var modifier in modifiers)
        {
            if (!int.TryParse(modifier.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                number < 0)
            {
                return false;
            }

            numbers.Add(number);
        }

        switch (baseName)
        {
            case "char":
            case "nchar":
            case "varchar":
            case "nvarchar":
            case "binary":
            case "varbinary":
                return numbers.Count == 1 && numbers[0] > 0;
            case "decimal":
                return numbers.Count <= 2 && numbers.Count > 0 &&
                    numbers[0] >= 1 && numbers[0] <= 65 &&
                    (numbers.Count == 1 || numbers[1] <= 30 && numbers[1] <= numbers[0]);
            case "float":
                return numbers.Count == 1 && numbers[0] >= 0 && numbers[0] <= 53;
            case "double":
            case "real":
                return numbers.Count == 1 && numbers[0] >= 0 && numbers[0] <= 30;
            case "time":
            case "datetime":
            case "timestamp":
                return numbers.Count == 1 && numbers[0] <= 6;
            case "tinyint":
                return numbers.Count == 1 && numbers[0] <= 255;
            case "smallint":
                return numbers.Count == 1 && numbers[0] <= 255;
            case "mediumint":
            case "int":
            case "bigint":
                return numbers.Count == 1 && numbers[0] <= 255;
            case "bit":
                return numbers.Count == 1 && numbers[0] >= 1 && numbers[0] <= 64;
            case "year":
                return numbers.Count == 1 && numbers[0] == 4;
            case "blob":
            case "tinyblob":
            case "mediumblob":
            case "longblob":
            case "text":
            case "tinytext":
            case "mediumtext":
            case "longtext":
                return numbers.Count == 1 && numbers[0] >= 0;
            default:
                return false;
        }
    }

    private static bool MySqlHasSingleModifier(string value, int expected)
    {
        var open = value.IndexOf('(');
        var close = value.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return false;
        }

        return int.TryParse(
                   value.Substring(open + 1, close - open - 1).Trim(),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var number) &&
            number == expected;
    }

    private static void MySqlValidateDecimalModifiers(int precision, int scale)
    {
        if (precision < 1 || precision > 65)
        {
            throw new ArgumentOutOfRangeException(nameof(precision), "MySQL decimal precision must be between 1 and 65.");
        }

        if (scale < 0 || scale > 30 || scale > precision)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "MySQL decimal scale must be between 0 and 30 and no greater than precision.");
        }
    }
}

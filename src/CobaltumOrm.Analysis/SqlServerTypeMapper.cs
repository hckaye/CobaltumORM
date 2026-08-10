using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CobaltumOrm.Analysis;

/// <summary>Maps SQL Server types to the common compile-time type model.</summary>
public sealed class SqlServerTypeMapper : ISqlTypeMapper
{
    public bool TryMap(string sqlType, out SqlValueKind kind)
    {
        var normalized = SqlServerNormalizeType(sqlType);
        var baseType = SqlServerRemoveTypeModifiers(normalized);
        if (normalized.IndexOf('(') >= 0 && !SqlServerAreTypeModifiersValid(normalized, baseType))
        {
            kind = SqlValueKind.Error;
            return false;
        }

        switch (baseType)
        {
            case "tinyint":
            case "smallint":
                kind = SqlValueKind.Int16;
                return true;
            case "int":
            case "integer":
                kind = SqlValueKind.Int32;
                return true;
            case "bigint":
                kind = SqlValueKind.Int64;
                return true;
            case "bit":
                kind = SqlValueKind.Bool;
                return true;
            case "decimal":
            case "numeric":
            case "money":
            case "smallmoney":
                kind = SqlValueKind.Decimal;
                return true;
            case "real":
                kind = SqlValueKind.Float;
                return true;
            case "float":
                kind = SqlServerFloatKind(normalized);
                return true;
            case "char":
            case "character":
            case "varchar":
            case "character varying":
            case "nchar":
            case "nvarchar":
            case "text":
            case "ntext":
            case "sysname":
            case "xml":
                kind = SqlValueKind.String;
                return true;
            case "binary":
            case "varbinary":
            case "image":
            case "rowversion":
            case "timestamp":
                kind = SqlValueKind.Bytes;
                return true;
            case "date":
                kind = SqlValueKind.DateOnly;
                return true;
            case "time":
                kind = SqlValueKind.TimeOnly;
                return true;
            case "datetime":
            case "smalldatetime":
            case "datetime2":
                kind = SqlValueKind.DateTime;
                return true;
            case "datetimeoffset":
                kind = SqlValueKind.DateTimeOffset;
                return true;
            case "uniqueidentifier":
                kind = SqlValueKind.Guid;
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
            case SqlValueKind.Bool: return "bit";
            case SqlValueKind.Int16: return "smallint";
            case SqlValueKind.Int32: return "int";
            case SqlValueKind.Int64: return "bigint";
            case SqlValueKind.Float: return "real";
            case SqlValueKind.Double: return "float";
            case SqlValueKind.Decimal: return "decimal";
            case SqlValueKind.String: return "nvarchar(max)";
            case SqlValueKind.Json:
            case SqlValueKind.JsonBinary: return "nvarchar(max)";
            case SqlValueKind.Guid: return "uniqueidentifier";
            case SqlValueKind.DateOnly: return "date";
            case SqlValueKind.TimeOnly: return "time";
            case SqlValueKind.DateTime: return "datetime2";
            case SqlValueKind.DateTimeOffset: return "datetimeoffset";
            case SqlValueKind.Bytes: return "varbinary(max)";
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

        var normalized = logicalType.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "int16":
            case "smallint":
                return "smallint";
            case "int32":
            case "int":
            case "integer":
                return "int";
            case "int64":
            case "bigint":
                return "bigint";
            case "boolean":
            case "bool":
            case "bit":
                return "bit";
            case "decimal":
            case "numeric":
                return SqlServerDecimalMigrationType(precision, scale);
            case "money":
                SqlServerRejectUnexpectedMigrationModifier(length, precision, scale, normalized);
                return "money";
            case "float":
            case "single":
            case "real":
                return "real";
            case "double":
            case "double precision":
                return "float";
            case "string":
            case "varchar":
            case "nvarchar":
                return SqlServerStringMigrationType(length);
            case "text":
            case "ntext":
            case "json":
            case "jsonb":
                SqlServerRejectUnexpectedMigrationModifier(length, precision, scale, normalized);
                return "nvarchar(max)";
            case "xml":
                SqlServerRejectUnexpectedMigrationModifier(length, precision, scale, normalized);
                return "xml";
            case "binary":
            case "varbinary":
                return SqlServerBinaryMigrationType(length);
            case "date":
                SqlServerRejectUnexpectedMigrationModifier(length, precision, scale, normalized);
                return "date";
            case "time":
                return SqlServerTemporalMigrationType("time", precision);
            case "datetime":
            case "datetime2":
                return SqlServerTemporalMigrationType("datetime2", precision);
            case "datetimeoffset":
                return SqlServerTemporalMigrationType("datetimeoffset", precision);
            case "guid":
            case "uuid":
            case "uniqueidentifier":
                SqlServerRejectUnexpectedMigrationModifier(length, precision, scale, normalized);
                return "uniqueidentifier";
            default:
                throw new ArgumentException(
                    $"Unknown SQL Server migration type '{logicalType}'.",
                    nameof(logicalType));
        }
    }

    private static string SqlServerNormalizeType(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var parts = value.Trim().ToLowerInvariant().Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    private static string SqlServerRemoveTypeModifiers(string value)
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

    private static SqlValueKind SqlServerFloatKind(string normalized)
    {
        var open = normalized.IndexOf('(');
        if (open < 0)
        {
            return SqlValueKind.Double;
        }

        var close = normalized.LastIndexOf(')');
        if (close <= open)
        {
            return SqlValueKind.Error;
        }

        return int.TryParse(
            normalized.Substring(open + 1, close - open - 1).Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var precision) && precision <= 24
            ? SqlValueKind.Float
            : SqlValueKind.Double;
    }

    private static bool SqlServerAreTypeModifiersValid(string value, string baseType)
    {
        var open = value.IndexOf('(');
        var close = value.LastIndexOf(')');
        if (open < 0 || close <= open || value.IndexOf('(', close + 1) >= 0 ||
            value.IndexOf(')', close + 1) >= 0)
        {
            return false;
        }

        var pieces = value.Substring(open + 1, close - open - 1).Split(',');
        if (pieces.Length == 0 || pieces.Any(SqlServerIsEmptyModifier))
        {
            return false;
        }

        switch (baseType)
        {
            case "decimal":
            case "numeric":
                if (pieces.Length > 2 || !SqlServerTryNonNegativeInt(pieces[0], out var decimalPrecision) ||
                    decimalPrecision < 1 || decimalPrecision > 38)
                {
                    return false;
                }

                return pieces.Length != 2 ||
                    SqlServerTryNonNegativeInt(pieces[1], out var decimalScale) && decimalScale <= decimalPrecision;
            case "float":
                return pieces.Length == 1 && SqlServerTryNonNegativeInt(pieces[0], out var floatPrecision) &&
                    floatPrecision >= 1 && floatPrecision <= 53;
            case "time":
            case "datetime2":
            case "datetimeoffset":
                return pieces.Length == 1 && SqlServerTryNonNegativeInt(pieces[0], out var temporalPrecision) &&
                    temporalPrecision <= 7;
            case "char":
            case "character":
            case "varchar":
            case "character varying":
            case "nchar":
            case "nvarchar":
            case "binary":
            case "varbinary":
                if (pieces.Length != 1)
                {
                    return false;
                }

                if (string.Equals(pieces[0].Trim(), "max", StringComparison.OrdinalIgnoreCase))
                {
                    return baseType == "varchar" || baseType == "character varying" ||
                        baseType == "nvarchar" || baseType == "varbinary";
                }

                return SqlServerTryNonNegativeInt(pieces[0], out var length) && length > 0 &&
                    (baseType == "nchar" || baseType == "nvarchar" ? length <= 4000 : length <= 8000);
            default:
                return false;
        }
    }

    private static bool SqlServerIsEmptyModifier(string value) => string.IsNullOrWhiteSpace(value);

    private static bool SqlServerTryNonNegativeInt(string value, out int result) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;

    private static string SqlServerDecimalMigrationType(int? precision, int? scale)
    {
        if (!precision.HasValue)
        {
            if (scale.HasValue)
            {
                throw new ArgumentException("SQL Server decimal scale requires a precision.", nameof(scale));
            }

            return "decimal";
        }

        if (precision.Value < 1 || precision.Value > 38)
        {
            throw new ArgumentOutOfRangeException(nameof(precision), "SQL Server decimal precision must be between 1 and 38.");
        }

        var selectedScale = scale ?? 0;
        if (selectedScale < 0 || selectedScale > precision.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "SQL Server decimal scale must be between zero and precision.");
        }

        return "decimal(" + precision.Value.ToString(CultureInfo.InvariantCulture) + "," +
            selectedScale.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static string SqlServerStringMigrationType(int? length)
    {
        if (!length.HasValue)
        {
            return "nvarchar(max)";
        }

        if (length.Value < 1 || length.Value > 4000)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "SQL Server nvarchar length must be between 1 and 4000.");
        }

        return "nvarchar(" + length.Value.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static string SqlServerBinaryMigrationType(int? length)
    {
        if (!length.HasValue)
        {
            return "varbinary(max)";
        }

        if (length.Value < 1 || length.Value > 8000)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "SQL Server varbinary length must be between 1 and 8000.");
        }

        return "varbinary(" + length.Value.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static string SqlServerTemporalMigrationType(string type, int? precision)
    {
        if (!precision.HasValue)
        {
            return type;
        }

        if (precision.Value < 0 || precision.Value > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(precision), "SQL Server temporal precision must be between zero and 7.");
        }

        return type + "(" + precision.Value.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static void SqlServerRejectUnexpectedMigrationModifier(
        int? length,
        int? precision,
        int? scale,
        string logicalType)
    {
        if (length.HasValue || precision.HasValue || scale.HasValue)
        {
            throw new ArgumentException(
                $"SQL Server migration type '{logicalType}' does not accept length, precision, or scale modifiers.",
                nameof(logicalType));
        }
    }
}

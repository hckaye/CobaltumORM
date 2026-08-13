using System;
using System.ComponentModel;
using System.Data.Common;
using System.Text;

namespace CobaltumOrm;

/// <summary>Runtime read helpers used by generated result mappers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class CobaltumResultReader
{
    /// <summary>Reads a generated result member by normalized column name.</summary>
    public static TResult Read<TResult>(
        DbDataReader reader,
        string memberName,
        string context,
        bool allowNull)
    {
        if (reader is null)
        {
            throw new ArgumentNullException(nameof(reader));
        }

        var ordinal = GetOrdinal(reader, memberName);
        if (reader.IsDBNull(ordinal))
        {
            if (allowNull)
            {
                return default!;
            }

            throw new InvalidOperationException(
                $"Column '{reader.GetName(ordinal)}' returned database null for non-nullable result member '{context}'.");
        }

        try
        {
            return reader.GetFieldValue<TResult>(ordinal);
        }
        catch (Exception exception) when (exception is InvalidCastException ||
                                          exception is FormatException ||
                                          exception is OverflowException)
        {
            throw new InvalidOperationException(
                $"Column '{reader.GetName(ordinal)}' cannot be read as the CLR type required by result member '{context}'.",
                exception);
        }
    }

    /// <summary>Gets one column ordinal using generated result-name matching.</summary>
    public static int GetOrdinal(DbDataReader reader, string memberName)
    {
        if (reader is null)
        {
            throw new ArgumentNullException(nameof(reader));
        }

        if (memberName is null)
        {
            throw new ArgumentNullException(nameof(memberName));
        }

        var normalizedMemberName = NormalizeName(memberName);
        var match = -1;
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            if (!string.Equals(
                    NormalizeName(reader.GetName(ordinal)),
                    normalizedMemberName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (match >= 0)
            {
                throw new InvalidOperationException(
                    $"More than one returned column matches result member '{memberName}'.");
            }

            match = ordinal;
        }

        if (match < 0)
        {
            throw new InvalidOperationException(
                $"No returned column matches result member '{memberName}'.");
        }

        return match;
    }

    private static string NormalizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }
}

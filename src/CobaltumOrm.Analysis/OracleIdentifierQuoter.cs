using System;

namespace CobaltumOrm.Analysis;

/// <summary>Safely double-quotes Oracle identifiers and qualified names.</summary>
public sealed class OracleIdentifierQuoter : ISqlIdentifierQuoter
{
    public string QuoteIdentifier(string identifier)
    {
        if (identifier is null)
        {
            throw new ArgumentNullException(nameof(identifier));
        }

        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("An Oracle identifier is required.", nameof(identifier));
        }

        if (identifier.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                "Oracle identifiers cannot contain a null character.",
                nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    public string QuoteQualifiedName(string? schema, string name)
    {
        return string.IsNullOrEmpty(schema)
            ? QuoteIdentifier(name)
            : QuoteIdentifier(schema!) + "." + QuoteIdentifier(name);
    }
}

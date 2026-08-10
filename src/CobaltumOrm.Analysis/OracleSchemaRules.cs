using System;

namespace CobaltumOrm.Analysis;

/// <summary>Applies Oracle's current-user schema and identifier case rules.</summary>
public sealed class OracleSchemaRules : ISchemaRules
{
    public bool SupportsSchemas => true;

    // The current user/schema is session state and is not known during analysis.
    public string? DefaultSchema => null;

    public bool IsDefaultSchema(string? schema) => string.IsNullOrEmpty(schema);

    public string NormalizeUnquotedIdentifier(string identifier) =>
        (identifier ?? throw new ArgumentNullException(nameof(identifier))).ToUpperInvariant();

    public string NormalizeQuotedIdentifier(string identifier) =>
        identifier ?? throw new ArgumentNullException(nameof(identifier));

    public bool AreIdentifiersEqual(string reference, bool referenceIsQuoted, string declared)
    {
        if (reference is null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

        if (declared is null)
        {
            throw new ArgumentNullException(nameof(declared));
        }

        return referenceIsQuoted
            ? string.Equals(reference, declared, StringComparison.Ordinal)
            : string.Equals(reference.ToUpperInvariant(), declared, StringComparison.Ordinal);
    }
}

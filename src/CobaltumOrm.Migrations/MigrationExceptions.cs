using System;

namespace CobaltumOrm.Migrations;

/// <summary>Reports an invalid migration declaration or inconsistent history.</summary>
public sealed class MigrationValidationException : Exception
{
    /// <summary>Initializes a validation exception.</summary>
    public MigrationValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a validation exception with an underlying cause.</summary>
    public MigrationValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

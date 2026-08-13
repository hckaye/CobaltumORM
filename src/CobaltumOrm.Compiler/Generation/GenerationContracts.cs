using System;
using System.Collections.Generic;

namespace CobaltumOrm.Compiler;

/// <summary>Describes what a generated artifact is used for.</summary>
public enum GeneratedArtifactKind
{
    /// <summary>A file that only contains generated code.</summary>
    Generated,

    /// <summary>A rewritten copy of a project source file that contained raw queries.</summary>
    Transformed,
}

/// <summary>The inputs a single generation run needs.</summary>
public sealed class GenerationRequest
{
    /// <summary>Project C# sources, as absolute paths.</summary>
    public IReadOnlyList<string> SourcePaths { get; set; } = Array.Empty<string>();

    /// <summary>Resolved reference assemblies, as absolute paths.</summary>
    public IReadOnlyList<string> ReferencePaths { get; set; } = Array.Empty<string>();

    /// <summary>Additional files, as absolute paths. SQL migrations are read from here.</summary>
    public IReadOnlyList<string> AdditionalFilePaths { get; set; } = Array.Empty<string>();

    /// <summary>C# migration sources owned by a referenced migration project.</summary>
    public IReadOnlyList<string> MigrationSourcePaths { get; set; } = Array.Empty<string>();

    /// <summary>Directory the generated files are written to. Only used to build file paths.</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>The compiler DefineConstants value.</summary>
    public string? DefineConstants { get; set; }

    /// <summary>The compiler LangVersion value.</summary>
    public string? LangVersion { get; set; }

    /// <summary>The compiler Nullable value.</summary>
    public string? Nullable { get; set; }

    /// <summary>The namespace generated code is placed in.</summary>
    public string? GeneratedNamespace { get; set; }

    /// <summary>The configured database provider name.</summary>
    public string? DatabaseProvider { get; set; }

    /// <summary>Directory used for successful SQL analysis cache entries.</summary>
    public string? AnalysisCacheDirectory { get; set; }

    /// <summary>Whether persistent SQL analysis caching is enabled.</summary>
    public bool AnalysisCacheEnabled { get; set; } = true;

    /// <summary>
    /// Runs the incremental source generator over the transformed compilation and returns its
    /// files as well. The MSBuild path leaves this off because the C# compiler runs the
    /// generator itself.
    /// </summary>
    public bool IncludeGeneratorOutput { get; set; }
}

/// <summary>A single file produced by a generation run.</summary>
public sealed class GeneratedArtifact
{
    /// <summary>Creates an artifact.</summary>
    public GeneratedArtifact(string fileName, string text, GeneratedArtifactKind kind, string? sourcePath)
    {
        FileName = fileName;
        Text = text;
        Kind = kind;
        SourcePath = sourcePath;
    }

    /// <summary>File name inside the output directory.</summary>
    public string FileName { get; }

    /// <summary>File contents.</summary>
    public string Text { get; }

    /// <summary>What the file is used for.</summary>
    public GeneratedArtifactKind Kind { get; }

    /// <summary>For a transformed artifact, the project source it replaces.</summary>
    public string? SourcePath { get; }
}

/// <summary>A diagnostic raised while analyzing SQL, migrations, or query call sites.</summary>
public sealed class GenerationDiagnostic
{
    /// <summary>Creates a diagnostic.</summary>
    public GenerationDiagnostic(
        string code,
        string message,
        string? filePath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        bool isError)
    {
        Code = code;
        Message = message;
        FilePath = filePath;
        StartLine = startLine;
        StartColumn = startColumn;
        EndLine = endLine;
        EndColumn = endColumn;
        IsError = isError;
    }

    /// <summary>The diagnostic identifier, for example SQL203 or COB104.</summary>
    public string Code { get; }

    /// <summary>The diagnostic text.</summary>
    public string Message { get; }

    /// <summary>The file the diagnostic points at, when there is one.</summary>
    public string? FilePath { get; }

    /// <summary>One-based start line, or zero when there is no location.</summary>
    public int StartLine { get; }

    /// <summary>One-based start column, or zero when there is no location.</summary>
    public int StartColumn { get; }

    /// <summary>One-based end line, or zero when there is no location.</summary>
    public int EndLine { get; }

    /// <summary>One-based end column, or zero when there is no location.</summary>
    public int EndColumn { get; }

    /// <summary>True when the diagnostic stops generation.</summary>
    public bool IsError { get; }
}

/// <summary>The outcome of a generation run.</summary>
public sealed class GenerationResult
{
    /// <summary>Creates a result.</summary>
    public GenerationResult(
        bool succeeded,
        IReadOnlyList<GenerationDiagnostic> diagnostics,
        IReadOnlyList<GeneratedArtifact> artifacts,
        IReadOnlyList<string> analyzedSourcePaths,
        IReadOnlyList<string> processedSourcePaths)
    {
        Succeeded = succeeded;
        Diagnostics = diagnostics;
        Artifacts = artifacts;
        AnalyzedSourcePaths = analyzedSourcePaths;
        ProcessedSourcePaths = processedSourcePaths;
    }

    /// <summary>True when no error was raised.</summary>
    public bool Succeeded { get; }

    /// <summary>Every diagnostic raised during the run.</summary>
    public IReadOnlyList<GenerationDiagnostic> Diagnostics { get; }

    /// <summary>Every file the run produced.</summary>
    public IReadOnlyList<GeneratedArtifact> Artifacts { get; }

    /// <summary>The project sources that were analyzed, in the order they were parsed.</summary>
    public IReadOnlyList<string> AnalyzedSourcePaths { get; }

    /// <summary>The project sources replaced by a transformed artifact.</summary>
    public IReadOnlyList<string> ProcessedSourcePaths { get; }
}

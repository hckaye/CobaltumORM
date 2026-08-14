using System.Reflection;
using CobaltumOrm.Compiler;
using ModelContextProtocol;

namespace CobaltumOrm.Tool;

internal sealed class McpDocumentation
{
    public const string MarkdownMimeType = "text/markdown; charset=utf-8";
    public const string PlainTextMimeType = "text/plain; charset=utf-8";

    public const string QuickReferenceEnglishUri = "cobaltum://docs/quick-reference/en";
    public const string QuickReferenceJapaneseUri = "cobaltum://docs/quick-reference/ja";
    public const string RecipesEnglishUri = "cobaltum://docs/recipes/en";
    public const string RecipesJapaneseUri = "cobaltum://docs/recipes/ja";
    public const string DiagnosticsEnglishUri = "cobaltum://docs/diagnostics/en";
    public const string DiagnosticsJapaneseUri = "cobaltum://docs/diagnostics/ja";
    public const string LlmsTextUri = "cobaltum://docs/llms.txt";

    private const string ResourcePrefix = "CobaltumOrm.Tool.Mcp.Resources.";

    private readonly IReadOnlyDictionary<string, McpEmbeddedDocument> _documents;

    private McpDocumentation(IReadOnlyDictionary<string, McpEmbeddedDocument> documents)
    {
        _documents = documents;
    }

    public static McpDocumentation Load()
    {
        var assembly = typeof(McpDocumentation).GetTypeInfo().Assembly;
        var documents = new[]
        {
            Read(assembly, QuickReferenceEnglishUri, MarkdownMimeType, "quick-reference.md"),
            Read(assembly, QuickReferenceJapaneseUri, MarkdownMimeType, "quick-reference.ja.md"),
            Read(assembly, RecipesEnglishUri, MarkdownMimeType, "recipes.md"),
            Read(assembly, RecipesJapaneseUri, MarkdownMimeType, "recipes.ja.md"),
            Read(assembly, DiagnosticsEnglishUri, MarkdownMimeType, "diagnostics.md"),
            Read(assembly, DiagnosticsJapaneseUri, MarkdownMimeType, "diagnostics.ja.md"),
            Read(assembly, LlmsTextUri, PlainTextMimeType, "llms.txt"),
        };
        return new McpDocumentation(documents.ToDictionary(document => document.Uri, StringComparer.Ordinal));
    }

    public McpEmbeddedDocument Get(string uri) => _documents.TryGetValue(uri, out var document)
        ? document
        : throw new InvalidOperationException($"The embedded MCP resource '{uri}' is not registered.");

    public McpDiagnosticExplanation ExplainDiagnostic(string? code, string? language)
    {
        var normalizedCode = code?.Trim().ToUpperInvariant() ?? string.Empty;
        var helpUri = GenerationDiagnosticHelpLinks.ForCode(normalizedCode);
        if (helpUri is null)
        {
            throw new McpException(
                $"Diagnostic code '{Display(code)}' is not documented. " +
                "Use COB001-COB010 or COB100-COB109.");
        }

        var normalizedLanguage = language?.Trim().ToLowerInvariant();
        var documentUri = normalizedLanguage switch
        {
            "en" => DiagnosticsEnglishUri,
            "ja" => DiagnosticsJapaneseUri,
            _ => throw new McpException(
                $"Language '{Display(language)}' is not supported. Use 'en' or 'ja'."),
        };
        var section = ExtractDiagnosticSection(Get(documentUri).Text, normalizedCode);
        return new McpDiagnosticExplanation(
            ProjectInspectionOutput.FormatVersion,
            normalizedCode,
            normalizedLanguage,
            helpUri,
            section);
    }

    private static McpEmbeddedDocument Read(
        Assembly assembly,
        string uri,
        string mimeType,
        string resourceFileName)
    {
        var resourceName = ResourcePrefix + resourceFileName;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The embedded MCP resource '{resourceName}' is missing from the tool.");
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return new McpEmbeddedDocument(uri, mimeType, reader.ReadToEnd());
    }

    private static string ExtractDiagnosticSection(string document, string code)
    {
        var heading = "### " + code;
        var start = document.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0 || (start != 0 && document[start - 1] != '\n'))
        {
            throw new InvalidOperationException(
                $"The embedded diagnostics document does not contain '{code}'.");
        }

        var end = document.IndexOf("\n### ", start + heading.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            end = document.Length;
        }

        return document.Substring(start, end - start).TrimEnd('\r', '\n');
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
}

internal sealed record McpEmbeddedDocument(string Uri, string MimeType, string Text);

internal sealed record McpDiagnosticExplanation(
    int FormatVersion,
    string Code,
    string Language,
    string HelpUri,
    string Section);

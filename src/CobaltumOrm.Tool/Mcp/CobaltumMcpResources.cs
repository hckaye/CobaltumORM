using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CobaltumOrm.Tool;

[McpServerResourceType]
internal sealed class CobaltumMcpResources
{
    private readonly McpDocumentation _documentation;

    public CobaltumMcpResources(McpDocumentation documentation)
    {
        _documentation = documentation;
    }

    [McpServerResource(
        UriTemplate = McpDocumentation.QuickReferenceEnglishUri,
        Name = "quick_reference_en",
        Title = "CobaltumORM quick reference (English)",
        MimeType = McpDocumentation.MarkdownMimeType)]
    [Description("Returns the embedded English quick reference for CobaltumORM queries, migrations, and project setup.")]
    public TextResourceContents QuickReferenceEnglish() =>
        Contents(McpDocumentation.QuickReferenceEnglishUri);

    [McpServerResource(
        UriTemplate = McpDocumentation.QuickReferenceJapaneseUri,
        Name = "quick_reference_ja",
        Title = "CobaltumORM quick reference (Japanese)",
        MimeType = McpDocumentation.MarkdownMimeType)]
    [Description("Returns the embedded Japanese quick reference for CobaltumORM queries, migrations, and project setup.")]
    public TextResourceContents QuickReferenceJapanese() =>
        Contents(McpDocumentation.QuickReferenceJapaneseUri);

    [McpServerResource(
        UriTemplate = McpDocumentation.RecipesEnglishUri,
        Name = "recipes_en",
        Title = "CobaltumORM recipes (English)",
        MimeType = McpDocumentation.MarkdownMimeType)]
    [Description("Returns the embedded English CobaltumORM recipes with checked query and migration examples.")]
    public TextResourceContents RecipesEnglish() => Contents(McpDocumentation.RecipesEnglishUri);

    [McpServerResource(
        UriTemplate = McpDocumentation.RecipesJapaneseUri,
        Name = "recipes_ja",
        Title = "CobaltumORM recipes (Japanese)",
        MimeType = McpDocumentation.MarkdownMimeType)]
    [Description("Returns the embedded Japanese CobaltumORM recipes with checked query and migration examples.")]
    public TextResourceContents RecipesJapanese() => Contents(McpDocumentation.RecipesJapaneseUri);

    [McpServerResource(
        UriTemplate = McpDocumentation.DiagnosticsEnglishUri,
        Name = "diagnostics_en",
        Title = "CobaltumORM diagnostics (English)",
        MimeType = McpDocumentation.MarkdownMimeType)]
    [Description("Returns the embedded English reference for documented CobaltumORM build diagnostics.")]
    public TextResourceContents DiagnosticsEnglish() =>
        Contents(McpDocumentation.DiagnosticsEnglishUri);

    [McpServerResource(
        UriTemplate = McpDocumentation.DiagnosticsJapaneseUri,
        Name = "diagnostics_ja",
        Title = "CobaltumORM diagnostics (Japanese)",
        MimeType = McpDocumentation.MarkdownMimeType)]
    [Description("Returns the embedded Japanese reference for documented CobaltumORM build diagnostics.")]
    public TextResourceContents DiagnosticsJapanese() =>
        Contents(McpDocumentation.DiagnosticsJapaneseUri);

    [McpServerResource(
        UriTemplate = McpDocumentation.LlmsTextUri,
        Name = "llms_txt",
        Title = "CobaltumORM llms.txt",
        MimeType = McpDocumentation.PlainTextMimeType)]
    [Description("Returns the embedded CobaltumORM llms.txt index and documentation links.")]
    public TextResourceContents LlmsText() => Contents(McpDocumentation.LlmsTextUri);

    private TextResourceContents Contents(string uri)
    {
        var document = _documentation.Get(uri);
        return new TextResourceContents
        {
            Uri = document.Uri,
            MimeType = document.MimeType,
            Text = document.Text,
        };
    }
}

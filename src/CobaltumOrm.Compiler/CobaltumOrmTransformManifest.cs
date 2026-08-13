using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace CobaltumOrm.Compiler;

internal static class CobaltumOrmTransformManifest
{
    private const string ManifestVersion = "1";

    internal static void WriteInputManifest(
        string path,
        IEnumerable<TransformInputPath> sourcePaths,
        IEnumerable<TransformInputPath> migrationPaths,
        IEnumerable<TransformInputPath> additionalPaths,
        IEnumerable<TransformInputPath> referencePaths,
        IEnumerable<KeyValuePair<string, string?>> properties)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "CobaltumOrmTransformInputs",
                new XAttribute("version", ManifestVersion),
                PathElements("Sources", sourcePaths),
                PathElements("MigrationSources", migrationPaths),
                PathElements("AdditionalFiles", additionalPaths),
                PathElements("References", referencePaths),
                new XElement(
                    "Properties",
                    properties.Select(property => new XElement(
                        "Property",
                        new XAttribute("name", property.Key),
                        new XAttribute("value", property.Value ?? string.Empty))))));

        WriteIfChanged(path, document.ToString() + Environment.NewLine);
    }

    internal static void WriteSuccessManifest(
        string path,
        IEnumerable<ITaskItem> processedSources,
        IEnumerable<ITaskItem> transformedSources)
    {
        var transformed = transformedSources.ToArray();
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "CobaltumOrmTransformSuccess",
                new XAttribute("version", ManifestVersion),
                new XElement(
                    "ProcessedSources",
                    processedSources.Select(source => new XElement(
                        "Source",
                        new XAttribute("itemSpec", source.ItemSpec)))),
                new XElement(
                    "TransformedSources",
                    transformed.Select(CreateTransformedSourceElement)),
                new XElement(
                    "Outputs",
                    transformed
                        .Select(item => FullPath(item))
                        .Where(pathValue => pathValue != null)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(pathValue => new XElement(
                            "Output",
                            new XAttribute("path", pathValue!))))));

        WriteAlways(path, document.ToString() + Environment.NewLine);
    }

    internal static bool TryReadSuccessManifest(
        string path,
        out TransformManifestData manifest)
    {
        manifest = TransformManifestData.Empty;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root == null ||
                root.Name != "CobaltumOrmTransformSuccess" ||
                (string?)root.Attribute("version") != ManifestVersion)
            {
                return false;
            }

            var processedSourceElements = root.Element("ProcessedSources");
            var transformedSourceElements = root.Element("TransformedSources");
            var outputElements = root.Element("Outputs");
            if (processedSourceElements == null ||
                transformedSourceElements == null ||
                outputElements == null)
            {
                return false;
            }

            var processedSources = processedSourceElements.Elements("Source")
                .Select(element => (string?)element.Attribute("itemSpec"))
                .Where(itemSpec => !string.IsNullOrEmpty(itemSpec))
                .Select(itemSpec => new TaskItem(itemSpec!))
                .Cast<ITaskItem>()
                .ToArray();
            var transformedSources = transformedSourceElements.Elements("Source")
                .Select(CreateTaskItem)
                .Where(item => item != null)
                .Cast<ITaskItem>()
                .ToArray();
            var outputs = outputElements.Elements("Output")
                .Select(element => (string?)element.Attribute("path"))
                .Where(outputPath => !string.IsNullOrEmpty(outputPath))
                .Select(outputPath => new TaskItem(outputPath!))
                .Cast<ITaskItem>()
                .ToArray();

            var outputPaths = new HashSet<string>(
                outputs
                    .Select(FullPath)
                    .Where(outputPath => outputPath != null)
                    .Cast<string>(),
                StringComparer.OrdinalIgnoreCase);
            if (transformedSources
                .Select(FullPath)
                .Where(transformedPath => transformedPath != null)
                .Any(transformedPath => !outputPaths.Contains(transformedPath!)))
            {
                return false;
            }

            manifest = new TransformManifestData(processedSources, transformedSources, outputs);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string? FullPath(ITaskItem item)
    {
        var itemSpec = item.ItemSpec;
        if (string.IsNullOrWhiteSpace(itemSpec))
        {
            return null;
        }

        var fullPath = item.GetMetadata("FullPath");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(fullPath) ? itemSpec : fullPath);
    }

    internal static string NormalizePath(string path)
    {
        return Path.GetFullPath(path);
    }

    private static IEnumerable<XElement> PathElements(
        string elementName,
        IEnumerable<TransformInputPath> paths)
    {
        yield return new XElement(
            elementName,
            paths.Select(CreatePathElement));
    }

    private static XElement CreatePathElement(TransformInputPath input)
    {
        var path = input.Path;
        var exists = File.Exists(path);
        return new XElement(
            "Path",
            new XAttribute("value", path),
            new XAttribute("itemSpec", input.ItemSpec),
            new XAttribute("exists", exists ? "true" : "false"),
            new XAttribute(
                "lastWriteTimeUtc",
                exists
                    ? File.GetLastWriteTimeUtc(path).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty),
            new XAttribute(
                "length",
                exists
                    ? new FileInfo(path).Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "-1"));
    }

    private static XElement CreateTransformedSourceElement(ITaskItem item)
    {
        var element = new XElement(
            "Source",
            new XAttribute("itemSpec", item.ItemSpec));
        AddMetadata(element, item, "CobaltumOrmTransformed");
        AddMetadata(element, item, "AutoGen");
        AddMetadata(element, item, "DesignTime");
        AddMetadata(element, item, "Visible");
        return element;
    }

    private static void AddMetadata(XElement element, ITaskItem item, string name)
    {
        var value = item.GetMetadata(name);
        if (!string.IsNullOrEmpty(value))
        {
            element.Add(new XAttribute(name, value));
        }
    }

    private static ITaskItem? CreateTaskItem(XElement element)
    {
        var itemSpec = (string?)element.Attribute("itemSpec");
        if (string.IsNullOrEmpty(itemSpec))
        {
            return null;
        }

        var item = new TaskItem(itemSpec);
        SetMetadata(item, element, "CobaltumOrmTransformed");
        SetMetadata(item, element, "AutoGen");
        SetMetadata(item, element, "DesignTime");
        SetMetadata(item, element, "Visible");
        return item;
    }

    private static void SetMetadata(TaskItem item, XElement element, string name)
    {
        var value = (string?)element.Attribute(name);
        if (value != null)
        {
            item.SetMetadata(name, value);
        }
    }

    private static void WriteIfChanged(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteAlways(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

internal sealed class TransformInputPath
{
    internal TransformInputPath(string path, string itemSpec)
    {
        Path = path;
        ItemSpec = itemSpec;
    }

    internal string Path { get; }

    internal string ItemSpec { get; }
}

internal sealed class TransformManifestData
{
    internal static readonly TransformManifestData Empty = new(
        Array.Empty<ITaskItem>(),
        Array.Empty<ITaskItem>(),
        Array.Empty<ITaskItem>());

    internal TransformManifestData(
        ITaskItem[] processedSources,
        ITaskItem[] transformedSources,
        ITaskItem[] outputs)
    {
        ProcessedSources = processedSources;
        TransformedSources = transformedSources;
        Outputs = outputs;
    }

    internal ITaskItem[] ProcessedSources { get; }

    internal ITaskItem[] TransformedSources { get; }

    internal ITaskItem[] Outputs { get; }
}

public sealed class CobaltumOrmCollectTransformInputsTask : Task
{
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] References { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] AdditionalFiles { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] MigrationSources { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public string InputManifestPath { get; set; } = string.Empty;

    [Required]
    public string SuccessManifestPath { get; set; } = string.Empty;

    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    public string? DefineConstants { get; set; }

    public string? LangVersion { get; set; }

    public string? Nullable { get; set; }

    public string? GeneratedNamespace { get; set; }

    public string? CobaltumOrmDatabaseProvider { get; set; }

    public string? CobaltumOrmCompileTimeQueries { get; set; }

    public string? ProjectPath { get; set; }

    public string? TaskAssemblyPath { get; set; }

    [Output]
    public ITaskItem[] InputFiles { get; private set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] CachedOutputs { get; private set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        try
        {
            var sourcePaths = Paths(Sources)
                .Where(input => !IsGeneratedSource(input.Path))
                .ToArray();
            var migrationPaths = Paths(MigrationSources).ToArray();
            var additionalPaths = Paths(AdditionalFiles).ToArray();
            var referencePaths = Paths(References).ToArray();
            var allInputPaths = sourcePaths
                .Concat(migrationPaths)
                .Concat(additionalPaths)
                .Concat(referencePaths)
                .Select(input => input.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new TaskItem(path))
                .Cast<ITaskItem>()
                .ToArray();

            CobaltumOrmTransformManifest.WriteInputManifest(
                InputManifestPath,
                sourcePaths,
                migrationPaths,
                additionalPaths,
                referencePaths,
                new[]
                {
                    new KeyValuePair<string, string?>("DefineConstants", DefineConstants),
                    new KeyValuePair<string, string?>("LangVersion", LangVersion),
                    new KeyValuePair<string, string?>("Nullable", Nullable),
                    new KeyValuePair<string, string?>("GeneratedNamespace", GeneratedNamespace),
                    new KeyValuePair<string, string?>("CobaltumOrmDatabaseProvider", CobaltumOrmDatabaseProvider),
                    new KeyValuePair<string, string?>("CobaltumOrmCompileTimeQueries", CobaltumOrmCompileTimeQueries),
                    new KeyValuePair<string, string?>("ProjectPath", ProjectPath),
                    new KeyValuePair<string, string?>("TaskAssemblyPath", TaskAssemblyPath),
                    new KeyValuePair<string, string?>("OutputDirectory", CobaltumOrmTransformManifest.NormalizePath(OutputDirectory)),
                    new KeyValuePair<string, string?>("InputManifestPath", CobaltumOrmTransformManifest.NormalizePath(InputManifestPath)),
                    new KeyValuePair<string, string?>("SuccessManifestPath", CobaltumOrmTransformManifest.NormalizePath(SuccessManifestPath)),
                });

            InputFiles = allInputPaths;
            CachedOutputs = ReadCachedOutputs(SuccessManifestPath);
            return true;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: true);
            return false;
        }
    }

    private static IEnumerable<TransformInputPath> Paths(IEnumerable<ITaskItem> items)
    {
        return items
            .Select(item =>
            {
                var path = CobaltumOrmTransformManifest.FullPath(item);
                return path == null ? null : new TransformInputPath(path, item.ItemSpec);
            })
            .Where(input => input != null)
            .Cast<TransformInputPath>()
            .GroupBy(input => input.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(input => input.Path, StringComparer.Ordinal);
    }

    private static bool IsGeneratedSource(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using (var reader = new StreamReader(path, Encoding.UTF8, true, 1024))
        {
            for (var line = 0; line < 4 && !reader.EndOfStream; line++)
            {
                var lineText = (reader.ReadLine() ?? string.Empty)
                    .Replace("-", string.Empty)
                    .Replace(" ", string.Empty)
                    .Replace("\t", string.Empty);
                if (lineText.IndexOf("<autogenerated", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ITaskItem[] ReadCachedOutputs(string path)
    {
        if (CobaltumOrmTransformManifest.TryReadSuccessManifest(path, out var manifest))
        {
            if (manifest.Outputs.All(output =>
                    CobaltumOrmTransformManifest.FullPath(output) is { } outputPath && File.Exists(outputPath)))
            {
                return manifest.Outputs;
            }

            File.Delete(path);
            return Array.Empty<ITaskItem>();
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Array.Empty<ITaskItem>();
    }
}

public sealed class CobaltumOrmReadTransformManifestTask : Task
{
    [Required]
    public string SuccessManifestPath { get; set; } = string.Empty;

    [Output]
    public ITaskItem[] ProcessedSources { get; private set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] TransformedSources { get; private set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] Outputs { get; private set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        if (!CobaltumOrmTransformManifest.TryReadSuccessManifest(SuccessManifestPath, out var manifest))
        {
            return true;
        }

        ProcessedSources = manifest.ProcessedSources;
        TransformedSources = manifest.TransformedSources;
        Outputs = manifest.Outputs;
        return true;
    }
}

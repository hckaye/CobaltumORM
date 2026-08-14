namespace CobaltumOrm.Tool;

internal static class ProjectPathResolver
{
    public static string Resolve(string project, string currentDirectory)
    {
        var resolved = Path.GetFullPath(project, currentDirectory);
        if (Directory.Exists(resolved))
        {
            var candidates = Directory
                .EnumerateFiles(resolved, "*.csproj", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length != 1)
            {
                throw new ToolUsageException(
                    $"Directory '{resolved}' must contain exactly one project file.");
            }

            return candidates[0];
        }

        if (!File.Exists(resolved))
        {
            throw new ToolUsageException($"Project path '{resolved}' does not exist.");
        }

        if (!string.Equals(Path.GetExtension(resolved), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolUsageException($"Project path '{resolved}' is not a .csproj file.");
        }

        return resolved;
    }
}

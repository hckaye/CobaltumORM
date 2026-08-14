namespace CobaltumOrm.Tool;

internal static class CSharpNameValidator
{
    public static bool IsValidNamespace(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var part in value.Split('.'))
        {
            if (part.Length == 0 || (!char.IsLetter(part[0]) && part[0] != '_'))
            {
                return false;
            }

            if (part.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            {
                return false;
            }
        }

        return true;
    }
}

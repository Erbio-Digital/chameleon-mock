using System.Text.RegularExpressions;

namespace Chameleon.Host.Plugins;

/// Reads a provider sheet (catalog/providers/<name>.md): the provider URL is the first https link of the Sources section.
public static partial class ProviderSheet
{
    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    public static string? ProviderUrl(string providersDir, string name)
    {
        if (!IsValidName(name)) return null;
        var path = Path.Combine(providersDir, $"{name}.md");
        if (!File.Exists(path)) return null;
        var section = SourcesSection().Match(File.ReadAllText(path));
        var link = section.Success ? HttpsLink().Match(section.Groups[1].Value) : null;
        return link is { Success: true } ? link.Value : null;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^##\s+Sources\s*$(.*?)(?=^##\s|\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex SourcesSection();

    [GeneratedRegex(@"https://[^\s)>\]]+")]
    private static partial Regex HttpsLink();
}

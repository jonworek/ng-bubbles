using System.Text.RegularExpressions;

namespace NgBubbles.Core;

public sealed record MessageLink(int Start, int Length, string Url);
public static partial class MessageLinks
{
    public static List<MessageLink> Find(string text)
    {
        var links = new List<MessageLink>();
        foreach (Match match in WebLinks().Matches(text))
        {
            var value = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('{', '}') })
                while (value.EndsWith(close) && value.Count(c => c == close) > value.Count(c => c == open)) value = value[..^1];
            if (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/') continue;
            var candidate = value;
            if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && !value.Contains('@')) candidate = "https://" + value;
            else if (!value.Contains(':')) candidate = "mailto:" + value;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
                links.Add(new(match.Index, value.Length, uri.AbsoluteUri));
        }
        return links;
    }
    [GeneratedRegex("""(?<![\p{L}\p{N}_@])(?:[a-z][a-z0-9+.-]*:[^\s\p{Cc}\p{Cf}<>"'“”‘’`]+|[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,63}|www\.[^\s\p{Cc}\p{Cf}<>"'“”‘’`]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex WebLinks();
}

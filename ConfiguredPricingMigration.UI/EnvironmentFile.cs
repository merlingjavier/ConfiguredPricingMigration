namespace ConfiguredPricingMigration.UI;

internal static class EnvironmentFile
{
    public static string? LoadNearest()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var startDirectories = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        foreach (var startDirectory in startDirectories)
        {
            for (
                var directory = new DirectoryInfo(startDirectory);
                directory is not null;
                directory = directory.Parent)
            {
                if (!visited.Add(directory.FullName))
                {
                    continue;
                }

                var path = Path.Combine(directory.FullName, ".env");
                if (!File.Exists(path))
                {
                    continue;
                }

                foreach (var line in File.ReadLines(path))
                {
                    LoadEnvironmentVariable(line);
                }

                return path;
            }
        }

        return null;
    }

    private static void LoadEnvironmentVariable(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#'))
        {
            return;
        }

        if (text.StartsWith("export ", StringComparison.Ordinal))
        {
            text = text[7..].TrimStart();
        }

        var separator = text.IndexOf('=');
        if (separator <= 0)
        {
            return;
        }

        var key = text[..separator].Trim();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
        {
            return;
        }

        var valueWithNoComment = WithoutInlineComment(text[(separator + 1)..]);
        var value = Unquote(valueWithNoComment.Trim());
        Environment.SetEnvironmentVariable(key, value);
    }

    private static string Unquote(string value)
    {
        var isQuoted = value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\''));

        return isQuoted ? value[1..^1] : value;
    }

    private static string WithoutInlineComment(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is '\'' or '"')
            {
                if (quote == '\0')
                {
                    quote = character;
                }
                else if (quote == character)
                {
                    quote = '\0';
                }

                continue;
            }

            var isCommentStart = character == '#'
                && quote == '\0'
                && (index == 0 || char.IsWhiteSpace(value[index - 1]));

            if (isCommentStart)
            {
                return value[..index];
            }
        }

        return value;
    }
}

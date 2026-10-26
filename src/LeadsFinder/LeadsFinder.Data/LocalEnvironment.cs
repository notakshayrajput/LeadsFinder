using System.Text;
using System.Text.RegularExpressions;

namespace LeadsFinder.Data;

/// <summary>Reads the repository-root .env into server configuration without changing process environment.</summary>
public static class LocalEnvironment
{
    public static Dictionary<string, string?> Read(string? startDirectory = null)
    {
        var origins = startDirectory is null
            ? new[] { Environment.CurrentDirectory, AppContext.BaseDirectory } : new[] { startDirectory };
        foreach (var origin in origins)
            for (var directory = new DirectoryInfo(origin); directory is not null; directory = directory.Parent)
            {
                if (!File.Exists(Path.Combine(directory.FullName, "src", "LeadsFinder", "LeadsFinder.slnx"))) continue;
                var path = Path.Combine(directory.FullName, ".env");
                return File.Exists(path) ? Parse(File.ReadLines(path)) : new(StringComparer.OrdinalIgnoreCase);
            }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    public static Dictionary<string, string?> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var separator = line.IndexOf('=');
            if (separator <= 0 || !Regex.IsMatch(line[..separator].Trim(), @"^[A-Za-z_][A-Za-z0-9_]*$"))
                throw InvalidLine(lineNumber);
            var key = line[..separator].Trim().Replace("__", ":", StringComparison.Ordinal);
            var value = line[(separator + 1)..].Trim();
            if (value.Length > 0 && value[0] is '\'' or '"')
            {
                var quote = value[0];
                var output = new StringBuilder();
                var closed = false;
                for (var i = 1; i < value.Length; i++)
                {
                    var c = value[i];
                    if (c == quote)
                    {
                        var tail = value[(i + 1)..].TrimStart();
                        if (tail.Length > 0 && tail[0] != '#') throw InvalidLine(lineNumber);
                        closed = true;
                        break;
                    }
                    if (quote == '"' && c == '\\' && i + 1 < value.Length)
                    {
                        var escaped = value[i + 1];
                        if (escaped is '\\' or '"' or 'n' or 'r' or 't')
                        {
                            output.Append(escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escaped });
                            i++;
                            continue;
                        }
                    }
                    output.Append(c);
                }
                if (!closed) throw InvalidLine(lineNumber);
                value = output.ToString();
            }
            else
            {
                // An unquoted hash starts a comment only at the start or after whitespace.
                var comment = Regex.Match(value, @"(^|\s)#");
                if (comment.Success) value = value[..comment.Index].TrimEnd();
            }
            // Blank sample entries do not erase appsettings, user-secrets, or local DB defaults.
            if (value.Length > 0) values[key] = value;
        }
        return values;
    }

    private static FormatException InvalidLine(int line) =>
        new($"Invalid .env syntax on line {line}. Use a single-line KEY=value entry; secret contents are omitted.");
}

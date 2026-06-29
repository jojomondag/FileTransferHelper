namespace FileTransferHelper.Services;

public static class PosixPath
{
    public static string Join(params string?[] parts)
    {
        var cleaned = parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Replace('\\', '/'))
            .ToArray();

        if (cleaned.Length == 0)
        {
            return ".";
        }

        var absolute = cleaned[0].StartsWith("/", StringComparison.Ordinal);
        var result = string.Join("/", cleaned.SelectMany((part, index) =>
        {
            var value = index == 0 ? part.TrimEnd('/') : part.Trim('/');
            return string.IsNullOrEmpty(value) ? [] : new[] { value };
        }));

        if (string.IsNullOrEmpty(result))
        {
            return absolute ? "/" : ".";
        }

        return absolute && !result.StartsWith("/", StringComparison.Ordinal) ? "/" + result : result;
    }

    public static string DirectoryName(string path)
    {
        path = NormalizeSlashes(path).TrimEnd('/');
        if (string.IsNullOrEmpty(path) || path == ".")
        {
            return ".";
        }

        var index = path.LastIndexOf('/');
        if (index < 0)
        {
            return ".";
        }

        return index == 0 ? "/" : path[..index];
    }

    public static string FileName(string path)
    {
        path = NormalizeSlashes(path).TrimEnd('/');
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    public static string CombineRelative(string root, string relative)
    {
        return string.IsNullOrEmpty(relative) || relative == "."
            ? NormalizeSlashes(root)
            : Join(root, relative);
    }

    public static string Normalize(string path)
    {
        path = NormalizeSlashes(path);
        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            return ".";
        }

        var absolute = path.StartsWith("/", StringComparison.Ordinal);
        var stack = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(part);
        }

        if (stack.Count == 0)
        {
            return absolute ? "/" : ".";
        }

        var result = string.Join("/", stack);
        return absolute ? "/" + result : result;
    }

    public static string NormalizeSlashes(string path) => path.Replace('\\', '/');
}

namespace FileTransferHelper.Services;

public static class LogWriter
{
    private static readonly object LockObject = new();

    public static void Write(string path, string message)
    {
        var line = string.IsNullOrEmpty(message)
            ? Environment.NewLine
            : $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";

        lock (LockObject)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
            File.AppendAllText(path, line);
        }
    }
}

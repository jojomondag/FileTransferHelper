namespace FileTransferHelper.Services;

public static class AppPaths
{
    public const string AppTitle = "FileTransferHelper";
    public const int SshPort = 22;
    public const int TransferRetryAttempts = 12;
    public const int TransferRetryDelaySeconds = 5;

    public static string BaseDirectory => AppContext.BaseDirectory;
    public static string RepositoryDirectory => Path.GetFullPath(Path.Combine(BaseDirectory, "..", "..", "..", ".."));
    public static string DeviceCachePath => Path.Combine(RepositoryDirectory, "devices.json");
    public static string UiSettingsPath => Path.Combine(RepositoryDirectory, "ui-settings.json");
    public static string DiscoveryLogPath => Path.Combine(RepositoryDirectory, "discovery.log");
    public static string TransferLogPath => Path.Combine(RepositoryDirectory, "transfer.log");
}

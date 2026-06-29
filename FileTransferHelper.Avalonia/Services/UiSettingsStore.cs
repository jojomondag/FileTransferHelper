using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileTransferHelper.Services;

public sealed class UiSettings
{
    public double LocalTreeColumnWidth { get; set; } = 120;

    public double RemoteTreeColumnWidth { get; set; } = 120;

    public double LocalPanelWidth { get; set; }

    public string LastLocalPath { get; set; } = "";

    public string LastConnectedHostAddress { get; set; } = "";

    public string LastConnectedUsername { get; set; } = "";

    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public double WindowWidth { get; set; } = 980;

    public double WindowHeight { get; set; } = 680;

    public bool IsMaximized { get; set; }

    public Dictionary<string, string> LastRemotePathsByHost { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public UiSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.UiSettingsPath))
            {
                return new UiSettings();
            }

            return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(AppPaths.UiSettingsPath), JsonOptions)
                   ?? new UiSettings();
        }
        catch (Exception exc) when (exc is IOException or JsonException or UnauthorizedAccessException)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte läsa ui-settings.json: {exc.Message}");
            return new UiSettings();
        }
    }

    public void Save(UiSettings settings)
    {
        try
        {
            File.WriteAllText(AppPaths.UiSettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte spara ui-settings.json: {exc.Message}");
        }
    }

    public string? LastRemotePathForHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        return Load().LastRemotePathsByHost.TryGetValue(host.Trim(), out var path) ? path : null;
    }
}

using Avalonia.Input;

namespace FileTransferHelper.Services;

public static class DragDropFormats
{
    public const string LocalPaths = "FileTransferHelper.LocalPaths";
    public const string SourceDevice = "FileTransferHelper.SourceDevice";

    public static DataFormat<string> LocalPathsDataFormat { get; } =
        DataFormat.CreateStringApplicationFormat(LocalPaths);

    public static DataFormat<string> SourceDeviceDataFormat { get; } =
        DataFormat.CreateStringApplicationFormat(SourceDevice);
}

using Avalonia.Input;

namespace FileTransferHelper.Services;

public static class DragDropFormats
{
    public const string LocalPaths = "FileTransferHelper.LocalPaths";

    public static DataFormat<string> LocalPathsDataFormat { get; } =
        DataFormat.CreateStringApplicationFormat(LocalPaths);
}
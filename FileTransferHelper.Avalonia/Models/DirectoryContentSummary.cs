namespace FileTransferHelper.Models;

public sealed record DirectoryContentSummary(
    int DirectDirectories,
    int DirectFiles,
    int Subdirectories,
    int NestedFiles,
    long DirectBytes = 0,
    long NestedBytes = 0)
{
    public bool HasNestedContent => Subdirectories > 0 || NestedFiles > 0;

    public long TotalBytes => DirectBytes + NestedBytes;
}

namespace FileTransferHelper.Models;

public sealed record LocalEntry(string Name, bool IsDirectory, long Size, string FullPath)
{
    public string DisplayName => IsDirectory ? $"{Name}/" : Name;
}

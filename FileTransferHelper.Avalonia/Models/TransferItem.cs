namespace FileTransferHelper.Models;

public sealed record TransferItem(string LocalPath, string RemotePath, string Action, string DisplayName);

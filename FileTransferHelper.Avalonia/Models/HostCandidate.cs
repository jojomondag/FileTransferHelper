namespace FileTransferHelper.Models;

public sealed record HostCandidate(
    string Name,
    string Address,
    string Source,
    string Username = "",
    bool UseKey = false,
    string KeyPath = "")
{
    public string Label => $"{Name} ({Address})";
}

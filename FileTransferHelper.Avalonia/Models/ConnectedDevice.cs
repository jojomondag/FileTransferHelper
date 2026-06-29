namespace FileTransferHelper.Models;

public sealed record ConnectedDevice(string Id, string Name, string Address, bool IsLocal)
{
    public string Label => Name;
}

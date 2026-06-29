namespace FileTransferHelper.Models;

public sealed record UploadProgressUpdate(
    string Label,
    int Percent,
    double BytesPerSecond,
    ulong UploadedBytes,
    long TotalBytes);

using System.Diagnostics;

namespace FileTransferHelper.Services;

public sealed class TransferSpeedMeter
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private ulong _lastBytes;
    private long _lastTick;
    private double _smoothedBytesPerSecond;

    public void Reset()
    {
        _stopwatch.Restart();
        _lastBytes = 0;
        _lastTick = 0;
        _smoothedBytesPerSecond = 0;
    }

    public double Update(ulong uploadedBytes)
    {
        var now = _stopwatch.ElapsedTicks;
        var elapsedTicks = now - _lastTick;
        if (elapsedTicks <= 0 || uploadedBytes < _lastBytes)
        {
            return _smoothedBytesPerSecond;
        }

        var deltaBytes = uploadedBytes - _lastBytes;
        var instantBytesPerSecond = deltaBytes / (elapsedTicks / (double)Stopwatch.Frequency);
        _smoothedBytesPerSecond = _smoothedBytesPerSecond <= 0
            ? instantBytesPerSecond
            : _smoothedBytesPerSecond * 0.75 + instantBytesPerSecond * 0.25;

        _lastBytes = uploadedBytes;
        _lastTick = now;
        return _smoothedBytesPerSecond;
    }

    public static string FormatBytesPerSecond(double bytesPerSecond)
    {
        if (bytesPerSecond < 1)
        {
            return "";
        }

        if (bytesPerSecond >= 1_048_576)
        {
            return $"{bytesPerSecond / 1_048_576:0.##} MB/s";
        }

        if (bytesPerSecond >= 1024)
        {
            return $"{bytesPerSecond / 1024:0.#} KB/s";
        }

        return $"{bytesPerSecond:0} B/s";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.##} {units[unit]}";
    }
}

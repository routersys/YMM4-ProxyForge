using System.Globalization;
using ProxyForge.Cache;

namespace ProxyForge.ViewModels;

internal sealed class ProxyCacheEntryViewModel(ProxyCacheEntry entry)
{
    public Guid Id { get; } = entry.Id;

    public string SourcePath { get; } = entry.SourcePath;

    public string FileName { get; } = System.IO.Path.GetFileName(entry.SourcePath);

    public string Resolution { get; } = string.Create(CultureInfo.InvariantCulture, $"{entry.ProxyWidth}×{entry.ProxyHeight}");

    public string Scale { get; } = string.Create(CultureInfo.InvariantCulture, $"{entry.Scale}%");

    public long Bytes { get; } = entry.FileLength;

    public string Size { get; } = ByteText.Format(entry.FileLength);

    public string LastUsed { get; } = new DateTime(entry.LastUsedTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
}

namespace ProxyForge.Interfaces;

internal interface IVideoCacheDatabase : IDisposable
{
    bool TryGet(string originalPath, float scale, out VideoCacheLookupResult result);
    VideoCacheLookupResult[] GetAll();
    void Add(string originalPath, float scale, uint proxyWidth, uint proxyHeight,
        long fileSize, uint fileCrc32, Guid uuid);
    bool Remove(Guid uuid);
    int RemoveByPath(string originalPath);
    int ClearAll();
    void UpdateLastAccess(Guid uuid);
    string GetCacheFilePath(Guid uuid);
    string CacheDirectory { get; }
}

internal readonly record struct VideoCacheLookupResult(
    Guid Uuid,
    string OriginalPath,
    string FileName,
    float Scale,
    uint ProxyWidth,
    uint ProxyHeight,
    long FileSize,
    uint FileCrc32,
    DateTime CreatedAt,
    DateTime LastAccessedAt
);
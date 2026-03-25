using System.Runtime.InteropServices;

namespace ZeroDiskProxy.Interop;

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
internal interface IMFSourceReader
{
    [PreserveSig]
    int GetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] out bool pfSelected);

    [PreserveSig]
    int SetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);

    [PreserveSig]
    int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType ppMediaType);

    [PreserveSig]
    int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType ppMediaType);

    [PreserveSig]
    int SetCurrentMediaType(uint dwStreamIndex, nint pdwReserved, IMFMediaType pMediaType);

    [PreserveSig]
    int SetCurrentPosition(in Guid guidTimeFormat, in PropVariant varPosition);

    [PreserveSig]
    int ReadSample(
        uint dwStreamIndex, uint dwControlFlags,
        out uint pdwActualStreamIndex, out uint pdwStreamFlags,
        out long pllTimestamp, out IMFSample? ppSample);

    [PreserveSig]
    int Flush(uint dwStreamIndex);

    [PreserveSig]
    int GetServiceForStream(uint dwStreamIndex, in Guid guidService, in Guid riid, out nint ppvObject);

    [PreserveSig]
    int GetPresentationAttribute(uint dwStreamIndex, in Guid guidAttribute, nint pvarAttribute);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d")]
internal interface IMFSinkWriter
{
    [PreserveSig]
    int AddStream(IMFMediaType pTargetMediaType, out uint pdwStreamIndex);

    [PreserveSig]
    int SetInputMediaType(uint dwStreamIndex, IMFMediaType pInputMediaType, IMFAttributes? pEncodingParameters);

    [PreserveSig]
    int BeginWriting();

    [PreserveSig]
    int WriteSample(uint dwStreamIndex, IMFSample pSample);

    [PreserveSig]
    int SendStreamTick(uint dwStreamIndex, long llTimestamp);

    [PreserveSig]
    int PlaceSampleMarker(uint dwStreamIndex, nint pvContext);

    [PreserveSig]
    int NotifyEndOfSegment(uint dwStreamIndex);

    [PreserveSig]
    int Flush(uint dwStreamIndex);

    [PreserveSig]
    int Finalize_();

    [PreserveSig]
    int GetServiceForStream(uint dwStreamIndex, in Guid guidService, in Guid riid, out nint ppvObject);

    [PreserveSig]
    int GetStatistics(uint dwStreamIndex, out MF_SINK_WRITER_STATISTICS pStats);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
internal interface IMFMediaType : IMFAttributes
{
    [PreserveSig]
    new int GetItem(in Guid guidKey, nint pValue);
    [PreserveSig]
    new int GetItemType(in Guid guidKey, out uint pType);
    [PreserveSig]
    new int CompareItem(in Guid guidKey, nint Value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig]
    new int Compare(IMFAttributes pTheirs, uint MatchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig]
    new int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig]
    new int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig]
    new int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig]
    new int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig]
    new int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig]
    new int GetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string pwszValue, uint cchBufSize, nint pcchLength);
    [PreserveSig]
    new int GetAllocatedString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    [PreserveSig]
    new int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig]
    new int GetBlob(in Guid guidKey, nint pBuf, uint cbBufSize, nint pcbBlobSize);
    [PreserveSig]
    new int GetAllocatedBlob(in Guid guidKey, out nint ppBuf, out uint pcbSize);
    [PreserveSig]
    new int GetUnknown(in Guid guidKey, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    [PreserveSig]
    new int SetItem(in Guid guidKey, nint Value);
    [PreserveSig]
    new int DeleteItem(in Guid guidKey);
    [PreserveSig]
    new int DeleteAllItems();
    [PreserveSig]
    new int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig]
    new int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig]
    new int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig]
    new int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig]
    new int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig]
    new int SetBlob(in Guid guidKey, nint pBuf, uint cbBufSize);
    [PreserveSig]
    new int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig]
    new int LockStore();
    [PreserveSig]
    new int UnlockStore();
    [PreserveSig]
    new int GetCount(out uint pcItems);
    [PreserveSig]
    new int GetItemByIndex(uint unIndex, out Guid pguidKey, nint pValue);
    [PreserveSig]
    new int CopyAllItems(IMFAttributes pDest);

    [PreserveSig]
    int GetMajorType(out Guid pguidMajorType);
    [PreserveSig]
    int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
    [PreserveSig]
    int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
    [PreserveSig]
    int GetRepresentation(Guid guidRepresentation, out nint ppvRepresentation);
    [PreserveSig]
    int FreeRepresentation(Guid guidRepresentation, nint pvRepresentation);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
internal interface IMFAttributes
{
    [PreserveSig]
    int GetItem(in Guid guidKey, nint pValue);
    [PreserveSig]
    int GetItemType(in Guid guidKey, out uint pType);
    [PreserveSig]
    int CompareItem(in Guid guidKey, nint Value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig]
    int Compare(IMFAttributes pTheirs, uint MatchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig]
    int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig]
    int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig]
    int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig]
    int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig]
    int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig]
    int GetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string pwszValue, uint cchBufSize, nint pcchLength);
    [PreserveSig]
    int GetAllocatedString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    [PreserveSig]
    int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig]
    int GetBlob(in Guid guidKey, nint pBuf, uint cbBufSize, nint pcbBlobSize);
    [PreserveSig]
    int GetAllocatedBlob(in Guid guidKey, out nint ppBuf, out uint pcbSize);
    [PreserveSig]
    int GetUnknown(in Guid guidKey, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    [PreserveSig]
    int SetItem(in Guid guidKey, nint Value);
    [PreserveSig]
    int DeleteItem(in Guid guidKey);
    [PreserveSig]
    int DeleteAllItems();
    [PreserveSig]
    int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig]
    int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig]
    int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig]
    int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig]
    int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig]
    int SetBlob(in Guid guidKey, nint pBuf, uint cbBufSize);
    [PreserveSig]
    int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig]
    int LockStore();
    [PreserveSig]
    int UnlockStore();
    [PreserveSig]
    int GetCount(out uint pcItems);
    [PreserveSig]
    int GetItemByIndex(uint unIndex, out Guid pguidKey, nint pValue);
    [PreserveSig]
    int CopyAllItems(IMFAttributes pDest);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
internal interface IMFSample : IMFAttributes
{
    [PreserveSig]
    new int GetItem(in Guid guidKey, nint pValue);
    [PreserveSig]
    new int GetItemType(in Guid guidKey, out uint pType);
    [PreserveSig]
    new int CompareItem(in Guid guidKey, nint Value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig]
    new int Compare(IMFAttributes pTheirs, uint MatchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig]
    new int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig]
    new int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig]
    new int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig]
    new int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig]
    new int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig]
    new int GetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string pwszValue, uint cchBufSize, nint pcchLength);
    [PreserveSig]
    new int GetAllocatedString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    [PreserveSig]
    new int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig]
    new int GetBlob(in Guid guidKey, nint pBuf, uint cbBufSize, nint pcbBlobSize);
    [PreserveSig]
    new int GetAllocatedBlob(in Guid guidKey, out nint ppBuf, out uint pcbSize);
    [PreserveSig]
    new int GetUnknown(in Guid guidKey, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    [PreserveSig]
    new int SetItem(in Guid guidKey, nint Value);
    [PreserveSig]
    new int DeleteItem(in Guid guidKey);
    [PreserveSig]
    new int DeleteAllItems();
    [PreserveSig]
    new int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig]
    new int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig]
    new int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig]
    new int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig]
    new int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig]
    new int SetBlob(in Guid guidKey, nint pBuf, uint cbBufSize);
    [PreserveSig]
    new int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig]
    new int LockStore();
    [PreserveSig]
    new int UnlockStore();
    [PreserveSig]
    new int GetCount(out uint pcItems);
    [PreserveSig]
    new int GetItemByIndex(uint unIndex, out Guid pguidKey, nint pValue);
    [PreserveSig]
    new int CopyAllItems(IMFAttributes pDest);

    [PreserveSig]
    int GetSampleFlags(out uint pdwSampleFlags);
    [PreserveSig]
    int SetSampleFlags(uint dwSampleFlags);
    [PreserveSig]
    int GetSampleTime(out long phnsSampleTime);
    [PreserveSig]
    int SetSampleTime(long hnsSampleTime);
    [PreserveSig]
    int GetSampleDuration(out long phnsSampleDuration);
    [PreserveSig]
    int SetSampleDuration(long hnsSampleDuration);
    [PreserveSig]
    int GetBufferCount(out uint pdwBufferCount);
    [PreserveSig]
    int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
    [PreserveSig]
    int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
    [PreserveSig]
    int AddBuffer(IMFMediaBuffer pBuffer);
    [PreserveSig]
    int RemoveBufferByIndex(uint dwIndex);
    [PreserveSig]
    int RemoveAllBuffers();
    [PreserveSig]
    int GetTotalLength(out uint pcbTotalLength);
    [PreserveSig]
    int CopyToBuffer(IMFMediaBuffer pBuffer);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
internal interface IMFMediaBuffer
{
    [PreserveSig]
    int Lock(out nint ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
    [PreserveSig]
    int Unlock();
    [PreserveSig]
    int GetCurrentLength(out int pcbCurrentLength);
    [PreserveSig]
    int SetCurrentLength(int cbCurrentLength);
    [PreserveSig]
    int GetMaxLength(out int pcbMaxLength);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBF")]
internal interface IMF2DBuffer
{
    [PreserveSig]
    int Lock2D(out nint ppbScanline0, out int plPitch);
    [PreserveSig]
    int Unlock2D();
    [PreserveSig]
    int GetScanline0AndPitch(out nint pbScanline0, out int plPitch);
    [PreserveSig]
    int IsContiguousFormat([MarshalAs(UnmanagedType.Bool)] out bool pfIsContiguous);
    [PreserveSig]
    int GetContiguousLength(out int pcbLength);
    [PreserveSig]
    int ContiguousCopyTo(nint pbDestBuffer, int cbDestBuffer);
    [PreserveSig]
    int ContiguousCopyFrom(nint pbSrcBuffer, int cbSrcBuffer);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MF_SINK_WRITER_STATISTICS
{
    public uint cb;
    public long llLastTimestampReceived;
    public long llLastTimestampEncoded;
    public long llLastTimestampProcessed;
    public long llLastStreamTickReceived;
    public long llLastSinkSampleRequest;
    public ulong qwNumSamplesReceived;
    public ulong qwNumSamplesEncoded;
    public ulong qwNumSamplesProcessed;
    public ulong qwNumStreamTicksReceived;
    public uint dwByteCountQueued;
    public ulong qwByteCountProcessed;
    public uint dwNumOutstandingSinkSampleRequests;
    public uint dwAverageSampleRateReceived;
    public uint dwAverageSampleRateEncoded;
    public uint dwAverageSampleRateProcessed;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant : IDisposable
{
    [FieldOffset(0)] internal ushort vt;
    [FieldOffset(8)] internal long hVal;

    internal static PropVariant FromInt64(long value) => new() { vt = 20, hVal = value };

    public void Dispose()
    {
        if (vt != 0)
            PropVariantClear(ref this);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}
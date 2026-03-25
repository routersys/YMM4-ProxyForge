using System.Runtime.InteropServices;

namespace ZeroDiskProxy.Interop;

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
internal interface IMFTransform
{
    [PreserveSig]
    int GetStreamLimits(out uint pdwInputMinimum, out uint pdwInputMaximum, out uint pdwOutputMinimum, out uint pdwOutputMaximum);

    [PreserveSig]
    int GetStreamCount(out uint pcInputStreams, out uint pcOutputStreams);

    [PreserveSig]
    int GetStreamIDs(uint dwInputIDArraySize, [Out] uint[] pdwInputIDs, uint dwOutputIDArraySize, [Out] uint[] pdwOutputIDs);

    [PreserveSig]
    int GetInputStreamInfo(uint dwInputStreamID, out MFT_INPUT_STREAM_INFO pStreamInfo);

    [PreserveSig]
    int GetOutputStreamInfo(uint dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo);

    [PreserveSig]
    int GetAttributes(out IMFAttributes pAttributes);

    [PreserveSig]
    int GetInputStreamAttributes(uint dwInputStreamID, out IMFAttributes pAttributes);

    [PreserveSig]
    int GetOutputStreamAttributes(uint dwOutputStreamID, out IMFAttributes pAttributes);

    [PreserveSig]
    int DeleteInputStream(uint dwStreamID);

    [PreserveSig]
    int AddInputStreams(uint cStreams, [In] uint[] adwStreamIDs);

    [PreserveSig]
    int GetInputAvailableType(uint dwInputStreamID, uint dwTypeIndex, out IMFMediaType ppType);

    [PreserveSig]
    int GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out IMFMediaType ppType);

    [PreserveSig]
    int SetInputType(uint dwInputStreamID, IMFMediaType? pType, uint dwFlags);

    [PreserveSig]
    int SetOutputType(uint dwOutputStreamID, IMFMediaType? pType, uint dwFlags);

    [PreserveSig]
    int GetInputCurrentType(uint dwInputStreamID, out IMFMediaType ppType);

    [PreserveSig]
    int GetOutputCurrentType(uint dwOutputStreamID, out IMFMediaType ppType);

    [PreserveSig]
    int GetInputStatus(uint dwInputStreamID, out uint pdwFlags);

    [PreserveSig]
    int GetOutputStatus(out uint pdwFlags);

    [PreserveSig]
    int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);

    [PreserveSig]
    int ProcessEvent(uint dwInputStreamID, nint pEvent);

    [PreserveSig]
    int ProcessMessage(uint eMessage, nint ulParam);

    [PreserveSig]
    int ProcessInput(uint dwInputStreamID, IMFSample pSample, uint dwFlags);

    [PreserveSig]
    int ProcessOutput(uint dwFlags, uint cOutputBufferCount, ref MFT_OUTPUT_DATA_BUFFER pOutputSamples, out uint pdwStatus);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_INPUT_STREAM_INFO
{
    public long hnsMaxLatency;
    public uint dwFlags;
    public uint cbSize;
    public uint cbMaxLookahead;
    public uint cbAlignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_STREAM_INFO
{
    public uint dwFlags;
    public uint cbSize;
    public uint cbAlignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_DATA_BUFFER
{
    public uint dwStreamID;
    [MarshalAs(UnmanagedType.Interface)]
    public IMFSample? pSample;
    public uint dwStatus;
    [MarshalAs(UnmanagedType.Interface)]
    public object? pEvents;
}
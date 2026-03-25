namespace ZeroDiskProxy.Interop;

internal static class MfAttributeKeys
{
    internal static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    internal static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    internal static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac5");
    internal static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    internal static readonly Guid MF_MT_MPEG2_LEVEL = new("96f66574-11c5-4015-8666-bff516436da7");

    internal static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

    internal static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    internal static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("f81da2c0-b537-4672-a8b2-a681b17307a3");
    internal static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    internal static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    internal static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    internal static readonly Guid MF_SOURCE_READER_MEDIASOURCE_CHARACTERISTICS = new("6d23f5c8-c5d7-4a9b-9971-5d11f8bca880");
}
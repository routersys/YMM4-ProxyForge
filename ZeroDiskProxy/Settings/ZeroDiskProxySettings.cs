using YukkuriMovieMaker.Plugin;
using ZeroDiskProxy.Localization;

namespace ZeroDiskProxy.Settings;

internal sealed class ZeroDiskProxySettings : SettingsBase<ZeroDiskProxySettings>
{
    public override SettingsCategory Category => SettingsCategory.VideoFileSource;
    public override string Name => Translate.SettingsName;
    public override bool HasSettingView => true;
    public override object? SettingView => new Views.ZeroDiskProxySettingsView();

    private bool _useProxy = true;
    public bool UseProxy
    {
        get => _useProxy;
        set => Set(ref _useProxy, value);
    }

    private int _scale = 50;
    public int Scale
    {
        get => _scale;
        set => Set(ref _scale, Math.Clamp(value, 10, 100));
    }

    private int _minFileSizeForProxy = 100;
    public int MinFileSizeForProxy
    {
        get => _minFileSizeForProxy;
        set => Set(ref _minFileSizeForProxy, Math.Clamp(value, 1, 10000));
    }

    private int _bitrateFactor = 50;
    public int BitrateFactor
    {
        get => _bitrateFactor;
        set => Set(ref _bitrateFactor, Math.Clamp(value, 1, 200));
    }

    private int _gopSize = 30;
    public int GopSize
    {
        get => _gopSize;
        set => Set(ref _gopSize, Math.Clamp(value, 1, 300));
    }

    private int _memoryReserveMb = 512;
    public int MemoryReserveMb
    {
        get => _memoryReserveMb;
        set => Set(ref _memoryReserveMb, Math.Clamp(value, 128, 8192));
    }

    private int _maxCacheMemoryMb = 2048;
    public int MaxCacheMemoryMb
    {
        get => _maxCacheMemoryMb;
        set => Set(ref _maxCacheMemoryMb, Math.Clamp(value, 256, 16384));
    }

    private bool _autoGenerate = true;
    public bool AutoGenerate
    {
        get => _autoGenerate;
        set => Set(ref _autoGenerate, value);
    }

    private bool _clearCacheOnExit;
    public bool ClearCacheOnExit
    {
        get => _clearCacheOnExit;
        set => Set(ref _clearCacheOnExit, value);
    }

    private bool _useAdvancedSetting;
    public bool UseAdvancedSetting
    {
        get => _useAdvancedSetting;
        set => Set(ref _useAdvancedSetting, value);
    }

    private bool _enableHardwareAcceleration = true;
    public bool EnableHardwareAcceleration
    {
        get => _enableHardwareAcceleration;
        set => Set(ref _enableHardwareAcceleration, value);
    }

    private bool _enableDiskFallback = true;
    public bool EnableDiskFallback
    {
        get => _enableDiskFallback;
        set => Set(ref _enableDiskFallback, value);
    }

    public override void Initialize() { }
}
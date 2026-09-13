using HostTexts = YukkuriMovieMaker.Resources.Localization.Texts;

namespace ProxyForge.Export;

public enum ExportPhase
{
    Idle,
    Preparing,
    Exporting,
}

public enum ExportWindowRole
{
    None,
    Configuration,
    Progress,
}

internal sealed class ExportDetector(
    bool commandLineEncode,
    Func<string> configurationTitle,
    Func<string> progressTitle,
    Func<Func<bool>, bool?> resolveOnUiThread)
{
    public const string EncodeOption = "--encode";

    int phase;

    public static ExportDetector Shared { get; } = new(
        HasEncodeOption(Environment.GetCommandLineArgs()),
        () => HostTexts.VideoExportWindowTitle,
        () => HostTexts.OutputProgressWindowTitle,
        ExportWindowWatcher.ResolveOnUiThread);

    public bool IsCommandLineEncode => commandLineEncode;

    public ExportPhase Phase => (ExportPhase)Volatile.Read(ref phase);

    public static bool HasEncodeOption(string[] arguments)
    {
        var index = Array.FindIndex(arguments, argument => string.Equals(argument, EncodeOption, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= arguments.Length)
            return false;

        var input = arguments[index + 1];
        return !string.IsNullOrWhiteSpace(input) && !input.StartsWith("--", StringComparison.Ordinal);
    }

    public ExportWindowRole Classify(string? title)
    {
        if (string.IsNullOrEmpty(title))
            return ExportWindowRole.None;
        if (string.Equals(title, progressTitle(), StringComparison.Ordinal))
            return ExportWindowRole.Progress;
        if (string.Equals(title, configurationTitle(), StringComparison.Ordinal))
            return ExportWindowRole.Configuration;
        return ExportWindowRole.None;
    }

    public void OnWindowOpened(ExportWindowRole role)
    {
        switch (role)
        {
            case ExportWindowRole.Configuration:
                Interlocked.CompareExchange(ref phase, (int)ExportPhase.Preparing, (int)ExportPhase.Idle);
                break;
            case ExportWindowRole.Progress:
                Interlocked.Exchange(ref phase, (int)ExportPhase.Exporting);
                break;
        }
    }

    public void OnWindowClosed(ExportWindowRole role)
    {
        if (role == ExportWindowRole.Progress)
            Interlocked.Exchange(ref phase, (int)ExportPhase.Idle);
    }

    public bool IsExporting()
    {
        if (commandLineEncode)
            return true;

        switch (Phase)
        {
            case ExportPhase.Idle:
                return false;
            case ExportPhase.Exporting:
                return true;
        }

        var resolved = resolveOnUiThread(() => Phase == ExportPhase.Exporting);
        if (resolved != false)
            return true;

        Interlocked.CompareExchange(ref phase, (int)ExportPhase.Idle, (int)ExportPhase.Preparing);
        return false;
    }
}

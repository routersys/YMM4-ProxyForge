namespace ProxyForge.Interfaces;

internal interface IExportDetector
{
    bool IsExporting();
    void Reset();
}
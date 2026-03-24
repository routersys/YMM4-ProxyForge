namespace ZeroDiskProxy.Interfaces;

internal interface IExportDetector
{
    bool IsExporting();
    void Reset();
}
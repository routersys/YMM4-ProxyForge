namespace ProxyForge.Tests;

static class TestUiThread
{
    static readonly Lock gate = new();

    public static void Post(Action action)
    {
        using (gate.EnterScope())
            action();
    }

    public static T Read<T>(Func<T> read)
    {
        using (gate.EnterScope())
            return read();
    }
}

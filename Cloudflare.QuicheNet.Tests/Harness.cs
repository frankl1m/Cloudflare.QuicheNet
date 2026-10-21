namespace Cloudflare.QuicheNet.Tests;

internal static class Harness
{
    private static readonly List<(string Name, Func<Task> Body)> Tests = new();
    public static void Test(string name, Action body) => Test(name, () => { body(); return Task.CompletedTask; });
    public static void Test(string name, Func<Task> body) => Tests.Add((name, body));
    public static void True(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Equal<T>(T expected, T actual, string message)
        => True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");
    public static void InRange(long value, long min, long max, string message) => True(value >= min && value <= max, message);
    public static async Task EsperarAsync(Func<bool> condition, string message, int seconds = 5)
    {
        var end = Environment.TickCount64 + seconds * 1000;
        while (!condition() && Environment.TickCount64 < end) await Task.Delay(10);
        True(condition(), message);
    }
    public static Task EsperarAsync(Func<bool> condition, Func<string> message, int seconds = 5)
        => EsperarAsync(condition, message(), seconds);
    public static async Task<int> RunAsync()
    {
        int failed = 0;
        foreach (var (name, body) in Tests)
        {
            try { await body().WaitAsync(TimeSpan.FromSeconds(45)); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
        }
        Console.WriteLine($"{Tests.Count - failed}/{Tests.Count} passed");
        return failed == 0 ? 0 : 1;
    }
}

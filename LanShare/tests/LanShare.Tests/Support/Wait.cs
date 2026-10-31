namespace LanShare.Tests;

static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string what = "condition")
    {
        var end = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(25);
        }
    }
}
using Suiyi.Core.Lifecycle;

namespace Suiyi.Core.Tests.Lifecycle;

public sealed class SingleInstanceGuardTests
{
    private static string UniqueName() => "Suiyi.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void DefaultName_IsSessionLocal()
    {
        Assert.Equal(@"Local\Suiyi.Client", SingleInstanceGuard.DefaultName);
    }

    [Fact]
    public void SecondAcquire_FailsUntilReleased()
    {
        var name = UniqueName();

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var first));
        Assert.NotNull(first);

        // 另一个线程模拟第二个实例（同线程上 Mutex 可重入）。
        bool secondAcquired = true;
        RunOnThread(() => secondAcquired = SingleInstanceGuard.TryAcquire(name, out var g) && Dispose(g));
        Assert.False(secondAcquired);

        first!.Dispose();

        bool thirdAcquired = false;
        RunOnThread(() => thirdAcquired = SingleInstanceGuard.TryAcquire(name, out var g) && Dispose(g));
        Assert.True(thirdAcquired);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        Assert.True(SingleInstanceGuard.TryAcquire(UniqueName(), out var guard));

        guard!.Dispose();
        guard.Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void EmptyName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => SingleInstanceGuard.TryAcquire(name, out _));
    }

    private static bool Dispose(SingleInstanceGuard? guard)
    {
        guard?.Dispose();
        return true;
    }

    private static void RunOnThread(Action action)
    {
        var thread = new Thread(() => action());
        thread.Start();
        thread.Join();
    }
}

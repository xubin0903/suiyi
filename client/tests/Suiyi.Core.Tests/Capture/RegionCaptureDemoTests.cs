using Suiyi.Core.Capture;

namespace Suiyi.Core.Tests.Capture;

public sealed class RegionCaptureDemoTests
{
    [Fact]
    public void FileName_LocalTimeAndSize()
    {
        var time = new DateTimeOffset(2026, 9, 25, 21, 5, 7, 89, TimeSpan.FromHours(8));

        Assert.Equal("region-20260925-210507-089-500x100.png", RegionCaptureDemo.BuildFileName(time, new PixelRect(-10, -10, 500, 100)));
    }

    [Fact]
    public void Directory_UnderTemp()
    {
        var temp = Path.GetTempPath();

        Assert.Equal(Path.Combine(temp, "suiyi-region-demo"), RegionCaptureDemo.ResolveDirectory(temp));
        Assert.Throws<ArgumentException>(() => RegionCaptureDemo.ResolveDirectory(" "));
    }

    [Fact]
    public void Switch_Name()
    {
        Assert.Equal("--region-demo", RegionCaptureDemo.Switch);
    }
}

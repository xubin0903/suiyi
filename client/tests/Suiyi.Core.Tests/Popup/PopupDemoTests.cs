using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Popup;

public sealed class PopupDemoTests
{
    [Fact]
    public void OneCycle_CoversAllStatesAndErrors()
    {
        var clock = new FakeTimeProvider();
        using var popup = new PopupViewModel(timeProvider: clock);
        var kinds = new List<PopupKind>();
        var errors = new List<PopupErrorKind>();
        var targets = new HashSet<string>();

        for (var step = 0; step < PopupDemo.StepCount; step++)
        {
            PopupDemo.ApplyStep(popup, step);
            clock.Advance(PopupDemo.Interval);
            kinds.Add(popup.Kind);
            if (popup.Error is { } error)
            {
                errors.Add(error.Kind);
            }

            if (popup.Kind == PopupKind.Result)
            {
                targets.Add(popup.LanguageLabel.Split(" → ")[1]);
            }

            Assert.True(popup.IsVisible);
        }

        Assert.Equal(PopupKind.Preparing, kinds[0]);
        Assert.Equal(PopupKind.Loading, kinds[1]);
        Assert.Contains(PopupKind.Result, kinds);
        Assert.Equal(
            [PopupErrorKind.ServiceUnavailable, PopupErrorKind.Timeout, PopupErrorKind.MissingModels, PopupErrorKind.DetectFailed, PopupErrorKind.TextTooLong],
            errors);
        Assert.Equal(new HashSet<string> { "中文", "English", "日本語" }, targets);
    }

    [Fact]
    public void Interval_IsThreeSeconds_ShorterThanAutoHide()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), PopupDemo.Interval);
        Assert.True(PopupDemo.Interval < TimeSpan.FromSeconds(new PopupOptions().AutoHideSeconds));
    }

    [Fact]
    public void Step_WrapsAndValidates()
    {
        using var popup = new PopupViewModel(timeProvider: new FakeTimeProvider());
        PopupDemo.ApplyStep(popup, PopupDemo.StepCount);
        Assert.Equal(PopupKind.Preparing, popup.Kind);

        PopupDemo.ApplyStep(popup, -1);
        Assert.Equal(PopupKind.Error, popup.Kind);

        Assert.Throws<ArgumentNullException>(() => PopupDemo.ApplyStep(null!, 0));
    }
}

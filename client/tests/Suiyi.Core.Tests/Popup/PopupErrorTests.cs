using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Popup;

public sealed class PopupErrorTests
{
    [Theory]
    [InlineData(PopupErrorKind.ServiceUnavailable, "翻译服务未运行或已退出")]
    [InlineData(PopupErrorKind.Timeout, "翻译超时，请重试")]
    [InlineData(PopupErrorKind.MissingModels, "未安装该语向的模型")]
    [InlineData(PopupErrorKind.DetectFailed, "无法识别原文语种，请点击语种标签手动指定")]
    [InlineData(PopupErrorKind.TextTooLong, "文本过长")]
    [InlineData(PopupErrorKind.Other, "翻译失败，请重试")]
    public void DefaultMessages(PopupErrorKind kind, string expected)
    {
        Assert.Equal(expected, new PopupError(kind).Message);
    }

    [Fact]
    public void MissingModels_ListsIds()
    {
        var error = new PopupError(PopupErrorKind.MissingModels) { MissingModels = ["opus-mt-en-zh", "opus-mt-ja-en"] };

        Assert.Equal("未安装语向模型：opus-mt-en-zh、opus-mt-ja-en", error.Message);
    }

    [Fact]
    public void TextTooLong_WithNumbers()
    {
        var error = new PopupError(PopupErrorKind.TextTooLong) { Limit = 5000, Length = 6234 };

        Assert.Equal("文本过长：6234 字，上限 5000 字", error.Message);
    }

    [Fact]
    public void Other_UsesDetail()
    {
        Assert.Equal("翻译服务内部错误", new PopupError(PopupErrorKind.Other) { Detail = " 翻译服务内部错误 " }.Message);
    }

    [Theory]
    [InlineData(PopupErrorKind.ServiceUnavailable, true)]
    [InlineData(PopupErrorKind.Timeout, true)]
    [InlineData(PopupErrorKind.MissingModels, true)]
    [InlineData(PopupErrorKind.DetectFailed, true)]
    [InlineData(PopupErrorKind.TextTooLong, false)]
    [InlineData(PopupErrorKind.Other, true)]
    public void CanRetry(PopupErrorKind kind, bool expected)
    {
        Assert.Equal(expected, new PopupError(kind).CanRetry);
    }

    [Fact]
    public void ServiceUnavailable_UsesDetailWhenPresent()
    {
        Assert.Equal("正在重启", new PopupError(PopupErrorKind.ServiceUnavailable) { Detail = " 正在重启 " }.Message);
        Assert.Equal("翻译服务未运行或已退出", new PopupError(PopupErrorKind.ServiceUnavailable) { Detail = " " }.Message);
    }
}

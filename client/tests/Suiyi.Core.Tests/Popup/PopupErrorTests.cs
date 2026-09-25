using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Popup;

public sealed class PopupErrorTests
{
    [Theory]
    [InlineData(PopupErrorKind.ServiceUnavailable, "翻译服务未运行或已退出，点「重试」会重启翻译服务")]
    [InlineData(PopupErrorKind.Timeout, "翻译超时，请重试")]
    [InlineData(PopupErrorKind.MissingModels, "未安装该语向的模型")]
    [InlineData(PopupErrorKind.DetectFailed, "无法识别原文语种，请点击语种标签手动指定")]
    [InlineData(PopupErrorKind.TextTooLong, "文本过长")]
    [InlineData(PopupErrorKind.Other, "翻译失败，请重试")]
    [InlineData(PopupErrorKind.EngineStartTimeout, "翻译服务启动超时，点「重试」会重启翻译服务")]
    [InlineData(PopupErrorKind.ImageTooLarge, "选区过大，请缩小选区后重新框选")]
    [InlineData(PopupErrorKind.InvalidImage, "截图无法识别，请重新框选")]
    [InlineData(PopupErrorKind.OcrUnavailable, "OCR 模型未安装")]
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
    [InlineData(PopupErrorKind.EngineStartTimeout, true)]
    [InlineData(PopupErrorKind.Cancelled, true)]
    public void CanRetry(PopupErrorKind kind, bool expected)
    {
        Assert.Equal(expected, new PopupError(kind).CanRetry);
    }

    [Fact]
    public void Cancelled_TellsUserToRetry_NoHint()
    {
        var error = new PopupError(PopupErrorKind.Cancelled);
        Assert.Equal("已取消：浮窗在完成前被关闭，点「重试」重新翻译", error.Message);
        Assert.Null(error.Hint);
    }

    [Fact]
    public void ServiceUnavailable_UsesDetailWhenPresent()
    {
        Assert.Equal("正在重启", new PopupError(PopupErrorKind.ServiceUnavailable) { Detail = " 正在重启 " }.Message);
        Assert.Equal("翻译服务未运行或已退出，点「重试」会重启翻译服务", new PopupError(PopupErrorKind.ServiceUnavailable) { Detail = " " }.Message);
    }

    [Theory]
    [InlineData(null, "OCR 模型未安装：det", @"请在随译仓库根目录运行 python scripts\download_ocr_models.py download 下载，完成后点「重试」")]
    [InlineData("models_missing", "OCR 模型未安装：det", @"请在随译仓库根目录运行 python scripts\download_ocr_models.py download 下载，完成后点「重试」")]
    [InlineData("future_reason", "OCR 模型未安装：det", @"请在随译仓库根目录运行 python scripts\download_ocr_models.py download 下载，完成后点「重试」")]
    [InlineData("models_invalid", "OCR 模型文件不完整或已损坏", @"请在随译仓库根目录运行 python scripts\download_ocr_models.py download 重新下载，完成后点「重试」")]
    [InlineData("dependency_missing", "OCR 组件未安装", "请在随译仓库根目录运行 pip install -e \"engine[ocr]\"，然后在托盘点「重启翻译服务」")]
    [InlineData("manifest_unavailable", "OCR 模型清单不可用", "请更新随译源码（git pull）后重启随译，详情见日志")]
    public void OcrUnavailable_MessageAndHintByReason(string? reason, string message, string hint)
    {
        var error = new PopupError(PopupErrorKind.OcrUnavailable) { MissingModels = ["det"], OcrReason = reason };

        Assert.Equal(message, error.Message);
        Assert.Equal(hint, error.Hint);
        Assert.True(error.CanRetry);
    }

    [Theory]
    [InlineData(PopupErrorKind.Timeout)]
    [InlineData(PopupErrorKind.MissingModels)]
    [InlineData(PopupErrorKind.ImageTooLarge)]
    public void NonOcrUnavailable_HasNoHint(PopupErrorKind kind)
    {
        Assert.Null(new PopupError(kind).Hint);
    }

    [Fact]
    public void OcrUnavailable_ListsModels()
    {
        Assert.Equal("OCR 模型未安装：det、rec", new PopupError(PopupErrorKind.OcrUnavailable) { MissingModels = ["det", "rec"] }.Message);
    }

    [Theory]
    [InlineData(PopupErrorKind.ImageTooLarge, false)]
    [InlineData(PopupErrorKind.TextTooLong, false)]
    [InlineData(PopupErrorKind.InvalidImage, true)]
    [InlineData(PopupErrorKind.OcrUnavailable, true)]
    public void OcrErrors_CanRetry(PopupErrorKind kind, bool expected)
    {
        Assert.Equal(expected, new PopupError(kind).CanRetry);
    }
}

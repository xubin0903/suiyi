using Suiyi.Core.Engine;
using Suiyi.Core.Flow;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Flow;

public sealed class PopupErrorMapperTests
{
    [Theory]
    [InlineData(EngineErrorKind.Unavailable, PopupErrorKind.ServiceUnavailable, "翻译服务未运行或已退出")]
    [InlineData(EngineErrorKind.Timeout, PopupErrorKind.Timeout, "翻译超时，请重试")]
    [InlineData(EngineErrorKind.UnsupportedPair, PopupErrorKind.MissingModels, "未安装该语向的模型")]
    [InlineData(EngineErrorKind.TextTooLong, PopupErrorKind.TextTooLong, "文本过长")]
    [InlineData(EngineErrorKind.DetectFailed, PopupErrorKind.DetectFailed, "无法识别原文语种，请点击语种标签手动指定")]
    [InlineData(EngineErrorKind.InvalidRequest, PopupErrorKind.Other, "翻译请求无效")]
    [InlineData(EngineErrorKind.Internal, PopupErrorKind.Other, "翻译服务内部错误")]
    public void Map_KindAndMessage(EngineErrorKind kind, PopupErrorKind expected, string message)
    {
        var error = PopupErrorMapper.Map(new EngineException(kind, "raw"));

        Assert.Equal(expected, error.Kind);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void Map_Unknown_IsOtherWithUserMessage()
    {
        var ex = new EngineException(EngineErrorKind.Unknown, "raw");

        var error = PopupErrorMapper.Map(ex);

        Assert.Equal(PopupErrorKind.Other, error.Kind);
        Assert.Equal(ex.UserMessage, error.Message);
        Assert.True(error.CanRetry);
    }

    [Fact]
    public void Map_UnsupportedPair_CarriesMissingModels()
    {
        var error = PopupErrorMapper.Map(new EngineException(EngineErrorKind.UnsupportedPair, "raw") { MissingModels = ["opus-mt-en-ja", "opus-mt-ja-en"] });

        Assert.Equal(["opus-mt-en-ja", "opus-mt-ja-en"], error.MissingModels);
        Assert.Equal("未安装语向模型：opus-mt-en-ja、opus-mt-ja-en", error.Message);
    }

    [Fact]
    public void Map_TextTooLong_CarriesLimitAndLength()
    {
        var error = PopupErrorMapper.Map(new EngineException(EngineErrorKind.TextTooLong, "raw") { Limit = 5000, Length = 5001 });

        Assert.Equal("文本过长：5001 字，上限 5000 字", error.Message);
        Assert.False(error.CanRetry);
    }

    [Fact]
    public void Map_Null_Throws() => Assert.Throws<ArgumentNullException>(() => PopupErrorMapper.Map(null!));

    [Fact]
    public void EngineFailed_PointsToTrayRestart()
    {
        var error = PopupErrorMapper.EngineFailed();

        Assert.Equal(PopupErrorKind.ServiceUnavailable, error.Kind);
        Assert.Equal(PopupErrorMapper.EngineFailedMessage, error.Message);
        Assert.Contains("重启翻译服务", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EngineFailureReason.LaunchFailed)]
    [InlineData(EngineFailureReason.ExitedBeforeReady)]
    [InlineData(EngineFailureReason.CrashedRepeatedly)]
    public void EngineFailed_OtherReasons_PointToTrayRestart(EngineFailureReason reason)
    {
        var error = PopupErrorMapper.EngineFailed(new EngineFailure(reason, "x"));

        Assert.Equal(PopupErrorMapper.EngineFailedMessage, error.Message);
    }

    [Fact]
    public void EngineFailed_StartupTimeout_IsStartTimeoutError()
    {
        var error = PopupErrorMapper.EngineFailed(new EngineFailure(EngineFailureReason.StartupTimeout, "x"));

        Assert.Equal(PopupErrorKind.EngineStartTimeout, error.Kind);
        Assert.True(error.CanRetry);
    }
}

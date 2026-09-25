using System.Globalization;

namespace Suiyi.Core.Engine;

/// <summary>
/// 开发用的环境变量覆盖，优先于设置文件里的 <c>engine.*</c>（<c>EngineSettings.ToEngineOptions</c> 之后应用），便于临时换端口、解释器等：
/// <c>SUIYI_ENGINE_PORT</c>、<c>SUIYI_ENGINE_PRELOAD</c>、<c>SUIYI_ENGINE_PYTHON</c>、
/// <c>SUIYI_ENGINE_MODELS_DIR</c>、<c>SUIYI_ENGINE_COMMAND</c>。
/// </summary>
public static class EngineOptionsOverrides
{
    /// <summary>端口。</summary>
    public const string PortVariable = "SUIYI_ENGINE_PORT";

    /// <summary>预热语向；设为空字符串表示不预热。</summary>
    public const string PreloadVariable = "SUIYI_ENGINE_PRELOAD";

    /// <summary>覆盖 <see cref="EngineOptions.PreloadOcr"/>：<c>1</c>/<c>true</c> 开启，<c>0</c>/<c>false</c> 关闭，其他值忽略。</summary>
    public const string PreloadOcrVariable = "SUIYI_ENGINE_PRELOAD_OCR";

    /// <summary>Python 解释器路径。</summary>
    public const string PythonVariable = "SUIYI_ENGINE_PYTHON";

    /// <summary>模型目录。</summary>
    public const string ModelsDirVariable = "SUIYI_ENGINE_MODELS_DIR";

    /// <summary>自定义启动命令（不支持额外参数）。</summary>
    public const string CommandVariable = "SUIYI_ENGINE_COMMAND";

    /// <summary>在 <paramref name="options"/> 上应用环境变量覆盖（纯函数）。</summary>
    /// <param name="options">基础配置。</param>
    /// <param name="getVariable">读取环境变量，未设置时返回 <see langword="null"/>。</param>
    public static EngineOptions Apply(EngineOptions options, Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getVariable);

        var result = options;
        if (int.TryParse(getVariable(PortVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535)
        {
            result = result with { Port = port };
        }

        if (getVariable(PreloadVariable) is { } preload)
        {
            result = result with { Preload = preload.Trim() };
        }

        switch (getVariable(PreloadOcrVariable)?.Trim().ToLowerInvariant())
        {
            case "1" or "true":
                result = result with { PreloadOcr = true };
                break;
            case "0" or "false":
                result = result with { PreloadOcr = false };
                break;
        }

        if (NonEmpty(getVariable(PythonVariable)) is { } python)
        {
            result = result with { PythonPath = python };
        }

        if (NonEmpty(getVariable(ModelsDirVariable)) is { } modelsDir)
        {
            result = result with { ModelsDir = modelsDir };
        }

        if (NonEmpty(getVariable(CommandVariable)) is { } command)
        {
            result = result with { Command = command };
        }

        return result;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

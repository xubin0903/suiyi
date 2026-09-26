namespace Suiyi.Core.Glossary;

// 与引擎的术语保护约定（#83 接口约定评论 issuecomment-5845549072，2026-09-26 版）。#83 代码尚未合入；
// 约定若有更新，以那条评论的最新内容为准，只需改本文件与 EngineDtos 里 HealthResponse 的 glossary_* 字段（及对应单测）。
// 客户端依赖约定的地方：本文件、Engine/EngineDtos.cs（TranslateRequest.Glossary、HealthResponse.Glossary*、GlossaryReloadResponse）、
// Engine/EngineCommandResolver.cs（启动参数 / 环境变量）、Engine/EngineClient.cs（请求字段、POST /glossary/reload）。

/// <summary>术语保护的启动配置怎样交给服务。</summary>
public enum GlossaryStartupTransport
{
    /// <summary>
    /// 环境变量 <c>SUIYI_GLOSSARY</c> / <c>SUIYI_USER_GLOSSARY</c>（约定第 1 节：命令行 &gt; 环境变量 &gt; 默认值，语义相同）。
    /// 旧版引擎忽略未知环境变量，因此客户端先于 #83 合入也不会让服务起不来。
    /// </summary>
    EnvironmentVariables,

    /// <summary>命令行 <c>--glossary</c> / <c>--no-glossary</c> 与 <c>--user-glossary &lt;path&gt;</c>。旧版引擎不认识，会启动失败。</summary>
    Arguments,
}

/// <summary>术语保护（#84）与引擎（#83）的对接约定。</summary>
public static class GlossaryContract
{
    /// <summary>设置项（<c>settings.json</c> 顶层节 <c>glossary</c>，字段 <c>enabled</c>）。</summary>
    public const string SettingName = "glossary.enabled";

    /// <summary>默认开启；旧设置文件缺这一节时按开启处理。</summary>
    public const bool DefaultEnabled = true;

    /// <summary>
    /// 启动配置的传递方式。当前用环境变量：与命令行等价，且不依赖 #83 与本 PR 的合入顺序。
    /// #83 合入、确认所有支持的引擎都认识新参数后，可改为 <see cref="GlossaryStartupTransport.Arguments"/>（约定建议的写法）。
    /// </summary>
    public const GlossaryStartupTransport StartupTransport = GlossaryStartupTransport.EnvironmentVariables;

    /// <summary>开启术语保护的 <c>serve</c> 参数。</summary>
    public const string EnableArgument = "--glossary";

    /// <summary>关闭术语保护的 <c>serve</c> 参数。</summary>
    public const string DisableArgument = "--no-glossary";

    /// <summary>用户术语表路径的 <c>serve</c> 参数。</summary>
    public const string UserGlossaryArgument = "--user-glossary";

    /// <summary>开关的环境变量（<c>1/0/true/false/on/off</c>）；客户端写 <c>1</c> / <c>0</c>。</summary>
    public const string EnabledVariable = "SUIYI_GLOSSARY";

    /// <summary>用户术语表路径的环境变量。</summary>
    public const string UserGlossaryVariable = "SUIYI_USER_GLOSSARY";

    /// <summary><c>POST /translate</c> 请求体里单次覆盖开关的字段（<c>true</c> / <c>false</c>）。客户端每次都按当前设置带上，切换后立即生效，不用重启服务。</summary>
    public const string RequestField = "glossary";

    /// <summary>立即重读用户术语表：<c>POST /glossary/reload</c>，返回与 <c>/health</c> 的 <c>glossary_*</c> 字段相同。</summary>
    public const string ReloadPath = "glossary/reload";

    /// <summary>用户术语表文件名：<c>&lt;设置目录&gt;\glossary.tsv</c>（设置目录与 <c>settings.json</c> 相同，可被 <c>SUIYI_CONFIG_DIR</c> 覆盖）。</summary>
    public const string UserFileName = "glossary.tsv";

    /// <summary>用户术语表大小上限（1 MiB），超过时整个用户表不生效。</summary>
    public const int MaxUserFileBytes = 1024 * 1024;

    /// <summary>用户术语表条数上限。</summary>
    public const int MaxUserEntries = 5000;
}

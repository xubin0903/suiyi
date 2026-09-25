namespace Suiyi.Core.Tests.Engine;

/// <summary>
/// 连接真实引擎的测试。默认跳过；设置环境变量 <c>SUIYI_ENGINE_PORT</c> 后运行：
/// <c>SUIYI_ENGINE_PORT=18780 dotnet test client/Suiyi.sln --filter Category=Engine</c>。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EngineFactAttribute : FactAttribute
{
    public const string PortVariable = "SUIYI_ENGINE_PORT";

    public EngineFactAttribute()
    {
        if (Port is null)
        {
            Skip = $"需要运行中的引擎：设置 {PortVariable}（例如 18780）后运行";
        }
    }

    public static int? Port =>
        int.TryParse(Environment.GetEnvironmentVariable(PortVariable), out var port) ? port : null;
}

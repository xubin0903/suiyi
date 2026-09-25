namespace Suiyi.Core.Capture;

/// <summary>框选截屏：显示全屏遮罩，用户拖拽选区，返回该区域截图。Windows 实现在 <c>Suiyi.App/Capture</c>。</summary>
public interface IRegionCapture
{
    /// <summary>
    /// 开始一次框选，直到用户完成或取消。必须在 UI 线程调用。
    /// </summary>
    /// <param name="cancellationToken">外部取消（例如退出程序）；取消时关闭遮罩并返回 <see langword="null"/>。</param>
    /// <returns>截图结果；用户取消（Esc、右键、选区过小）或外部取消时为 <see langword="null"/>。</returns>
    Task<RegionCaptureResult?> CaptureAsync(CancellationToken cancellationToken);
}

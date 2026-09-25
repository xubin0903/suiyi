using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Suiyi.Core.Engine;

/// <summary>
/// 用 <see cref="Process"/> 拉起服务：<c>UseShellExecute=false</c>、<c>CreateNoWindow=true</c>（不弹控制台），
/// 参数走 <see cref="ProcessStartInfo.ArgumentList"/>，stdout / stderr 以 UTF-8 异步逐行读取。
/// </summary>
public sealed class ProcessEngineLauncher : IEngineProcessLauncher
{
    private readonly Action<Process>? _afterStart;

    /// <summary>创建启动器。</summary>
    /// <param name="afterStart">
    /// 进程启动后立即调用，例如 Windows 上把进程加入 Job Object（在 <c>Suiyi.App</c> 实现）。
    /// 抛出的异常会结束刚启动的进程并以 <see cref="EngineLaunchException"/> 抛出。
    /// </param>
    public ProcessEngineLauncher(Action<Process>? afterStart = null)
    {
        _afterStart = afterStart;
    }

    /// <inheritdoc />
    public IEngineProcess Start(EngineCommand command, Action<EngineOutputLine> onOutput)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(onOutput);

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Python 在管道下默认按系统代码页和块缓冲输出；统一成 UTF-8、逐行刷新，日志里的中文才不会乱码或滞后。
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Forward(e.Data, isError: false);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, isError: true);

        try
        {
            if (!process.Start())
            {
                throw new EngineLaunchException($"无法启动 {command.FileName}");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            process.Dispose();
            throw new EngineLaunchException($"无法启动 {command.FileName}：{ex.Message}", ex);
        }

        try
        {
            _afterStart?.Invoke(process);
        }
        catch (Exception ex)
        {
            TryKill(process);
            process.Dispose();
            throw new EngineLaunchException($"进程已启动但初始化失败：{ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new ProcessHandle(process);

        void Forward(string? data, bool isError)
        {
            if (data is not null)
            {
                onOutput(new EngineOutputLine(data, isError));
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 已经退出。
        }
        catch (Win32Exception)
        {
            // 无权限或正在退出，交给调用方的超时处理。
        }
    }

    private sealed class ProcessHandle(Process process) : IEngineProcess
    {
        public int Id { get; } = process.Id;

        public bool HasExited => process.HasExited;

        public int? ExitCode => process.HasExited ? process.ExitCode : null;

        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

        public void Kill() => TryKill(process);

        public void Dispose() => process.Dispose();
    }
}

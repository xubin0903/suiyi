using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Suiyi.App.Interop;

/// <summary>
/// 设置了 <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> 的 Job Object。加入其中的进程在最后一个 Job 句柄关闭时被系统结束；
/// 客户端进程持有句柄，因此客户端被任务管理器强杀时，翻译服务随之退出。
/// </summary>
/// <remarks>加入 Job 之后由该进程创建的子进程默认也在 Job 里（例如 venv 的 python.exe 重定向器拉起的真正解释器）。</remarks>
internal sealed partial class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private IntPtr _handle;

    private JobObject(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>创建一个关闭即结束成员进程的匿名 Job。</summary>
    /// <exception cref="Win32Exception">创建或设置失败。</exception>
    public static JobObject CreateKillOnClose()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject 失败");
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        var size = (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, ref info, size))
        {
            var error = Marshal.GetLastPInvokeError();
            CloseHandle(handle);
            throw new Win32Exception(error, "SetInformationJobObject 失败");
        }

        return new JobObject(handle);
    }

    /// <summary>把进程加入 Job。</summary>
    /// <param name="process">刚启动的进程。</param>
    /// <exception cref="Win32Exception">加入失败。</exception>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject 失败");
        }
    }

    /// <summary>关闭 Job 句柄；仍在 Job 中的进程会被系统结束。</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateJobObjectW(IntPtr lpJobAttributes, IntPtr lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        IntPtr hJob,
        int jobObjectInformationClass,
        ref JobObjectExtendedLimitInformation lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}

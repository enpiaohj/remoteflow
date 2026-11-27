using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RemoteFlow.Protocol.PowerShell;

/// <summary>
/// 在 Windows 伪终端（ConPTY，Windows 10 1809 / Server 2019 起）里运行一个进程。
/// 只负责字节收发、改尺寸与结束进程，不解析终端内容。
/// </summary>
internal sealed class ConPtyProcess : IDisposable
{
    private const nint PseudoConsoleAttribute = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int UseStdHandles = 0x100;

    private readonly Lock _sync = new();
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly ProcessWaitHandle _processHandle;
    private IntPtr _console;
    private RegisteredWaitHandle? _exitRegistration;
    private bool _disposed;

    public int ProcessId { get; }

    /// <summary>终端输出的原始字节，在专用读取线程上触发。</summary>
    public event Action<byte[]>? Output;

    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>进程结束时完成。</summary>
    public Task Exited => _exited.Task;

    private ConPtyProcess(IntPtr console, SafeFileHandle input, SafeFileHandle output, IntPtr process, int processId)
    {
        _console = console;
        ProcessId = processId;
        _input = new FileStream(input, FileAccess.Write);
        _output = new FileStream(output, FileAccess.Read);
        _processHandle = new ProcessWaitHandle(process);
    }

    /// <param name="onOutput">输出处理器。启动即开始读取，需在构造时挂上以免丢掉开头内容。</param>
    public static ConPtyProcess Start(string commandLine, int columns, int rows, Action<byte[]> onOutput)
    {
        Check(CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0));
        Check(CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0));

        var console = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        try
        {
            var result = CreatePseudoConsole(ToSize(columns, rows), inRead, outWrite, 0, out console);
            if (result != 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            nint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            Check(UpdateProcThreadAttribute(attributes, 0, PseudoConsoleAttribute, console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero));

            var startup = new StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
            // 父进程的标准句柄若被重定向，子进程会沿用它们而绕过伪终端；显式置空。
            startup.StartupInfo.Flags = UseStdHandles;

            Check(CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                ExtendedStartupInfoPresent, IntPtr.Zero, null, ref startup, out var info));
            CloseHandle(info.Thread);

            var process = new ConPtyProcess(console, inWrite, outRead, info.Process, info.ProcessId);
            console = IntPtr.Zero; // 所有权已转交
            process.Output += onOutput;
            process.BeginWatching();
            return process;
        }
        catch
        {
            inWrite.Dispose();
            outRead.Dispose();
            if (console != IntPtr.Zero)
            {
                ClosePseudoConsole(console);
            }

            throw;
        }
        finally
        {
            // 子进程一侧的管道端由伪终端持有，本进程不再需要。
            inRead.Dispose();
            outWrite.Dispose();
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
        }
    }

    private void BeginWatching()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY reader" }.Start();
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(
            _processHandle, (_, _) => _exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private void ReadLoop()
    {
        var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = _output.Read(buffer, 0, buffer.Length)) > 0)
            {
                Output?.Invoke(buffer.AsSpan(0, count).ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 进程结束或终端关闭后管道断开，属正常收尾。
        }
    }

    public bool HasExited => _exited.Task.IsCompleted;

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _input.Write(data);
                _input.Flush();
            }
            catch (IOException)
            {
                // 进程已退出。
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_sync)
        {
            if (!_disposed && _console != IntPtr.Zero)
            {
                ResizePseudoConsole(_console, ToSize(columns, rows));
            }
        }
    }

    /// <summary>结束进程（若仍在运行）并释放终端。幂等。</summary>
    public void Dispose()
    {
        IntPtr console;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            console = _console;
            _console = IntPtr.Zero;
        }

        if (!HasExited)
        {
            TerminateProcess(_processHandle.SafeWaitHandle.DangerousGetHandle(), 1);
        }

        if (console != IntPtr.Zero)
        {
            ClosePseudoConsole(console);
        }

        _input.Dispose();
        _output.Dispose();
        _exitRegistration?.Unregister(null);
        _exited.TrySetResult();
        _processHandle.Dispose();
    }

    private static Coord ToSize(int columns, int rows) => new()
    {
        X = (short)Math.Clamp(columns, 20, 500),
        Y = (short)Math.Clamp(rows, 5, 300)
    };

    private static void Check(bool succeeded)
    {
        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr process) => SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nint attribute, IntPtr value, nint size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startupInfo, out ProcessInformation information);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}

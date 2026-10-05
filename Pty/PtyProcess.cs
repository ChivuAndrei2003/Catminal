using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Catminal.Pty;

/// <summary>
/// A child process attached to a pseudo-terminal.
///
/// Threading model (intentionally simple):
///   - <see cref="Read"/> blocks; call it from one dedicated reader thread.
///   - <see cref="Write(ReadOnlySpan{byte})"/> and <see cref="Resize"/> may be called from any thread
///     (typically the UI thread).
///   - A background thread waits for the child and completes <see cref="Exited"/>.
/// </summary>
public sealed unsafe class PtyProcess : IDisposable
{
    private readonly MasterHandle _master;

    private readonly TaskCompletionSource<int> _exited =
    new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public int Pid { get; }

    public PtySize Size { get; private set; }

    /// <summary>Completes with the exit code (128 + signal if the child was killed by a signal).</summary>
    public Task<int> Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    private PtyProcess(int pid, int masterFd, PtySize size)
    {
        Pid = pid;
        Size = size;
        _master = new MasterHandle(masterFd);

        var waiter = new Thread(WaitForChild) { IsBackground = true, Name = $"pty-wait-{pid}" };

        waiter.Start();
    }

    public static PtyProcess Start(PtyStartInfo? info = null)
    {
        info ??= new PtyStartInfo();
        var file = info.ResolveExecutable();

        var argv0 = Path.GetFileName(file);
        if (info.LoginShell) argv0 = "-" + argv0;

        var args = new List<string>(info.Arguments.Count + 1) { argv0 };
        args.AddRange(info.Arguments);
        var env = info.Environment.Select(kv => $"{kv.Key}={kv.Value}").ToList();

        // Everything the child needs is prepared *before* fork(): the child must not allocate.
        byte** argv = AllocCStringArray(args);
        byte** envp = AllocCStringArray(env);
        try
        {
            var size = info.Size;
            int pid = Native.PtySpawn(file, argv, envp, info.WorkingDirectory,
                                      checked((ushort)size.Columns), checked((ushort)size.Rows),
                                      out int masterFd);
            Native.Check(pid, $"spawn '{file}'");
            return new PtyProcess(pid, masterFd, size);
        }
        finally
        {
            FreeCStringArray(argv);
            FreeCStringArray(envp);
        }
    }

    /// <summary>
    /// Blocking read of terminal output. Returns 0 once the child (and everything else holding
    /// the slave side) has exited, or after <see cref="Dispose"/>.
    /// </summary>
    public int Read(Span<byte> buffer)
    {
        bool added = false;
        try
        {
            _master.DangerousAddRef(ref added); // keeps the fd from being closed (and reused) mid-read
            fixed (byte* p = buffer)
                return Native.Check(Native.Read(_master.Fd, p, buffer.Length), "read");
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
        finally
        {
            if (added) _master.DangerousRelease();
        }
    }

    /// <summary>Sends input (keystrokes, pasted text) to the child.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        bool added = false;
        try
        {
            _master.DangerousAddRef(ref added);
            fixed (byte* p = data)
                Native.Check(Native.WriteAll(_master.Fd, p, data.Length), "write");
        }
        finally
        {
            if (added) _master.DangerousRelease();
        }
    }

    public void Write(string text)
    {
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        Span<byte> buffer = max <= 1024 ? stackalloc byte[max] : new byte[max];
        int n = Encoding.UTF8.GetBytes(text, buffer);
        Write(buffer[..n]);
    }

    /// <summary>Changes the window size; the kernel delivers SIGWINCH to the foreground job.</summary>
    public void Resize(PtySize size, int widthPx = 0, int heightPx = 0)
    {
        bool added = false;
        try
        {
            _master.DangerousAddRef(ref added);
            Native.Check(Native.PtyResize(_master.Fd, checked((ushort)size.Columns), checked((ushort)size.Rows),
                                          (ushort)Math.Clamp(widthPx, 0, ushort.MaxValue),
                                          (ushort)Math.Clamp(heightPx, 0, ushort.MaxValue)), "resize");
            Size = size;
        }
        finally
        {
            if (added) _master.DangerousRelease();
        }
    }

    /// <summary>Sends a signal to the child process (not its process group).</summary>
    public void Signal(int signal)
    {
        if (HasExited) return;
        int rc = Native.Kill(Pid, signal);
        if (rc < 0 && -rc != 3 /* ESRCH: already gone */)
            Native.Check(rc, "kill");
    }

    /// <summary>
    /// Hangs up like closing a terminal window: SIGHUP, then SIGKILL if the child ignores it,
    /// then closes the master fd once no Read/Write is in flight.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (!HasExited)
        {
            Signal(Signals.SIGHUP);
            if (!Exited.Wait(TimeSpan.FromMilliseconds(500)))
                Signal(Signals.SIGKILL);
        }
        _master.Dispose();
    }

    private void WaitForChild()
    {
        int rc = Native.Wait(Pid, out int exitCode);
        if (rc < 0)
            _exited.TrySetException(new PtyException("waitpid", -rc));
        else
            _exited.TrySetResult(exitCode);
    }

    private static byte** AllocCStringArray(IReadOnlyList<string> items)
    {
        var array = (byte**)NativeMemory.AllocZeroed((nuint)(items.Count + 1), (nuint)sizeof(byte*));
        for (int i = 0; i < items.Count; i++)
            array[i] = (byte*)Marshal.StringToCoTaskMemUTF8(items[i]);
        return array; // NULL-terminated, as execve expects
    }

    private static void FreeCStringArray(byte** array)
    {
        if (array == null) return;
        for (int i = 0; array[i] != null; i++)
            Marshal.FreeCoTaskMem((nint)array[i]);
        NativeMemory.Free(array);
    }

    /// <summary>
    /// SafeHandle gives us reference counting for free: Dispose() only closes the fd after
    /// in-flight reads/writes release it, so a concurrent Read never hits a reused fd number.
    /// </summary>
    private sealed class MasterHandle : SafeHandle
    {
        public MasterHandle(int fd) : base(invalidHandleValue: -1, ownsHandle: true) => SetHandle(fd);

        public int Fd => (int)handle;
        public override bool IsInvalid => handle == -1;
        protected override bool ReleaseHandle() => Native.Close((int)handle) == 0;
    }



}
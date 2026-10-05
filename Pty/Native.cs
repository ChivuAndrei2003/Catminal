using System.Runtime.InteropServices;

namespace Catminal.Pty;

/// <summary>
/// P/Invoke surface of Native/Catminal_pty.c. Every function returns 0 or a
/// positive value on success and -errno on failure.
/// </summary>
internal static unsafe partial class Native
{
    private const string Lib = "Catminal_pty";

    [LibraryImport(Lib, EntryPoint = "Catminal_pty_spawn", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PtySpawn(string file, byte** argv, byte** envp, string? cwd,
                                         ushort cols, ushort rows, out int masterFd);

    [LibraryImport(Lib, EntryPoint = "Catminal_pty_resize")]
    internal static partial int PtyResize(int masterFd, ushort cols, ushort rows, ushort widthPx, ushort heightPx);

    [LibraryImport(Lib, EntryPoint = "Catminal_get_winsize")]
    internal static partial int GetWinSize(int fd, out ushort cols, out ushort rows);

    [LibraryImport(Lib, EntryPoint = "Catminal_read")]
    internal static partial int Read(int fd, byte* buffer, int length);

    [LibraryImport(Lib, EntryPoint = "Catminal_write_all")]
    internal static partial int WriteAll(int fd, byte* buffer, int length);

    [LibraryImport(Lib, EntryPoint = "Catminal_close")]
    internal static partial int Close(int fd);

    [LibraryImport(Lib, EntryPoint = "Catminal_kill")]
    internal static partial int Kill(int pid, int signal);

    [LibraryImport(Lib, EntryPoint = "Catminal_wait")]
    internal static partial int Wait(int pid, out int exitCode);

    [LibraryImport(Lib, EntryPoint = "Catminal_term_make_raw")]
    internal static partial int TermMakeRaw(int fd);

    [LibraryImport(Lib, EntryPoint = "Catminal_term_restore")]
    internal static partial int TermRestore();

    [LibraryImport(Lib, EntryPoint = "Catminal_isatty")]
    internal static partial int IsATty(int fd);

    /// <summary>Throws <see cref="PtyException"/> if <paramref name="rc"/> is a negative errno.</summary>
    internal static int Check(int rc, string operation)
    {
        if (rc < 0) throw new PtyException(operation, -rc);
        return rc;
    }
}

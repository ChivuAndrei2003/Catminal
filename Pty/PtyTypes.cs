using System.IO;
using System.Runtime.InteropServices;

namespace Catminal.Pty;

/// <summary>A native PTY operation failed. <see cref="Errno"/> is the raw errno value.</summary>
public sealed class PtyException : IOException
{
    public int Errno { get; }

    public PtyException(string operation, int errno)
        : base($"{operation} failed: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno})")
    {
        Errno = errno;
        HResult = errno;
    }
}

/// <summary>Terminal size in character cells.</summary>
public readonly record struct PtySize(int Columns, int Rows)
{
    public static readonly PtySize Default = new(80, 24);
}

/// <summary>Standard POSIX signal numbers that are identical on macOS and Linux.</summary>
public static class Signals
{
    public const int SIGHUP = 1;
    public const int SIGINT = 2;
    public const int SIGKILL = 9;
    public const int SIGTERM = 15;
}

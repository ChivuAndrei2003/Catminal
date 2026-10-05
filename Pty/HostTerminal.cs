using System;

namespace Catminal.Pty;

/// <summary>
/// Helpers for the terminal *this* process is running in. Used by the console demo to act
/// as a pass-through terminal (raw keystrokes in, raw bytes out), similar to `script(1)`.
/// Reads and writes go straight to fd 0/1, bypassing System.Console, which would otherwise
/// line-buffer and echo stdin itself.
/// </summary>
public static unsafe class HostTerminal
{
    public const int StdIn = 0;
    public const int StdOut = 1;

    public static bool IsTerminal(int fd) => Native.IsATty(fd) == 1;

    public static PtySize GetSize(int fd = StdOut)
    {
        Native.Check(Native.GetWinSize(fd, out ushort cols, out ushort rows), "TIOCGWINSZ");
        return new PtySize(cols, rows);
    }

    /// <summary>Puts the host terminal in raw mode (no echo, no line editing, no signal keys).</summary>
    public static void EnterRawMode(int fd = StdIn) => Native.Check(Native.TermMakeRaw(fd), "tcsetattr");

    public static void RestoreMode() => Native.TermRestore();

    public static int ReadInput(Span<byte> buffer)
    {
        fixed (byte* p = buffer)
            return Native.Check(Native.Read(StdIn, p, buffer.Length), "read stdin");
    }

    public static void WriteOutput(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
            Native.Check(Native.WriteAll(StdOut, p, data.Length), "write stdout");
    }
}

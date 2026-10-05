using System;
using System.Collections.Generic;
using System.IO;

namespace Catminal.Pty;

/// <summary>What to run inside the pseudo-terminal and how.</summary>
public sealed class PtyStartInfo
{
    /// <summary>
    /// Program to run. An absolute path, or a name looked up on PATH.
    /// When null, the user's shell ($SHELL, falling back to /bin/zsh on macOS and /bin/sh elsewhere).
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>Arguments after argv[0].</summary>
    public List<string> Arguments { get; } = new();

    /// <summary>
    /// Start as a login shell by prefixing argv[0] with '-' (e.g. "-zsh"), which is what
    /// Terminal.app does, so ~/.zprofile is read.
    /// </summary>
    public bool LoginShell { get; set; }

    public string? WorkingDirectory { get; set; }

    /// <summary>Environment for the child. Starts as a copy of the current process environment.</summary>
    public Dictionary<string, string> Environment { get; } = CopyCurrentEnvironment();

    public PtySize Size { get; set; } = PtySize.Default;

    public static string DefaultShell =>
        System.Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell
            ? shell
            : OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/sh";

    private static Dictionary<string, string> CopyCurrentEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        return env;
    }

    /// <summary>Resolves <see cref="FileName"/> to an absolute path using the child's PATH.</summary>
    internal string ResolveExecutable()
    {
        var file = FileName ?? DefaultShell;
        if (file.Contains('/'))
            return file;

        var path = Environment.TryGetValue("PATH", out var p) ? p : "/usr/bin:/bin";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new PtyException($"resolve '{file}' on PATH", errno: 2 /* ENOENT */);
    }
}

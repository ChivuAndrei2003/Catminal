 * Catminal_pty.c - native PTY (pseudo-terminal) layer for Catminal
 * (macOS, Linux).
 *
 * What this file is for
 *   A shell such as bash or zsh expects to be connected to a terminal: a
 *   device it reads keystrokes from and prints text to. Catminal is a
 *   terminal emulator, so it has to provide that device itself. The operating
 *   system offers one for this purpose, the pseudo-terminal (PTY).
 *
 *   A PTY is a two-ended channel, like a pipe that goes both ways:
 *     - the "slave" end is given to the shell, which treats it as a real
 *       terminal;
 *     - the "master" end is kept by Catminal. Bytes written to the master
 *       arrive at the shell as if typed on a keyboard, and everything the
 *       shell prints can be read back from the master.
 *
 *   This file starts the shell on a new PTY and gives the C# side a small
 *   set of functions to talk to it.
 *
 * Terms used below
 *   process      A running program. Each has a numeric id, the "pid".
 *   fd           File descriptor: a small integer the OS hands out for an
 *                open file, pipe or terminal. The "master fd" is the number
 *                that identifies Catminal's end of the PTY.
 *   errno        The numeric code the OS sets when a call fails, for example
 *                2 (ENOENT) for "no such file or directory".
 *   fork/exec    How Unix starts a program: fork() duplicates the current
 *                process, then the copy (the "child") calls execve() to
 *                replace itself with the new program. The original is the
 *                "parent".
 *   signal       A short notification the OS delivers to a process, such as
 *                "you were interrupted" or "the window changed size". Each
 *                signal has a default effect, often ending the process, and
 *                a program can install a "handler" to react differently.
 *   foreground   The command currently running in the shell and reading the
 *                keyboard, as opposed to "background" jobs started with `&`.
 *   blocking     A call that does not return until it has something to
 *                report. Call these from a background thread, not the UI one.
 *
 * Conventions
 *   - Every function returns 0 or a positive value on success and a negative
 *     errno on failure, so -2 means the call failed with ENOENT.
 *   - Reading and writing may happen on two different threads, but only one
 *     thread should read and only one should write.
 *   - When the shell ends, call Catminal_wait on its pid. Until then the OS
 *     keeps a leftover entry for the dead process (a "zombie").
 *   - When done with a terminal, call Catminal_close on its master fd.
 *
 * Functions
 *   Catminal_pty_spawn       Start a program (normally the shell) on a new
 *                            PTY. Returns its pid and stores the master fd.
 *                            If the program or the start directory does not
 *                            exist, returns the matching negative errno.
 *   Catminal_pty_resize      Tell the shell the window size changed, in
 *                            character cells and in pixels.
 *   Catminal_get_winsize     Read the current size of a terminal.
 *   Catminal_read            Wait for output from the shell and return the
 *                            number of bytes read. 0 means the shell is gone.
 *   Catminal_write_all       Send input to the shell. Keeps going until the
 *                            whole buffer is sent.
 *   Catminal_close           Close an fd.
 *   Catminal_kill            Send a signal to a process, by pid.
 *   Catminal_wait            Wait for a process to end and get its exit
 *                            code. If a signal ended it, the code is 128 +
 *                            the signal number, as shells report it.
 *   Catminal_term_make_raw   Switch the terminal Catminal itself was started
 *                            from into "raw mode": keys are passed through
 *                            one at a time, without echo or line editing.
 *                            The previous settings are saved.
 *   Catminal_term_restore    Put those saved settings back.
 *   Catminal_isatty          1 if the fd is a terminal, 0 if not.
 *
 * Signals
 *   Catminal almost never sends signals itself. It writes bytes to the
 *   master or changes the window size, and the OS turns some of those
 *   actions into signals for the programs inside the terminal. That is how
 *   pressing Ctrl+C stops a command: Catminal just sends one byte.
 *
 *   For this to work, Catminal_pty_spawn sets the shell up in two ways:
 *     - It makes the PTY the shell's "controlling terminal", the terminal
 *       the OS associates with the shell and everything started from it.
 *     - It clears any signal settings inherited from Catminal, so the shell
 *       starts with the defaults, like a shell opened in any other terminal.
 *
 *   Signal    Caused by                      Effect
 *   SIGINT    Ctrl+C (byte 0x03) written     Interrupts the foreground
 *             to the master.                 command. Usually ends it.

 *   SIGQUIT   Ctrl+\ (byte 0x1C).            Ends the foreground command
 *                                            and saves a crash dump.

 *   SIGTSTP   Ctrl+Z (byte 0x1A).            Pauses the foreground command;
 *                                            `fg` resumes it.

 *   SIGWINCH  Catminal_pty_resize, when      Tells the foreground program
 *             the size really changed.       to redraw for the new size.

 *   SIGHUP    Closing the master fd          "The terminal is gone." The
 *             (the window was closed).       shell and its commands exit.

 *   SIGTTIN   A background job tries to      Pauses that job until it is
 *             read from the terminal.        brought to the foreground.

 *   SIGTTOU   A background job tries to      Same, but only if the terminal
 *             write to the terminal.         is configured to forbid it.

 *   SIGCHLD   The shell exits.               Sent to Catminal as a notice;
 *                                            call Catminal_wait to collect
 *                                            the exit code.
 
 *   SIGPIPE   A program writes to a pipe     Ends the writer. Restored to
 *             whose reader has exited.       this default for the shell so
 *                                            `cmd | head` stops as expected.
 *
 *   Two caveats:
 *     - Full-screen programs such as vim or ssh turn off the Ctrl+C, Ctrl+\
 *       and Ctrl+Z translation. They receive those keys as ordinary input
 *       and decide for themselves what to do.
 *     - Catminal_kill bypasses the terminal: it delivers a signal straight
 *       to a pid, whatever state the terminal is in.
 

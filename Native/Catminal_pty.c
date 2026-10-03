#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdint.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>

#if defined(__APPLE__)
#include <util.h>
#else
#include <pty.h>
#endif

#if defined(__GNUC__)
#define EXPORT __attribute__((visibility("default")))
#else
#define EXPORT
#endif

#ifndef NSIG
#define NSIG 32
#endif

static int set_cloexec(int fd)
{
    int flags = fcntl(fd, F_GETFD);
    if (flags < 0 || fcntl(fd, F_SETFD, flags | FD_CLOEXEC) < 0)
        return -errno;

    return 0;
}

/* Child side of spawn. Only async-signal-safe calls from here until execve(). */

static void child_exec(int slave, int err_pipe, const char *file,
                       char *const argv[], char *const envp[], const char *cwd)
{
    int err;

    /* Unblock everything and restore default handlers (SIGPIPE, SIGCHLD, ...). */
    sigset_t all;
    sigemptyset(&all);
    sigprocmask(SIG_SETMASK, &all, NULL);
    struct sigaction dfl;
    memset(&dfl, 0, sizeof dfl);
    dfl.sa_handler = SIG_DFL;
    for (int sig = 1; sig < NSIG; sig++)
        sigaction(sig, &dfl, NULL); /* fails harmlessly for SIGKILL/SIGSTOP */

    /* New session, with the pty slave as controlling terminal.
     * This is what makes Ctrl+C, job control
      and SIGHUP-on-close work inside the shell. */
    if (setsid() < 0)
        goto fail;
    if (ioctl(slave, TIOCSCTTY, 0) < 0)
        goto fail;

    if (dup2(slave, STDIN_FILENO) < 0 || dup2(slave, STDOUT_FILENO) < 0 || dup2(slave, STDERR_FILENO) < 0)
        goto fail;
    if (slave > STDERR_FILENO)
        close(slave);

    if (cwd && cwd[0] && chdir(cwd) < 0)
        goto fail;

    execve(file, argv, envp);

fail:
    err = errno;
    /* Tell the parent why we failed; the pipe is CLOEXEC so a successful exec
     * closes it and the parent reads EOF instead. */
    while (write(err_pipe, &err, sizeof err) < 0 && errno == EINTR)
    {
    }
    _exit(127);
}

/*
 * Spawn `file` (absolute path) attached to a new pseudo-terminal.
 * Returns the child pid and stores the master fd in *out_master_fd.
 */
EXPORT int Catminal_pty_spawn(const char *file, char *const argv[], char *const envp[],
                              const char *cwd, uint16_t cols,
                              uint16_t rows, int *out_master_fd)
{
    int master = -1, slave = -1, pipefd[2] = {-1, -1}, rc;
    struct winsize ws;
    memset(&ws, 0, sizeof ws);
    ws.ws_col = cols;
    ws.ws_row = rows;

    if (openpty(&master, &slave, NULL, NULL, &ws) < 0)
        return -errno;

    if ((rc = set_cloexec(master)) < 0)
        goto error;

    if (pipe(pipefd) < 0)
    {
        rc = -errno;
        goto error;
    }

    if ((rc = set_cloexec(pipefd[0])) < 0 || (rc = set_cloexec(pipefd[1])) < 0)
        goto error;

    pid_t pid = fork();
    if (pid < 0)
    {
        rc = -errno;
        goto error;
    }

    if (pid == 0)
    {
        close(master);
        close(pipefd[0]);
        child_exec(slave, pipefd[1], file, argv, envp, cwd);
        /*  NOT reached */
    }

    /* PARENT */
    close(slave);
    slave = -1;
    close(pipefd[1]);
    pipefd[1] = -1;

    int child_err = 0;
    ssize_t n;
    do
    {
        n = read(pipefd[0], &child_err, sizeof child_err);
    } while (n < 0 && errno == EINTR);
    close(pipefd[0]);
    pipefd[0] = -1;

    if (n == (ssize_t)sizeof child_err)
    {

        int status;
        while (waitpid(pid, &status, 0) < 0 && errno == EINTR)
        {
        }
        close(master);
        return -child_err;
    }

    *out_master_fd = master;
    return (int)pid;

error:
    if (master >= 0)
        close(master);
    if (slave >= 0)
        close(slave);
    if (pipefd[0] >= 0)
        close(pipefd[0]);
    if (pipefd[1] >= 0)
        close(pipefd[1]);
    return rc;
}

/* Tell the kernel (and therefore the shell, via SIGWINCH) the new window size. */
EXPORT int Catminal_pty_resize(int master_fd, uint16_t cols, uint16_t rows, uint16_t width_px, uint16_t height_px)
{
    struct winsize ws;
    ws.ws_col = cols;
    ws.ws_row = rows;
    ws.ws_xpixel = width_px;
    ws.ws_ypixel = height_px;

    return ioctl(master_fd, TIOCSWINSZ, &ws) < 0 ? -errno : 0;
}

EXPORT int Catminal_get_winsize(int fd, uint16_t *cols, uint16_t *rows)
{
    struct winsize ws;
    if (ioctl(fd, TIOCGWINSZ, &ws) < 0)
        return -errno;
    *cols = ws.ws_col;
    *rows = ws.ws_row;

    return 0;
}

/*
 * Blocking read. Returns bytes read, 0 on EOF, -errno on error.
 * Once every process holding the slave side exits, reading the master fails
 * with EIO (Linux) or returns 0 (macOS); both are reported as EOF.
 */

EXPORT int Catminal_read(int fd, void *buf, int len)
{
    ssize_t n;
    do
    {
        n = read(fd, buf, (size_t)len);
    } while (n < 0 && errno == EINTR);
    if (n < 0)
        return errno == EIO ? 0 : -errno;

    return (int)n;
}

/* Writes the whole buffer (retrying partial writes). Returnss 0 or -errno. */
EXPORT int Catminal_write_all(int fd, const void *buf, int len)
{
    const char *p = (const char *)buf;

    while (len > 0)
    {
        ssize_t n = write(fd, p, (size_t)len);
        if (n < 0)
        {
            if (errno == EINTR)
                continue;
            return -errno;
        }
        p += n;
        len -= (int)n;
    }
    return 0;
}

EXPORT int Catminal_close(int fd)
{
    return close(fd) < 0 ? -errno : 0;
}

EXPORT int Catminal_kill(int pid, int sig)
{
    return kill((pid_t)pid, sig) < 0 ? -errno : 0;
}

/*
 * Blocking wait for `pid`. On success stores the exit code (128 + signal number
 * if the process was killed by a signal, like shells report it) and returns 0.
 */
EXPORT int Catminal_wait(int pid, int *exit_code)
{
    int status;
    pid_t r;
    do
    {
        r = waitpid((pid_t)pid, &status, 0);
    } while (r < 0 && errno == EINTR);

    if (r < 0)
        return -errno;

    if (WIFEXITED(status))
        *exit_code = WEXITSTATUS(status);
    else if (WIFSIGNALED(status))
        *exit_code = 128 + WTERMSIG(status);
    else
        *exit_code = -1;

    return 0;
}

/* ---- Raw mode for the *host* terminal () ---- */

static struct termios saved_termios;
static int saved_fd = -1;

EXPORT int Catminal_term_make_raw(int fd)
{
    struct termios t;
    if (tcgetattr(fd, &t) < 0)
        return -errno;
    saved_termios = t;
    saved_fd = fd;
    cfmakeraw(&t);
    return tcsetattr(fd, TCSAFLUSH, &t) < 0 ? -errno : 0;
}

EXPORT int Catminal_term_restore(void)
{
    if (saved_fd < 0)
        return 0;
    int rc = tcsetattr(saved_fd, TCSAFLUSH, &saved_termios) < 0 ? -errno : 0;
    saved_fd = -1;
    return rc;
}

EXPORT int Catminal_isatty(int fd)
{
    return isatty(fd);
}

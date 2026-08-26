// SPDX-License-Identifier: Apache-2.0

#define _GNU_SOURCE

#include <errno.h>
#include <fcntl.h>
#include <sched.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

static int write_all(int descriptor, const char *buffer, size_t length)
{
    while (length > 0)
    {
        ssize_t written = write(descriptor, buffer, length);
        if (written < 0)
        {
            if (errno == EINTR) continue;
            return -1;
        }
        buffer += written;
        length -= (size_t)written;
    }
    return 0;
}

static int write_process_file(pid_t process_id, const char *name, const char *contents, int allow_missing)
{
    char path[128];
    int path_length = snprintf(path, sizeof(path), "/proc/%ld/%s", (long)process_id, name);
    if (path_length < 0 || (size_t)path_length >= sizeof(path))
    {
        errno = ENAMETOOLONG;
        return -1;
    }

    int descriptor = open(path, O_WRONLY | O_CLOEXEC);
    if (descriptor < 0)
    {
        if (allow_missing && errno == ENOENT) return 0;
        return -1;
    }

    int result = write_all(descriptor, contents, strlen(contents));
    int write_error = errno;
    if (close(descriptor) < 0 && result == 0)
    {
        result = -1;
        write_error = errno;
    }
    errno = write_error;
    return result;
}

static int write_id_map(pid_t process_id, const char *name, unsigned int identifier)
{
    char mapping[64];
    int mapping_length = snprintf(mapping, sizeof(mapping), "%u %u 1\n", identifier, identifier);
    if (mapping_length < 0 || (size_t)mapping_length >= sizeof(mapping))
    {
        errno = EOVERFLOW;
        return -1;
    }
    return write_process_file(process_id, name, mapping, 0);
}

static int wait_for_child(pid_t process_id)
{
    int status;
    while (waitpid(process_id, &status, 0) < 0)
    {
        if (errno == EINTR) continue;
        fprintf(stderr, "murchalka-netns-exec: waitpid failed: %s\n", strerror(errno));
        return EXIT_FAILURE;
    }
    if (WIFEXITED(status)) return WEXITSTATUS(status);
    if (WIFSIGNALED(status)) return 128 + WTERMSIG(status);
    return EXIT_FAILURE;
}

int main(int argument_count, char **arguments)
{
    if (argument_count < 2)
    {
        fprintf(stderr, "usage: murchalka-netns-exec PROGRAM [ARGUMENT...]\n");
        return 2;
    }

    int ready_pipe[2];
    int continue_pipe[2];
    if (pipe2(ready_pipe, O_CLOEXEC) < 0 || pipe2(continue_pipe, O_CLOEXEC) < 0)
    {
        fprintf(stderr, "murchalka-netns-exec: pipe creation failed: %s\n", strerror(errno));
        return EXIT_FAILURE;
    }

    uid_t user_id = geteuid();
    gid_t group_id = getegid();
    pid_t child = fork();
    if (child < 0)
    {
        fprintf(stderr, "murchalka-netns-exec: fork failed: %s\n", strerror(errno));
        return EXIT_FAILURE;
    }

    if (child == 0)
    {
        close(ready_pipe[0]);
        close(continue_pipe[1]);
        pid_t parent = getppid();
        if (prctl(PR_SET_PDEATHSIG, SIGKILL) < 0 || getppid() != parent)
        {
            fprintf(stderr, "murchalka-netns-exec: parent-death protection failed\n");
            _exit(EXIT_FAILURE);
        }
        if (unshare(CLONE_NEWUSER | CLONE_NEWNET) < 0)
        {
            fprintf(stderr, "murchalka-netns-exec: namespace creation failed: %s\n", strerror(errno));
            _exit(EXIT_FAILURE);
        }
        if (write_all(ready_pipe[1], "R", 1) < 0)
            _exit(EXIT_FAILURE);
        close(ready_pipe[1]);

        char command;
        ssize_t received;
        do received = read(continue_pipe[0], &command, 1); while (received < 0 && errno == EINTR);
        close(continue_pipe[0]);
        if (received != 1 || command != 'C')
            _exit(EXIT_FAILURE);

        execv(arguments[1], &arguments[1]);
        fprintf(stderr, "murchalka-netns-exec: exec failed: %s\n", strerror(errno));
        _exit(127);
    }

    close(ready_pipe[1]);
    close(continue_pipe[0]);
    char ready;
    ssize_t received;
    do received = read(ready_pipe[0], &ready, 1); while (received < 0 && errno == EINTR);
    close(ready_pipe[0]);
    if (received != 1 || ready != 'R')
    {
        close(continue_pipe[1]);
        return wait_for_child(child);
    }

    if (write_process_file(child, "setgroups", "deny\n", 1) < 0 ||
        write_id_map(child, "uid_map", (unsigned int)user_id) < 0 ||
        write_id_map(child, "gid_map", (unsigned int)group_id) < 0)
    {
        fprintf(stderr, "murchalka-netns-exec: namespace identity mapping failed: %s\n", strerror(errno));
        close(continue_pipe[1]);
        kill(child, SIGKILL);
        (void)wait_for_child(child);
        return EXIT_FAILURE;
    }

    if (write_all(continue_pipe[1], "C", 1) < 0)
    {
        close(continue_pipe[1]);
        kill(child, SIGKILL);
        (void)wait_for_child(child);
        return EXIT_FAILURE;
    }
    close(continue_pipe[1]);
    return wait_for_child(child);
}

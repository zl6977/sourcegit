// Test-only low-memory fork fixture. Build with cc on the Linux test host.
#include <errno.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

int main(int argc, char **argv)
{
    int wanted = argc == 2 ? atoi(argv[1]) : 0;
    if (wanted < 1 || wanted > 4097) return 2;
    puts("ROOT_READY"); fflush(stdout);
    char command[32];
    if (!fgets(command, sizeof(command), stdin)) return 3;
    int ready[2];
    if (pipe(ready) != 0) return 4;
    int count = 0;
    for (; count < wanted; ++count)
    {
        pid_t child = fork();
        if (child < 0) { fprintf(stderr, "fork failed at %d: errno=%d\n", count, errno); break; }
        if (child == 0)
        {
            close(ready[0]);
            close(STDIN_FILENO); close(STDOUT_FILENO); close(STDERR_FILENO);
            char byte = 1;
            if (write(ready[1], &byte, 1) != 1) _exit(5);
            close(ready[1]);
            for (;;) pause(); // Native signal wait, never a periodic status loop.
        }
    }
    close(ready[1]);
    for (int observed = 0; observed < count; )
    {
        char bytes[256];
        ssize_t received = read(ready[0], bytes, sizeof(bytes));
        if (received < 0 && errno == EINTR) continue;
        if (received <= 0) return 6;
        observed += (int)received;
    }
    close(ready[0]);
    printf("CHILDREN:%d\n", count); fflush(stdout);
    // The managed runner first installs subreaper/containment, then requests
    // root exit. Descendants remain alive with all output pipes closed.
    if (!fgets(command, sizeof(command), stdin)) return 7;
    return 37;
}

#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <netinet/in.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

/*
 * DHMP Ring-3 Fixed-Slab Latest reference benchmark.
 *
 * Model:
 *   - fixed frame size is known before steady-state traffic;
 *   - three permanent state slots retain only the newest useful history;
 *   - oldest retained state is always overwritten;
 *   - one fixed reusable receive workspace amortizes socket I/O;
 *   - obsolete complete frames are skipped by fixed-size arithmetic;
 *   - no per-frame allocation, queue growth, shifting, or clearing.
 *
 * This is an architecture lab, not the production DHMP implementation.
 */

typedef struct {
    int listen_fd;
    int client_fd;
    size_t frame_size;
    size_t slab_bytes;
    double seconds;
    _Atomic int ready;
    _Atomic int start;
    _Atomic int stop;
    uint64_t frames;
    uint64_t reads;
    uint64_t published;
    uint64_t overwritten;
    double wall_seconds;
    double cpu_seconds;
} bench_t;

static double seconds_between(struct timespec a, struct timespec b) {
    return (double)(b.tv_sec - a.tv_sec)
         + (double)(b.tv_nsec - a.tv_nsec) / 1e9;
}

static void *receiver_main(void *arg) {
    bench_t *b = (bench_t *)arg;
    struct sockaddr_in peer;
    socklen_t peer_len = sizeof(peer);
    int fd = accept(b->listen_fd, (struct sockaddr *)&peer, &peer_len);
    if (fd < 0) {
        perror("accept");
        atomic_store(&b->stop, 1);
        return NULL;
    }

    const size_t f = b->frame_size;
    const size_t workspace_capacity = b->slab_bytes + f - 1;

    uint8_t *workspace = NULL;
    uint8_t *ring = NULL;
    if (posix_memalign((void **)&workspace, 64, workspace_capacity) != 0 ||
        posix_memalign((void **)&ring, 64, 3 * f) != 0) {
        fprintf(stderr, "allocation failed\n");
        atomic_store(&b->stop, 1);
        close(fd);
        return NULL;
    }

    size_t carry = 0;
    unsigned write_index = 0;

    atomic_store(&b->ready, 1);
    while (!atomic_load(&b->start) && !atomic_load(&b->stop)) { }

    struct timespec wall0, wall1, cpu0, cpu1, now;
    clock_gettime(CLOCK_MONOTONIC, &wall0);
    clock_gettime(CLOCK_THREAD_CPUTIME_ID, &cpu0);

    for (;;) {
        clock_gettime(CLOCK_MONOTONIC, &now);
        if (seconds_between(wall0, now) >= b->seconds)
            break;

        ssize_t n = recv(fd,
                         workspace + carry,
                         b->slab_bytes - carry,
                         0);

        if (n == 0)
            break;
        if (n < 0) {
            if (errno == EINTR)
                continue;
            perror("recv");
            break;
        }

        b->reads++;

        size_t total = carry + (size_t)n;
        size_t complete = total / f;
        size_t tail = total - complete * f;
        b->frames += complete;

        if (complete) {
            size_t keep = complete < 3 ? complete : 3;
            size_t first = complete - keep;

            for (size_t i = 0; i < keep; ++i) {
                memcpy(ring + (size_t)write_index * f,
                       workspace + (first + i) * f,
                       f);

                write_index++;
                if (write_index == 3)
                    write_index = 0;
            }

            b->published += keep;
            if (complete > keep)
                b->overwritten += complete - keep;
        }

        if (tail)
            memmove(workspace, workspace + complete * f, tail);
        carry = tail;
    }

    clock_gettime(CLOCK_THREAD_CPUTIME_ID, &cpu1);
    clock_gettime(CLOCK_MONOTONIC, &wall1);

    b->cpu_seconds = seconds_between(cpu0, cpu1);
    b->wall_seconds = seconds_between(wall0, wall1);

    atomic_store(&b->stop, 1);
    shutdown(fd, SHUT_RDWR);
    close(fd);
    free(ring);
    free(workspace);
    return NULL;
}

static void *sender_main(void *arg) {
    bench_t *b = (bench_t *)arg;
    uint8_t *sendbuf = NULL;

    if (posix_memalign((void **)&sendbuf, 64, b->slab_bytes) != 0) {
        fprintf(stderr, "sender allocation failed\n");
        atomic_store(&b->stop, 1);
        return NULL;
    }

    for (size_t i = 0; i < b->slab_bytes; ++i)
        sendbuf[i] = (uint8_t)i;

    while (!atomic_load(&b->start) && !atomic_load(&b->stop)) { }

    while (!atomic_load(&b->stop)) {
        size_t off = 0;

        while (off < b->slab_bytes && !atomic_load(&b->stop)) {
            ssize_t n = send(b->client_fd,
                             sendbuf + off,
                             b->slab_bytes - off,
                             MSG_NOSIGNAL);

            if (n > 0) {
                off += (size_t)n;
                continue;
            }
            if (n < 0 && errno == EINTR)
                continue;

            atomic_store(&b->stop, 1);
            break;
        }
    }

    free(sendbuf);
    return NULL;
}

int main(int argc, char **argv) {
    size_t frame = argc > 1 ? strtoull(argv[1], NULL, 10) : 32;
    double seconds = argc > 2 ? strtod(argv[2], NULL) : 2.0;
    size_t slab = argc > 3 ? strtoull(argv[3], NULL, 10) : 12288;

    if (!frame || slab < frame || seconds <= 0.0) {
        fprintf(stderr,
                "usage: %s [frame_bytes=32] [seconds=2] [slab_bytes=12288]\n",
                argv[0]);
        return 2;
    }

    slab -= slab % frame;
    if (!slab)
        slab = frame;

    int listen_fd = socket(AF_INET, SOCK_STREAM, 0);
    if (listen_fd < 0) {
        perror("socket");
        return 1;
    }

    int one = 1;
    setsockopt(listen_fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));

    struct sockaddr_in addr = {0};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = 0;

    if (bind(listen_fd, (struct sockaddr *)&addr, sizeof(addr)) != 0 ||
        listen(listen_fd, 1) != 0) {
        perror("bind/listen");
        return 1;
    }

    socklen_t alen = sizeof(addr);
    if (getsockname(listen_fd, (struct sockaddr *)&addr, &alen) != 0) {
        perror("getsockname");
        return 1;
    }

    bench_t b = {0};
    b.listen_fd = listen_fd;
    b.frame_size = frame;
    b.slab_bytes = slab;
    b.seconds = seconds;

    pthread_t receiver;
    pthread_t sender;
    pthread_create(&receiver, NULL, receiver_main, &b);

    int client_fd = socket(AF_INET, SOCK_STREAM, 0);
    if (client_fd < 0) {
        perror("client socket");
        return 1;
    }

    if (connect(client_fd, (struct sockaddr *)&addr, sizeof(addr)) != 0) {
        perror("connect");
        return 1;
    }

    b.client_fd = client_fd;
    pthread_create(&sender, NULL, sender_main, &b);

    while (!atomic_load(&b.ready) && !atomic_load(&b.stop)) { }
    atomic_store(&b.start, 1);

    pthread_join(receiver, NULL);
    pthread_join(sender, NULL);

    close(client_fd);
    close(listen_fd);

    double fps = b.wall_seconds > 0.0
        ? (double)b.frames / b.wall_seconds
        : 0.0;

    double payload_gbps = fps * (double)frame / 1e9;

    double cpu_ns_per_frame = b.frames
        ? b.cpu_seconds * 1e9 / (double)b.frames
        : 0.0;

    double overwritten_pct = b.frames
        ? 100.0 * (double)b.overwritten / (double)b.frames
        : 0.0;

    printf(
        "frame_bytes=%zu slab_bytes=%zu ring_payload_bytes=%zu "
        "wall_s=%.6f frames=%llu reads=%llu fps=%.3f "
        "payload_GBps=%.6f receiver_cpu_ns_per_frame=%.3f "
        "published=%llu overwritten=%llu overwritten_pct=%.6f\n",
        frame,
        slab,
        3 * frame,
        b.wall_seconds,
        (unsigned long long)b.frames,
        (unsigned long long)b.reads,
        fps,
        payload_gbps,
        cpu_ns_per_frame,
        (unsigned long long)b.published,
        (unsigned long long)b.overwritten,
        overwritten_pct);

    return 0;
}

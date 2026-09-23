#define _GNU_SOURCE
#include <pthread.h>
#include <sched.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

#define ITERATIONS 50000000ULL
#define FRAME_BYTES 32
#define SLOTS 3

typedef struct __attribute__((aligned(64))) {
    _Atomic uint64_t seq;
    unsigned char pad[56];
} publication_line_t;

typedef struct __attribute__((aligned(64))) {
    uint8_t data[FRAME_BYTES];
    uint8_t pad[64 - FRAME_BYTES];
} isolated_slot_t;

typedef struct {
    int isolated;
    _Atomic int start;
    _Atomic int stop;
    publication_line_t pub;
    uint8_t packed[SLOTS][FRAME_BYTES] __attribute__((aligned(64)));
    isolated_slot_t isolated_slots[SLOTS];
    double producer_cpu_s;
    double consumer_cpu_s;
    uint64_t consumer_samples;
    uint64_t checksum;
} bench_t;

static void pin_cpu(int cpu) {
    cpu_set_t set;
    CPU_ZERO(&set);
    CPU_SET(cpu, &set);
    pthread_setaffinity_np(pthread_self(), sizeof(set), &set);
}

static double thread_cpu_seconds(void) {
    struct timespec t;
    clock_gettime(CLOCK_THREAD_CPUTIME_ID, &t);
    return (double)t.tv_sec + (double)t.tv_nsec / 1e9;
}

static inline uint8_t *slot_ptr(bench_t *b, int index) {
    return b->isolated
        ? b->isolated_slots[index].data
        : b->packed[index];
}

static void *producer_main(void *arg) {
    bench_t *b = arg;
    pin_cpu(0);

    while (!atomic_load_explicit(&b->start, memory_order_acquire)) { }

    double t0 = thread_cpu_seconds();

    for (uint64_t n = 1; n <= ITERATIONS; ++n) {
        uint64_t *a = (uint64_t *)slot_ptr(b, 0);
        uint64_t *c = (uint64_t *)slot_ptr(b, 1);
        uint64_t *d = (uint64_t *)slot_ptr(b, 2);

        a[0] = n - 2; a[1] = n;     a[2] = ~n; a[3] = n + 1;
        c[0] = n - 1; c[1] = n + 2; c[2] = ~n; c[3] = n + 3;
        d[0] = n;     d[1] = n + 4; d[2] = ~n; d[3] = n + 5;

        atomic_store_explicit(&b->pub.seq, n, memory_order_release);
    }

    b->producer_cpu_s = thread_cpu_seconds() - t0;
    atomic_store_explicit(&b->stop, 1, memory_order_release);
    return NULL;
}

static void *consumer_main(void *arg) {
    bench_t *b = arg;
    pin_cpu(1);

    while (!atomic_load_explicit(&b->start, memory_order_acquire)) { }

    double t0 = thread_cpu_seconds();
    uint64_t last = 0;
    uint64_t samples = 0;
    uint64_t sum = 0;

    while (!atomic_load_explicit(&b->stop, memory_order_acquire)) {
        uint64_t seq = atomic_load_explicit(&b->pub.seq, memory_order_acquire);
        if (seq == last) continue;

        volatile uint64_t *d = (volatile uint64_t *)slot_ptr(b, 2);
        sum += d[0] + d[1] + d[2] + d[3];
        last = seq;
        ++samples;
    }

    b->consumer_cpu_s = thread_cpu_seconds() - t0;
    b->consumer_samples = samples;
    b->checksum = sum;
    return NULL;
}

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: %s packed|isolated\n", argv[0]);
        return 2;
    }

    bench_t *b = NULL;
    if (posix_memalign((void **)&b, 64, sizeof(*b)) != 0) return 1;
    memset(b, 0, sizeof(*b));
    b->isolated = strcmp(argv[1], "isolated") == 0;

    pthread_t producer;
    pthread_t consumer;
    pthread_create(&producer, NULL, producer_main, b);
    pthread_create(&consumer, NULL, consumer_main, b);

    usleep(10000);
    atomic_store_explicit(&b->start, 1, memory_order_release);

    pthread_join(producer, NULL);
    pthread_join(consumer, NULL);

    double ns_batch = b->producer_cpu_s * 1e9 / (double)ITERATIONS;

    printf(
        "%s producer_cpu_s=%.6f ns_per_three_state_batch=%.3f "
        "logical_ns_per_frame_equivalent=%.3f consumer_cpu_s=%.6f "
        "consumer_samples=%llu checksum=%llu\n",
        b->isolated ? "isolated" : "packed",
        b->producer_cpu_s,
        ns_batch,
        ns_batch / 3.0,
        b->consumer_cpu_s,
        (unsigned long long)b->consumer_samples,
        (unsigned long long)b->checksum
    );

    free(b);
    return 0;
}

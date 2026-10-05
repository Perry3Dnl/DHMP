#define _GNU_SOURCE
#include <errno.h>
#include <linux/if_xdp.h>
#include <net/if.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>
#include <arpa/inet.h>
#include <xdp/xsk.h>

#define DHMP_NUM_FRAMES 4096u
#define DHMP_FRAME_SIZE 2048u
#define DHMP_RING_SIZE 2048u
#define DHMP_BATCH 64u
#define DHMP_L2_BYTES 14u
#define DHMP_IPV6_BYTES 40u
#define DHMP_NEXT_HEADER 253u

enum dhmp_afxdp_mode {
    DHMP_AFXDP_UNAVAILABLE = 0,
    DHMP_AFXDP_COPY = 1,
    DHMP_AFXDP_ZERO_COPY = 2
};

struct dhmp_afxdp_result {
    int mode;
    int64_t packets_completed;
    int64_t payload_bytes_completed;
    double seconds;
};

struct dhmp_afxdp_context {
    struct xsk_ring_prod fill;
    struct xsk_ring_cons comp;
    struct xsk_ring_prod tx;
    struct xsk_umem *umem;
    struct xsk_socket *xsk;
    void *buffer;
    uint64_t free_frames[DHMP_NUM_FRAMES];
    uint32_t free_count;
    uint32_t outstanding;
    int mode;
};

static void set_error(char *buffer, size_t capacity, const char *message)
{
    if (!buffer || capacity == 0)
        return;

    if (!message)
        message = "unknown AF_XDP error";

    snprintf(buffer, capacity, "%s", message);
}

static void set_errno_error(
    char *buffer,
    size_t capacity,
    const char *prefix,
    int error_code)
{
    char temp[384];
    int positive = error_code < 0 ? -error_code : error_code;
    snprintf(
        temp,
        sizeof(temp),
        "%s: %s (%d)",
        prefix,
        strerror(positive),
        positive);
    set_error(buffer, capacity, temp);
}

static double seconds_between(
    const struct timespec *start,
    const struct timespec *end)
{
    return (double)(end->tv_sec - start->tv_sec) +
        (double)(end->tv_nsec - start->tv_nsec) / 1000000000.0;
}

static void destroy_context(struct dhmp_afxdp_context *ctx)
{
    if (!ctx)
        return;

    if (ctx->xsk) {
        xsk_socket__delete(ctx->xsk);
        ctx->xsk = NULL;
    }

    if (ctx->umem) {
        xsk_umem__delete(ctx->umem);
        ctx->umem = NULL;
    }

    free(ctx->buffer);
    ctx->buffer = NULL;
}

static int prepare_context(
    struct dhmp_afxdp_context *ctx,
    const char *ifname,
    uint32_t queue_id,
    int prefer_zero_copy,
    char *error,
    size_t error_capacity)
{
    memset(ctx, 0, sizeof(*ctx));

    if (!ifname || !*ifname) {
        set_error(error, error_capacity, "AF_XDP interface name is empty.");
        return -EINVAL;
    }

    if (if_nametoindex(ifname) == 0) {
        set_errno_error(
            error,
            error_capacity,
            "AF_XDP interface lookup failed",
            errno ? errno : ENODEV);
        return -(errno ? errno : ENODEV);
    }

    struct rlimit rlim = {
        .rlim_cur = 64u * 1024u * 1024u,
        .rlim_max = 64u * 1024u * 1024u
    };
    (void)setrlimit(RLIMIT_MEMLOCK, &rlim);

    const size_t umem_bytes =
        (size_t)DHMP_NUM_FRAMES * DHMP_FRAME_SIZE;

    int align_status = posix_memalign(
        &ctx->buffer,
        (size_t)getpagesize(),
        umem_bytes);

    if (align_status != 0 || !ctx->buffer) {
        set_errno_error(
            error,
            error_capacity,
            "AF_XDP UMEM allocation failed",
            align_status ? align_status : ENOMEM);
        destroy_context(ctx);
        return -(align_status ? align_status : ENOMEM);
    }

    memset(ctx->buffer, 0, umem_bytes);

    struct xsk_umem_config umem_config = {
        .fill_size = DHMP_RING_SIZE,
        .comp_size = DHMP_RING_SIZE,
        .frame_size = DHMP_FRAME_SIZE,
        .frame_headroom = 0,
        .flags = 0
    };

    int status = xsk_umem__create(
        &ctx->umem,
        ctx->buffer,
        umem_bytes,
        &ctx->fill,
        &ctx->comp,
        &umem_config);

    if (status != 0) {
        set_errno_error(
            error,
            error_capacity,
            "AF_XDP UMEM registration failed",
            status);
        destroy_context(ctx);
        return status;
    }

    struct xsk_socket_config socket_config = {
        .rx_size = 0,
        .tx_size = DHMP_RING_SIZE,
        .libxdp_flags = XSK_LIBXDP_FLAGS__INHIBIT_PROG_LOAD,
        .xdp_flags = 0,
        .bind_flags = (uint16_t)(
            XDP_USE_NEED_WAKEUP |
            (prefer_zero_copy ? XDP_ZEROCOPY : XDP_COPY))
    };

    status = xsk_socket__create(
        &ctx->xsk,
        ifname,
        queue_id,
        ctx->umem,
        NULL,
        &ctx->tx,
        &socket_config);

    if (status != 0 && prefer_zero_copy) {
        socket_config.bind_flags =
            (uint16_t)(XDP_USE_NEED_WAKEUP | XDP_COPY);

        status = xsk_socket__create(
            &ctx->xsk,
            ifname,
            queue_id,
            ctx->umem,
            NULL,
            &ctx->tx,
            &socket_config);

        if (status == 0)
            ctx->mode = DHMP_AFXDP_COPY;
    } else if (status == 0) {
        ctx->mode =
            prefer_zero_copy
                ? DHMP_AFXDP_ZERO_COPY
                : DHMP_AFXDP_COPY;
    }

    if (status != 0) {
        set_errno_error(
            error,
            error_capacity,
            "AF_XDP socket bind failed",
            status);
        destroy_context(ctx);
        return status;
    }

    if (ctx->mode == DHMP_AFXDP_UNAVAILABLE)
        ctx->mode = DHMP_AFXDP_COPY;

    ctx->free_count = DHMP_NUM_FRAMES;
    for (uint32_t i = 0; i < DHMP_NUM_FRAMES; ++i)
        ctx->free_frames[i] = (uint64_t)i * DHMP_FRAME_SIZE;

    return 0;
}

static void build_frame(
    void *frame,
    int payload_bytes,
    uint64_t sequence)
{
    unsigned char *bytes = (unsigned char *)frame;
    const uint32_t frame_bytes =
        DHMP_L2_BYTES + DHMP_IPV6_BYTES + (uint32_t)payload_bytes;

    memset(bytes, 0, frame_bytes);

    for (int i = 0; i < 6; ++i)
        bytes[i] = 0xff;

    bytes[6] = 0x02;
    bytes[11] = 0x01;
    bytes[12] = 0x86;
    bytes[13] = 0xdd;

    unsigned char *ipv6 = bytes + DHMP_L2_BYTES;
    ipv6[0] = 0x60;

    uint16_t payload_length = htons((uint16_t)payload_bytes);
    memcpy(ipv6 + 4, &payload_length, sizeof(payload_length));
    ipv6[6] = DHMP_NEXT_HEADER;
    ipv6[7] = 64;

    ipv6[23] = 1;
    ipv6[39] = 2;

    unsigned char *payload =
        bytes + DHMP_L2_BYTES + DHMP_IPV6_BYTES;

    memcpy(
        payload,
        &sequence,
        payload_bytes >= (int)sizeof(sequence)
            ? sizeof(sequence)
            : (size_t)payload_bytes);

    for (int i = (int)sizeof(sequence); i < payload_bytes; ++i)
        payload[i] = (unsigned char)(sequence + (uint64_t)i);
}

static uint32_t reclaim_completions(
    struct dhmp_afxdp_context *ctx)
{
    uint32_t index = 0;
    uint32_t completed = xsk_ring_cons__peek(
        &ctx->comp,
        DHMP_BATCH,
        &index);

    if (completed == 0)
        return 0;

    for (uint32_t i = 0; i < completed; ++i) {
        uint64_t address =
            *xsk_ring_cons__comp_addr(
                &ctx->comp,
                index + i);

        if (ctx->free_count < DHMP_NUM_FRAMES)
            ctx->free_frames[ctx->free_count++] = address;
    }

    xsk_ring_cons__release(
        &ctx->comp,
        completed);

    ctx->outstanding =
        completed >= ctx->outstanding
            ? 0
            : ctx->outstanding - completed;

    return completed;
}

__attribute__((visibility("default")))
int dhmp_afxdp_probe(
    const char *ifname,
    uint32_t queue_id,
    int prefer_zero_copy,
    char *error,
    size_t error_capacity,
    int *mode)
{
    if (mode)
        *mode = DHMP_AFXDP_UNAVAILABLE;

    struct dhmp_afxdp_context ctx;
    int status = prepare_context(
        &ctx,
        ifname,
        queue_id,
        prefer_zero_copy,
        error,
        error_capacity);

    if (status != 0)
        return status;

    if (mode)
        *mode = ctx.mode;

    if (ctx.mode == DHMP_AFXDP_ZERO_COPY)
        set_error(error, error_capacity, "AF_XDP zero-copy TX initialized.");
    else
        set_error(error, error_capacity, "AF_XDP copy-mode TX initialized.");

    destroy_context(&ctx);
    return 0;
}

__attribute__((visibility("default")))
int dhmp_afxdp_tx_benchmark(
    const char *ifname,
    uint32_t queue_id,
    int payload_bytes,
    int64_t packets,
    int prefer_zero_copy,
    char *error,
    size_t error_capacity,
    struct dhmp_afxdp_result *result)
{
    if (!result)
        return -EINVAL;

    memset(result, 0, sizeof(*result));

    if (payload_bytes <= 0 || payload_bytes > 1408 || packets <= 0) {
        set_error(
            error,
            error_capacity,
            "AF_XDP benchmark arguments are outside the supported range.");
        return -EINVAL;
    }

    struct dhmp_afxdp_context ctx;
    int status = prepare_context(
        &ctx,
        ifname,
        queue_id,
        prefer_zero_copy,
        error,
        error_capacity);

    if (status != 0)
        return status;

    const uint32_t frame_bytes =
        DHMP_L2_BYTES + DHMP_IPV6_BYTES + (uint32_t)payload_bytes;

    for (uint32_t i = 0; i < DHMP_NUM_FRAMES; ++i) {
        build_frame(
            (unsigned char *)ctx.buffer +
                (size_t)i * DHMP_FRAME_SIZE,
            payload_bytes,
            i);
    }

    int64_t submitted = 0;
    int64_t completed_total = 0;
    struct timespec started;
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC_RAW, &started);

    while (submitted < packets || ctx.outstanding > 0) {
        completed_total += reclaim_completions(&ctx);

        if (submitted < packets && ctx.free_count > 0) {
            uint32_t wanted = DHMP_BATCH;

            int64_t remaining = packets - submitted;
            if (remaining < (int64_t)wanted)
                wanted = (uint32_t)remaining;

            if (ctx.free_count < wanted)
                wanted = ctx.free_count;

            uint32_t index = 0;
            uint32_t reserved =
                xsk_ring_prod__reserve(
                    &ctx.tx,
                    wanted,
                    &index);

            if (reserved > 0) {
                for (uint32_t i = 0; i < reserved; ++i) {
                    uint64_t address =
                        ctx.free_frames[--ctx.free_count];

                    build_frame(
                        (unsigned char *)ctx.buffer + address,
                        payload_bytes,
                        (uint64_t)(submitted + i));

                    struct xdp_desc *desc =
                        xsk_ring_prod__tx_desc(
                            &ctx.tx,
                            index + i);

                    desc->addr = address;
                    desc->len = frame_bytes;
                    desc->options = 0;
                }

                xsk_ring_prod__submit(
                    &ctx.tx,
                    reserved);

                submitted += reserved;
                ctx.outstanding += reserved;
            }
        }

        if (ctx.outstanding > 0 &&
            xsk_ring_prod__needs_wakeup(&ctx.tx)) {
            (void)sendto(
                xsk_socket__fd(ctx.xsk),
                NULL,
                0,
                MSG_DONTWAIT,
                NULL,
                0);
        }

        clock_gettime(CLOCK_MONOTONIC_RAW, &now);
        if (seconds_between(&started, &now) > 20.0) {
            set_error(
                error,
                error_capacity,
                "AF_XDP TX benchmark timed out waiting for completions.");
            destroy_context(&ctx);
            return -ETIMEDOUT;
        }
    }

    clock_gettime(CLOCK_MONOTONIC_RAW, &now);

    result->mode = ctx.mode;
    result->packets_completed = completed_total;
    result->payload_bytes_completed =
        completed_total * (int64_t)payload_bytes;
    result->seconds =
        seconds_between(&started, &now);

    if (ctx.mode == DHMP_AFXDP_ZERO_COPY)
        set_error(error, error_capacity, "AF_XDP zero-copy TX benchmark completed.");
    else
        set_error(error, error_capacity, "AF_XDP copy-mode TX benchmark completed.");

    destroy_context(&ctx);
    return 0;
}

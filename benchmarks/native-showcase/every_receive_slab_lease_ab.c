#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <immintrin.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <pthread.h>
#include <sched.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#define FRAME 32u
#define SLAB 12288u
#define POOL 8u
#define SEND_FRAMES 8192u
#define SEND_BYTES (SEND_FRAMES*FRAME)
#define TOTAL_FRAMES 30000000ull
#define MAGIC 0x9e3779b97f4a7c15ULL

typedef struct { uint64_t a,b,c,d; } frame_t;
typedef enum { MODE_COPY=0, MODE_BORROW=1 } mode_t2;

typedef struct __attribute__((aligned(64))) {
    unsigned char data[SLAB] __attribute__((aligned(64)));
    uint32_t offset;
    uint32_t count;
    _Atomic uint32_t ready;
    unsigned char pad[52];
} slab_t;

typedef struct {
    mode_t2 mode;
    int tx,rx;
    _Atomic int start;
    slab_t slabs[POOL];
    uint64_t received_frames,delivered_frames,batches;
    uint64_t producer_wait_spins,consumer_wait_spins,errors;
    uint64_t boundary_events,boundary_bytes_copied;
    double receiver_cpu_s,consumer_cpu_s,wall_s;
} bench_t;

static inline uint64_t ns_now(void){ struct timespec t; clock_gettime(CLOCK_MONOTONIC_RAW,&t); return (uint64_t)t.tv_sec*1000000000ull+t.tv_nsec; }
static inline double cpu_now(void){ struct timespec t; clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t); return (double)t.tv_sec+(double)t.tv_nsec/1e9; }
static inline void relax(void){ _mm_pause(); }
static void pin(int c){ cpu_set_t s; CPU_ZERO(&s); CPU_SET(c,&s); pthread_setaffinity_np(pthread_self(),sizeof(s),&s); }

static inline frame_t make_frame(uint64_t n){ frame_t x={n,n^MAGIC,~n,n+0x12345678ULL}; return x; }
static inline int valid_frame(frame_t x,uint64_t n){ frame_t e=make_frame(n); return x.a==e.a && x.b==e.b && x.c==e.c && x.d==e.d; }

static int make_pair(int *tx,int *rx){
    int l=socket(AF_INET,SOCK_STREAM,0); if(l<0) return -1;
    int one=1; setsockopt(l,SOL_SOCKET,SO_REUSEADDR,&one,sizeof(one));
    struct sockaddr_in a={0}; a.sin_family=AF_INET; a.sin_addr.s_addr=htonl(INADDR_LOOPBACK); a.sin_port=0;
    if(bind(l,(struct sockaddr*)&a,sizeof(a)) || listen(l,1)){ close(l); return -1; }
    socklen_t alen=sizeof(a); getsockname(l,(struct sockaddr*)&a,&alen);
    int c=socket(AF_INET,SOCK_STREAM,0); if(c<0){close(l);return -1;}
    setsockopt(c,IPPROTO_TCP,TCP_NODELAY,&one,sizeof(one));
    if(connect(c,(struct sockaddr*)&a,sizeof(a))){close(c);close(l);return -1;}
    int s=accept(l,NULL,NULL); close(l); if(s<0){close(c);return -1;}
    setsockopt(s,IPPROTO_TCP,TCP_NODELAY,&one,sizeof(one));
    int sz=4*1024*1024;
    setsockopt(c,SOL_SOCKET,SO_SNDBUF,&sz,sizeof(sz));
    setsockopt(s,SOL_SOCKET,SO_RCVBUF,&sz,sizeof(sz));
    *tx=c; *rx=s; return 0;
}

static void *sender(void *arg){
    bench_t*b=arg; pin(0);
    frame_t *buf=NULL; posix_memalign((void**)&buf,64,SEND_BYTES);
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    uint64_t sent=0;
    while(sent<TOTAL_FRAMES){
        size_t frames=(TOTAL_FRAMES-sent<SEND_FRAMES)?(size_t)(TOTAL_FRAMES-sent):SEND_FRAMES;
        for(size_t i=0;i<frames;i++) buf[i]=make_frame(sent+i+1);
        size_t bytes=frames*FRAME,off=0;
        while(off<bytes){
            ssize_t n=send(b->tx,(unsigned char*)buf+off,bytes-off,MSG_NOSIGNAL);
            if(n>0){ off+=(size_t)n; continue; }
            if(n<0 && errno==EINTR) continue;
            goto done;
        }
        sent+=frames;
    }
done:
    shutdown(b->tx,SHUT_WR); free(buf); return NULL;
}

static slab_t *claim_slab(bench_t*b,unsigned slot){
    slab_t*s=&b->slabs[slot];
    while(atomic_load_explicit(&s->ready,memory_order_acquire)){ b->producer_wait_spins++; relax(); }
    return s;
}

static void *receiver(void *arg){
    bench_t*b=arg; pin(1);
    unsigned slot=0;
    unsigned char carry[FRAME] __attribute__((aligned(32)));
    size_t carry_len=0;
    unsigned char *workspace=NULL;
    if(b->mode==MODE_COPY) posix_memalign((void**)&workspace,64,SLAB+FRAME);

    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    double c0=cpu_now();

    for(;;){
        slab_t *out=claim_slab(b,slot);
        unsigned char *dst=(b->mode==MODE_BORROW)?out->data:workspace;

        if(carry_len){
            memcpy(dst,carry,carry_len);
            b->boundary_bytes_copied+=carry_len;
        }

        ssize_t n=recv(b->rx,dst+carry_len,SLAB-carry_len,0);
        if(n==0) break;
        if(n<0){ if(errno==EINTR) continue; break; }

        size_t total=carry_len+(size_t)n;
        size_t complete=total/FRAME;
        size_t tail=total%FRAME;

        if(tail){
            memcpy(carry,dst+complete*FRAME,tail);
            b->boundary_events++;
            b->boundary_bytes_copied+=tail;
        }
        carry_len=tail;

        if(complete){
            if(b->mode==MODE_COPY) memcpy(out->data,dst,complete*FRAME);
            out->offset=0;
            out->count=(uint32_t)complete;
            atomic_store_explicit(&out->ready,1,memory_order_release);
            b->received_frames+=complete;
            b->batches++;
            slot=(slot+1)%POOL;
        }
    }

    if(carry_len) b->errors++;
    b->receiver_cpu_s=cpu_now()-c0;
    free(workspace);
    return NULL;
}

static void *consumer(void *arg){
    bench_t*b=arg; pin(2);
    unsigned slot=0;
    uint64_t expected=1,delivered=0,errors=0;

    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    double c0=cpu_now();

    while(delivered<TOTAL_FRAMES){
        slab_t*s=&b->slabs[slot];
        if(!atomic_load_explicit(&s->ready,memory_order_acquire)){
            b->consumer_wait_spins++; relax(); continue;
        }

        frame_t *frames=(frame_t*)(s->data+s->offset);
        for(uint32_t i=0;i<s->count;i++,expected++,delivered++)
            if(!valid_frame(frames[i],expected)) errors++;

        atomic_store_explicit(&s->ready,0,memory_order_release);
        slot=(slot+1)%POOL;
    }

    b->consumer_cpu_s=cpu_now()-c0;
    b->delivered_frames=delivered;
    b->errors+=errors;
    return NULL;
}

int main(int argc,char**argv){
    if(argc<2) return 2;

    bench_t*b=NULL;
    if(posix_memalign((void**)&b,64,sizeof(*b))) return 1;
    memset(b,0,sizeof(*b));
    b->mode=!strcmp(argv[1],"borrow")?MODE_BORROW:MODE_COPY;

    if(make_pair(&b->tx,&b->rx)){ perror("pair"); return 1; }

    pthread_t ts,tr,tc;
    pthread_create(&tc,NULL,consumer,b);
    pthread_create(&tr,NULL,receiver,b);
    pthread_create(&ts,NULL,sender,b);

    usleep(20000);
    uint64_t w0=ns_now();
    atomic_store_explicit(&b->start,1,memory_order_release);

    pthread_join(ts,NULL);
    pthread_join(tr,NULL);
    pthread_join(tc,NULL);
    b->wall_s=(double)(ns_now()-w0)/1e9;

    printf("mode=%s wall_s=%.6f received=%llu delivered=%llu e2e_fps=%.3f receiver_cpu_ns_frame=%.6f consumer_cpu_ns_frame=%.6f batches=%llu frames_per_batch=%.3f boundary_events=%llu boundary_bytes_copied=%llu producer_wait_spins=%llu consumer_wait_spins=%llu errors=%llu\n",
      b->mode==MODE_BORROW?"borrow":"copy",b->wall_s,
      (unsigned long long)b->received_frames,(unsigned long long)b->delivered_frames,
      (double)b->delivered_frames/b->wall_s,
      b->receiver_cpu_s*1e9/(double)b->received_frames,
      b->consumer_cpu_s*1e9/(double)b->delivered_frames,
      (unsigned long long)b->batches,
      (double)b->received_frames/(double)b->batches,
      (unsigned long long)b->boundary_events,
      (unsigned long long)b->boundary_bytes_copied,
      (unsigned long long)b->producer_wait_spins,
      (unsigned long long)b->consumer_wait_spins,
      (unsigned long long)b->errors);

    int rc=(b->errors||b->received_frames!=TOTAL_FRAMES||b->delivered_frames!=TOTAL_FRAMES)?3:0;
    close(b->tx); close(b->rx); free(b); return rc;
}

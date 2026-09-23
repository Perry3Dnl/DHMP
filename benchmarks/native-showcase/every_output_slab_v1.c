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
#define FRAMES_PER_SLAB (SLAB/FRAME)
#define OUT_SLABS 8u
#define OUT_CAP (OUT_SLABS*FRAMES_PER_SLAB)
#define SEND_FRAMES 8192u
#define SEND_BYTES (SEND_FRAMES*FRAME)
#define TOTAL_FRAMES 30000000ull
#define MAGIC 0x9e3779b97f4a7c15ULL

typedef struct { uint64_t a,b,c,d; } frame_t;
typedef enum { MODE_PER_RESULT=0, MODE_BATCH=1 } bench_mode_t;

typedef struct __attribute__((aligned(64))) {
    frame_t items[OUT_CAP];
    _Atomic uint64_t head;
    char pad0[56];
    _Atomic uint64_t tail;
    char pad1[56];
} result_ring_t;

typedef struct __attribute__((aligned(64))) {
    frame_t items[FRAMES_PER_SLAB];
    uint32_t count;
    uint32_t pad;
    _Atomic uint32_t ready;
    char pad2[52];
} out_slab_t;

typedef struct {
    bench_mode_t mode;
    int tx,rx;
    _Atomic int start;
    result_ring_t ring;
    out_slab_t slabs[OUT_SLABS];
    uint64_t processed,consumed,output_publications;
    uint64_t producer_wait_spins,consumer_wait_spins,errors;
    double receiver_cpu_s,consumer_cpu_s,receiver_wall_s,end_to_end_wall_s;
} bench_t;

static inline uint64_t ns_now(void){
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC_RAW,&t);
    return (uint64_t)t.tv_sec*1000000000ull+t.tv_nsec;
}
static inline double cpu_now(void){
    struct timespec t;
    clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t);
    return (double)t.tv_sec+(double)t.tv_nsec/1e9;
}
static inline void relax(void){ _mm_pause(); }
static void pin(int c){
    cpu_set_t s;
    CPU_ZERO(&s);
    CPU_SET(c,&s);
    pthread_setaffinity_np(pthread_self(),sizeof(s),&s);
}

static inline frame_t make_input(uint64_t n){
    frame_t x={n,n^MAGIC,~n,n+0x12345678ULL};
    return x;
}
static inline frame_t transform(frame_t x){
    frame_t y;
    y.a=x.a+0x1111111111111111ULL;
    y.b=x.b^y.a;
    y.c=x.c+((y.b<<13)|(y.b>>51));
    y.d=(x.d^y.c)+0x87654321ULL;
    return y;
}
static inline int valid_out(frame_t y,uint64_t expected_n){
    frame_t e=transform(make_input(expected_n));
    return y.a==e.a && y.b==e.b && y.c==e.c && y.d==e.d;
}

static int make_pair(int *tx,int *rx){
    int l=socket(AF_INET,SOCK_STREAM,0);
    if(l<0) return -1;
    int one=1;
    setsockopt(l,SOL_SOCKET,SO_REUSEADDR,&one,sizeof(one));

    struct sockaddr_in a={0};
    a.sin_family=AF_INET;
    a.sin_addr.s_addr=htonl(INADDR_LOOPBACK);
    a.sin_port=0;

    if(bind(l,(struct sockaddr*)&a,sizeof(a)) || listen(l,1)){
        close(l);
        return -1;
    }

    socklen_t alen=sizeof(a);
    getsockname(l,(struct sockaddr*)&a,&alen);

    int c=socket(AF_INET,SOCK_STREAM,0);
    if(c<0){ close(l); return -1; }

    setsockopt(c,IPPROTO_TCP,TCP_NODELAY,&one,sizeof(one));
    if(connect(c,(struct sockaddr*)&a,sizeof(a))){
        close(c); close(l); return -1;
    }

    int s=accept(l,NULL,NULL);
    close(l);
    if(s<0){ close(c); return -1; }

    setsockopt(s,IPPROTO_TCP,TCP_NODELAY,&one,sizeof(one));
    int sz=4*1024*1024;
    setsockopt(c,SOL_SOCKET,SO_SNDBUF,&sz,sizeof(sz));
    setsockopt(s,SOL_SOCKET,SO_RCVBUF,&sz,sizeof(sz));

    *tx=c; *rx=s;
    return 0;
}

static void *sender(void *arg){
    bench_t*b=arg;
    pin(0);

    frame_t *buf=NULL;
    posix_memalign((void**)&buf,64,SEND_BYTES);
    for(unsigned i=0;i<SEND_FRAMES;i++) buf[i]=make_input((uint64_t)i+1);

    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();

    uint64_t sent_frames=0;
    while(sent_frames<TOTAL_FRAMES){
        uint64_t remain=TOTAL_FRAMES-sent_frames;
        size_t frames=(remain<SEND_FRAMES)?(size_t)remain:SEND_FRAMES;
        size_t bytes=frames*FRAME,off=0;

        while(off<bytes){
            ssize_t n=send(b->tx,(unsigned char*)buf+off,bytes-off,MSG_NOSIGNAL);
            if(n>0){ off+=(size_t)n; continue; }
            if(n<0 && errno==EINTR) continue;
            goto done;
        }
        sent_frames+=frames;
    }

done:
    shutdown(b->tx,SHUT_WR);
    free(buf);
    return NULL;
}

static inline void publish_one(bench_t*b,frame_t y){
    uint64_t h=atomic_load_explicit(&b->ring.head,memory_order_relaxed);

    for(;;){
        uint64_t t=atomic_load_explicit(&b->ring.tail,memory_order_acquire);
        if(h-t<OUT_CAP) break;
        b->producer_wait_spins++;
        relax();
    }

    b->ring.items[h%OUT_CAP]=y;
    atomic_store_explicit(&b->ring.head,h+1,memory_order_release);
    b->output_publications++;
}

static inline out_slab_t *claim_batch(bench_t*b,unsigned slot){
    out_slab_t*s=&b->slabs[slot];
    while(atomic_load_explicit(&s->ready,memory_order_acquire)){
        b->producer_wait_spins++;
        relax();
    }
    return s;
}

static inline void publish_batch(bench_t*b,unsigned *slot,out_slab_t*s,uint32_t count){
    s->count=count;
    atomic_store_explicit(&s->ready,1,memory_order_release);
    b->output_publications++;
    *slot=(*slot+1)%OUT_SLABS;
}

static void *receiver(void *arg){
    bench_t*b=arg;
    pin(1);

    unsigned char *work=NULL;
    posix_memalign((void**)&work,64,SLAB+FRAME);

    size_t carry=0;
    unsigned outslot=0;

    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();

    double c0=cpu_now();
    uint64_t w0=ns_now();

    for(;;){
        ssize_t n=recv(b->rx,work+carry,SLAB-carry,0);
        if(n==0) break;
        if(n<0){
            if(errno==EINTR) continue;
            break;
        }

        size_t total=carry+(size_t)n;
        size_t complete=total/FRAME;
        size_t tail=total%FRAME;

        if(complete){
            frame_t *in=(frame_t*)work;

            if(b->mode==MODE_PER_RESULT){
                for(size_t i=0;i<complete;i++)
                    publish_one(b,transform(in[i]));
            }else{
                out_slab_t *s=claim_batch(b,outslot);
                for(size_t i=0;i<complete;i++)
                    s->items[i]=transform(in[i]);

                /* Publish every populated receive batch immediately.
                   Never wait for the slab to become full. */
                publish_batch(b,&outslot,s,(uint32_t)complete);
            }

            b->processed+=complete;
        }

        if(tail){
            const unsigned char *src=work+complete*FRAME;
            __m256i v=_mm256_loadu_si256((const __m256i*)src);
            _mm256_store_si256((__m256i*)work,v);
        }
        carry=tail;
    }

    b->receiver_cpu_s=cpu_now()-c0;
    b->receiver_wall_s=(double)(ns_now()-w0)/1e9;
    return NULL;
}

static void *consumer(void *arg){
    bench_t*b=arg;
    pin(2);

    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();

    uint64_t consumed=0,errors=0;
    double c0=cpu_now();

    if(b->mode==MODE_PER_RESULT){
        uint64_t t=0;

        while(consumed<TOTAL_FRAMES){
            uint64_t h=atomic_load_explicit(&b->ring.head,memory_order_acquire);
            if(t==h){
                b->consumer_wait_spins++;
                relax();
                continue;
            }

            frame_t y=b->ring.items[t%OUT_CAP];
            uint64_t expected=(consumed%SEND_FRAMES)+1;
            if(!valid_out(y,expected)) errors++;

            t++;
            consumed++;
            atomic_store_explicit(&b->ring.tail,t,memory_order_release);
        }
    }else{
        unsigned slot=0;

        while(consumed<TOTAL_FRAMES){
            out_slab_t*s=&b->slabs[slot];

            if(!atomic_load_explicit(&s->ready,memory_order_acquire)){
                b->consumer_wait_spins++;
                relax();
                continue;
            }

            uint32_t count=s->count;
            for(uint32_t i=0;i<count;i++){
                uint64_t expected=(consumed%SEND_FRAMES)+1;
                if(!valid_out(s->items[i],expected)) errors++;
                consumed++;
            }

            /* Every semantics: return ownership only after all results
               in this populated slab have been consumed. */
            atomic_store_explicit(&s->ready,0,memory_order_release);
            slot=(slot+1)%OUT_SLABS;
        }
    }

    b->consumer_cpu_s=cpu_now()-c0;
    b->consumed=consumed;
    b->errors=errors;
    return NULL;
}

int main(int argc,char**argv){
    if(argc<2) return 2;

    bench_t*b=NULL;
    if(posix_memalign((void**)&b,64,sizeof(*b))) return 1;
    memset(b,0,sizeof(*b));

    b->mode=!strcmp(argv[1],"batch")?MODE_BATCH:MODE_PER_RESULT;

    if(make_pair(&b->tx,&b->rx)){
        perror("pair");
        return 1;
    }

    pthread_t ts,tr,tc;
    pthread_create(&tc,NULL,consumer,b);
    pthread_create(&tr,NULL,receiver,b);
    pthread_create(&ts,NULL,sender,b);

    usleep(20000);
    uint64_t wall0=ns_now();
    atomic_store_explicit(&b->start,1,memory_order_release);

    pthread_join(ts,NULL);
    pthread_join(tr,NULL);
    pthread_join(tc,NULL);

    b->end_to_end_wall_s=(double)(ns_now()-wall0)/1e9;

    double ingest_fps=(double)b->processed/b->receiver_wall_s;
    double e2e_fps=(double)b->consumed/b->end_to_end_wall_s;

    printf(
      "mode=%s receiver_wall_s=%.6f end_to_end_wall_s=%.6f "
      "processed=%llu consumed=%llu ingest_fps=%.3f end_to_end_fps=%.3f "
      "receiver_cpu_ns_result=%.6f consumer_cpu_ns_result=%.6f "
      "output_publications=%llu results_per_publication=%.3f "
      "producer_wait_spins=%llu consumer_wait_spins=%llu errors=%llu\n",
      b->mode==MODE_BATCH?"batch":"per_result",
      b->receiver_wall_s,b->end_to_end_wall_s,
      (unsigned long long)b->processed,
      (unsigned long long)b->consumed,
      ingest_fps,e2e_fps,
      b->receiver_cpu_s*1e9/(double)b->processed,
      b->consumer_cpu_s*1e9/(double)b->consumed,
      (unsigned long long)b->output_publications,
      (double)b->processed/(double)b->output_publications,
      (unsigned long long)b->producer_wait_spins,
      (unsigned long long)b->consumer_wait_spins,
      (unsigned long long)b->errors);

    close(b->tx);
    close(b->rx);

    int rc=(b->errors || b->processed!=TOTAL_FRAMES || b->consumed!=TOTAL_FRAMES)?3:0;
    free(b);
    return rc;
}

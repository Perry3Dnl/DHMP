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
#define FPSLAB (SLAB/FRAME)
#define OUT_SLABS 8u
#define SEND_FRAMES 8192u
#define SEND_BYTES (SEND_FRAMES*FRAME)
#define TOTAL_FRAMES 12000000ull
#define MAGIC 0x9e3779b97f4a7c15ULL

typedef struct { uint64_t a,b,c,d; } frame_t;
typedef enum { MODE_COPY=0, MODE_DIRECT=1 } bench_mode_t;

typedef struct __attribute__((aligned(64))) {
    frame_t items[FPSLAB];
    uint32_t count;
    uint32_t pad;
    _Atomic uint32_t ready;
    char pad2[52];
} slab_t;

typedef struct {
    bench_mode_t mode;
    int in_tx,in_rx,out_tx,out_rx;
    _Atomic int start;
    slab_t slabs[OUT_SLABS];
    uint64_t processed,forwarded,received;
    uint64_t processor_wait_spins,sender_wait_spins,errors;
    uint64_t send_calls,partial_sends;
    double processor_cpu_s,sender_cpu_s,sink_cpu_s,wall_s;
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

    *tx=c;
    *rx=s;
    return 0;
}

static void *ingress_sender(void *arg){
    bench_t*b=arg;
    pin(0);

    frame_t *buf=NULL;
    posix_memalign((void**)&buf,64,SEND_BYTES);
    for(unsigned i=0;i<SEND_FRAMES;i++) buf[i]=make_input((uint64_t)i+1);

    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();

    uint64_t sent=0;
    while(sent<TOTAL_FRAMES){
        uint64_t remain=TOTAL_FRAMES-sent;
        size_t frames=remain<SEND_FRAMES?(size_t)remain:SEND_FRAMES;
        size_t bytes=frames*FRAME,off=0;

        while(off<bytes){
            ssize_t n=send(b->in_tx,(unsigned char*)buf+off,bytes-off,MSG_NOSIGNAL);
            if(n>0){ off+=(size_t)n; continue; }
            if(n<0 && errno==EINTR) continue;
            goto done;
        }
        sent+=frames;
    }
done:
    shutdown(b->in_tx,SHUT_WR);
    free(buf);
    return NULL;
}

static void *processor(void *arg){
    bench_t*b=arg;
    pin(1);

    unsigned char *work=NULL;
    posix_memalign((void**)&work,64,SLAB+FRAME);
    size_t carry=0;
    unsigned slot=0;

    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    double c0=cpu_now();

    for(;;){
        ssize_t n=recv(b->in_rx,work+carry,SLAB-carry,0);
        if(n==0) break;
        if(n<0){ if(errno==EINTR) continue; break; }

        size_t total=carry+(size_t)n;
        size_t complete=total/FRAME;
        size_t tail=total%FRAME;

        if(complete){
            slab_t*s=&b->slabs[slot];
            while(atomic_load_explicit(&s->ready,memory_order_acquire)){
                b->processor_wait_spins++;
                relax();
            }

            frame_t*in=(frame_t*)work;
            for(size_t i=0;i<complete;i++) s->items[i]=transform(in[i]);
            s->count=(uint32_t)complete;

            atomic_store_explicit(&s->ready,1,memory_order_release);
            b->processed+=complete;
            slot=(slot+1)%OUT_SLABS;
        }

        if(tail){
            const unsigned char *src=work+complete*FRAME;
            __m256i v=_mm256_loadu_si256((const __m256i*)src);
            _mm256_store_si256((__m256i*)work,v);
        }
        carry=tail;
    }

    b->processor_cpu_s=cpu_now()-c0;
    free(work);
    return NULL;
}

static void *forward_sender(void *arg){
    bench_t*b=arg;
    pin(2);

    unsigned slot=0;
    unsigned char *scratch=NULL;
    if(b->mode==MODE_COPY) posix_memalign((void**)&scratch,64,SLAB);

    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    double c0=cpu_now();
    uint64_t forwarded=0;

    while(forwarded<TOTAL_FRAMES){
        slab_t*s=&b->slabs[slot];

        if(!atomic_load_explicit(&s->ready,memory_order_acquire)){
            b->sender_wait_spins++;
            relax();
            continue;
        }

        uint32_t count=s->count;
        size_t bytes=(size_t)count*FRAME;
        const unsigned char*sendbuf=(const unsigned char*)s->items;

        if(b->mode==MODE_COPY){
            memcpy(scratch,s->items,bytes);
            sendbuf=scratch;
        }

        size_t off=0;
        while(off<bytes){
            ssize_t n=send(b->out_tx,sendbuf+off,bytes-off,MSG_NOSIGNAL);
            b->send_calls++;

            if(n>0){
                if((size_t)n<bytes-off) b->partial_sends++;
                off+=(size_t)n;
                continue;
            }
            if(n<0 && errno==EINTR) continue;
            goto done;
        }

        forwarded+=count;

        /* The output slab remains sender-owned until every byte from this
           population has been accepted by the blocking socket. */
        atomic_store_explicit(&s->ready,0,memory_order_release);
        slot=(slot+1)%OUT_SLABS;
    }

done:
    b->forwarded=forwarded;
    b->sender_cpu_s=cpu_now()-c0;
    shutdown(b->out_tx,SHUT_WR);
    free(scratch);
    return NULL;
}

static void *sink(void *arg){
    bench_t*b=arg;
    pin(3);

    unsigned char *work=NULL;
    posix_memalign((void**)&work,64,SLAB+FRAME);
    size_t carry=0;
    uint64_t got=0,errors=0;

    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    double c0=cpu_now();

    for(;;){
        ssize_t n=recv(b->out_rx,work+carry,SLAB-carry,0);
        if(n==0) break;
        if(n<0){ if(errno==EINTR) continue; break; }

        size_t total=carry+(size_t)n;
        size_t complete=total/FRAME;
        size_t tail=total%FRAME;
        frame_t*in=(frame_t*)work;

        for(size_t i=0;i<complete;i++){
            uint64_t expected=(got%SEND_FRAMES)+1;
            if(!valid_out(in[i],expected)) errors++;
            got++;
        }

        if(tail){
            const unsigned char*src=work+complete*FRAME;
            __m256i v=_mm256_loadu_si256((const __m256i*)src);
            _mm256_store_si256((__m256i*)work,v);
        }
        carry=tail;
    }

    b->sink_cpu_s=cpu_now()-c0;
    b->received=got;
    b->errors=errors;
    free(work);
    return NULL;
}

int main(int argc,char**argv){
    if(argc<2) return 2;

    bench_t*b=NULL;
    if(posix_memalign((void**)&b,64,sizeof(*b))) return 1;
    memset(b,0,sizeof(*b));

    b->mode=!strcmp(argv[1],"direct")?MODE_DIRECT:MODE_COPY;

    if(make_pair(&b->in_tx,&b->in_rx) || make_pair(&b->out_tx,&b->out_rx)){
        perror("pair");
        return 1;
    }

    pthread_t ingress,processor_thread,forward,sink_thread;
    pthread_create(&sink_thread,NULL,sink,b);
    pthread_create(&forward,NULL,forward_sender,b);
    pthread_create(&processor_thread,NULL,processor,b);
    pthread_create(&ingress,NULL,ingress_sender,b);

    usleep(20000);
    uint64_t w0=ns_now();
    atomic_store_explicit(&b->start,1,memory_order_release);

    pthread_join(ingress,NULL);
    pthread_join(processor_thread,NULL);
    pthread_join(forward,NULL);
    pthread_join(sink_thread,NULL);

    b->wall_s=(double)(ns_now()-w0)/1e9;

    printf(
      "mode=%s wall_s=%.6f processed=%llu forwarded=%llu received=%llu "
      "e2e_fps=%.3f processor_cpu_ns_result=%.6f sender_cpu_ns_result=%.6f "
      "sink_cpu_ns_result=%.6f send_calls=%llu partial_sends=%llu "
      "processor_wait_spins=%llu sender_wait_spins=%llu errors=%llu\n",
      b->mode==MODE_DIRECT?"direct":"copy",
      b->wall_s,
      (unsigned long long)b->processed,
      (unsigned long long)b->forwarded,
      (unsigned long long)b->received,
      (double)b->received/b->wall_s,
      b->processor_cpu_s*1e9/(double)b->processed,
      b->sender_cpu_s*1e9/(double)b->forwarded,
      b->sink_cpu_s*1e9/(double)b->received,
      (unsigned long long)b->send_calls,
      (unsigned long long)b->partial_sends,
      (unsigned long long)b->processor_wait_spins,
      (unsigned long long)b->sender_wait_spins,
      (unsigned long long)b->errors);

    int rc=(b->errors ||
            b->processed!=TOTAL_FRAMES ||
            b->forwarded!=TOTAL_FRAMES ||
            b->received!=TOTAL_FRAMES)?3:0;

    close(b->in_tx); close(b->in_rx);
    close(b->out_tx); close(b->out_rx);
    free(b);
    return rc;
}

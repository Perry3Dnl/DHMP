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

typedef struct { uint64_t a,b,c,d; } wire_t;
typedef struct { uint64_t x,y,z,w; } prepared_t;
typedef enum { MODE_INLINE=0, MODE_WORKER=1 } mode_t2;

typedef struct __attribute__((aligned(64))) {
    wire_t items[FPSLAB];
    uint32_t count;
    uint32_t pad;
    _Atomic uint32_t ready;
    char pad2[52];
} slab_t;

typedef struct {
    mode_t2 mode;
    int tx,rx;
    int sender_cpu, protocol_cpu, worker_cpu;
    unsigned work_rounds;
    _Atomic int start;
    slab_t slabs[OUT_SLABS];
    uint64_t protocol_frames, delivered_frames, adapter_calls;
    uint64_t producer_wait_spins, worker_wait_spins, errors;
    uint64_t checksum;
    double protocol_cpu_s, worker_cpu_s, protocol_wall_s, end_to_end_wall_s;
} bench_t;

static inline uint64_t ns_now(void){
    struct timespec t; clock_gettime(CLOCK_MONOTONIC_RAW,&t);
    return (uint64_t)t.tv_sec*1000000000ull+t.tv_nsec;
}
static inline double cpu_now(void){
    struct timespec t; clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t);
    return (double)t.tv_sec+(double)t.tv_nsec/1e9;
}
static inline void relax(void){ _mm_pause(); }
static void pin(int c){
    cpu_set_t s; CPU_ZERO(&s); CPU_SET(c,&s);
    pthread_setaffinity_np(pthread_self(),sizeof(s),&s);
}

static inline wire_t make_wire(uint64_t n){
    wire_t x={n,n^MAGIC,~n,n+0x12345678ULL}; return x;
}
static inline prepared_t decode_convert_rounds(wire_t x,unsigned rounds){
    prepared_t p={x.a,x.b,x.c,x.d};
    for(unsigned r=0;r<rounds;r++){
        uint64_t a=p.x + 0x1111111111111111ULL + r;
        uint64_t y=(p.y ^ a) + ((p.x<<7)|(p.x>>57));
        uint64_t z=p.z + ((y<<13)|(y>>51));
        uint64_t w=(p.w ^ z) + (a*0x9e3779b1u);
        p.x=a; p.y=y; p.z=z; p.w=w;
    }
    return p;
}
static inline prepared_t expected_prepared(uint64_t n,unsigned rounds){
    return decode_convert_rounds(make_wire(n),rounds);
}

__attribute__((noinline))
static uint64_t adapter_batch(prepared_t *p,uint32_t count,uint64_t base,unsigned rounds,uint64_t *errors){
    uint64_t sum=0;
    for(uint32_t i=0;i<count;i++){
        prepared_t e=expected_prepared(base+i,rounds);
        if(p[i].x!=e.x || p[i].y!=e.y || p[i].z!=e.z || p[i].w!=e.w) (*errors)++;
        sum ^= p[i].x + (p[i].y<<1) + (p[i].z>>1) + p[i].w;
    }
    return sum;
}

static void process_and_deliver(bench_t*b,slab_t*s,uint64_t base){
    prepared_t *out=(prepared_t*)s->items;
    for(uint32_t i=0;i<s->count;i++){
        wire_t x=s->items[i];
        out[i]=decode_convert_rounds(x,b->work_rounds);
    }
    b->checksum ^= adapter_batch(out,s->count,base,b->work_rounds,&b->errors);
    b->adapter_calls++;
    b->delivered_frames += s->count;
}

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
    bench_t*b=arg; pin(b->sender_cpu);
    wire_t *buf=NULL; posix_memalign((void**)&buf,64,SEND_BYTES);
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    uint64_t sent=0;
    while(sent<TOTAL_FRAMES){
        size_t frames=(TOTAL_FRAMES-sent<SEND_FRAMES)?(size_t)(TOTAL_FRAMES-sent):SEND_FRAMES;
        for(size_t i=0;i<frames;i++) buf[i]=make_wire(sent+i+1);
        size_t bytes=frames*FRAME,off=0;
        while(off<bytes){
            ssize_t n=send(b->tx,(unsigned char*)buf+off,bytes-off,MSG_NOSIGNAL);
            if(n>0){off+=(size_t)n;continue;}
            if(n<0&&errno==EINTR)continue;
            goto done;
        }
        sent+=frames;
    }
done:
    shutdown(b->tx,SHUT_WR); free(buf); return NULL;
}

static void *protocol(void *arg){
    bench_t*b=arg; pin(b->protocol_cpu);
    unsigned char *work=NULL; posix_memalign((void**)&work,64,SLAB+FRAME);
    size_t carry=0; unsigned slot=0; uint64_t sequence=1;
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    double c0=cpu_now(); uint64_t w0=ns_now();

    for(;;){
        ssize_t n=recv(b->rx,work+carry,SLAB-carry,0);
        if(n==0) break;
        if(n<0){ if(errno==EINTR)continue; break; }

        size_t total=carry+(size_t)n;
        size_t complete=total/FRAME,tail=total%FRAME;

        if(complete){
            slab_t*s=&b->slabs[slot];
            while(atomic_load_explicit(&s->ready,memory_order_acquire)){
                b->producer_wait_spins++; relax();
            }

            memcpy(s->items,work,complete*FRAME);
            s->count=(uint32_t)complete;
            b->protocol_frames+=complete;

            if(b->mode==MODE_INLINE){
                process_and_deliver(b,s,sequence);
            }else{
                atomic_store_explicit(&s->ready,1,memory_order_release);
            }

            sequence+=complete;
            slot=(slot+1)%OUT_SLABS;
        }

        if(tail){
            const unsigned char *src=work+complete*FRAME;
            __m256i v=_mm256_loadu_si256((const __m256i*)src);
            _mm256_store_si256((__m256i*)work,v);
        }
        carry=tail;
    }

    b->protocol_cpu_s=cpu_now()-c0;
    b->protocol_wall_s=(double)(ns_now()-w0)/1e9;
    free(work); return NULL;
}

static void *worker(void *arg){
    bench_t*b=arg; pin(b->worker_cpu);
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();

    double c0=cpu_now();
    unsigned slot=0;
    uint64_t sequence=1;

    while(b->delivered_frames<TOTAL_FRAMES){
        slab_t*s=&b->slabs[slot];

        if(!atomic_load_explicit(&s->ready,memory_order_acquire)){
            b->worker_wait_spins++; relax(); continue;
        }

        process_and_deliver(b,s,sequence);
        sequence+=s->count;
        atomic_store_explicit(&s->ready,0,memory_order_release);
        slot=(slot+1)%OUT_SLABS;
    }

    b->worker_cpu_s=cpu_now()-c0;
    return NULL;
}

int main(int argc,char**argv){
    if(argc<3)return 2;

    bench_t*b=NULL;
    if(posix_memalign((void**)&b,64,sizeof(*b)))return 1;
    memset(b,0,sizeof(*b));

    b->mode=!strcmp(argv[1],"worker")?MODE_WORKER:MODE_INLINE;
    b->work_rounds=(unsigned)strtoul(argv[2],NULL,10);
    b->sender_cpu=argc>3?atoi(argv[3]):4;
    b->protocol_cpu=argc>4?atoi(argv[4]):2;
    b->worker_cpu=argc>5?atoi(argv[5]):3;

    if(make_pair(&b->tx,&b->rx)){perror("pair");return 1;}

    pthread_t ts,tp,tw;
    if(b->mode==MODE_WORKER) pthread_create(&tw,NULL,worker,b);
    pthread_create(&tp,NULL,protocol,b);
    pthread_create(&ts,NULL,sender,b);

    usleep(20000);
    uint64_t wall0=ns_now();
    atomic_store_explicit(&b->start,1,memory_order_release);

    pthread_join(ts,NULL);
    pthread_join(tp,NULL);
    if(b->mode==MODE_WORKER)pthread_join(tw,NULL);

    b->end_to_end_wall_s=(double)(ns_now()-wall0)/1e9;

    printf("mode=%s rounds=%u sender_cpu=%d protocol_cpu=%d worker_cpu=%d protocol_wall_s=%.6f e2e_wall_s=%.6f protocol_frames=%llu delivered_frames=%llu e2e_fps=%.3f protocol_cpu_ns_frame=%.6f worker_cpu_ns_frame=%.6f adapter_calls=%llu frames_per_adapter_call=%.3f producer_wait_spins=%llu worker_wait_spins=%llu errors=%llu checksum=%llu\n",
      b->mode==MODE_WORKER?"worker":"inline",b->work_rounds,b->sender_cpu,b->protocol_cpu,b->worker_cpu,
      b->protocol_wall_s,b->end_to_end_wall_s,
      (unsigned long long)b->protocol_frames,(unsigned long long)b->delivered_frames,
      (double)b->delivered_frames/b->end_to_end_wall_s,
      b->protocol_cpu_s*1e9/(double)b->protocol_frames,
      b->mode==MODE_WORKER?b->worker_cpu_s*1e9/(double)b->delivered_frames:0.0,
      (unsigned long long)b->adapter_calls,
      (double)b->delivered_frames/(double)b->adapter_calls,
      (unsigned long long)b->producer_wait_spins,
      (unsigned long long)b->worker_wait_spins,
      (unsigned long long)b->errors,
      (unsigned long long)b->checksum);

    int rc=(b->errors||b->protocol_frames!=TOTAL_FRAMES||b->delivered_frames!=TOTAL_FRAMES)?3:0;
    close(b->tx); close(b->rx); free(b);
    return rc;
}

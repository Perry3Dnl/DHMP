#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <immintrin.h>
#include <math.h>
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

typedef struct { float x,y,z,vx,vy,vz,temp,scale; } in_t;
typedef struct { float x,y,z,speed2,temp,energy,aux1,aux2; } out_t;
typedef enum { MODE_PER_MESSAGE=0, MODE_FUSED_SCALAR=1, MODE_FUSED_AVX512=2, MODE_FUSED_AVX2=3 } mode_t2;

typedef struct __attribute__((aligned(64))) {
    out_t items[FPSLAB];
    uint32_t count;
    uint32_t pad;
    _Atomic uint32_t ready;
    unsigned char pad2[52];
} out_slab_t;

typedef struct {
    mode_t2 mode;
    int tx,rx;
    _Atomic int start;
    out_slab_t slabs[OUT_SLABS];
    uint64_t processed,consumed,batches,errors;
    uint64_t producer_wait_spins,consumer_wait_spins;
    double processor_cpu_s,consumer_cpu_s,wall_s;
} bench_t;

static inline uint64_t ns_now(void){ struct timespec t; clock_gettime(CLOCK_MONOTONIC_RAW,&t); return (uint64_t)t.tv_sec*1000000000ull+t.tv_nsec; }
static inline double cpu_now(void){ struct timespec t; clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t); return (double)t.tv_sec+(double)t.tv_nsec/1e9; }
static inline void relax(void){ _mm_pause(); }
static void pin(int c){ cpu_set_t s; CPU_ZERO(&s); CPU_SET(c,&s); pthread_setaffinity_np(pthread_self(),sizeof(s),&s); }

static inline in_t make_input(uint64_t n){
    float f=(float)(n & 8191u);
    in_t x;
    x.x=f*0.125f+1.0f; x.y=f*0.0625f-2.0f; x.z=f*0.03125f+3.0f;
    x.vx=(float)((n*3u)&1023u)*0.015625f;
    x.vy=(float)((n*5u)&1023u)*0.015625f;
    x.vz=(float)((n*7u)&1023u)*0.015625f;
    x.temp=20.0f+(float)(n&63u)*0.25f;
    x.scale=0.75f+(float)(n&15u)*0.015625f;
    return x;
}

static inline out_t transform_inline(in_t a){
    const float dt=0.03125f;
    out_t o;
    o.x=(a.x*a.scale)+(a.vx*dt)+10.0f;
    o.y=(a.y*a.scale)+(a.vy*dt)-5.0f;
    o.z=(a.z*a.scale)+(a.vz*dt)+2.0f;
    o.speed2=(a.vx*a.vx)+(a.vy*a.vy)+(a.vz*a.vz);
    o.temp=(a.temp*1.8f)+32.0f;
    o.energy=(o.speed2*0.5f)+(o.temp*0.125f);
    o.aux1=((o.x+o.y)*0.25f)+(o.z*0.5f);
    o.aux2=(o.energy*a.scale)+o.aux1;
    return o;
}

__attribute__((noinline))
static out_t transform_one(in_t a){ return transform_inline(a); }

__attribute__((noinline,optimize("no-tree-vectorize")))
static void process_fused_scalar(const in_t *in,out_t *out,size_t n){
    for(size_t i=0;i<n;i++) out[i]=transform_inline(in[i]);
}

static void process_per_message(const in_t *in,out_t*out,size_t n){
    for(size_t i=0;i<n;i++) out[i]=transform_one(in[i]);
}

static inline void transpose8_ps(__m256 *r0,__m256 *r1,__m256 *r2,__m256 *r3,__m256 *r4,__m256 *r5,__m256 *r6,__m256 *r7){
    __m256 t0=_mm256_unpacklo_ps(*r0,*r1);
    __m256 t1=_mm256_unpackhi_ps(*r0,*r1);
    __m256 t2=_mm256_unpacklo_ps(*r2,*r3);
    __m256 t3=_mm256_unpackhi_ps(*r2,*r3);
    __m256 t4=_mm256_unpacklo_ps(*r4,*r5);
    __m256 t5=_mm256_unpackhi_ps(*r4,*r5);
    __m256 t6=_mm256_unpacklo_ps(*r6,*r7);
    __m256 t7=_mm256_unpackhi_ps(*r6,*r7);
    __m256 u0=_mm256_shuffle_ps(t0,t2,0x44);
    __m256 u1=_mm256_shuffle_ps(t0,t2,0xEE);
    __m256 u2=_mm256_shuffle_ps(t1,t3,0x44);
    __m256 u3=_mm256_shuffle_ps(t1,t3,0xEE);
    __m256 u4=_mm256_shuffle_ps(t4,t6,0x44);
    __m256 u5=_mm256_shuffle_ps(t4,t6,0xEE);
    __m256 u6=_mm256_shuffle_ps(t5,t7,0x44);
    __m256 u7=_mm256_shuffle_ps(t5,t7,0xEE);
    *r0=_mm256_permute2f128_ps(u0,u4,0x20);
    *r1=_mm256_permute2f128_ps(u1,u5,0x20);
    *r2=_mm256_permute2f128_ps(u2,u6,0x20);
    *r3=_mm256_permute2f128_ps(u3,u7,0x20);
    *r4=_mm256_permute2f128_ps(u0,u4,0x31);
    *r5=_mm256_permute2f128_ps(u1,u5,0x31);
    *r6=_mm256_permute2f128_ps(u2,u6,0x31);
    *r7=_mm256_permute2f128_ps(u3,u7,0x31);
}

__attribute__((target("avx2")))
static void process_fused_avx2(const in_t *in,out_t*out,size_t n){
    const __m256 dt=_mm256_set1_ps(0.03125f);
    const __m256 ten=_mm256_set1_ps(10.0f), neg5=_mm256_set1_ps(-5.0f), two=_mm256_set1_ps(2.0f);
    const __m256 c18=_mm256_set1_ps(1.8f), c32=_mm256_set1_ps(32.0f);
    const __m256 c05=_mm256_set1_ps(0.5f), c0125=_mm256_set1_ps(0.125f), c025=_mm256_set1_ps(0.25f);
    size_t i=0;
    for(;i+8<=n;i+=8){
        __m256 r0=_mm256_loadu_ps((const float*)(in+i+0));
        __m256 r1=_mm256_loadu_ps((const float*)(in+i+1));
        __m256 r2=_mm256_loadu_ps((const float*)(in+i+2));
        __m256 r3=_mm256_loadu_ps((const float*)(in+i+3));
        __m256 r4=_mm256_loadu_ps((const float*)(in+i+4));
        __m256 r5=_mm256_loadu_ps((const float*)(in+i+5));
        __m256 r6=_mm256_loadu_ps((const float*)(in+i+6));
        __m256 r7=_mm256_loadu_ps((const float*)(in+i+7));
        transpose8_ps(&r0,&r1,&r2,&r3,&r4,&r5,&r6,&r7);
        __m256 ox=_mm256_add_ps(_mm256_add_ps(_mm256_mul_ps(r0,r7),_mm256_mul_ps(r3,dt)),ten);
        __m256 oy=_mm256_add_ps(_mm256_add_ps(_mm256_mul_ps(r1,r7),_mm256_mul_ps(r4,dt)),neg5);
        __m256 oz=_mm256_add_ps(_mm256_add_ps(_mm256_mul_ps(r2,r7),_mm256_mul_ps(r5,dt)),two);
        __m256 speed2=_mm256_add_ps(_mm256_add_ps(_mm256_mul_ps(r3,r3),_mm256_mul_ps(r4,r4)),_mm256_mul_ps(r5,r5));
        __m256 ot=_mm256_add_ps(_mm256_mul_ps(r6,c18),c32);
        __m256 energy=_mm256_add_ps(_mm256_mul_ps(speed2,c05),_mm256_mul_ps(ot,c0125));
        __m256 aux1=_mm256_add_ps(_mm256_mul_ps(_mm256_add_ps(ox,oy),c025),_mm256_mul_ps(oz,c05));
        __m256 aux2=_mm256_add_ps(_mm256_mul_ps(energy,r7),aux1);
        r0=ox;r1=oy;r2=oz;r3=speed2;r4=ot;r5=energy;r6=aux1;r7=aux2;
        transpose8_ps(&r0,&r1,&r2,&r3,&r4,&r5,&r6,&r7);
        _mm256_storeu_ps((float*)(out+i+0),r0);
        _mm256_storeu_ps((float*)(out+i+1),r1);
        _mm256_storeu_ps((float*)(out+i+2),r2);
        _mm256_storeu_ps((float*)(out+i+3),r3);
        _mm256_storeu_ps((float*)(out+i+4),r4);
        _mm256_storeu_ps((float*)(out+i+5),r5);
        _mm256_storeu_ps((float*)(out+i+6),r6);
        _mm256_storeu_ps((float*)(out+i+7),r7);
    }
    for(;i<n;i++) out[i]=transform_inline(in[i]);
}

__attribute__((target("avx512f")))
static void process_fused_avx512(const in_t *in,out_t*out,size_t n){
    const __m512 dt=_mm512_set1_ps(0.03125f);
    const __m512 ten=_mm512_set1_ps(10.0f), neg5=_mm512_set1_ps(-5.0f), two=_mm512_set1_ps(2.0f);
    const __m512 c18=_mm512_set1_ps(1.8f), c32=_mm512_set1_ps(32.0f);
    const __m512 c05=_mm512_set1_ps(0.5f), c0125=_mm512_set1_ps(0.125f), c025=_mm512_set1_ps(0.25f);
    const __m512i idx=_mm512_setr_epi32(0,8,16,24,32,40,48,56,64,72,80,88,96,104,112,120);
    size_t i=0;
    for(;i+16<=n;i+=16){
        const float *base=(const float*)(in+i);
        float *obase=(float*)(out+i);
        __m512 x=_mm512_i32gather_ps(idx,base+0,4);
        __m512 y=_mm512_i32gather_ps(idx,base+1,4);
        __m512 z=_mm512_i32gather_ps(idx,base+2,4);
        __m512 vx=_mm512_i32gather_ps(idx,base+3,4);
        __m512 vy=_mm512_i32gather_ps(idx,base+4,4);
        __m512 vz=_mm512_i32gather_ps(idx,base+5,4);
        __m512 temp=_mm512_i32gather_ps(idx,base+6,4);
        __m512 scale=_mm512_i32gather_ps(idx,base+7,4);

        __m512 ox=_mm512_add_ps(_mm512_add_ps(_mm512_mul_ps(x,scale),_mm512_mul_ps(vx,dt)),ten);
        __m512 oy=_mm512_add_ps(_mm512_add_ps(_mm512_mul_ps(y,scale),_mm512_mul_ps(vy,dt)),neg5);
        __m512 oz=_mm512_add_ps(_mm512_add_ps(_mm512_mul_ps(z,scale),_mm512_mul_ps(vz,dt)),two);
        __m512 speed2=_mm512_add_ps(_mm512_add_ps(_mm512_mul_ps(vx,vx),_mm512_mul_ps(vy,vy)),_mm512_mul_ps(vz,vz));
        __m512 ot=_mm512_add_ps(_mm512_mul_ps(temp,c18),c32);
        __m512 energy=_mm512_add_ps(_mm512_mul_ps(speed2,c05),_mm512_mul_ps(ot,c0125));
        __m512 aux1=_mm512_add_ps(_mm512_mul_ps(_mm512_add_ps(ox,oy),c025),_mm512_mul_ps(oz,c05));
        __m512 aux2=_mm512_add_ps(_mm512_mul_ps(energy,scale),aux1);

        _mm512_i32scatter_ps(obase+0,idx,ox,4);
        _mm512_i32scatter_ps(obase+1,idx,oy,4);
        _mm512_i32scatter_ps(obase+2,idx,oz,4);
        _mm512_i32scatter_ps(obase+3,idx,speed2,4);
        _mm512_i32scatter_ps(obase+4,idx,ot,4);
        _mm512_i32scatter_ps(obase+5,idx,energy,4);
        _mm512_i32scatter_ps(obase+6,idx,aux1,4);
        _mm512_i32scatter_ps(obase+7,idx,aux2,4);
    }
    for(;i<n;i++) out[i]=transform_inline(in[i]);
}

static inline int closef(float a,float b){
    float d=fabsf(a-b), m=fmaxf(fabsf(a),fabsf(b));
    return d <= 1e-5f*(1.0f+m);
}
static inline int valid_out(out_t o,uint64_t n){
    out_t e=transform_inline(make_input(n));
    return closef(o.x,e.x)&&closef(o.y,e.y)&&closef(o.z,e.z)&&closef(o.speed2,e.speed2)&&
           closef(o.temp,e.temp)&&closef(o.energy,e.energy)&&closef(o.aux1,e.aux1)&&closef(o.aux2,e.aux2);
}

static int make_pair(int *tx,int *rx){
    int l=socket(AF_INET,SOCK_STREAM,0); if(l<0) return -1; int one=1; setsockopt(l,SOL_SOCKET,SO_REUSEADDR,&one,sizeof(one));
    struct sockaddr_in a={0}; a.sin_family=AF_INET; a.sin_addr.s_addr=htonl(INADDR_LOOPBACK); a.sin_port=0;
    if(bind(l,(struct sockaddr*)&a,sizeof(a))||listen(l,1)){close(l);return -1;}
    socklen_t alen=sizeof(a); getsockname(l,(struct sockaddr*)&a,&alen);
    int c=socket(AF_INET,SOCK_STREAM,0); if(c<0){close(l);return -1;} setsockopt(c,IPPROTO_TCP,TCP_NODELAY,&one,sizeof(one));
    if(connect(c,(struct sockaddr*)&a,sizeof(a))){close(c);close(l);return -1;}
    int s=accept(l,NULL,NULL); close(l); if(s<0){close(c);return -1;} setsockopt(s,IPPROTO_TCP,TCP_NODELAY,&one,sizeof(one));
    int sz=4*1024*1024; setsockopt(c,SOL_SOCKET,SO_SNDBUF,&sz,sizeof(sz)); setsockopt(s,SOL_SOCKET,SO_RCVBUF,&sz,sizeof(sz));
    *tx=c;*rx=s;return 0;
}

static void *sender(void *arg){
    bench_t*b=arg; pin(4);
    in_t *buf=NULL; posix_memalign((void**)&buf,64,SEND_BYTES);
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    uint64_t sent=0;
    while(sent<TOTAL_FRAMES){
        size_t frames=(TOTAL_FRAMES-sent<SEND_FRAMES)?(size_t)(TOTAL_FRAMES-sent):SEND_FRAMES;
        for(size_t i=0;i<frames;i++) buf[i]=make_input(sent+i+1);
        size_t bytes=frames*FRAME,off=0;
        while(off<bytes){ ssize_t n=send(b->tx,(unsigned char*)buf+off,bytes-off,MSG_NOSIGNAL); if(n>0){off+=(size_t)n;continue;} if(n<0&&errno==EINTR)continue; goto done; }
        sent+=frames;
    }
done: shutdown(b->tx,SHUT_WR); free(buf); return NULL;
}

static void *processor(void *arg){
    bench_t*b=arg; pin(2);
    unsigned char *work=NULL; posix_memalign((void**)&work,64,SLAB+FRAME);
    size_t carry=0; unsigned slot=0;
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    double c0=cpu_now();
    for(;;){
        ssize_t rn=recv(b->rx,work+carry,SLAB-carry,0);
        if(rn==0) break;
        if(rn<0){ if(errno==EINTR) continue; break; }
        size_t total=carry+(size_t)rn, complete=total/FRAME, tail=total%FRAME;
        if(complete){
            out_slab_t*s=&b->slabs[slot];
            while(atomic_load_explicit(&s->ready,memory_order_acquire)){b->producer_wait_spins++;relax();}
            const in_t *in=(const in_t*)work;
            if(b->mode==MODE_PER_MESSAGE) process_per_message(in,s->items,complete);
            else if(b->mode==MODE_FUSED_SCALAR) process_fused_scalar(in,s->items,complete);
            else if(b->mode==MODE_FUSED_AVX512) process_fused_avx512(in,s->items,complete);
            else process_fused_avx2(in,s->items,complete);
            s->count=(uint32_t)complete;
            atomic_store_explicit(&s->ready,1,memory_order_release);
            b->processed+=complete; b->batches++; slot=(slot+1)%OUT_SLABS;
        }
        if(tail){ const unsigned char *src=work+complete*FRAME; __m256i v=_mm256_loadu_si256((const __m256i*)src); _mm256_store_si256((__m256i*)work,v); }
        carry=tail;
    }
    b->processor_cpu_s=cpu_now()-c0; free(work); return NULL;
}

static void *consumer(void *arg){
    bench_t*b=arg; pin(3); unsigned slot=0; uint64_t expected=1,consumed=0,errors=0;
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    double c0=cpu_now();
    while(consumed<TOTAL_FRAMES){
        out_slab_t*s=&b->slabs[slot];
        if(!atomic_load_explicit(&s->ready,memory_order_acquire)){b->consumer_wait_spins++;relax();continue;}
        for(uint32_t i=0;i<s->count;i++,expected++,consumed++) if(!valid_out(s->items[i],expected)) errors++;
        atomic_store_explicit(&s->ready,0,memory_order_release); slot=(slot+1)%OUT_SLABS;
    }
    b->consumer_cpu_s=cpu_now()-c0; b->consumed=consumed; b->errors=errors; return NULL;
}

static const char*mode_name(mode_t2 m){return m==MODE_PER_MESSAGE?"per_message":m==MODE_FUSED_SCALAR?"fused_scalar":m==MODE_FUSED_AVX512?"fused_avx512":"fused_avx2";}
int main(int argc,char**argv){
    if(argc<2)return 2;
    bench_t*b=NULL; if(posix_memalign((void**)&b,64,sizeof(*b)))return 1; memset(b,0,sizeof(*b));
    if(!strcmp(argv[1],"per_message"))b->mode=MODE_PER_MESSAGE; else if(!strcmp(argv[1],"fused_scalar"))b->mode=MODE_FUSED_SCALAR; else if(!strcmp(argv[1],"fused_avx512"))b->mode=MODE_FUSED_AVX512; else b->mode=MODE_FUSED_AVX2;
    if(make_pair(&b->tx,&b->rx)){perror("pair");return 1;}
    pthread_t ts,tp,tc; pthread_create(&tc,NULL,consumer,b); pthread_create(&tp,NULL,processor,b); pthread_create(&ts,NULL,sender,b);
    usleep(20000); uint64_t w0=ns_now(); atomic_store_explicit(&b->start,1,memory_order_release);
    pthread_join(ts,NULL); pthread_join(tp,NULL); pthread_join(tc,NULL); b->wall_s=(double)(ns_now()-w0)/1e9;
    printf("mode=%s wall_s=%.6f processed=%llu consumed=%llu e2e_fps=%.3f payload_GBps=%.6f processor_cpu_ns_frame=%.6f consumer_cpu_ns_frame=%.6f batches=%llu frames_per_batch=%.3f producer_wait_spins=%llu consumer_wait_spins=%llu errors=%llu\n",
      mode_name(b->mode),b->wall_s,(unsigned long long)b->processed,(unsigned long long)b->consumed,(double)b->consumed/b->wall_s,(double)b->consumed*FRAME/b->wall_s/1e9,
      b->processor_cpu_s*1e9/(double)b->processed,b->consumer_cpu_s*1e9/(double)b->consumed,(unsigned long long)b->batches,(double)b->processed/(double)b->batches,
      (unsigned long long)b->producer_wait_spins,(unsigned long long)b->consumer_wait_spins,(unsigned long long)b->errors);
    int rc=(b->errors||b->processed!=TOTAL_FRAMES||b->consumed!=TOTAL_FRAMES)?3:0; close(b->tx);close(b->rx);free(b);return rc;
}

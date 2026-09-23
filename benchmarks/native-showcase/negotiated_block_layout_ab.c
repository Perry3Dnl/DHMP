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

#define NMSG 384u
#define NFIELDS 8u
#define FRAME 32u
#define BLOCK_BYTES (NMSG*FRAME)
#define OUT_SLABS 8u
#define TOTAL_BLOCKS 31250ull
#define TOTAL_FRAMES (TOTAL_BLOCKS*NMSG)

typedef struct { float x,y,z,vx,vy,vz,temp,scale; } aos_t;
typedef struct { float x,y,z,speed2,temp,energy,aux1,aux2; } out_t;
typedef struct { float f[NFIELDS][NMSG]; } soa_block_t;
typedef enum { AOS_SCALAR=0, AOS_AVX2=1, SOA_SCALAR=2, SOA_AVX2=3 } mode_t2;

typedef struct __attribute__((aligned(64))) {
    out_t items[NMSG];
    _Atomic uint32_t ready;
    uint32_t count;
    unsigned char pad[56];
} out_slab_t;

typedef struct {
    mode_t2 mode;
    int tx,rx;
    _Atomic int start;
    out_slab_t out[OUT_SLABS];
    uint64_t blocks,processed,consumed,errors;
    uint64_t prod_wait,cons_wait;
    double sender_cpu_s,processor_cpu_s,consumer_cpu_s,wall_s;
} bench_t;

static inline uint64_t ns_now(void){struct timespec t;clock_gettime(CLOCK_MONOTONIC_RAW,&t);return (uint64_t)t.tv_sec*1000000000ull+t.tv_nsec;}
static inline double cpu_now(void){struct timespec t;clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t);return (double)t.tv_sec+(double)t.tv_nsec/1e9;}
static inline void relax(void){_mm_pause();}
static void pin(int c){cpu_set_t s;CPU_ZERO(&s);CPU_SET(c,&s);pthread_setaffinity_np(pthread_self(),sizeof(s),&s);}

static inline aos_t make_input(uint64_t n){
    float f=(float)(n & 8191u);
    aos_t x;
    x.x=f*0.125f+1.0f; x.y=f*0.0625f-2.0f; x.z=f*0.03125f+3.0f;
    x.vx=(float)((n*3u)&1023u)*0.015625f;
    x.vy=(float)((n*5u)&1023u)*0.015625f;
    x.vz=(float)((n*7u)&1023u)*0.015625f;
    x.temp=20.0f+(float)(n&63u)*0.25f;
    x.scale=0.75f+(float)(n&15u)*0.015625f;
    return x;
}
static inline out_t transform(aos_t a){
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
static inline int closef2(float a,float b){float d=fabsf(a-b),m=fmaxf(fabsf(a),fabsf(b));return d<=1e-5f*(1.0f+m);}
static inline int valid_out(out_t o,uint64_t n){out_t e=transform(make_input(n));return closef2(o.x,e.x)&&closef2(o.y,e.y)&&closef2(o.z,e.z)&&closef2(o.speed2,e.speed2)&&closef2(o.temp,e.temp)&&closef2(o.energy,e.energy)&&closef2(o.aux1,e.aux1)&&closef2(o.aux2,e.aux2);}

__attribute__((noinline,optimize("no-tree-vectorize")))
static void aos_scalar(const aos_t *in,out_t*out){for(unsigned i=0;i<NMSG;i++)out[i]=transform(in[i]);}

static inline void transpose8_ps(__m256 *r0,__m256 *r1,__m256 *r2,__m256 *r3,__m256 *r4,__m256 *r5,__m256 *r6,__m256 *r7){
    __m256 t0=_mm256_unpacklo_ps(*r0,*r1), t1=_mm256_unpackhi_ps(*r0,*r1);
    __m256 t2=_mm256_unpacklo_ps(*r2,*r3), t3=_mm256_unpackhi_ps(*r2,*r3);
    __m256 t4=_mm256_unpacklo_ps(*r4,*r5), t5=_mm256_unpackhi_ps(*r4,*r5);
    __m256 t6=_mm256_unpacklo_ps(*r6,*r7), t7=_mm256_unpackhi_ps(*r6,*r7);
    __m256 u0=_mm256_shuffle_ps(t0,t2,0x44), u1=_mm256_shuffle_ps(t0,t2,0xEE);
    __m256 u2=_mm256_shuffle_ps(t1,t3,0x44), u3=_mm256_shuffle_ps(t1,t3,0xEE);
    __m256 u4=_mm256_shuffle_ps(t4,t6,0x44), u5=_mm256_shuffle_ps(t4,t6,0xEE);
    __m256 u6=_mm256_shuffle_ps(t5,t7,0x44), u7=_mm256_shuffle_ps(t5,t7,0xEE);
    *r0=_mm256_permute2f128_ps(u0,u4,0x20); *r1=_mm256_permute2f128_ps(u1,u5,0x20);
    *r2=_mm256_permute2f128_ps(u2,u6,0x20); *r3=_mm256_permute2f128_ps(u3,u7,0x20);
    *r4=_mm256_permute2f128_ps(u0,u4,0x31); *r5=_mm256_permute2f128_ps(u1,u5,0x31);
    *r6=_mm256_permute2f128_ps(u2,u6,0x31); *r7=_mm256_permute2f128_ps(u3,u7,0x31);
}
__attribute__((target("avx2")))
static void aos_avx2(const aos_t *in,out_t*out){
    const __m256 dt=_mm256_set1_ps(.03125f),ten=_mm256_set1_ps(10),neg5=_mm256_set1_ps(-5),two=_mm256_set1_ps(2);
    const __m256 c18=_mm256_set1_ps(1.8f),c32=_mm256_set1_ps(32),c05=_mm256_set1_ps(.5f),c0125=_mm256_set1_ps(.125f),c025=_mm256_set1_ps(.25f);
    for(unsigned i=0;i<NMSG;i+=8){
        __m256 r0=_mm256_loadu_ps((float*)(in+i+0)),r1=_mm256_loadu_ps((float*)(in+i+1)),r2=_mm256_loadu_ps((float*)(in+i+2)),r3=_mm256_loadu_ps((float*)(in+i+3));
        __m256 r4=_mm256_loadu_ps((float*)(in+i+4)),r5=_mm256_loadu_ps((float*)(in+i+5)),r6=_mm256_loadu_ps((float*)(in+i+6)),r7=_mm256_loadu_ps((float*)(in+i+7));
        transpose8_ps(&r0,&r1,&r2,&r3,&r4,&r5,&r6,&r7);
        __m256 ox=_mm256_add_ps(_mm256_add_ps(_mm256_mul_ps(r0,r7),_mm256_mul_ps(r3,dt)),ten);
        __m256 oy=_mm256_add_ps(_mm256_add_ps(_mm256_mul_ps(r1,r7),_mm256_mul_ps(r4,dt)),neg5);
        __m256 oz=_mm256_add_ps(_mm256_add_ps(_mm256_mul_ps(r2,r7),_mm256_mul_ps(r5,dt)),two);
        __m256 speed2=_mm256_add_ps(_mm256_add_ps(_mm256_mul_ps(r3,r3),_mm256_mul_ps(r4,r4)),_mm256_mul_ps(r5,r5));
        __m256 ot=_mm256_add_ps(_mm256_mul_ps(r6,c18),c32);
        __m256 energy=_mm256_add_ps(_mm256_mul_ps(speed2,c05),_mm256_mul_ps(ot,c0125));
        __m256 aux1=_mm256_add_ps(_mm256_mul_ps(_mm256_add_ps(ox,oy),c025),_mm256_mul_ps(oz,c05));
        __m256 aux2=_mm256_add_ps(_mm256_mul_ps(energy,r7),aux1);
        r0=ox;r1=oy;r2=oz;r3=speed2;r4=ot;r5=energy;r6=aux1;r7=aux2;transpose8_ps(&r0,&r1,&r2,&r3,&r4,&r5,&r6,&r7);
        _mm256_storeu_ps((float*)(out+i+0),r0);_mm256_storeu_ps((float*)(out+i+1),r1);_mm256_storeu_ps((float*)(out+i+2),r2);_mm256_storeu_ps((float*)(out+i+3),r3);
        _mm256_storeu_ps((float*)(out+i+4),r4);_mm256_storeu_ps((float*)(out+i+5),r5);_mm256_storeu_ps((float*)(out+i+6),r6);_mm256_storeu_ps((float*)(out+i+7),r7);
    }
}

__attribute__((noinline,optimize("no-tree-vectorize")))
static void soa_scalar(const soa_block_t *s,out_t*out){
    for(unsigned i=0;i<NMSG;i++){
        aos_t a={s->f[0][i],s->f[1][i],s->f[2][i],s->f[3][i],s->f[4][i],s->f[5][i],s->f[6][i],s->f[7][i]};
        out[i]=transform(a);
    }
}
__attribute__((target("avx2")))
static void soa_avx2(const soa_block_t *s,out_t*out){
    const __m256 dt=_mm256_set1_ps(.03125f),ten=_mm256_set1_ps(10),neg5=_mm256_set1_ps(-5),two=_mm256_set1_ps(2);
    const __m256 c18=_mm256_set1_ps(1.8f),c32=_mm256_set1_ps(32),c05=_mm256_set1_ps(.5f),c0125=_mm256_set1_ps(.125f),c025=_mm256_set1_ps(.25f);
    for(unsigned i=0;i<NMSG;i+=8){
        __m256 x=_mm256_loadu_ps(&s->f[0][i]),y=_mm256_loadu_ps(&s->f[1][i]),z=_mm256_loadu_ps(&s->f[2][i]);
        __m256 vx=_mm256_loadu_ps(&s->f[3][i]),vy=_mm256_loadu_ps(&s->f[4][i]),vz=_mm256_loadu_ps(&s->f[5][i]);
        __m256 temp=_mm256_loadu_ps(&s->f[6][i]),scale=_mm256_loadu_ps(&s->f[7][i]);
        __m256 ox=_mm256_add_ps(_mm256_add_ps(_mm256_mul_ps(x,scale),_mm256_mul_ps(vx,dt)),ten);
        __m256 oy=_mm256_add_ps(_mm256_add_ps(_mm256_mul_ps(y,scale),_mm256_mul_ps(vy,dt)),neg5);
        __m256 oz=_mm256_add_ps(_mm256_add_ps(_mm256_mul_ps(z,scale),_mm256_mul_ps(vz,dt)),two);
        __m256 speed2=_mm256_add_ps(_mm256_add_ps(_mm256_mul_ps(vx,vx),_mm256_mul_ps(vy,vy)),_mm256_mul_ps(vz,vz));
        __m256 ot=_mm256_add_ps(_mm256_mul_ps(temp,c18),c32);
        __m256 energy=_mm256_add_ps(_mm256_mul_ps(speed2,c05),_mm256_mul_ps(ot,c0125));
        __m256 aux1=_mm256_add_ps(_mm256_mul_ps(_mm256_add_ps(ox,oy),c025),_mm256_mul_ps(oz,c05));
        __m256 aux2=_mm256_add_ps(_mm256_mul_ps(energy,scale),aux1);
        __m256 r0=ox,r1=oy,r2=oz,r3=speed2,r4=ot,r5=energy,r6=aux1,r7=aux2;
        transpose8_ps(&r0,&r1,&r2,&r3,&r4,&r5,&r6,&r7);
        _mm256_storeu_ps((float*)(out+i+0),r0);_mm256_storeu_ps((float*)(out+i+1),r1);_mm256_storeu_ps((float*)(out+i+2),r2);_mm256_storeu_ps((float*)(out+i+3),r3);
        _mm256_storeu_ps((float*)(out+i+4),r4);_mm256_storeu_ps((float*)(out+i+5),r5);_mm256_storeu_ps((float*)(out+i+6),r6);_mm256_storeu_ps((float*)(out+i+7),r7);
    }
}

static int make_pair(int *tx,int *rx){
    int l=socket(AF_INET,SOCK_STREAM,0);if(l<0)return -1;int one=1;setsockopt(l,SOL_SOCKET,SO_REUSEADDR,&one,sizeof(one));
    struct sockaddr_in a={0};a.sin_family=AF_INET;a.sin_addr.s_addr=htonl(INADDR_LOOPBACK);a.sin_port=0;
    if(bind(l,(struct sockaddr*)&a,sizeof(a))||listen(l,1)){close(l);return -1;}socklen_t alen=sizeof(a);getsockname(l,(struct sockaddr*)&a,&alen);
    int c=socket(AF_INET,SOCK_STREAM,0);if(c<0){close(l);return -1;}setsockopt(c,IPPROTO_TCP,TCP_NODELAY,&one,sizeof(one));
    if(connect(c,(struct sockaddr*)&a,sizeof(a))){close(c);close(l);return -1;}int s=accept(l,NULL,NULL);close(l);if(s<0){close(c);return -1;}setsockopt(s,IPPROTO_TCP,TCP_NODELAY,&one,sizeof(one));
    int sz=4*1024*1024;setsockopt(c,SOL_SOCKET,SO_SNDBUF,&sz,sizeof(sz));setsockopt(s,SOL_SOCKET,SO_RCVBUF,&sz,sizeof(sz));*tx=c;*rx=s;return 0;
}

static void *sender(void *arg){
    bench_t*b=arg;pin(4);void *buf=NULL;posix_memalign(&buf,64,BLOCK_BYTES);while(!atomic_load_explicit(&b->start,memory_order_acquire))relax();double c0=cpu_now();
    uint64_t base=1;
    for(uint64_t blk=0;blk<TOTAL_BLOCKS;blk++){
        if(b->mode==SOA_SCALAR||b->mode==SOA_AVX2){soa_block_t*s=(soa_block_t*)buf;for(unsigned i=0;i<NMSG;i++){aos_t a=make_input(base+i);s->f[0][i]=a.x;s->f[1][i]=a.y;s->f[2][i]=a.z;s->f[3][i]=a.vx;s->f[4][i]=a.vy;s->f[5][i]=a.vz;s->f[6][i]=a.temp;s->f[7][i]=a.scale;}}
        else {aos_t*a=(aos_t*)buf;for(unsigned i=0;i<NMSG;i++)a[i]=make_input(base+i);} base+=NMSG;
        size_t off=0;while(off<BLOCK_BYTES){ssize_t n=send(b->tx,(unsigned char*)buf+off,BLOCK_BYTES-off,MSG_NOSIGNAL);if(n>0){off+=(size_t)n;continue;}if(n<0&&errno==EINTR)continue;goto done;}
    }
done:b->sender_cpu_s=cpu_now()-c0;shutdown(b->tx,SHUT_WR);free(buf);return NULL;
}

static void *processor(void *arg){
    bench_t*b=arg;pin(2);void *in=NULL;posix_memalign(&in,64,BLOCK_BYTES);unsigned slot=0;while(!atomic_load_explicit(&b->start,memory_order_acquire))relax();double c0=cpu_now();
    for(uint64_t blk=0;blk<TOTAL_BLOCKS;blk++){
        size_t off=0;while(off<BLOCK_BYTES){ssize_t n=recv(b->rx,(unsigned char*)in+off,BLOCK_BYTES-off,0);if(n>0){off+=(size_t)n;continue;}if(n<0&&errno==EINTR)continue;goto done;}
        out_slab_t*s=&b->out[slot];while(atomic_load_explicit(&s->ready,memory_order_acquire)){b->prod_wait++;relax();}
        if(b->mode==AOS_SCALAR)aos_scalar((aos_t*)in,s->items);else if(b->mode==AOS_AVX2)aos_avx2((aos_t*)in,s->items);else if(b->mode==SOA_SCALAR)soa_scalar((soa_block_t*)in,s->items);else soa_avx2((soa_block_t*)in,s->items);
        s->count=NMSG;atomic_store_explicit(&s->ready,1,memory_order_release);b->blocks++;b->processed+=NMSG;slot=(slot+1)%OUT_SLABS;
    }
done:b->processor_cpu_s=cpu_now()-c0;free(in);return NULL;
}

static void *consumer(void *arg){
    bench_t*b=arg;pin(3);unsigned slot=0;uint64_t expected=1,cons=0,err=0;while(!atomic_load_explicit(&b->start,memory_order_acquire))relax();double c0=cpu_now();
    while(cons<TOTAL_FRAMES){out_slab_t*s=&b->out[slot];if(!atomic_load_explicit(&s->ready,memory_order_acquire)){b->cons_wait++;relax();continue;}for(unsigned i=0;i<s->count;i++,expected++,cons++)if(!valid_out(s->items[i],expected))err++;atomic_store_explicit(&s->ready,0,memory_order_release);slot=(slot+1)%OUT_SLABS;}
    b->consumer_cpu_s=cpu_now()-c0;b->consumed=cons;b->errors=err;return NULL;
}

static const char*name(mode_t2 m){return m==AOS_SCALAR?"aos_scalar":m==AOS_AVX2?"aos_avx2":m==SOA_SCALAR?"soa_scalar":"soa_avx2";}
int main(int argc,char**argv){
    if(argc<2)return 2;
    bench_t*b=NULL;
    if(posix_memalign((void**)&b,64,sizeof(*b)))return 1;
    memset(b,0,sizeof(*b));
    if(!strcmp(argv[1],"aos_scalar"))b->mode=AOS_SCALAR;else if(!strcmp(argv[1],"aos_avx2"))b->mode=AOS_AVX2;else if(!strcmp(argv[1],"soa_scalar"))b->mode=SOA_SCALAR;else b->mode=SOA_AVX2;
    if(make_pair(&b->tx,&b->rx)){perror("pair");return 1;}
    pthread_t ts,tp,tc;
    pthread_create(&tc,NULL,consumer,b);
    pthread_create(&tp,NULL,processor,b);
    pthread_create(&ts,NULL,sender,b);
    usleep(20000);
    uint64_t w0=ns_now();
    atomic_store_explicit(&b->start,1,memory_order_release);
    pthread_join(ts,NULL);pthread_join(tp,NULL);pthread_join(tc,NULL);
    b->wall_s=(double)(ns_now()-w0)/1e9;
    printf("mode=%s wall_s=%.6f frames=%llu fps=%.3f payload_GBps=%.6f sender_cpu_ns_frame=%.6f processor_cpu_ns_frame=%.6f consumer_cpu_ns_frame=%.6f blocks=%llu prod_wait=%llu cons_wait=%llu errors=%llu\n",name(b->mode),b->wall_s,(unsigned long long)b->consumed,(double)b->consumed/b->wall_s,(double)b->consumed*FRAME/b->wall_s/1e9,b->sender_cpu_s*1e9/(double)b->consumed,b->processor_cpu_s*1e9/(double)b->processed,b->consumer_cpu_s*1e9/(double)b->consumed,(unsigned long long)b->blocks,(unsigned long long)b->prod_wait,(unsigned long long)b->cons_wait,(unsigned long long)b->errors);
    int rc=(b->errors||b->processed!=TOTAL_FRAMES||b->consumed!=TOTAL_FRAMES)?3:0;
    close(b->tx);close(b->rx);free(b);return rc;
}

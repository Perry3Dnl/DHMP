#define _GNU_SOURCE
#include <pthread.h>
#include <sched.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <immintrin.h>

#define ITERS 50000000ULL
#define SLAB 12288

typedef struct {
    size_t frame_size;
    uint8_t *workspace;
    uint8_t ring[96] __attribute__((aligned(64)));
    unsigned back;
    uint64_t checksum;
} ctx_t;

static inline double cpu_now(void){struct timespec t;clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t);return(double)t.tv_sec+(double)t.tv_nsec/1e9;}
static void pin0(void){cpu_set_t s;CPU_ZERO(&s);CPU_SET(0,&s);sched_setaffinity(0,sizeof(s),&s);}

__attribute__((noinline)) static void process_generic(ctx_t *c, size_t total){
    size_t f=c->frame_size;
    size_t complete=total/f;
    size_t tail=total-complete*f;
    if(complete){
        uint8_t *dst=c->ring+(size_t)c->back*f;
        const uint8_t *src=c->workspace+total-tail-f;
        memcpy(dst,src,f);
        c->checksum += *(const uint64_t*)dst;
        c->back++; if(c->back==3)c->back=0;
    }
}

__attribute__((noinline)) static void process_special32(ctx_t *c, size_t total){
    size_t complete=total>>5;
    size_t tail=total&31u;
    if(complete){
        uint8_t *dst=c->ring+((size_t)c->back<<5);
        const uint8_t *src=c->workspace+total-tail-32;
        __m256i v=_mm256_loadu_si256((const __m256i*)src);
        _mm256_storeu_si256((__m256i*)dst,v);
        c->checksum += *(const uint64_t*)dst;
        c->back++; if(c->back==3)c->back=0;
    }
}

int main(int argc,char**argv){
    if (argc < 2) return 2;
    int special = !strcmp(argv[1], "special32");
    pin0();
    uint8_t *w=NULL; if(posix_memalign((void**)&w,64,SLAB+64))return 1; for(size_t i=0;i<SLAB+64;i++)w[i]=(uint8_t)(i*131u+17u);
    ctx_t c={0};c.frame_size=32;c.workspace=w;
    static const size_t totals[16]={12288,12287,12273,12001,8192,8191,8177,4096,4095,4081,2048,2033,1024,1009,513,511};
    double t0=cpu_now();
    for(uint64_t i=0;i<ITERS;i++){
        size_t total=totals[i&15u];
        if(special) process_special32(&c,total); else process_generic(&c,total);
    }
    double dt=cpu_now()-t0;
    printf("%s ns_batch=%.6f batches_s=%.3f checksum=%llu back=%u\n",special?"special32":"generic",dt*1e9/(double)ITERS,(double)ITERS/dt,(unsigned long long)c.checksum,c.back);
    free(w);return 0;
}

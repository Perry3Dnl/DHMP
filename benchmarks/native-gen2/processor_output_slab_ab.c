#define _GNU_SOURCE
#include <immintrin.h>
#include <pthread.h>
#include <sched.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

#define FRAME 32u
#define FRAMES 384u
#define BATCH_BYTES (FRAME*FRAMES)
#define BATCHES 250000u
#define DIRTY 4u
#define IDX_MASK 3u
#define HOLD_NS 10000ull
#define MAGIC 0x9e3779b97f4a7c15ULL

typedef struct { uint64_t a,b,c,d; } frame_t;

static inline frame_t transform(frame_t x, uint64_t batch){
    frame_t y;
    y.a=x.a+batch+MAGIC;
    y.b=x.b^y.a;
    y.c=x.c+((y.b<<13)|(y.b>>51));
    y.d=(x.d^y.c)+0x12345678ULL;
    return y;
}
static inline uint64_t mono_ns(void){struct timespec t;clock_gettime(CLOCK_MONOTONIC_RAW,&t);return(uint64_t)t.tv_sec*1000000000ull+t.tv_nsec;}
static inline double cpu_s(void){struct timespec t;clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t);return(double)t.tv_sec+(double)t.tv_nsec/1e9;}
static void pin(int cpu){cpu_set_t s;CPU_ZERO(&s);CPU_SET(cpu,&s);pthread_setaffinity_np(pthread_self(),sizeof(s),&s);}
static inline void relax(void){_mm_pause();}

typedef struct __attribute__((aligned(64))) {
    _Atomic uint32_t token;
    unsigned char pad[60];
    frame_t slabs[3][FRAMES] __attribute__((aligned(64)));
} slab3_t;

typedef struct __attribute__((aligned(64))) {
    _Atomic uint32_t token;
    unsigned char pad[60];
    frame_t slots[3] __attribute__((aligned(64)));
} frame3_t;

typedef struct {
    int batch_mode;
    _Atomic int start,stop;
    frame_t input[FRAMES] __attribute__((aligned(64)));
    slab3_t slabs;
    frame3_t frames;
    uint64_t producer_frames,producer_batches,consumer_pubs,consumer_retries,errors;
    double producer_cpu;
} bench_t;

static void *producer(void *arg){
    bench_t*b=arg; pin(0);
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    unsigned back=2; double t0=cpu_s();
    for(uint64_t batch=1;batch<=BATCHES;batch++){
        if(b->batch_mode){
            frame_t *out=b->slabs.slabs[back];
            for(unsigned i=0;i<FRAMES;i++) out[i]=transform(b->input[i],batch);
            uint32_t old=atomic_exchange_explicit(&b->slabs.token,back|DIRTY,memory_order_acq_rel);
            back=old&IDX_MASK;
            b->producer_batches++;
            b->producer_frames+=FRAMES;
        }else{
            for(unsigned i=0;i<FRAMES;i++){
                b->frames.slots[back]=transform(b->input[i],batch);
                uint32_t old=atomic_exchange_explicit(&b->frames.token,back|DIRTY,memory_order_acq_rel);
                back=old&IDX_MASK;
                b->producer_frames++;
            }
            b->producer_batches++;
        }
    }
    b->producer_cpu=cpu_s()-t0;
    atomic_store_explicit(&b->stop,1,memory_order_release);
    return NULL;
}

static void *consumer(void *arg){
    bench_t*b=arg; pin(1);
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) relax();
    unsigned front=0; uint64_t pubs=0,retries=0,errors=0;
    while(!atomic_load_explicit(&b->stop,memory_order_acquire)){
        _Atomic uint32_t *tok=b->batch_mode?&b->slabs.token:&b->frames.token;
        uint32_t v=atomic_load_explicit(tok,memory_order_relaxed);
        if(!(v&DIRTY)){relax();continue;}
        uint32_t desired=front;
        if(!atomic_compare_exchange_weak_explicit(tok,&v,desired,memory_order_acq_rel,memory_order_relaxed)){
            retries++;relax();continue;
        }
        front=v&IDX_MASK;
        if(b->batch_mode){
            volatile frame_t *s=b->slabs.slabs[front];
            uint64_t batch_id=s[0].a-b->input[0].a-MAGIC;
            for(unsigned i=0;i<FRAMES;i++){
                frame_t exp=transform(b->input[i],batch_id);
                if(s[i].a!=exp.a || s[i].b!=exp.b || s[i].c!=exp.c || s[i].d!=exp.d){
                    errors++; break;
                }
            }
        }else{
            volatile frame_t *s=&b->frames.slots[front];
            if(s->a==0) errors++;
        }
        pubs++;
        uint64_t until=mono_ns()+HOLD_NS;
        while(mono_ns()<until) relax();
    }
    b->consumer_pubs=pubs;b->consumer_retries=retries;b->errors=errors;
    return NULL;
}

int main(int argc,char**argv){
    if(argc<2)return 2;
    bench_t*b=NULL;
    if(posix_memalign((void**)&b,64,sizeof(*b))!=0)return 1;
    memset(b,0,sizeof(*b));
    b->batch_mode=!strcmp(argv[1],"batch_slab");
    for(unsigned i=0;i<FRAMES;i++){
        b->input[i].a=i+1;b->input[i].b=i*3+7;b->input[i].c=i*5+11;b->input[i].d=i*7+13;
    }
    atomic_store(&b->slabs.token,1u);
    atomic_store(&b->frames.token,1u);
    pthread_t p,c;
    pthread_create(&p,NULL,producer,b);
    pthread_create(&c,NULL,consumer,b);
    usleep(10000);
    atomic_store_explicit(&b->start,1,memory_order_release);
    pthread_join(p,NULL);
    pthread_join(c,NULL);
    double nsf=b->producer_cpu*1e9/(double)b->producer_frames;
    printf("mode=%s frames=%llu batches=%llu producer_cpu_s=%.6f ns_frame=%.6f frames_s_cpu=%.3f consumer_pubs=%llu consumer_retries=%llu errors=%llu\n",
      b->batch_mode?"batch_slab":"per_result",
      (unsigned long long)b->producer_frames,
      (unsigned long long)b->producer_batches,
      b->producer_cpu,nsf,(double)b->producer_frames/b->producer_cpu,
      (unsigned long long)b->consumer_pubs,
      (unsigned long long)b->consumer_retries,
      (unsigned long long)b->errors);
    free(b);
    return 0;
}

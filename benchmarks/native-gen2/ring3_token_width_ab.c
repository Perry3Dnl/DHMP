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
#include <immintrin.h>

#ifndef TOKEN_BITS
#define TOKEN_BITS 32
#endif
#if TOKEN_BITS == 8
typedef uint8_t token_t;
#elif TOKEN_BITS == 16
typedef uint16_t token_t;
#elif TOKEN_BITS == 32
typedef uint32_t token_t;
#elif TOKEN_BITS == 64
typedef uint64_t token_t;
#else
#error bad TOKEN_BITS
#endif

#define FRAME_BYTES 32
#define HOLD_NS 10000ULL
#define PUBS 50000000ULL
#define SLOTS 3
#define DIRTY_BIT ((token_t)4)
#define IDX_MASK ((token_t)3)

typedef struct __attribute__((aligned(64))) {
    _Atomic token_t middle_token;
    uint8_t padtok[64-sizeof(_Atomic token_t)];
    _Atomic int start;
    _Atomic int stop;
    uint8_t padctl[64-2*sizeof(_Atomic int)];
    uint8_t payload[SLOTS][FRAME_BYTES] __attribute__((aligned(64)));
    uint64_t producer_pubs, consumer_pubs, consumer_retries, validation_errors;
    double producer_cpu_s, consumer_cpu_s;
} bench_t;

static inline uint64_t ns_now(void){struct timespec t;clock_gettime(CLOCK_MONOTONIC_RAW,&t);return(uint64_t)t.tv_sec*1000000000ULL+t.tv_nsec;}
static inline double cpu_now(void){struct timespec t;clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t);return(double)t.tv_sec+(double)t.tv_nsec/1e9;}
static void pin_cpu(int cpu){cpu_set_t s;CPU_ZERO(&s);CPU_SET(cpu,&s);pthread_setaffinity_np(pthread_self(),sizeof(s),&s);}
static inline void fill32(uint8_t*p,uint64_t n){uint64_t*q=(uint64_t*)p;q[0]=n;q[1]=n^0x9e3779b97f4a7c15ULL;q[2]=~n;q[3]=n+0x12345678ULL;}
static inline int valid32(const uint8_t*p){const uint64_t*q=(const uint64_t*)p;uint64_t a=q[0];return q[1]==(a^0x9e3779b97f4a7c15ULL)&&q[2]==~a&&q[3]==a+0x12345678ULL;}

static void* producer(void*arg){
    bench_t*b=arg;pin_cpu(0);while(!atomic_load_explicit(&b->start,memory_order_acquire))_mm_pause();
    token_t back=2;double c0=cpu_now();
    for(uint64_t seq=1;seq<=PUBS;++seq){
        fill32(b->payload[back],seq);
        token_t old=atomic_exchange_explicit(&b->middle_token,(token_t)(back|DIRTY_BIT),memory_order_release);
        back=(token_t)(old&IDX_MASK);
    }
    b->producer_cpu_s=cpu_now()-c0;b->producer_pubs=PUBS;atomic_store_explicit(&b->stop,1,memory_order_release);return NULL;
}
static void* consumer(void*arg){
    bench_t*b=arg;pin_cpu(1);while(!atomic_load_explicit(&b->start,memory_order_acquire))_mm_pause();
    token_t front=0;uint64_t pubs=0,retries=0,errors=0;double c0=cpu_now();
    while(!atomic_load_explicit(&b->stop,memory_order_acquire)){
        token_t tok=atomic_load_explicit(&b->middle_token,memory_order_relaxed);
        if(!(tok&DIRTY_BIT)){_mm_pause();continue;}
        token_t desired=front;
        if(!atomic_compare_exchange_weak_explicit(&b->middle_token,&tok,desired,memory_order_acq_rel,memory_order_relaxed)){retries++;_mm_pause();continue;}
        front=(token_t)(tok&IDX_MASK);
        if(!valid32(b->payload[front]))errors++;
        pubs++;
        uint64_t until=ns_now()+HOLD_NS;while(ns_now()<until)_mm_pause();
    }
    b->consumer_cpu_s=cpu_now()-c0;b->consumer_pubs=pubs;b->consumer_retries=retries;b->validation_errors=errors;return NULL;
}
int main(void){
    bench_t*b=NULL;if(posix_memalign((void**)&b,64,sizeof(*b)))return 1;memset(b,0,sizeof(*b));
    fill32(b->payload[0],0);fill32(b->payload[1],0);fill32(b->payload[2],0);atomic_store(&b->middle_token,(token_t)1);
    pthread_t p,c;pthread_create(&p,NULL,producer,b);pthread_create(&c,NULL,consumer,b);usleep(10000);atomic_store_explicit(&b->start,1,memory_order_release);pthread_join(p,NULL);pthread_join(c,NULL);
    printf("token%d producer_ns_pub=%.6f producer_pubs_s=%.3f consumer_pubs=%llu consumer_retries=%llu validation_errors=%llu\n",TOKEN_BITS,b->producer_cpu_s*1e9/(double)b->producer_pubs,(double)b->producer_pubs/b->producer_cpu_s,(unsigned long long)b->consumer_pubs,(unsigned long long)b->consumer_retries,(unsigned long long)b->validation_errors);
    free(b);return 0;
}

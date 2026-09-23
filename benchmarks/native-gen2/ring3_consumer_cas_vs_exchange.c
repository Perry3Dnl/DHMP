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

#define N 10000000ULL
#define DIRTY 4u
#define MASK 3u
#define MAGIC 0x9e3779b97f4a7c15ULL

typedef struct __attribute__((aligned(64))){
    _Atomic unsigned token;
    unsigned char pad0[60];
    _Atomic int start,stop;
    unsigned char pad1[56];
    uint64_t slot[3][4] __attribute__((aligned(64)));
    uint64_t producer_pubs,consumer_acquisitions,consumer_retries,errors;
    double producer_cpu,consumer_cpu;
    int consumer_exchange;
} bench_t;

static inline double cpu_now(void){
    struct timespec t; clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t);
    return (double)t.tv_sec+(double)t.tv_nsec/1e9;
}
static void pin_cpu(int n){
    cpu_set_t s; CPU_ZERO(&s); CPU_SET(n,&s);
    pthread_setaffinity_np(pthread_self(),sizeof(s),&s);
}
static inline void fill(uint64_t*q,uint64_t n){
    q[0]=n; q[1]=n^MAGIC; q[2]=~n; q[3]=n+0x12345678ULL;
}
static inline int valid(const uint64_t*q){
    uint64_t n=q[0];
    return q[1]==(n^MAGIC) && q[2]==~n && q[3]==n+0x12345678ULL;
}

static void *producer(void *arg){
    bench_t*b=arg; pin_cpu(0);
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) _mm_pause();
    unsigned back=2; double t0=cpu_now();
    for(uint64_t n=1;n<=N;n++){
        fill(b->slot[back],n);
        unsigned old=atomic_exchange_explicit(&b->token,back|DIRTY,memory_order_acq_rel);
        back=old&MASK;
    }
    b->producer_cpu=cpu_now()-t0;
    b->producer_pubs=N;
    atomic_store_explicit(&b->stop,1,memory_order_release);
    return NULL;
}

static void *consumer(void *arg){
    bench_t*b=arg; pin_cpu(1);
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) _mm_pause();
    unsigned front=0; uint64_t pubs=0,retries=0,errors=0; double t0=cpu_now();
    while(!atomic_load_explicit(&b->stop,memory_order_acquire)){
        unsigned tok=atomic_load_explicit(&b->token,memory_order_relaxed);
        if(!(tok&DIRTY)){ _mm_pause(); continue; }

        if(b->consumer_exchange){
            tok=atomic_exchange_explicit(&b->token,front,memory_order_acq_rel);
            if(!(tok&DIRTY)){ errors++; continue; }
        } else {
            unsigned desired=front;
            if(!atomic_compare_exchange_weak_explicit(
                    &b->token,&tok,desired,
                    memory_order_acq_rel,memory_order_relaxed)){
                retries++;
                continue;
            }
        }

        front=tok&MASK;
        if(!valid(b->slot[front])) errors++;
        pubs++;
    }
    b->consumer_cpu=cpu_now()-t0;
    b->consumer_acquisitions=pubs;
    b->consumer_retries=retries;
    b->errors=errors;
    return NULL;
}

int main(int argc,char**argv){
    if(argc<2) return 2;
    bench_t*b=NULL;
    if(posix_memalign((void**)&b,64,sizeof(*b))) return 1;
    memset(b,0,sizeof(*b));
    b->consumer_exchange=!strcmp(argv[1],"xchg");
    fill(b->slot[0],0); fill(b->slot[1],0); fill(b->slot[2],0);
    atomic_store(&b->token,1u);

    pthread_t p,c;
    pthread_create(&p,NULL,producer,b);
    pthread_create(&c,NULL,consumer,b);
    usleep(5000);
    atomic_store_explicit(&b->start,1,memory_order_release);
    pthread_join(p,NULL);
    pthread_join(c,NULL);

    printf("mode=%s producer_ns_pub=%.6f consumer_acquisitions=%llu "
           "consumer_cpu_ns_acq=%.6f consumer_retries=%llu errors=%llu\n",
           b->consumer_exchange?"xchg":"cas",
           b->producer_cpu*1e9/(double)N,
           (unsigned long long)b->consumer_acquisitions,
           b->consumer_acquisitions
             ? b->consumer_cpu*1e9/(double)b->consumer_acquisitions : 0.0,
           (unsigned long long)b->consumer_retries,
           (unsigned long long)b->errors);
    free(b);
    return 0;
}

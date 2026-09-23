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

#define N 20000000ULL
#define DIRTY 4u
#define MASK 3u
#define MAGIC 0x9e3779b97f4a7c15ULL

typedef struct __attribute__((aligned(64))) {
    _Atomic unsigned token;
    unsigned char pad0[60];
    _Atomic int start, stop;
    unsigned char pad1[56];
    uint64_t slot[3][4] __attribute__((aligned(64)));
    int producer_cpu, consumer_cpu;
    uint64_t acquisitions, retries, errors;
    double producer_cpu_s, consumer_cpu_s;
} bench_t;

static inline double cpu_now(void){
    struct timespec t;
    clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t);
    return (double)t.tv_sec + (double)t.tv_nsec/1e9;
}

static void pin_cpu(int cpu){
    cpu_set_t set;
    CPU_ZERO(&set);
    CPU_SET(cpu,&set);
    pthread_setaffinity_np(pthread_self(),sizeof(set),&set);
}

static inline void fill32(uint64_t *q,uint64_t n){
    q[0]=n;
    q[1]=n^MAGIC;
    q[2]=~n;
    q[3]=n+0x12345678ULL;
}

static inline int valid32(const uint64_t *q){
    uint64_t n=q[0];
    return q[1]==(n^MAGIC) && q[2]==~n && q[3]==n+0x12345678ULL;
}

static void *producer(void *arg){
    bench_t *b=arg;
    pin_cpu(b->producer_cpu);
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) _mm_pause();

    unsigned back=2;
    double t0=cpu_now();
    for(uint64_t n=1;n<=N;n++){
        fill32(b->slot[back],n);
        unsigned old=atomic_exchange_explicit(
            &b->token,back|DIRTY,memory_order_acq_rel);
        back=old&MASK;
    }
    b->producer_cpu_s=cpu_now()-t0;
    atomic_store_explicit(&b->stop,1,memory_order_release);
    return NULL;
}

static void *consumer(void *arg){
    bench_t *b=arg;
    pin_cpu(b->consumer_cpu);
    while(!atomic_load_explicit(&b->start,memory_order_acquire)) _mm_pause();

    unsigned front=0;
    uint64_t acquisitions=0,retries=0,errors=0;
    double t0=cpu_now();

    while(!atomic_load_explicit(&b->stop,memory_order_acquire)){
        unsigned tok=atomic_load_explicit(&b->token,memory_order_relaxed);
        if(!(tok&DIRTY)){
            _mm_pause();
            continue;
        }

        unsigned desired=front;
        if(!atomic_compare_exchange_weak_explicit(
                &b->token,&tok,desired,
                memory_order_acq_rel,memory_order_relaxed)){
            retries++;
            continue;
        }

        front=tok&MASK;
        if(!valid32(b->slot[front])) errors++;
        acquisitions++;
    }

    b->consumer_cpu_s=cpu_now()-t0;
    b->acquisitions=acquisitions;
    b->retries=retries;
    b->errors=errors;
    return NULL;
}

int main(int argc,char **argv){
    if(argc<3){
        fprintf(stderr,"usage: %s producer_cpu consumer_cpu\n",argv[0]);
        return 2;
    }

    bench_t *b=NULL;
    if(posix_memalign((void**)&b,64,sizeof(*b))) return 1;
    memset(b,0,sizeof(*b));

    b->producer_cpu=atoi(argv[1]);
    b->consumer_cpu=atoi(argv[2]);

    fill32(b->slot[0],0);
    fill32(b->slot[1],0);
    fill32(b->slot[2],0);
    atomic_store(&b->token,1u);

    pthread_t pt,ct;
    pthread_create(&ct,NULL,consumer,b);
    pthread_create(&pt,NULL,producer,b);
    usleep(5000);
    atomic_store_explicit(&b->start,1,memory_order_release);
    pthread_join(pt,NULL);
    pthread_join(ct,NULL);

    printf(
        "producer=%d consumer=%d producer_ns_pub=%.6f "
        "consumer_acquisitions=%llu consumer_ns_acq=%.6f "
        "retries=%llu errors=%llu\n",
        b->producer_cpu,b->consumer_cpu,
        b->producer_cpu_s*1e9/(double)N,
        (unsigned long long)b->acquisitions,
        b->acquisitions
            ? b->consumer_cpu_s*1e9/(double)b->acquisitions : 0.0,
        (unsigned long long)b->retries,
        (unsigned long long)b->errors);

    int rc=b->errors?3:0;
    free(b);
    return rc;
}

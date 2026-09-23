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

#define FRAME_BYTES 32
#define HOLD_NS 10000ULL
#define RUN_NS 2000000000ULL
#define SLOTS 3
#define DIRTY_BIT 4u
#define IDX_MASK 3u

enum { FREE=0, WRITING=1, PUBLISHED=2, READING=3 };

typedef struct __attribute__((aligned(64))) {
    _Atomic int state[SLOTS], published_idx, start, stop;
    uint8_t payload[SLOTS][FRAME_BYTES] __attribute__((aligned(64)));
    uint64_t producer_pubs, producer_retries, consumer_pubs, consumer_retries, validation_errors;
    double producer_cpu_s, consumer_cpu_s;
} baseline_t;

typedef struct __attribute__((aligned(64))) {
    _Atomic unsigned middle_token;
    _Atomic int start, stop;
    uint8_t payload[SLOTS][FRAME_BYTES] __attribute__((aligned(64)));
    uint64_t producer_pubs, consumer_pubs, consumer_retries, validation_errors;
    double producer_cpu_s, consumer_cpu_s;
} exchange_t;

static inline uint64_t ns_now(void){struct timespec t;clock_gettime(CLOCK_MONOTONIC_RAW,&t);return(uint64_t)t.tv_sec*1000000000ULL+t.tv_nsec;}
static inline double cpu_now(void){struct timespec t;clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t);return(double)t.tv_sec+(double)t.tv_nsec/1e9;}
static void pin_cpu(int cpu){cpu_set_t s;CPU_ZERO(&s);CPU_SET(cpu,&s);pthread_setaffinity_np(pthread_self(),sizeof(s),&s);}
static inline void fill32(uint8_t*p,uint64_t n){uint64_t*q=(uint64_t*)p;q[0]=n;q[1]=n^0x9e3779b97f4a7c15ULL;q[2]=~n;q[3]=n+0x12345678ULL;}
static inline int valid32(const uint8_t*p){const uint64_t*q=(const uint64_t*)p;uint64_t a=q[0];return q[1]==(a^0x9e3779b97f4a7c15ULL)&&q[2]==~a&&q[3]==a+0x12345678ULL;}

static int baseline_claim(baseline_t*b,uint64_t*retries,int*hint){
    for(int k=0;k<3;k++){int i=(*hint+k)%3,e=FREE;if(atomic_compare_exchange_weak_explicit(&b->state[i],&e,WRITING,memory_order_acquire,memory_order_relaxed)){*hint=(i+1)%3;return i;}(*retries)++;}
    int p=atomic_load_explicit(&b->published_idx,memory_order_relaxed);
    if(p>=0){int e=PUBLISHED;if(atomic_compare_exchange_weak_explicit(&b->state[p],&e,WRITING,memory_order_acquire,memory_order_relaxed))return p;(*retries)++;}
    return -1;
}
static inline void baseline_publish(baseline_t*b,int idx){
    atomic_store_explicit(&b->state[idx],PUBLISHED,memory_order_release);
    int old=atomic_exchange_explicit(&b->published_idx,idx,memory_order_acq_rel);
    if(old>=0&&old!=idx){int e=PUBLISHED;atomic_compare_exchange_strong_explicit(&b->state[old],&e,FREE,memory_order_acq_rel,memory_order_relaxed);}
}
static void* baseline_producer(void*arg){
    baseline_t*b=arg;pin_cpu(0);while(!atomic_load_explicit(&b->start,memory_order_acquire))_mm_pause();
    uint64_t deadline=ns_now()+RUN_NS,seq=1,pubs=0,retries=0;int hint=0;double c0=cpu_now();
    while(ns_now()<deadline){int i=baseline_claim(b,&retries,&hint);if(i<0){_mm_pause();continue;}fill32(b->payload[i],seq++);baseline_publish(b,i);pubs++;}
    b->producer_cpu_s=cpu_now()-c0;b->producer_pubs=pubs;b->producer_retries=retries;atomic_store_explicit(&b->stop,1,memory_order_release);return NULL;
}
static void* baseline_consumer(void*arg){
    baseline_t*b=arg;pin_cpu(1);while(!atomic_load_explicit(&b->start,memory_order_acquire))_mm_pause();
    uint64_t pubs=0,retries=0,errors=0;double c0=cpu_now();
    while(!atomic_load_explicit(&b->stop,memory_order_acquire)){
        int p=atomic_load_explicit(&b->published_idx,memory_order_acquire);if(p<0){retries++;continue;}int e=PUBLISHED;
        if(!atomic_compare_exchange_weak_explicit(&b->state[p],&e,READING,memory_order_acq_rel,memory_order_relaxed)){retries++;_mm_pause();continue;}
        if(!valid32(b->payload[p]))errors++;pubs++;uint64_t until=ns_now()+HOLD_NS;while(ns_now()<until)_mm_pause();atomic_store_explicit(&b->state[p],FREE,memory_order_release);
    }
    b->consumer_cpu_s=cpu_now()-c0;b->consumer_pubs=pubs;b->consumer_retries=retries;b->validation_errors=errors;return NULL;
}

static void* exchange_producer(void*arg){
    exchange_t*b=arg;pin_cpu(0);while(!atomic_load_explicit(&b->start,memory_order_acquire))_mm_pause();
    uint64_t deadline=ns_now()+RUN_NS,seq=1,pubs=0;unsigned back=2;double c0=cpu_now();
    while(ns_now()<deadline){fill32(b->payload[back],seq++);unsigned old=atomic_exchange_explicit(&b->middle_token,back|DIRTY_BIT,memory_order_acq_rel);back=old&IDX_MASK;pubs++;}
    b->producer_cpu_s=cpu_now()-c0;b->producer_pubs=pubs;atomic_store_explicit(&b->stop,1,memory_order_release);return NULL;
}
static void* exchange_consumer(void*arg){
    exchange_t*b=arg;pin_cpu(1);while(!atomic_load_explicit(&b->start,memory_order_acquire))_mm_pause();
    unsigned front=0;uint64_t pubs=0,retries=0,errors=0;double c0=cpu_now();
    while(!atomic_load_explicit(&b->stop,memory_order_acquire)){
        unsigned tok=atomic_load_explicit(&b->middle_token,memory_order_acquire);if(!(tok&DIRTY_BIT)){_mm_pause();continue;}
        unsigned desired=front;
        if(!atomic_compare_exchange_weak_explicit(&b->middle_token,&tok,desired,memory_order_acq_rel,memory_order_acquire)){retries++;_mm_pause();continue;}
        front=tok&IDX_MASK;if(!valid32(b->payload[front]))errors++;pubs++;uint64_t until=ns_now()+HOLD_NS;while(ns_now()<until)_mm_pause();
    }
    b->consumer_cpu_s=cpu_now()-c0;b->consumer_pubs=pubs;b->consumer_retries=retries;b->validation_errors=errors;return NULL;
}

int main(int argc,char**argv){
    if(argc<2)return 2;
    if(!strcmp(argv[1],"baseline")){
        baseline_t*b=NULL;if(posix_memalign((void**)&b,64,sizeof(*b)))return 1;memset(b,0,sizeof(*b));
        for(int i=0;i<SLOTS;i++)atomic_init(&b->state[i],FREE);fill32(b->payload[0],0);atomic_store(&b->state[0],PUBLISHED);atomic_store(&b->published_idx,0);
        pthread_t p,c;pthread_create(&p,NULL,baseline_producer,b);pthread_create(&c,NULL,baseline_consumer,b);usleep(10000);atomic_store(&b->start,1);pthread_join(p,NULL);pthread_join(c,NULL);
        printf("baseline producer_pubs_s=%.3f producer_ns_pub=%.3f producer_retries=%llu consumer_pubs_s=%.3f consumer_retries=%llu validation_errors=%llu\n",b->producer_pubs/2.0,b->producer_cpu_s*1e9/b->producer_pubs,(unsigned long long)b->producer_retries,b->consumer_pubs/2.0,(unsigned long long)b->consumer_retries,(unsigned long long)b->validation_errors);free(b);return 0;
    }
    if(!strcmp(argv[1],"exchange")){
        exchange_t*b=NULL;if(posix_memalign((void**)&b,64,sizeof(*b)))return 1;memset(b,0,sizeof(*b));fill32(b->payload[0],0);fill32(b->payload[1],0);fill32(b->payload[2],0);atomic_store(&b->middle_token,1u);
        pthread_t p,c;pthread_create(&p,NULL,exchange_producer,b);pthread_create(&c,NULL,exchange_consumer,b);usleep(10000);atomic_store(&b->start,1);pthread_join(p,NULL);pthread_join(c,NULL);
        printf("exchange producer_pubs_s=%.3f producer_ns_pub=%.3f producer_retries=0 consumer_pubs_s=%.3f consumer_retries=%llu validation_errors=%llu\n",b->producer_pubs/2.0,b->producer_cpu_s*1e9/b->producer_pubs,b->consumer_pubs/2.0,(unsigned long long)b->consumer_retries,(unsigned long long)b->validation_errors);free(b);return 0;
    }
    return 2;
}

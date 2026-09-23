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
#define MAX_SLOTS 3

enum { FREE=0, WRITING=1, PUBLISHED=2, READING=3 };

typedef struct __attribute__((aligned(64))) {
    _Atomic int state[MAX_SLOTS];
    _Atomic int published_idx;
    _Atomic int start;
    _Atomic int stop;
    uint8_t pad[40];
    uint8_t payload[MAX_SLOTS][FRAME_BYTES] __attribute__((aligned(64)));
    int slots;
    uint64_t producer_pubs, producer_claim_retries;
    double producer_cpu_s;
    uint64_t consumer_pubs, consumer_claim_retries, validation_errors;
    double consumer_cpu_s;
    uint64_t checksum;
} bench_t;

static inline uint64_t ns_now(void){struct timespec t;clock_gettime(CLOCK_MONOTONIC_RAW,&t);return(uint64_t)t.tv_sec*1000000000ULL+t.tv_nsec;}
static inline double cpu_now(void){struct timespec t;clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t);return(double)t.tv_sec+(double)t.tv_nsec/1e9;}
static void pin_cpu(int cpu){cpu_set_t s;CPU_ZERO(&s);CPU_SET(cpu,&s);pthread_setaffinity_np(pthread_self(),sizeof(s),&s);}
static inline void fill32(uint8_t*p,uint64_t n){uint64_t*q=(uint64_t*)p;q[0]=n;q[1]=n^0x9e3779b97f4a7c15ULL;q[2]=~n;q[3]=n+0x12345678ULL;}

static int claim2(bench_t*b,uint64_t*retries){
    int p=atomic_load_explicit(&b->published_idx,memory_order_relaxed);
    int order[2]={p>=0?p:0,p>=0?1-p:1};
    for(int k=0;k<2;k++){
        int i=order[k], e=PUBLISHED;
        if(atomic_compare_exchange_weak_explicit(&b->state[i],&e,WRITING,memory_order_acquire,memory_order_relaxed))return i;
        e=FREE;
        if(atomic_compare_exchange_weak_explicit(&b->state[i],&e,WRITING,memory_order_acquire,memory_order_relaxed))return i;
        (*retries)++;
    }
    return -1;
}

static int claim3(bench_t*b,uint64_t*retries,int*hint){
    for(int k=0;k<3;k++){
        int i=(*hint+k)%3,e=FREE;
        if(atomic_compare_exchange_weak_explicit(&b->state[i],&e,WRITING,memory_order_acquire,memory_order_relaxed)){
            *hint=(i+1)%3;return i;
        }
        (*retries)++;
    }
    int p=atomic_load_explicit(&b->published_idx,memory_order_relaxed);
    if(p>=0){int e=PUBLISHED;if(atomic_compare_exchange_weak_explicit(&b->state[p],&e,WRITING,memory_order_acquire,memory_order_relaxed))return p;(*retries)++;}
    return -1;
}

static inline void publish(bench_t*b,int idx){
    atomic_store_explicit(&b->state[idx],PUBLISHED,memory_order_release);
    int old=atomic_exchange_explicit(&b->published_idx,idx,memory_order_acq_rel);
    if(old>=0&&old!=idx){int e=PUBLISHED;atomic_compare_exchange_strong_explicit(&b->state[old],&e,FREE,memory_order_acq_rel,memory_order_relaxed);}
}

static void *producer(void*arg){
    bench_t*b=arg;pin_cpu(0);while(!atomic_load_explicit(&b->start,memory_order_acquire))_mm_pause();
    uint64_t deadline=ns_now()+RUN_NS,seq=1,pubs=0,retries=0;int hint=0;double c0=cpu_now();
    while(ns_now()<deadline){
        int i=b->slots==2?claim2(b,&retries):claim3(b,&retries,&hint);
        if(i<0){_mm_pause();continue;}
        fill32(b->payload[i],seq++);publish(b,i);pubs++;
    }
    b->producer_cpu_s=cpu_now()-c0;b->producer_pubs=pubs;b->producer_claim_retries=retries;atomic_store_explicit(&b->stop,1,memory_order_release);return NULL;
}

static void *consumer(void*arg){
    bench_t*b=arg;pin_cpu(1);while(!atomic_load_explicit(&b->start,memory_order_acquire))_mm_pause();
    uint64_t pubs=0,retries=0,errors=0,sum=0;double c0=cpu_now();
    while(!atomic_load_explicit(&b->stop,memory_order_acquire)){
        int p=atomic_load_explicit(&b->published_idx,memory_order_acquire);if(p<0){retries++;continue;}
        int e=PUBLISHED;
        if(!atomic_compare_exchange_weak_explicit(&b->state[p],&e,READING,memory_order_acq_rel,memory_order_relaxed)){retries++;_mm_pause();continue;}
        uint64_t*q=(uint64_t*)b->payload[p];uint64_t a=q[0],bb=q[1],cc=q[2],dd=q[3];
        if(bb!=(a^0x9e3779b97f4a7c15ULL)||cc!=~a||dd!=a+0x12345678ULL)errors++;
        sum+=a+bb+cc+dd;pubs++;
        uint64_t until=ns_now()+HOLD_NS;while(ns_now()<until)_mm_pause();
        atomic_store_explicit(&b->state[p],FREE,memory_order_release);
    }
    b->consumer_cpu_s=cpu_now()-c0;b->consumer_pubs=pubs;b->consumer_claim_retries=retries;b->validation_errors=errors;b->checksum=sum;return NULL;
}

int main(int argc,char**argv){
    if(argc<2)return 2;int slots=atoi(argv[1]);if(slots!=2&&slots!=3)return 2;
    bench_t*b=NULL;if(posix_memalign((void**)&b,64,sizeof(*b)))return 1;memset(b,0,sizeof(*b));b->slots=slots;
    for(int i=0;i<MAX_SLOTS;i++)atomic_init(&b->state[i],FREE);fill32(b->payload[0],0);atomic_store(&b->state[0],PUBLISHED);atomic_store(&b->published_idx,0);
    pthread_t pt,ct;pthread_create(&pt,NULL,producer,b);pthread_create(&ct,NULL,consumer,b);usleep(10000);atomic_store_explicit(&b->start,1,memory_order_release);pthread_join(pt,NULL);pthread_join(ct,NULL);
    printf("ring%d producer_pubs_s=%.3f producer_ns_pub=%.3f producer_retries=%llu consumer_pubs_s=%.3f consumer_retries=%llu validation_errors=%llu checksum=%llu\n",slots,b->producer_pubs/2.0,b->producer_cpu_s*1e9/(double)b->producer_pubs,(unsigned long long)b->producer_claim_retries,b->consumer_pubs/2.0,(unsigned long long)b->consumer_claim_retries,(unsigned long long)b->validation_errors,(unsigned long long)b->checksum);
    free(b);return 0;
}

// DHMP post-stream processor pipeline A/B benchmark
// Build: gcc -O3 -march=native -pthread processor_pipeline_exchange_ab.c -o pipeline-ab
// Run:   ./pipeline-ab direct|spsc|batch [messages] [batch]
#define _GNU_SOURCE
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
#include <time.h>

typedef struct { uint32_t v[8]; } package32;
typedef struct { uint32_t v[8]; } model32;
typedef struct {
    package32 *slots;
    size_t mask;
    _Atomic size_t head;
    _Atomic size_t tail;
} spsc_q;
typedef struct {
    spsc_q *q; uint64_t n; uint32_t batch; uint64_t checksum; double cpu_s;
} ctx;

static double now_s(void){struct timespec t;clock_gettime(CLOCK_MONOTONIC_RAW,&t);return t.tv_sec+t.tv_nsec*1e-9;}
static double thread_cpu_s(void){struct timespec t;clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t);return t.tv_sec+t.tv_nsec*1e-9;}
static inline model32 materialize(const package32 *p){model32 m; memcpy(&m,p,sizeof m); return m;}
static inline uint64_t consume_model(const model32 *m){return (uint64_t)m->v[0]+m->v[3]+m->v[7];}
static inline package32 make_pkg(uint64_t i){package32 p={{(uint32_t)i,1,2,3,4,5,6,(uint32_t)(i^0x9e3779b9u)}};return p;}

static int q_push(spsc_q *q,const package32 *p){
    size_t h=atomic_load_explicit(&q->head,memory_order_relaxed);
    size_t n=(h+1)&q->mask;
    if(n==atomic_load_explicit(&q->tail,memory_order_acquire)) return 0;
    q->slots[h]=*p; atomic_store_explicit(&q->head,n,memory_order_release); return 1;
}
static int q_pop(spsc_q *q,package32 *p){
    size_t t=atomic_load_explicit(&q->tail,memory_order_relaxed);
    if(t==atomic_load_explicit(&q->head,memory_order_acquire)) return 0;
    *p=q->slots[t]; atomic_store_explicit(&q->tail,(t+1)&q->mask,memory_order_release); return 1;
}
static void *producer(void *arg){ctx*c=arg; double a=thread_cpu_s();
    for(uint64_t i=0;i<c->n;i++){package32 p=make_pkg(i);while(!q_push(c->q,&p)){}}
    c->cpu_s=thread_cpu_s()-a; return 0;
}
static void *consumer(void *arg){ctx*c=arg; double a=thread_cpu_s(); uint64_t sum=0,done=0; package32 p;
    while(done<c->n){
        uint32_t got=0;
        while(got<c->batch && done<c->n && q_pop(c->q,&p)){model32 m=materialize(&p);sum+=consume_model(&m);got++;done++;}
    }
    c->checksum=sum;c->cpu_s=thread_cpu_s()-a;return 0;
}
int main(int argc,char**argv){
    const char*mode=argc>1?argv[1]:"direct"; uint64_t n=argc>2?strtoull(argv[2],0,10):50000000ull;
    uint32_t batch=argc>3?(uint32_t)strtoul(argv[3],0,10):384u; uint64_t sum=0; double wall0=now_s();
    if(!strcmp(mode,"direct")){
        double c0=thread_cpu_s();for(uint64_t i=0;i<n;i++){package32 p=make_pkg(i);model32 m=materialize(&p);sum+=consume_model(&m);}
        double cpu=thread_cpu_s()-c0,wall=now_s()-wall0;
        printf("mode=direct messages=%llu wall_s=%.6f mps=%.3f cpu_ns_msg=%.3f checksum=%llu\n",(unsigned long long)n,wall,n/wall/1e6,cpu*1e9/n,(unsigned long long)sum);return 0;
    }
    size_t cap=1u<<15; spsc_q q={calloc(cap,sizeof(package32)),cap-1,0,0}; if(!q.slots)return 2;
    ctx pc={&q,n,batch,0,0},cc={&q,n,!strcmp(mode,"batch")?batch:1,0,0}; pthread_t pt,ct;
    wall0=now_s();pthread_create(&ct,0,consumer,&cc);pthread_create(&pt,0,producer,&pc);pthread_join(pt,0);pthread_join(ct,0);
    double wall=now_s()-wall0;sum=cc.checksum;
    printf("mode=%s messages=%llu batch=%u wall_s=%.6f mps=%.3f producer_cpu_ns_msg=%.3f model_cpu_ns_msg=%.3f total_cpu_ns_msg=%.3f checksum=%llu\n",
      mode,(unsigned long long)n,cc.batch,wall,n/wall/1e6,pc.cpu_s*1e9/n,cc.cpu_s*1e9/n,(pc.cpu_s+cc.cpu_s)*1e9/n,(unsigned long long)sum);
    free(q.slots);return 0;
}

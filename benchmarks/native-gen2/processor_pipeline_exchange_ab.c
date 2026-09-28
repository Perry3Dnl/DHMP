// DHMP post-stream processor pipeline A/B benchmark
// Build: gcc -O3 -march=native -pthread processor_pipeline_exchange_ab.c -o pipeline-ab
// Run:   ./pipeline-ab direct|spsc|slab [messages] [slab_size]
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

#define SLAB_SLOTS 8
typedef struct {
    package32 *data;
    uint32_t count;
} slab_slot;
typedef struct {
    slab_slot slots[SLAB_SLOTS];
    uint32_t slab_size;
    _Atomic uint32_t head;
    _Atomic uint32_t tail;
} slab_q;

typedef struct {
    spsc_q *q;
    slab_q *sq;
    uint64_t n;
    uint32_t slab_size;
    uint64_t checksum;
    uint64_t publications;
    uint64_t spins;
    double cpu_s;
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
    q->slots[h]=*p;
    atomic_store_explicit(&q->head,n,memory_order_release);
    return 1;
}
static int q_pop(spsc_q *q,package32 *p){
    size_t t=atomic_load_explicit(&q->tail,memory_order_relaxed);
    if(t==atomic_load_explicit(&q->head,memory_order_acquire)) return 0;
    *p=q->slots[t];
    atomic_store_explicit(&q->tail,(t+1)&q->mask,memory_order_release);
    return 1;
}

static void *producer_spsc(void *arg){
    ctx*c=arg; double a=thread_cpu_s();
    for(uint64_t i=0;i<c->n;i++){
        package32 p=make_pkg(i);
        while(!q_push(c->q,&p)) c->spins++;
        c->publications++;
    }
    c->cpu_s=thread_cpu_s()-a; return 0;
}
static void *consumer_spsc(void *arg){
    ctx*c=arg; double a=thread_cpu_s(); uint64_t sum=0,done=0; package32 p;
    while(done<c->n){
        if(q_pop(c->q,&p)){model32 m=materialize(&p);sum+=consume_model(&m);done++;}
        else c->spins++;
    }
    c->checksum=sum;c->cpu_s=thread_cpu_s()-a;return 0;
}

static void *producer_slab(void *arg){
    ctx*c=arg; slab_q*q=c->sq; double a=thread_cpu_s(); uint64_t i=0;
    while(i<c->n){
        uint32_t h=atomic_load_explicit(&q->head,memory_order_relaxed);
        uint32_t next=(h+1u)%SLAB_SLOTS;
        while(next==atomic_load_explicit(&q->tail,memory_order_acquire)) c->spins++;
        slab_slot*s=&q->slots[h];
        uint32_t count=(uint32_t)((c->n-i)<c->slab_size?(c->n-i):c->slab_size);
        for(uint32_t j=0;j<count;j++) s->data[j]=make_pkg(i+j);
        s->count=count;
        atomic_store_explicit(&q->head,next,memory_order_release);
        c->publications++;
        i+=count;
    }
    c->cpu_s=thread_cpu_s()-a;return 0;
}
static void *consumer_slab(void *arg){
    ctx*c=arg; slab_q*q=c->sq; double a=thread_cpu_s(); uint64_t sum=0,done=0;
    while(done<c->n){
        uint32_t t=atomic_load_explicit(&q->tail,memory_order_relaxed);
        while(t==atomic_load_explicit(&q->head,memory_order_acquire)){
            c->spins++;
            t=atomic_load_explicit(&q->tail,memory_order_relaxed);
        }
        slab_slot*s=&q->slots[t];
        for(uint32_t j=0;j<s->count;j++){model32 m=materialize(&s->data[j]);sum+=consume_model(&m);}
        done+=s->count;
        atomic_store_explicit(&q->tail,(t+1u)%SLAB_SLOTS,memory_order_release);
    }
    c->checksum=sum;c->cpu_s=thread_cpu_s()-a;return 0;
}

int main(int argc,char**argv){
    const char*mode=argc>1?argv[1]:"direct";
    uint64_t n=argc>2?strtoull(argv[2],0,10):50000000ull;
    uint32_t slab_size=argc>3?(uint32_t)strtoul(argv[3],0,10):384u;
    if(slab_size==0) return 2;
    uint64_t sum=0; double wall0=now_s();

    if(!strcmp(mode,"direct")){
        double c0=thread_cpu_s();
        for(uint64_t i=0;i<n;i++){package32 p=make_pkg(i);model32 m=materialize(&p);sum+=consume_model(&m);}
        double cpu=thread_cpu_s()-c0,wall=now_s()-wall0;
        printf("mode=direct messages=%llu wall_s=%.6f mps=%.3f cpu_ns_msg=%.3f checksum=%llu\n",
          (unsigned long long)n,wall,n/wall/1e6,cpu*1e9/n,(unsigned long long)sum);
        return 0;
    }

    pthread_t pt,ct;
    if(!strcmp(mode,"spsc")){
        size_t cap=1u<<15;
        spsc_q q={calloc(cap,sizeof(package32)),cap-1,0,0};
        if(!q.slots)return 2;
        ctx pc={.q=&q,.n=n},cc={.q=&q,.n=n};
        wall0=now_s();
        pthread_create(&ct,0,consumer_spsc,&cc);pthread_create(&pt,0,producer_spsc,&pc);
        pthread_join(pt,0);pthread_join(ct,0);
        double wall=now_s()-wall0;sum=cc.checksum;
        printf("mode=spsc messages=%llu wall_s=%.6f mps=%.3f producer_cpu_ns_msg=%.3f model_cpu_ns_msg=%.3f total_cpu_ns_msg=%.3f publications=%llu producer_spins=%llu consumer_spins=%llu checksum=%llu\n",
          (unsigned long long)n,wall,n/wall/1e6,pc.cpu_s*1e9/n,cc.cpu_s*1e9/n,(pc.cpu_s+cc.cpu_s)*1e9/n,
          (unsigned long long)pc.publications,(unsigned long long)pc.spins,(unsigned long long)cc.spins,(unsigned long long)sum);
        free(q.slots);return 0;
    }

    if(!strcmp(mode,"slab")){
        slab_q q={0}; q.slab_size=slab_size;
        for(uint32_t i=0;i<SLAB_SLOTS;i++){
            q.slots[i].data=aligned_alloc(64,((size_t)slab_size*sizeof(package32)+63u)&~63u);
            if(!q.slots[i].data) return 2;
        }
        ctx pc={.sq=&q,.n=n,.slab_size=slab_size},cc={.sq=&q,.n=n,.slab_size=slab_size};
        wall0=now_s();
        pthread_create(&ct,0,consumer_slab,&cc);pthread_create(&pt,0,producer_slab,&pc);
        pthread_join(pt,0);pthread_join(ct,0);
        double wall=now_s()-wall0;sum=cc.checksum;
        printf("mode=slab messages=%llu slab=%u wall_s=%.6f mps=%.3f producer_cpu_ns_msg=%.3f model_cpu_ns_msg=%.3f total_cpu_ns_msg=%.3f publications=%llu producer_spins=%llu consumer_spins=%llu checksum=%llu\n",
          (unsigned long long)n,slab_size,wall,n/wall/1e6,pc.cpu_s*1e9/n,cc.cpu_s*1e9/n,(pc.cpu_s+cc.cpu_s)*1e9/n,
          (unsigned long long)pc.publications,(unsigned long long)pc.spins,(unsigned long long)cc.spins,(unsigned long long)sum);
        for(uint32_t i=0;i<SLAB_SLOTS;i++) free(q.slots[i].data);
        return 0;
    }
    fprintf(stderr,"unknown mode: %s\n",mode);return 2;
}

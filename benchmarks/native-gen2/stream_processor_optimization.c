// DHMP protocol-side stream framing benchmark.
// Measures ONLY work DHMP needs: fixed-size framing, partial-frame carry, and span/latest publication.
// Payload bytes are opaque: no checksum, parsing, model materialization, or per-package inspection.
#define _POSIX_C_SOURCE 200809L
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
enum { FRAME=32 };
typedef struct { const uint8_t *ptr; uint32_t count; uint32_t bytes; } Span;
typedef struct { uint64_t logical; uint64_t publications; uint64_t copied_partial_bytes; uintptr_t guard; } Stats;
static volatile uintptr_t sink;
static double now_s(void){struct timespec t;clock_gettime(CLOCK_MONOTONIC_RAW,&t);return t.tv_sec+t.tv_nsec*1e-9;}
static void report(const char*m,uint64_t n,double s,Stats x){
 printf("mode=%s logical_messages=%llu logical_bytes=%llu publications=%llu partial_copy_bytes=%llu wall_s=%.9f logical_GBps=%.3f logical_mps=%.3f ns_msg=%.6f guard=%llu\n",
 m,(unsigned long long)n,(unsigned long long)(n*FRAME),(unsigned long long)x.publications,(unsigned long long)x.copied_partial_bytes,s,n*FRAME/s/1e9,n/s/1e6,s*1e9/n,(unsigned long long)x.guard);
}
static Stats sequential_spans(const uint8_t*p,size_t total,const size_t*chunks,size_t nc){
 size_t pos=0,phase=0;Stats x={0};Span out;uint8_t carry[FRAME];size_t ci=0;
 while(pos<total){size_t got=chunks[ci++%nc];if(got>total-pos)got=total-pos;const uint8_t*q=p+pos;pos+=got;
  if(phase){size_t take=FRAME-phase;if(take>got)take=got;memcpy(carry+phase,q,take);x.copied_partial_bytes+=take;phase+=take;q+=take;got-=take;if(phase==FRAME){out.ptr=carry;out.count=1;out.bytes=FRAME;x.logical++;x.publications++;x.guard^=(uintptr_t)out.ptr+out.count;phase=0;}}
  size_t count=got/FRAME;if(count){out.ptr=q;out.count=(uint32_t)count;out.bytes=(uint32_t)(count*FRAME);x.logical+=count;x.publications++;x.guard^=(uintptr_t)out.ptr+out.count;q+=count*FRAME;got-=count*FRAME;}
  if(got){memcpy(carry,q,got);x.copied_partial_bytes+=got;phase=got;}
 }
 if(phase){fprintf(stderr,"incomplete final frame\n");exit(3);}sink=x.guard;return x;
}
static Stats latest_spans(const uint8_t*p,size_t total,const size_t*chunks,size_t nc){
 size_t pos=0,phase=0;Stats x={0};uint8_t carry[FRAME];size_t ci=0;
 while(pos<total){size_t got=chunks[ci++%nc];if(got>total-pos)got=total-pos;const uint8_t*q=p+pos;pos+=got;const uint8_t*latest=0;
  if(phase){size_t take=FRAME-phase;if(take>got)take=got;memcpy(carry+phase,q,take);x.copied_partial_bytes+=take;phase+=take;q+=take;got-=take;if(phase==FRAME){latest=carry;x.logical++;phase=0;}}
  size_t count=got/FRAME;if(count){latest=q+(count-1)*FRAME;x.logical+=count;q+=count*FRAME;got-=count*FRAME;}
  if(got){memcpy(carry,q,got);x.copied_partial_bytes+=got;phase=got;}
  if(latest){x.publications++;x.guard^=(uintptr_t)latest;}
 }
 if(phase){fprintf(stderr,"incomplete final frame\n");exit(3);}sink=x.guard;return x;
}
static void run(const char*name,int latest,const uint8_t*p,size_t total,const size_t*chunks,size_t nc,uint64_t n){
 double t=now_s();Stats x=latest?latest_spans(p,total,chunks,nc):sequential_spans(p,total,chunks,nc);double e=now_s();
 if(x.logical!=n){fprintf(stderr,"logical count mismatch: %llu != %llu\n",(unsigned long long)x.logical,(unsigned long long)n);exit(4);}report(name,n,e-t,x);
}
int main(int argc,char**argv){
 uint64_t n=argc>1?strtoull(argv[1],0,10):10000000ULL;size_t total=(size_t)n*FRAME;
 uint8_t*p=aligned_alloc(64,(total+63)&~(size_t)63);if(!p)return 2;memset(p,0xA5,total);
 const size_t aligned4k[]={4096},aligned12k[]={12288},aligned64k[]={65536};
 const size_t fragmented[]={4093,8191,12287,16381,32749,65521};
 run("sequential-span-4k",0,p,total,aligned4k,1,n);
 run("sequential-span-12k",0,p,total,aligned12k,1,n);
 run("sequential-span-64k",0,p,total,aligned64k,1,n);
 run("sequential-span-fragmented",0,p,total,fragmented,6,n);
 run("latest-span-12k",1,p,total,aligned12k,1,n);
 run("latest-span-fragmented",1,p,total,fragmented,6,n);
 free(p);return 0;
}

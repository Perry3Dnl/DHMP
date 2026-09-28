// DHMP Stream Processor cost-ladder benchmark.
// Protocol side only. Each stage adds one responsibility so throughput loss is attributable.
#define _POSIX_C_SOURCE 200809L
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
static volatile uint64_t sink;
static double now_s(void){struct timespec t;clock_gettime(CLOCK_MONOTONIC_RAW,&t);return t.tv_sec+t.tv_nsec*1e-9;}
static inline uint64_t mix32(const uint8_t*p){uint64_t a,b;memcpy(&a,p,8);memcpy(&b,p+24,8);return a+b;}
static void fill(uint8_t*p,uint64_t n){for(uint64_t i=0;i<n;i++){uint64_t v[4]={i,1,2,i^0x9e3779b97f4a7c15ULL};memcpy(p+i*32,v,32);}}
static void report(const char*m,uint64_t n,double s,uint64_t touched,uint64_t pubs,uint64_t sum){
 printf("mode=%s logical_messages=%llu logical_bytes=%llu touched_messages=%llu publications=%llu wall_s=%.6f logical_GBps=%.3f logical_mps=%.3f ns_logical_msg=%.3f checksum=%llu\n",m,(unsigned long long)n,(unsigned long long)(n*32),(unsigned long long)touched,(unsigned long long)pubs,s,n*32.0/s/1e9,n/s/1e6,s*1e9/n,(unsigned long long)sum);
}
static void advance_only(const uint8_t*p,uint64_t n,size_t chunk){size_t total=(size_t)n*32,pos=0;uint64_t frames=0;double t=now_s();while(pos<total){size_t got=chunk;if(got>total-pos)got=total-pos;frames+=got/32;pos+=got;}double e=now_s();sink=frames+(uintptr_t)p;report("L0-advance-only",n,e-t,0,0,frames);}
static void latest_touch(const uint8_t*p,uint64_t n,size_t chunk){size_t total=(size_t)n*32,pos=0;uint64_t s=0,touched=0,pubs=0;double t=now_s();while(pos<total){size_t got=chunk;if(got>total-pos)got=total-pos;size_t complete=got/32;if(complete){const uint8_t*q=p+pos;s+=mix32(q+(complete-1)*32);touched++;pubs++;}pos+=got;}double e=now_s();sink=s;report("L1-latest-touch-last",n,e-t,touched,pubs,s);}
static void aligned(const uint8_t*p,uint64_t n){uint64_t s=0;double t=now_s();for(uint64_t i=0;i<n;i++)s+=mix32(p+i*32);double e=now_s();sink=s;report("L2-read-every-package",n,e-t,n,n,s);}
static void batch4(const uint8_t*p,uint64_t n){uint64_t a=0,b=0,c=0,d=0,i=0;double t=now_s();for(;i+4<=n;i+=4){const uint8_t*q=p+i*32;a+=mix32(q);b+=mix32(q+32);c+=mix32(q+64);d+=mix32(q+96);}uint64_t s=a+b+c+d;for(;i<n;i++)s+=mix32(p+i*32);double e=now_s();sink=s;report("L3-batch4-read-every",n,e-t,n,n,s);}
static void cursor_batch4(const uint8_t*p,uint64_t n,size_t chunk){size_t total=(size_t)n*32,pos=0,carry=0;uint8_t tail[32];uint64_t a=0,b=0,c=0,d=0,done=0;double t=now_s();while(pos<total){size_t got=chunk;if(got>total-pos)got=total-pos;const uint8_t*q=p+pos;pos+=got;if(carry){size_t need=32-carry,take=got<need?got:need;memcpy(tail+carry,q,take);carry+=take;q+=take;got-=take;if(carry==32){a+=mix32(tail);done++;carry=0;}}while(got>=128){a+=mix32(q);b+=mix32(q+32);c+=mix32(q+64);d+=mix32(q+96);q+=128;got-=128;done+=4;}while(got>=32){a+=mix32(q);q+=32;got-=32;done++;}if(got){memcpy(tail,q,got);carry=got;}}double e=now_s();if(done!=n||carry){fprintf(stderr,"validation failed done=%llu carry=%zu\n",(unsigned long long)done,carry);exit(3);}uint64_t s=a+b+c+d;sink=s;char name[64];snprintf(name,sizeof name,"L4-stream-batch4-%zu",chunk);report(name,n,e-t,n,n,s);}
int main(int argc,char**argv){uint64_t n=argc>1?strtoull(argv[1],0,10):50000000ULL;size_t bytes=(size_t)n*32;uint8_t*p=aligned_alloc(64,(bytes+63)&~(size_t)63);if(!p)return 2;fill(p,n);advance_only(p,n,12288);latest_touch(p,n,12288);aligned(p,n);batch4(p,n);cursor_batch4(p,n,4096);cursor_batch4(p,n,12288);cursor_batch4(p,n,65536);free(p);return 0;}

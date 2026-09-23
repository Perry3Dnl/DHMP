#define _GNU_SOURCE
#include <sched.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <immintrin.h>
#define ITERS 50000000ULL
#define CAP (12288+64)
static inline double cpu_now(void){struct timespec t;clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t);return(double)t.tv_sec+(double)t.tv_nsec/1e9;}
static void pin0(void){cpu_set_t s;CPU_ZERO(&s);CPU_SET(0,&s);sched_setaffinity(0,sizeof(s),&s);}
__attribute__((noinline)) static void generic(uint8_t*d,const uint8_t*s,size_t total,uint64_t*sum){size_t complete=total>>5,tail=total&31u;if(tail){const uint8_t*src=s+(complete<<5);memmove(d,src,tail);*sum+=d[0]+d[tail-1];}}
__attribute__((noinline)) static void fixed_if(uint8_t*d,const uint8_t*s,size_t total,uint64_t*sum){size_t complete=total>>5,tail=total&31u;if(tail){const uint8_t*src=s+(complete<<5);__m256i v=_mm256_loadu_si256((const __m256i*)src);_mm256_store_si256((__m256i*)d,v);*sum+=d[0]+d[tail-1];}}
__attribute__((noinline)) static void fixed_always(uint8_t*d,const uint8_t*s,size_t total,uint64_t*sum){size_t complete=total>>5,tail=total&31u;const uint8_t*src=s+(complete<<5);__m256i v=_mm256_loadu_si256((const __m256i*)src);_mm256_store_si256((__m256i*)d,v);if(tail)*sum+=d[0]+d[tail-1];}
int main(int argc,char**argv){if(argc<2)return 2;int mode=!strcmp(argv[1],"fixed_if")?1:!strcmp(argv[1],"fixed_always")?2:0;pin0();uint8_t*s=NULL,*d=NULL;posix_memalign((void**)&s,64,CAP);posix_memalign((void**)&d,64,64);for(size_t i=0;i<CAP;i++)s[i]=(uint8_t)(i*17+3);memset(d,0,64);static const size_t totals[16]={12288,12287,12273,12257,12001,8191,8177,8161,4095,4081,4065,2033,2017,1009,993,511};uint64_t sum=0;double t0=cpu_now();for(uint64_t i=0;i<ITERS;i++){size_t total=totals[i&15u];if(mode==1)fixed_if(d,s,total,&sum);else if(mode==2)fixed_always(d,s,total,&sum);else generic(d,s,total,&sum);}double dt=cpu_now()-t0;printf("%s ns_batch=%.6f batches_s=%.3f sum=%llu\n",mode==1?"fixed_if":mode==2?"fixed_always":"generic",dt*1e9/ITERS,ITERS/dt,(unsigned long long)sum);free(s);free(d);}

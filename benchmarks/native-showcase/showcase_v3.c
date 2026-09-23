#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <immintrin.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <openssl/err.h>
#include <openssl/ssl.h>
#include <pthread.h>
#include <signal.h>
#include <sched.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>

#define FRAME_BYTES 32u
#define SLAB_BYTES 12288u
#define SEND_BATCH_BYTES 262144u
#define HOLD_NS 10000ull
#define DIRTY_BIT 4u
#define IDX_MASK 3u
#define RING_SLOTS 3u
#define UDP_BATCH 64
#define MAX_RECORD_BYTES 64u
#define MAGIC 0x9e3779b97f4a7c15ULL

typedef enum { P_DHMP, P_DHMPS, P_RAW, P_LEN4, P_WS, P_HTTP, P_UDP } path_t;

typedef struct __attribute__((aligned(64))) {
    _Atomic uint32_t middle_token;
    unsigned char pad0[60];
    unsigned char slot[RING_SLOTS][FRAME_BYTES] __attribute__((aligned(64)));
} triple_t;

typedef struct {
    path_t path;
    int tx_fd, rx_fd;
    SSL *tx_ssl, *rx_ssl;
    double seconds;
    _Atomic int start, stop;
    triple_t triple;
    uint64_t input_frames;
    uint64_t recv_ops;
    uint64_t producer_pubs;
    uint64_t overwritten;
    uint64_t consumer_pubs;
    uint64_t consumer_retries;
    uint64_t validation_errors;
    uint64_t framing_errors;
    double wall_s;
    double receiver_cpu_s;
} bench_t;

static inline uint64_t mono_ns(void){ struct timespec t; clock_gettime(CLOCK_MONOTONIC_RAW,&t); return (uint64_t)t.tv_sec*1000000000ull+t.tv_nsec; }
static inline double thread_cpu_s(void){ struct timespec t; clock_gettime(CLOCK_THREAD_CPUTIME_ID,&t); return (double)t.tv_sec+(double)t.tv_nsec/1e9; }
static void pin_cpu(int cpu){ cpu_set_t set; CPU_ZERO(&set); CPU_SET(cpu,&set); pthread_setaffinity_np(pthread_self(),sizeof(set),&set); }
static inline void cpu_relax(void){ _mm_pause(); }

static inline void fill_payload(unsigned char *p, uint64_t n){
    uint64_t *q=(uint64_t*)p;
    q[0]=n; q[1]=n^MAGIC; q[2]=~n; q[3]=n+0x12345678ULL;
}
static inline int valid_payload(const unsigned char *p){
    const uint64_t *q=(const uint64_t*)p; uint64_t a=q[0];
    return q[1]==(a^MAGIC) && q[2]==~a && q[3]==a+0x12345678ULL;
}

static inline void triple_init(triple_t *t){
    memset(t,0,sizeof(*t));
    fill_payload(t->slot[0],0); fill_payload(t->slot[1],0); fill_payload(t->slot[2],0);
    atomic_store_explicit(&t->middle_token,1u,memory_order_relaxed);
}

/* Producer owns BACK. One acq_rel RMW publishes completed bytes and safely acquires
   whichever slot ownership the consumer returned through MIDDLE. */
static inline unsigned publish32(triple_t *t, unsigned back, const unsigned char *src){
    __m256i v=_mm256_loadu_si256((const __m256i*)src);
    _mm256_storeu_si256((__m256i*)t->slot[back],v);
    uint32_t old=atomic_exchange_explicit(&t->middle_token,(uint32_t)back|DIRTY_BIT,memory_order_acq_rel);
    return (unsigned)(old & IDX_MASK);
}

static void *consumer_main(void *arg){
    bench_t *b=(bench_t*)arg; pin_cpu(2);
    while(!atomic_load_explicit(&b->start,memory_order_acquire) && !atomic_load_explicit(&b->stop,memory_order_acquire)) cpu_relax();
    unsigned front=0; uint64_t pubs=0,retries=0,errors=0;
    while(!atomic_load_explicit(&b->stop,memory_order_acquire)){
        uint32_t tok=atomic_load_explicit(&b->triple.middle_token,memory_order_relaxed);
        if(!(tok & DIRTY_BIT)){ cpu_relax(); continue; }
        uint32_t desired=(uint32_t)front;
        if(!atomic_compare_exchange_weak_explicit(&b->triple.middle_token,&tok,desired,memory_order_acq_rel,memory_order_relaxed)){
            retries++; cpu_relax(); continue;
        }
        front=(unsigned)(tok & IDX_MASK);
        if(!valid_payload(b->triple.slot[front])) errors++;
        pubs++;
        uint64_t until=mono_ns()+HOLD_NS;
        while(mono_ns()<until) cpu_relax();
    }
    b->consumer_pubs=pubs; b->consumer_retries=retries; b->validation_errors=errors;
    return NULL;
}

static ssize_t stream_read(bench_t *b, void *buf, size_t len){
    if(b->path==P_DHMPS){
        int n=SSL_read(b->rx_ssl,buf,(int)len);
        if(n>0) return n;
        int e=SSL_get_error(b->rx_ssl,n);
        if(e==SSL_ERROR_WANT_READ || e==SSL_ERROR_WANT_WRITE){ errno=EINTR; return -1; }
        return 0;
    }
    return recv(b->rx_fd,buf,len,0);
}
static ssize_t stream_write(bench_t *b, const void *buf, size_t len){
    if(b->path==P_DHMPS){
        int n=SSL_write(b->tx_ssl,buf,(int)len);
        if(n>0) return n;
        int e=SSL_get_error(b->tx_ssl,n);
        if(e==SSL_ERROR_WANT_READ || e==SSL_ERROR_WANT_WRITE){ errno=EINTR; return -1; }
        return 0;
    }
    return send(b->tx_fd,buf,len,MSG_NOSIGNAL);
}

static size_t record_bytes(path_t p){
    switch(p){
        case P_LEN4: return 4+FRAME_BYTES;
        case P_WS: return 2+FRAME_BYTES;
        case P_HTTP: return 4+FRAME_BYTES+2;
        default: return FRAME_BYTES;
    }
}
static size_t payload_offset(path_t p){
    switch(p){ case P_LEN4: return 4; case P_WS: return 2; case P_HTTP: return 4; default: return 0; }
}

static int validate_record(path_t p, const unsigned char *r){
    if(p==P_LEN4){ uint32_t n; memcpy(&n,r,4); return ntohl(n)==FRAME_BYTES; }
    if(p==P_WS){ return r[0]==0x82 && r[1]==FRAME_BYTES; }
    if(p==P_HTTP){ return r[0]=='2'&&r[1]=='0'&&r[2]=='\r'&&r[3]=='\n'&&r[36]=='\r'&&r[37]=='\n'; }
    return 1;
}

static void *receiver_stream_main(void *arg){
    bench_t *b=(bench_t*)arg; pin_cpu(1);
    const int optimized=(b->path==P_DHMP || b->path==P_DHMPS);
    const size_t rec=record_bytes(b->path), poff=payload_offset(b->path);
    const size_t cap=SLAB_BYTES + MAX_RECORD_BYTES;
    unsigned char *work=NULL;
    if(posix_memalign((void**)&work,64,cap)!=0){ atomic_store(&b->stop,1); return NULL; }
    memset(work,0,cap);
    size_t carry=0; unsigned back=2;
    while(!atomic_load_explicit(&b->start,memory_order_acquire) && !atomic_load_explicit(&b->stop,memory_order_acquire)) cpu_relax();
    uint64_t wall0=mono_ns(); double cpu0=thread_cpu_s();
    for(;;){
        if(atomic_load_explicit(&b->stop,memory_order_acquire)) break;
        size_t want=SLAB_BYTES-carry;
        ssize_t n=stream_read(b,work+carry,want);
        if(n==0) break;
        if(n<0){ if(errno==EINTR) continue; break; }
        b->recv_ops++;
        size_t total=carry+(size_t)n;
        size_t complete,tail;
        if(optimized){
            complete=total>>5;
            tail=total&31u;
        } else {
            complete=total/rec;
            tail=total-complete*rec;
        }
        if(complete){
            if(!optimized && b->path!=P_RAW){
                const unsigned char *r=work;
                for(size_t i=0;i<complete;i++,r+=rec) if(!validate_record(b->path,r)) b->framing_errors++;
            }
            const unsigned char *latest;
            if(optimized || b->path==P_RAW) latest=work+(complete-1)*FRAME_BYTES;
            else latest=work+(complete-1)*rec+poff;
            back=publish32(&b->triple,back,latest);
            b->producer_pubs++;
            b->input_frames+=complete;
            if(complete>1) b->overwritten+=complete-1;
        }
        if(tail){
            const unsigned char *src=work+complete*(optimized?FRAME_BYTES:rec);
            if(optimized){
                __m256i v=_mm256_loadu_si256((const __m256i*)src);
                _mm256_store_si256((__m256i*)work,v);
            } else {
                memmove(work,src,tail);
            }
        }
        carry=tail;
    }
    b->receiver_cpu_s=thread_cpu_s()-cpu0;
    b->wall_s=(double)(mono_ns()-wall0)/1e9;
    atomic_store_explicit(&b->stop,1,memory_order_release);
    if(b->path==P_DHMPS) SSL_shutdown(b->rx_ssl); else shutdown(b->rx_fd,SHUT_RDWR);
    free(work); return NULL;
}

static unsigned char *build_stream_batch(path_t p,size_t *out_bytes){
    size_t rec=record_bytes(p);
    size_t count=(p==P_DHMP || p==P_DHMPS || p==P_RAW)?(SEND_BATCH_BYTES/FRAME_BYTES):(SEND_BATCH_BYTES/rec);
    size_t bytes=count*rec;
    unsigned char *buf=NULL; if(posix_memalign((void**)&buf,64,bytes+64)!=0) return NULL;
    memset(buf,0,bytes+64);
    for(size_t i=0;i<count;i++){
        unsigned char *r=buf+i*rec; unsigned char *payload=r+payload_offset(p);
        if(p==P_LEN4){ uint32_t n=htonl(FRAME_BYTES); memcpy(r,&n,4); }
        else if(p==P_WS){ r[0]=0x82; r[1]=FRAME_BYTES; }
        else if(p==P_HTTP){ r[0]='2'; r[1]='0'; r[2]='\r'; r[3]='\n'; r[36]='\r'; r[37]='\n'; }
        fill_payload(payload,(uint64_t)(i+1));
    }
    *out_bytes=bytes; return buf;
}

static void *sender_stream_main(void *arg){
    bench_t *b=(bench_t*)arg; pin_cpu(0);
    size_t bytes=0; unsigned char *buf=build_stream_batch(b->path,&bytes);
    if(!buf){ atomic_store(&b->stop,1); return NULL; }
    while(!atomic_load_explicit(&b->start,memory_order_acquire) && !atomic_load_explicit(&b->stop,memory_order_acquire)) cpu_relax();
    while(!atomic_load_explicit(&b->stop,memory_order_acquire)){
        size_t off=0;
        while(off<bytes && !atomic_load_explicit(&b->stop,memory_order_acquire)){
            ssize_t n=stream_write(b,buf+off,bytes-off);
            if(n>0){ off+=(size_t)n; continue; }
            if(n<0 && errno==EINTR) continue;
            atomic_store_explicit(&b->stop,1,memory_order_release); break;
        }
    }
    free(buf); return NULL;
}

static void *receiver_udp_main(void *arg){
    bench_t *b=(bench_t*)arg; pin_cpu(1);
    struct mmsghdr msgs[UDP_BATCH]; struct iovec iov[UDP_BATCH];
    unsigned char *data=NULL; posix_memalign((void**)&data,64,UDP_BATCH*FRAME_BYTES);
    memset(msgs,0,sizeof(msgs));
    for(int i=0;i<UDP_BATCH;i++){ iov[i].iov_base=data+i*FRAME_BYTES; iov[i].iov_len=FRAME_BYTES; msgs[i].msg_hdr.msg_iov=&iov[i]; msgs[i].msg_hdr.msg_iovlen=1; }
    unsigned back=2;
    while(!atomic_load_explicit(&b->start,memory_order_acquire) && !atomic_load_explicit(&b->stop,memory_order_acquire)) cpu_relax();
    uint64_t wall0=mono_ns(); double cpu0=thread_cpu_s();
    while(!atomic_load_explicit(&b->stop,memory_order_acquire)){
        int n=recvmmsg(b->rx_fd,msgs,UDP_BATCH,MSG_WAITFORONE,NULL);
        if(n<0){ if(errno==EINTR) continue; break; }
        if(n==0) continue;
        b->recv_ops++;
        int valid=0;
        for(int i=0;i<n;i++) if(msgs[i].msg_len==FRAME_BYTES) valid++; else b->framing_errors++;
        if(valid){
            int last=n-1; while(last>=0 && msgs[last].msg_len!=FRAME_BYTES) last--;
            if(last>=0){ back=publish32(&b->triple,back,data+last*FRAME_BYTES); b->producer_pubs++; }
            b->input_frames+=(uint64_t)valid; if(valid>1) b->overwritten+=(uint64_t)valid-1;
        }
    }
    b->receiver_cpu_s=thread_cpu_s()-cpu0; b->wall_s=(double)(mono_ns()-wall0)/1e9;
    atomic_store_explicit(&b->stop,1,memory_order_release); shutdown(b->rx_fd,SHUT_RDWR); free(data); return NULL;
}

static void *sender_udp_main(void *arg){
    bench_t *b=(bench_t*)arg; pin_cpu(0);
    struct mmsghdr msgs[UDP_BATCH]; struct iovec iov[UDP_BATCH];
    unsigned char *data=NULL; posix_memalign((void**)&data,64,UDP_BATCH*FRAME_BYTES); memset(msgs,0,sizeof(msgs));
    for(int i=0;i<UDP_BATCH;i++){ fill_payload(data+i*FRAME_BYTES,(uint64_t)(i+1)); iov[i].iov_base=data+i*FRAME_BYTES; iov[i].iov_len=FRAME_BYTES; msgs[i].msg_hdr.msg_iov=&iov[i]; msgs[i].msg_hdr.msg_iovlen=1; }
    while(!atomic_load_explicit(&b->start,memory_order_acquire) && !atomic_load_explicit(&b->stop,memory_order_acquire)) cpu_relax();
    while(!atomic_load_explicit(&b->stop,memory_order_acquire)){
        int n=sendmmsg(b->tx_fd,msgs,UDP_BATCH,0);
        if(n<0 && errno==EINTR) continue;
        if(n<=0){ atomic_store(&b->stop,1); break; }
    }
    free(data); return NULL;
}

static int make_tcp_pair(int *tx,int *rx){
    int l=socket(AF_INET,SOCK_STREAM,0); if(l<0) return -1; int one=1; setsockopt(l,SOL_SOCKET,SO_REUSEADDR,&one,sizeof(one));
    struct sockaddr_in a={0}; a.sin_family=AF_INET; a.sin_addr.s_addr=htonl(INADDR_LOOPBACK); a.sin_port=0;
    if(bind(l,(struct sockaddr*)&a,sizeof(a))||listen(l,1)){ close(l); return -1; }
    socklen_t alen=sizeof(a); getsockname(l,(struct sockaddr*)&a,&alen);
    int c=socket(AF_INET,SOCK_STREAM,0); if(c<0){close(l);return -1;} setsockopt(c,IPPROTO_TCP,TCP_NODELAY,&one,sizeof(one));
    if(connect(c,(struct sockaddr*)&a,sizeof(a))){close(c);close(l);return -1;}
    int s=accept(l,NULL,NULL); close(l); if(s<0){close(c);return -1;} setsockopt(s,IPPROTO_TCP,TCP_NODELAY,&one,sizeof(one));
    int sz=4*1024*1024; setsockopt(c,SOL_SOCKET,SO_SNDBUF,&sz,sizeof(sz)); setsockopt(s,SOL_SOCKET,SO_RCVBUF,&sz,sizeof(sz));
    *tx=c; *rx=s; return 0;
}

static int make_udp_pair(int *tx,int *rx){
    int r=socket(AF_INET,SOCK_DGRAM,0), s=socket(AF_INET,SOCK_DGRAM,0); if(r<0||s<0) return -1;
    struct sockaddr_in a={0}; a.sin_family=AF_INET; a.sin_addr.s_addr=htonl(INADDR_LOOPBACK); a.sin_port=0;
    if(bind(r,(struct sockaddr*)&a,sizeof(a))) return -1;
    struct timeval tv={0,100000};
    setsockopt(r,SOL_SOCKET,SO_RCVTIMEO,&tv,sizeof(tv));
    socklen_t alen=sizeof(a);
    getsockname(r,(struct sockaddr*)&a,&alen);
    if(connect(s,(struct sockaddr*)&a,sizeof(a))) return -1;
    *tx=s; *rx=r; return 0;
}

typedef struct { SSL *ssl; int rc; } hs_arg_t;
static void *accept_ssl_thread(void *arg){ hs_arg_t *h=(hs_arg_t*)arg; h->rc=SSL_accept(h->ssl); return NULL; }
static int setup_tls(bench_t *b,const char *cert,const char *key,SSL_CTX **out_sctx,SSL_CTX **out_cctx){
    SSL_CTX *sctx=SSL_CTX_new(TLS_server_method()), *cctx=SSL_CTX_new(TLS_client_method()); if(!sctx||!cctx) return -1;
    SSL_CTX_set_min_proto_version(sctx,TLS1_3_VERSION); SSL_CTX_set_max_proto_version(sctx,TLS1_3_VERSION);
    SSL_CTX_set_min_proto_version(cctx,TLS1_3_VERSION); SSL_CTX_set_max_proto_version(cctx,TLS1_3_VERSION);
    SSL_CTX_set_verify(cctx,SSL_VERIFY_NONE,NULL);
    if(SSL_CTX_use_certificate_file(sctx,cert,SSL_FILETYPE_PEM)!=1 || SSL_CTX_use_PrivateKey_file(sctx,key,SSL_FILETYPE_PEM)!=1) return -1;
    b->rx_ssl=SSL_new(sctx); b->tx_ssl=SSL_new(cctx); if(!b->rx_ssl||!b->tx_ssl) return -1;
    SSL_set_fd(b->rx_ssl,b->rx_fd); SSL_set_fd(b->tx_ssl,b->tx_fd);
    hs_arg_t h={b->rx_ssl,0}; pthread_t th; pthread_create(&th,NULL,accept_ssl_thread,&h); int cr=SSL_connect(b->tx_ssl); pthread_join(th,NULL);
    if(cr!=1 || h.rc!=1) return -1;
    *out_sctx=sctx; *out_cctx=cctx; return 0;
}

static path_t parse_path(const char *s){
    if(!strcmp(s,"dhmp")) return P_DHMP;
    if(!strcmp(s,"dhmps")) return P_DHMPS;
    if(!strcmp(s,"raw")) return P_RAW;
    if(!strcmp(s,"len4")) return P_LEN4;
    if(!strcmp(s,"ws")) return P_WS;
    if(!strcmp(s,"http")) return P_HTTP;
    return P_UDP;
}
static const char *path_name(path_t p){ const char*n[]={"dhmp","dhmps","raw","len4","ws","http","udp"}; return n[p]; }

int main(int argc,char **argv){
    signal(SIGPIPE,SIG_IGN);
    if(argc<3){ fprintf(stderr,"usage: %s path seconds [cert key]\n",argv[0]); return 2; }
    SSL_library_init(); SSL_load_error_strings();
    bench_t b; memset(&b,0,sizeof(b)); b.path=parse_path(argv[1]); b.seconds=strtod(argv[2],NULL); triple_init(&b.triple);
    if(b.path==P_UDP){ if(make_udp_pair(&b.tx_fd,&b.rx_fd)){perror("udp");return 1;} }
    else { if(make_tcp_pair(&b.tx_fd,&b.rx_fd)){perror("tcp");return 1;} }
    SSL_CTX *sctx=NULL,*cctx=NULL;
    if(b.path==P_DHMPS){ if(argc<5){fprintf(stderr,"dhmps requires cert key\n");return 2;} if(setup_tls(&b,argv[3],argv[4],&sctx,&cctx)){ERR_print_errors_fp(stderr);return 1;} }
    pthread_t tr,ts,tc;
    pthread_create(&tc,NULL,consumer_main,&b);
    if(b.path==P_UDP){ pthread_create(&tr,NULL,receiver_udp_main,&b); pthread_create(&ts,NULL,sender_udp_main,&b); }
    else { pthread_create(&tr,NULL,receiver_stream_main,&b); pthread_create(&ts,NULL,sender_stream_main,&b); }
    usleep(20000);
    atomic_store_explicit(&b.start,1,memory_order_release);
    struct timespec req={(time_t)b.seconds,(long)((b.seconds-(time_t)b.seconds)*1e9)};
    while(nanosleep(&req,&req)!=0 && errno==EINTR){}
    atomic_store_explicit(&b.stop,1,memory_order_release);
    shutdown(b.tx_fd,SHUT_WR);
    if(b.path==P_UDP) shutdown(b.rx_fd,SHUT_RDWR);
    pthread_join(tr,NULL); pthread_join(ts,NULL); pthread_join(tc,NULL);
    if(b.path==P_DHMPS){ SSL_free(b.rx_ssl); SSL_free(b.tx_ssl); SSL_CTX_free(sctx); SSL_CTX_free(cctx); }
    close(b.tx_fd); close(b.rx_fd);
    double fps=b.wall_s>0?(double)b.input_frames/b.wall_s:0.0;
    double pubs=b.wall_s>0?(double)b.consumer_pubs/b.wall_s:0.0;
    double gbps=fps*FRAME_BYTES/1e9;
    double ops=b.wall_s>0?(double)b.recv_ops/b.wall_s:0.0;
    double cpu_ns=b.input_frames?b.receiver_cpu_s*1e9/(double)b.input_frames:0.0;
    double skip=b.input_frames?100.0*(double)b.overwritten/(double)b.input_frames:0.0;
    printf("path=%s seconds=%.6f input_fps=%.3f published_fps=%.3f payload_GBps=%.6f recv_ops_s=%.3f receiver_cpu_ns_frame=%.6f producer_pubs=%llu consumer_retries=%llu skipped_pct=%.6f validation_errors=%llu framing_errors=%llu\n",
      path_name(b.path),b.wall_s,fps,pubs,gbps,ops,cpu_ns,(unsigned long long)b.producer_pubs,(unsigned long long)b.consumer_retries,skip,(unsigned long long)b.validation_errors,(unsigned long long)b.framing_errors);
    return (b.validation_errors||b.framing_errors)?3:0;
}

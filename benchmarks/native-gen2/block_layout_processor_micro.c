#define main dhmp_block_original_main
#include "../native-showcase/negotiated_block_layout_ab.c"
#undef main

int main(int argc,char**argv){
    if(argc<2)return 2;
    mode_t2 m;
    if(!strcmp(argv[1],"aos_scalar"))m=AOS_SCALAR;
    else if(!strcmp(argv[1],"aos_avx2"))m=AOS_AVX2;
    else if(!strcmp(argv[1],"soa_scalar"))m=SOA_SCALAR;
    else m=SOA_AVX2;

    void *in=NULL;
    out_t *out=NULL;
    posix_memalign(&in,64,BLOCK_BYTES);
    posix_memalign((void**)&out,64,BLOCK_BYTES);

    if(m==SOA_SCALAR||m==SOA_AVX2){
        soa_block_t*s=(soa_block_t*)in;
        for(unsigned i=0;i<NMSG;i++){
            aos_t a=make_input((uint64_t)i+1);
            s->f[0][i]=a.x;s->f[1][i]=a.y;s->f[2][i]=a.z;s->f[3][i]=a.vx;
            s->f[4][i]=a.vy;s->f[5][i]=a.vz;s->f[6][i]=a.temp;s->f[7][i]=a.scale;
        }
    }else{
        aos_t*a=(aos_t*)in;
        for(unsigned i=0;i<NMSG;i++)a[i]=make_input((uint64_t)i+1);
    }

    const uint64_t loops=200000ull;
    volatile double checksum=0;
    double c0=cpu_now();
    for(uint64_t k=0;k<loops;k++){
        if(m==AOS_SCALAR)aos_scalar((aos_t*)in,out);
        else if(m==AOS_AVX2)aos_avx2((aos_t*)in,out);
        else if(m==SOA_SCALAR)soa_scalar((soa_block_t*)in,out);
        else soa_avx2((soa_block_t*)in,out);
        checksum += out[k%NMSG].energy;
    }
    double dt=cpu_now()-c0;
    double frames=(double)loops*NMSG;
    printf("mode=%s cpu_ns_frame=%.6f fps_cpu=%.3f checksum=%.6f errors=%d\n",
      name(m),dt*1e9/frames,frames/dt,(double)checksum,valid_out(out[0],1)?0:1);
    free(in);free(out);return 0;
}

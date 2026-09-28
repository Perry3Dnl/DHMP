using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

[StructLayout(LayoutKind.Sequential,Pack=1)] struct P32{public ulong A,B,C,D;}
[StructLayout(LayoutKind.Sequential,Pack=1)] struct M32{public ulong A,B,C,D;}

static class Program {
 static ulong sink;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static M32 Cast(in P32 p)=>Unsafe.As<P32,M32>(ref Unsafe.AsRef(in p));
 static P32[] Data(int n){var a=new P32[n];for(int i=0;i<n;i++)a[i]=new P32{A=(ulong)i,B=1,C=2,D=(ulong)i^0x9e3779b97f4a7c15UL};return a;}
 static void Out(string stage,int n,long ticks,ulong sum){double s=(double)ticks/Stopwatch.Frequency;Console.WriteLine($"stage={stage} messages={n} bytes={n*32L} wall_s={s:F6} GBps={n*32.0/s/1e9:F3} mps={n/s/1e6:F3} ns_msg={s*1e9/n:F3} checksum={sum}");}
 static void Baseline(P32[] a){ulong s=0;long t=Stopwatch.GetTimestamp();for(int i=0;i<a.Length;i++)s+=a[i].A+a[i].D;long e=Stopwatch.GetTimestamp();sink=s;Out("baseline-read",a.Length,e-t,s);}
 static void DirectCast(P32[] a){ulong s=0;long t=Stopwatch.GetTimestamp();for(int i=0;i<a.Length;i++){var m=Cast(in a[i]);s+=m.A+m.D;}long e=Stopwatch.GetTimestamp();sink=s;Out("direct-cast",a.Length,e-t,s);}

 // Real bounded producer -> slab pool -> N workers pipeline.
 // Producer copies complete fixed packages into a free slab and publishes once per slab.
 // Workers claim published slabs, materialize models, checksum, then release the slab.
 static void Pipeline(P32[] input,int workers){
   const int slabSize=512, slots=64;
   var slabs=new P32[slots][]; for(int i=0;i<slots;i++)slabs[i]=new P32[slabSize];
   var counts=new int[slots]; var state=new int[slots]; // 0 free, 1 ready
   var sums=new ulong[workers]; var consumed=new long[workers];
   long nextClaim=-1, published=0, producerStallTicks=0, producerStalls=0, maxDepth=0;
   int done=0; using var gate=new ManualResetEventSlim(false); var ts=new Thread[workers];

   for(int w=0;w<workers;w++){int id=w;ts[w]=new Thread(()=>{
     gate.Wait(); ulong local=0; long localCount=0;
     while(true){
       long ticket=Interlocked.Increment(ref nextClaim);
       if(ticket>=Volatile.Read(ref published)){
         // Ticket may be ahead of publication: wait for that exact slab sequence unless producer is finished.
         while(ticket>=Volatile.Read(ref published)){
           if(Volatile.Read(ref done)!=0){sums[id]=local;consumed[id]=localCount;return;}
           Thread.SpinWait(1);
         }
       }
       int slot=(int)(ticket%slots);
       while(Volatile.Read(ref state[slot])!=1)Thread.SpinWait(1);
       int count=counts[slot]; var slab=slabs[slot];
       for(int j=0;j<count;j++){var m=Cast(in slab[j]);local+=m.A+m.D;}
       localCount+=count;
       Volatile.Write(ref state[slot],0);
     }
   }){IsBackground=true};ts[w].Start();}

   long start=Stopwatch.GetTimestamp(); gate.Set();
   int p=0; long seq=0;
   while(p<input.Length){
     int slot=(int)(seq%slots);
     if(Volatile.Read(ref state[slot])!=0){
       producerStalls++; long st=Stopwatch.GetTimestamp();
       while(Volatile.Read(ref state[slot])!=0)Thread.SpinWait(1);
       producerStallTicks+=Stopwatch.GetTimestamp()-st;
     }
     int count=Math.Min(slabSize,input.Length-p);
     input.AsSpan(p,count).CopyTo(slabs[slot]);
     counts[slot]=count;
     Volatile.Write(ref state[slot],1);
     seq++; Volatile.Write(ref published,seq);
     long claimed=Math.Min(Volatile.Read(ref nextClaim)+1,seq);
     long depth=seq-claimed; if(depth>maxDepth)maxDepth=depth;
     p+=count;
   }
   // Wait until all published slots have been released before terminating workers.
   bool busy; do{busy=false;for(int i=0;i<slots;i++)if(Volatile.Read(ref state[i])!=0){busy=true;break;}if(busy)Thread.SpinWait(1);}while(busy);
   Volatile.Write(ref done,1); for(int w=0;w<workers;w++)ts[w].Join();
   long end=Stopwatch.GetTimestamp(); ulong sum=0;long totalConsumed=0;for(int w=0;w<workers;w++){sum+=sums[w];totalConsumed+=consumed[w];}
   sink=sum; double sec=(double)(end-start)/Stopwatch.Frequency, stallSec=(double)producerStallTicks/Stopwatch.Frequency;
   Console.WriteLine($"stage=pipeline-{workers}w messages={input.Length} bytes={input.Length*32L} wall_s={sec:F6} GBps={input.Length*32.0/sec/1e9:F3} mps={input.Length/sec/1e6:F3} ns_msg={sec*1e9/input.Length:F3} producer_publish_mps={input.Length/sec/1e6:F3} producer_stalls={producerStalls} producer_stall_ms={stallSec*1e3:F3} producer_stall_pct={stallSec/sec*100:F3} max_depth_slabs={maxDepth} slab_slots={slots} slab_size={slabSize} consumed={totalConsumed} checksum={sum}");
 }
 static void Warm(P32[] a){int n=Math.Min(10000,a.Length);ulong s=0;for(int i=0;i<n;i++){var m=Cast(in a[i]);s+=m.A+m.D;}sink=s;}
 static void Main(string[] args){string stage=args[0];int n=int.Parse(args[1]);var a=Data(n);Warm(a);switch(stage){case "baseline-read":Baseline(a);break;case "direct-cast":DirectCast(a);break;case "pipeline-1w":Pipeline(a,1);break;case "pipeline-2w":Pipeline(a,2);break;case "pipeline-4w":Pipeline(a,4);break;default:throw new ArgumentException(stage);}}
}
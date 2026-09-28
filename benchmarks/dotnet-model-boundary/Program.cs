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

 // One producer, one independent SPSC slab ring per worker.
 // No shared claim counter, no MPMC state array, no per-message synchronization.
 static void SpscPipeline(P32[] input,int workers){
   const int slabSize=512, ringSlots=32;
   var rings=new P32[workers][][];
   var counts=new int[workers][];
   var published=new long[workers];
   var consumedSeq=new long[workers];
   var sums=new ulong[workers];
   var consumedMsgs=new long[workers];
   var threads=new Thread[workers];
   using var gate=new ManualResetEventSlim(false);
   for(int w=0;w<workers;w++){
     rings[w]=new P32[ringSlots][]; counts[w]=new int[ringSlots];
     for(int s=0;s<ringSlots;s++)rings[w][s]=new P32[slabSize];
     int id=w;
     threads[w]=new Thread(()=>{
       gate.Wait(); ulong local=0; long msgs=0, seq=0;
       while(true){
         long pub=Volatile.Read(ref published[id]);
         if(seq>=pub){if(pub<0)break;Thread.SpinWait(4);continue;}
         int slot=(int)(seq%ringSlots);int count=counts[id][slot];var slab=rings[id][slot];
         for(int j=0;j<count;j++){var m=Cast(in slab[j]);local+=m.A+m.D;}
         msgs+=count;seq++;Volatile.Write(ref consumedSeq[id],seq);
       }
       sums[id]=local;consumedMsgs[id]=msgs;
     }){IsBackground=true};
     threads[w].Start();
   }

   long[] produced=new long[workers], stalls=new long[workers], stallTicks=new long[workers], maxDepth=new long[workers];
   long start=Stopwatch.GetTimestamp();gate.Set();
   int p=0,wid=0;
   while(p<input.Length){
     long seq=produced[wid];
     if(seq-Volatile.Read(ref consumedSeq[wid])>=ringSlots){
       stalls[wid]++;long st=Stopwatch.GetTimestamp();
       while(seq-Volatile.Read(ref consumedSeq[wid])>=ringSlots)Thread.SpinWait(4);
       stallTicks[wid]+=Stopwatch.GetTimestamp()-st;
     }
     int slot=(int)(seq%ringSlots),count=Math.Min(slabSize,input.Length-p);
     input.AsSpan(p,count).CopyTo(rings[wid][slot]);counts[wid][slot]=count;
     produced[wid]=++seq;Volatile.Write(ref published[wid],seq);
     long depth=seq-Volatile.Read(ref consumedSeq[wid]);if(depth>maxDepth[wid])maxDepth[wid]=depth;
     p+=count;wid++;if(wid==workers)wid=0;
   }
   for(int w=0;w<workers;w++)while(Volatile.Read(ref consumedSeq[w])<produced[w])Thread.SpinWait(4);
   for(int w=0;w<workers;w++)Volatile.Write(ref published[w],-1);
   for(int w=0;w<workers;w++)threads[w].Join();
   long end=Stopwatch.GetTimestamp();ulong sum=0;long msgs=0,totalStalls=0,totalStallTicks=0,maxD=0;
   for(int w=0;w<workers;w++){sum+=sums[w];msgs+=consumedMsgs[w];totalStalls+=stalls[w];totalStallTicks+=stallTicks[w];maxD=Math.Max(maxD,maxDepth[w]);}
   sink=sum;double sec=(double)(end-start)/Stopwatch.Frequency,stallSec=(double)totalStallTicks/Stopwatch.Frequency;
   Console.WriteLine($"stage=spsc-{workers}w messages={input.Length} bytes={input.Length*32L} wall_s={sec:F6} GBps={input.Length*32.0/sec/1e9:F3} mps={input.Length/sec/1e6:F3} ns_msg={sec*1e9/input.Length:F3} producer_stalls={totalStalls} producer_stall_ms={stallSec*1e3:F3} producer_stall_pct={stallSec/sec*100:F3} max_worker_depth_slabs={maxD} ring_slots_per_worker={ringSlots} slab_size={slabSize} consumed={msgs} checksum={sum}");
 }
 static void Warm(P32[] a){int n=Math.Min(10000,a.Length);ulong s=0;for(int i=0;i<n;i++){var m=Cast(in a[i]);s+=m.A+m.D;}sink=s;}
 static void Main(string[] args){string stage=args[0];int n=int.Parse(args[1]);var a=Data(n);Warm(a);switch(stage){case "baseline-read":Baseline(a);break;case "direct-cast":DirectCast(a);break;case "spsc-1w":SpscPipeline(a,1);break;case "spsc-2w":SpscPipeline(a,2);break;case "spsc-4w":SpscPipeline(a,4);break;default:throw new ArgumentException(stage);}}
}
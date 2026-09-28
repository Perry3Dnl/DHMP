using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

[StructLayout(LayoutKind.Sequential, Pack=1)] struct Package32 { public uint V0,V1,V2,V3,V4,V5,V6,V7; }
[StructLayout(LayoutKind.Sequential, Pack=1)] struct Model32 { public uint V0,V1,V2,V3,V4,V5,V6,V7; }

sealed class Slab {
    public readonly Package32[] Items;
    public int Count;
    public int State; // 0 free, 1 filling, 2 ready, 3 claimed
    public Slab(int size) => Items=new Package32[size];
}

static class Program {
 static long _sink;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static Model32 Materialize(in Package32 p)=>Unsafe.As<Package32,Model32>(ref Unsafe.AsRef(in p));
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static ulong Consume(in Model32 m)=>(ulong)m.V0+m.V3+m.V7;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static Package32 Make(int i)=>new(){V0=(uint)i,V1=1,V2=2,V3=3,V4=4,V5=5,V6=6,V7=(uint)((ulong)i^0x9e3779b9u)};

 static ulong Expected(int n){ulong s=0;for(int i=0;i<n;i++)s+=(ulong)(uint)i+3+(uint)((ulong)i^0x9e3779b9u);return s;}

 static void Counter(int n) {
   ulong s=0; var sw=Stopwatch.StartNew();
   for(int i=0;i<n;i++){var p=Make(i);var m=Materialize(in p);s+=Consume(in m);}
   sw.Stop(); Volatile.Write(ref _sink,(long)s);
   Console.WriteLine($"mode=dotnet-direct workers=1 messages={n} wall_s={sw.Elapsed.TotalSeconds:F6} mps={n/sw.Elapsed.TotalSeconds/1e6:F3} ns_msg={sw.Elapsed.TotalSeconds*1e9/n:F3} checksum={s}");
 }

 static void Exchange(int n,int workers,int slabSize=512,int slabCount=32) {
   var slabs=new Slab[slabCount]; for(int i=0;i<slabCount;i++)slabs[i]=new Slab(slabSize);
   var sums=new ulong[workers]; var spins=new long[workers]; var threads=new Thread[workers];
   using var gate=new ManualResetEventSlim(false);
   int done=0;
   for(int w=0;w<workers;w++){int id=w;threads[w]=new Thread(()=>{
     ulong sum=0; long spin=0; int scan=id%slabCount; gate.Wait();
     while(true){
       bool found=false;
       for(int k=0;k<slabCount;k++){
         int idx=(scan+k)%slabCount; var slab=slabs[idx];
         if(Interlocked.CompareExchange(ref slab.State,3,2)!=2) continue;
         found=true; int count=slab.Count;
         for(int j=0;j<count;j++){var m=Materialize(in slab.Items[j]);sum+=Consume(in m);}
         Volatile.Write(ref slab.State,0); scan=(idx+1)%slabCount; break;
       }
       if(!found){if(Volatile.Read(ref done)!=0)break;spin++;Thread.SpinWait(1);}
     }
     sums[id]=sum;spins[id]=spin;
   }){IsBackground=true};threads[w].Start();}

   long alloc0=GC.GetTotalAllocatedBytes(true); long producerSpins=0, publications=0;
   var sw=Stopwatch.StartNew(); gate.Set();
   int produced=0, next=0;
   while(produced<n){
     var slab=slabs[next];
     if(Interlocked.CompareExchange(ref slab.State,1,0)!=0){producerSpins++;Thread.SpinWait(1);next=(next+1)%slabCount;continue;}
     int count=Math.Min(slabSize,n-produced);
     for(int j=0;j<count;j++)slab.Items[j]=Make(produced+j);
     slab.Count=count; Volatile.Write(ref slab.State,2); publications++; produced+=count; next=(next+1)%slabCount;
   }
   while(true){bool busy=false;for(int i=0;i<slabCount;i++)if(Volatile.Read(ref slabs[i].State)!=0){busy=true;break;}if(!busy)break;Thread.SpinWait(1);}
   Volatile.Write(ref done,1); foreach(var t in threads)t.Join(); sw.Stop();
   ulong sum=0;long consumerSpins=0;foreach(var x in sums)sum+=x;foreach(var x in spins)consumerSpins+=x;
   Volatile.Write(ref _sink,(long)sum); long allocated=GC.GetTotalAllocatedBytes(true)-alloc0;
   Console.WriteLine($"mode=dotnet-exchange workers={workers} messages={n} slab={slabSize} slots={slabCount} wall_s={sw.Elapsed.TotalSeconds:F6} mps={n/sw.Elapsed.TotalSeconds/1e6:F3} ns_msg={sw.Elapsed.TotalSeconds*1e9/n:F3} publications={publications} producer_spins={producerSpins} consumer_spins={consumerSpins} allocated_bytes={allocated} checksum={sum}");
 }

 static void Main(string[] args){
   int n=args.Length>0?int.Parse(args[0]):10_000_000;
   // JIT warmup outside measured runs.
   Counter(Math.Min(n,200_000));
   ulong expected=Expected(n); Volatile.Write(ref _sink,(long)expected);
   Counter(n); Exchange(n,1); Exchange(n,2); Exchange(n,4);
 }
}
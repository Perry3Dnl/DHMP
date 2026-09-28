using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
[StructLayout(LayoutKind.Sequential,Pack=1)] struct P32{public ulong A,B,C,D;}
[StructLayout(LayoutKind.Sequential,Pack=1)] struct M32{public ulong A,B,C,D;}
static class Program{
 static ulong sink;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static M32 Model(in P32 p)=>Unsafe.As<P32,M32>(ref Unsafe.AsRef(in p));
 static void Out(string stage,int n,long ticks,ulong sum){double s=(double)ticks/Stopwatch.Frequency;Console.WriteLine($"stage={stage} messages={n} bytes={n*32L} wall_s={s:F6} GBps={n*32.0/s/1e9:F3} mps={n/s/1e6:F3} ns_msg={s*1e9/n:F3} checksum={sum}");}
 static P32[] Data(int n){var a=new P32[n];for(int i=0;i<n;i++)a[i]=new P32{A=(ulong)i,B=1,C=2,D=(ulong)i^0x9e3779b97f4a7c15UL};return a;}
 static void Raw(int n){var a=Data(n);ulong s=0;long t=Stopwatch.GetTimestamp();for(int i=0;i<n;i++)s+=a[i].A+a[i].D;long e=Stopwatch.GetTimestamp();sink=s;Out("raw-read",n,e-t,s);}
 static void PackageModel(int n){var a=Data(n);ulong s=0;long t=Stopwatch.GetTimestamp();for(int i=0;i<n;i++){var m=Model(in a[i]);s+=m.A+m.D;}long e=Stopwatch.GetTimestamp();sink=s;Out("package-model",n,e-t,s);}
 static void Exchange(int n,bool model){const int size=512,slots=32;var q=new P32[slots][];for(int x=0;x<slots;x++)q[x]=new P32[size];var state=new int[slots];ulong sum=0;int done=0;using var gate=new ManualResetEventSlim(false);var c=new Thread(()=>{int r=0;gate.Wait();while(true){if(Volatile.Read(ref state[r])==2){var slab=q[r];if(model){for(int j=0;j<size;j++){var m=Model(in slab[j]);sum+=m.A+m.D;}}else{for(int j=0;j<size;j++)sum+=slab[j].A+slab[j].D;}Volatile.Write(ref state[r],0);r=(r+1)%slots;}else if(Volatile.Read(ref done)!=0)break;else Thread.SpinWait(1);}});c.Start();int p=0,w=0;long t=Stopwatch.GetTimestamp();gate.Set();while(p<n){while(Volatile.Read(ref state[w])!=0)Thread.SpinWait(1);int count=Math.Min(size,n-p);for(int j=0;j<count;j++){int i=p+j;q[w][j]=new P32{A=(ulong)i,B=1,C=2,D=(ulong)i^0x9e3779b97f4a7c15UL};}for(int j=count;j<size;j++)q[w][j]=default;Volatile.Write(ref state[w],2);p+=count;w=(w+1)%slots;}while(true){bool busy=false;for(int i=0;i<slots;i++)if(Volatile.Read(ref state[i])!=0){busy=true;break;}if(!busy)break;Thread.SpinWait(1);}Volatile.Write(ref done,1);c.Join();long e=Stopwatch.GetTimestamp();sink=sum;Out(model?"exchange-model":"exchange",n,e-t,sum);}
 static void Main(string[] args){string stage=args[0];int n=int.Parse(args[1]); // small unmeasured JIT warmup in a separate code path
 var warm=Data(10000);for(int i=0;i<warm.Length;i++){var m=Model(in warm[i]);sink+=m.A+m.D;}
 switch(stage){case "raw-read":Raw(n);break;case "package-model":PackageModel(n);break;case "exchange":Exchange(n,false);break;case "exchange-model":Exchange(n,true);break;default:throw new ArgumentException(stage);}}
}
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential,Pack=1)] struct P32 { public ulong A,B,C,D; }
[StructLayout(LayoutKind.Sequential,Pack=1)] struct M32 { public ulong A,B,C,D; }

static class Program {
 static ulong sink;
 [MethodImpl(MethodImplOptions.AggressiveInlining)]
 static M32 Model(in P32 p) => Unsafe.As<P32,M32>(ref Unsafe.AsRef(in p));

 static void Out(string stage,int n,long ticks,ulong sum) {
   double s=(double)ticks/Stopwatch.Frequency;
   Console.WriteLine($"stage={stage} messages={n} bytes={n*32L} wall_s={s:F6} GBps={n*32.0/s/1e9:F3} mps={n/s/1e6:F3} ns_msg={s*1e9/n:F3} checksum={sum}");
 }

 static P32[] Data(int n) {
   var a=new P32[n];
   for(int i=0;i<n;i++) a[i]=new P32{A=(ulong)i,B=1,C=2,D=(ulong)i^0x9e3779b97f4a7c15UL};
   return a;
 }

 static void Raw(int n) {
   var a=Data(n); ulong s=0; long t=Stopwatch.GetTimestamp();
   for(int i=0;i<n;i++) s+=a[i].A+a[i].D;
   long e=Stopwatch.GetTimestamp(); sink=s; Out("raw-read",n,e-t,s);
 }

 static void PackageModel(int n) {
   var a=Data(n); ulong s=0; long t=Stopwatch.GetTimestamp();
   for(int i=0;i<n;i++){var m=Model(in a[i]);s+=m.A+m.D;}
   long e=Stopwatch.GetTimestamp(); sink=s; Out("package-model",n,e-t,s);
 }

 static void DirectModelBuffer(int n) {
   var frames=Data(n);
   // Preallocated model buffer: allocation is deliberately outside the timed hot path.
   var models=new M32[n];
   ulong s=0; long t=Stopwatch.GetTimestamp();
   for(int i=0;i<n;i++) {
     // Complete fixed frame is already known; materialize T directly into its final slot.
     models[i]=Model(in frames[i]);
     s+=models[i].A+models[i].D;
   }
   long e=Stopwatch.GetTimestamp(); sink=s; Out("direct-model-buffer",n,e-t,s);
 }

 static void Main(string[] args) {
   string stage=args[0]; int n=int.Parse(args[1]);
   var warm=Data(10000); for(int i=0;i<warm.Length;i++){var m=Model(in warm[i]);sink+=m.A+m.D;}
   switch(stage) {
     case "raw-read": Raw(n); break;
     case "package-model": PackageModel(n); break;
     case "direct-model-buffer": DirectModelBuffer(n); break;
     default: throw new ArgumentException(stage);
   }
 }
}
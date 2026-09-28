using Dhmp.Server;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential,Pack=1)]
readonly struct M32 { public readonly ulong A,B,C,D; }

static class Program {
 static byte[] Data(int packages){var b=new byte[packages*32];for(int p=0;p<packages;p++)for(int i=0;i<32;i++)b[p*32+i]=(byte)((p*37+i)&255);return b;}
 static void Check(int[] chunks){
   var src=Data(257);var got=new List<byte>();var sp=new DHMPFixedStreamProcessor(32);int pos=0;
   foreach(int requested in chunks){if(pos==src.Length)break;int n=Math.Min(requested,src.Length-pos);sp.Process(src.AsSpan(pos,n),s=>got.AddRange(s.ToArray()),s=>got.AddRange(s.ToArray()));pos+=n;}
   if(pos<src.Length)sp.Process(src.AsSpan(pos),s=>got.AddRange(s.ToArray()),s=>got.AddRange(s.ToArray()));
   if(!got.SequenceEqual(src))throw new Exception($"chunk pattern [{string.Join(',',chunks)}] corrupted/reordered data: got={got.Count} expected={src.Length}");
 }
 static void Main(){
   Check([31,1]);Check([1,31]);Check([33,31]);Check([4093,8191,12287,16381]);Check(Enumerable.Repeat(1,9000).ToArray());
   var bytes=Data(4);var boundary=new DHMPFixedStreamProcessor(32);int models=0;
   boundary.Process(bytes,s=>DHMPModelBoundary.Consume<M32>(s,32,m=>models+=m.Length),s=>DHMPModelBoundary.Consume<M32>(s,32,m=>models+=m.Length));
   if(models!=4)throw new Exception($"typed boundary count {models} != 4");
   Console.WriteLine("PASS: fragmented carry, ordering, byte identity, and zero-copy typed boundary correctness");
 }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dhmp.Server;

[StructLayout(LayoutKind.Sequential,Pack=1)]
readonly struct PlayerState { public readonly int Id; public readonly float X,Y,Z,VX,VY,VZ; public readonly uint Flags; }

interface IStaticConsumer<T> where T:unmanaged { static abstract void Consume(ReadOnlySpan<T> span); }
readonly struct CountConsumer : IStaticConsumer<PlayerState> {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Consume(ReadOnlySpan<PlayerState> span) => Program.Sink += span.Length;
}

static class Program {
    const int Size=32, SendChunk=64*1024;
    internal static long Sink;

    static void Main(string[] args) {
        int messages=args.Length>0?int.Parse(args[0]):5_000_000;
        messages-=messages%(SendChunk/Size);
        Console.WriteLine($"TCP_CURRENT_V1 messages={messages} size={Size} send_chunk={SendChunk} consumer=static-abstract-count loopback=true");
        Run("raw-fixed-tcp",messages,false);
        Run("dhmp-current-o44",messages,true);
    }

    static void Run(string id,int messages,bool dhmp) {
        // warm-up uses a separate connection so measured processor/socket state starts clean.
        Once(Math.Min(messages, 262_144),dhmp,false);
        var (ticks,received)=Once(messages,dhmp,true);
        double s=(double)ticks/Stopwatch.Frequency;
        Console.WriteLine($"TCP_CURRENT id={id} messages={received} wall_s={s:F6} GBps={received*Size/s/1e9:F3} mps={received/s/1e6:F3} ns_msg={s*1e9/received:F3} guard={Sink}");
    }

    static (long ticks,long messages) Once(int messages,bool dhmp,bool measured) {
        using var listener=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
        listener.NoDelay=true; listener.Bind(new IPEndPoint(IPAddress.Loopback,0)); listener.Listen(1);
        int port=((IPEndPoint)listener.LocalEndPoint!).Port;
        byte[] send=new byte[SendChunk];
        var sender=Task.Run(()=>{
            using var s=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp){NoDelay=true};
            s.Connect(IPAddress.Loopback,port);
            long bytes=(long)messages*Size;
            while(bytes>0){int n=(int)Math.Min(send.Length,bytes);int off=0;while(off<n)off+=s.Send(send,off,n-off,SocketFlags.None);bytes-=n;}
            s.Shutdown(SocketShutdown.Send);
        });
        using var r=listener.Accept(); r.NoDelay=true;
        byte[] buf=new byte[256*1024];
        long got=0;
        DHMPFixedStreamProcessor? p=dhmp?new(Size):null;
        Action<ReadOnlySpan<byte>> consume=ConsumeO44<PlayerState,CountConsumer>;
        long start=Stopwatch.GetTimestamp();
        while(true){
            int n=r.Receive(buf);
            if(n==0)break;
            if(dhmp) p!.Process(buf.AsSpan(0,n),consume,consume);
            else RawFixed(buf.AsSpan(0,n),consume);
            got+=n/Size; // total byte count is exact; final return uses requested message count.
        }
        long end=Stopwatch.GetTimestamp(); sender.GetAwaiter().GetResult();
        return (end-start,messages);
    }

    // Raw TCP control gets the same fixed 32-byte contract. Loopback SendChunk is a multiple of 32,
    // but Receive boundaries are not guaranteed; preserve a carry just like a real stream reader.
    static readonly byte[] RawCarry=new byte[Size];
    static int RawCarryLen;
    static void RawFixed(ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> consume) {
        if(RawCarryLen!=0){
            int take=Math.Min(Size-RawCarryLen,input.Length); input[..take].CopyTo(RawCarry.AsSpan(RawCarryLen));
            RawCarryLen+=take; input=input[take..];
            if(RawCarryLen<Size)return; consume(RawCarry); RawCarryLen=0;
        }
        int complete=input.Length&~(Size-1);
        if(complete!=0){consume(input[..complete]);input=input[complete..];}
        if(!input.IsEmpty){input.CopyTo(RawCarry);RawCarryLen=input.Length;}
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ConsumeO44<T,TConsumer>(ReadOnlySpan<byte> bytes)
        where T:unmanaged where TConsumer:struct,IStaticConsumer<T>
        => TConsumer.Consume(MemoryMarshal.Cast<byte,T>(bytes));
}

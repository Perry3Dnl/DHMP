using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;

namespace DHMP.Unity
{
    public abstract class ArenaSession : IDisposable
    {
        protected readonly IDhmpPacketSocket Data, Control;
        protected readonly byte[] ReceiveBuffer = new byte[65536], SendBuffer = new byte[DhmpWire.MaximumPayload];
        protected readonly ArenaRecord[] Records = new ArenaRecord[DhmpWire.MaximumPayload / ArenaRecord.Size];
        protected bool Disposed;
        public long SentPayloadBytes { get; private set; }
        public long ReceivedPayloadBytes { get; protected set; }
        public long DroppedSends { get; private set; }
        protected ArenaSession(IDhmpPacketSocket data, IDhmpPacketSocket control)
        { Data = data ?? throw new ArgumentNullException(nameof(data)); Control = control ?? throw new ArgumentNullException(nameof(control)); }
        protected void SendData(IPAddress address, byte[] bytes, int length)
        {
            try { if (Data.Send(address, bytes, length)) SentPayloadBytes += length; else DroppedSends++; }
            catch (IOException) { DroppedSends++; } // A failed peer/path does not take down other peers.
        }
        protected void SendControl(IPAddress address, byte[] bytes)
        {
            try { if (Control.Send(address, bytes, bytes.Length)) SentPayloadBytes += bytes.Length; else DroppedSends++; }
            catch (IOException) { DroppedSends++; }
        }
        protected static ulong RandomToken()
        {
            var bytes = new byte[8];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                do { rng.GetBytes(bytes); } while (DhmpWire.Read64(bytes, 0) == 0);
            }
            return DhmpWire.Read64(bytes, 0);
        }
        protected void EnsureOpen() { if (Disposed) throw new ObjectDisposedException(GetType().Name); }
        public virtual void Dispose() { if (Disposed) return; Disposed = true; try { Data.Dispose(); } finally { Control.Dispose(); } }
    }

    public sealed class ArenaServer : ArenaSession
    {
        private sealed class Peer
        {
            public IPAddress Address;
            public int SendLimit;
            public double LastSeen;
            public uint Id, AcknowledgedInput, LastSentAt;
            public ulong Token, Nonce;
            public string Name;
            public MotorState State;
            public readonly List<ArenaRecord> Inputs = new List<ArenaRecord>(32);
        }
        private readonly Dictionary<IPAddress, Peer> peers = new Dictionary<IPAddress, Peer>();
        private readonly List<IPAddress> expired = new List<IPAddress>(32);
        private readonly int maxPlayers;
        private uint nextPlayer, tick;
        public int PlayerCount { get; private set; }
        public ArenaServer(IDhmpPacketSocket data, IDhmpPacketSocket control, int maximumPlayers = 16) : base(data, control)
        {
            if (maximumPlayers < 1 || maximumPlayers > 32) throw new ArgumentOutOfRangeException(nameof(maximumPlayers));
            maxPlayers = maximumPlayers;
        }

        public void Poll(double now)
        {
            EnsureOpen();
            for (int i = 0; i < 64 && Control.TryReceive(ReceiveBuffer, out IPAddress address, out int length); i++)
            {
                ReceivedPayloadBytes += length;
                if (!DhmpWire.TryControl(ReceiveBuffer, length, out ControlMessage message) || message.Type != 1) continue;
                byte error = DhmpWire.CompatibilityError(message);
                if (error != 0) { SendControl(address, DhmpWire.Control(3, message.Correlation, error)); continue; }
                if (!peers.TryGetValue(address, out Peer peer))
                {
                    if (peers.Count >= maxPlayers) continue;
                    peer = new Peer { Address = address, LastSeen = now,
                        SendLimit = Math.Min(DhmpWire.MaximumPayload, message.MaximumPayload) / ArenaRecord.Size * ArenaRecord.Size };
                    peers.Add(address, peer);
                }
                peer.SendLimit = Math.Min(peer.SendLimit, message.MaximumPayload / ArenaRecord.Size * ArenaRecord.Size);
                SendControl(address, DhmpWire.Control(2, message.Correlation));
            }
            for (int i = 0; i < 128 && Data.TryReceive(ReceiveBuffer, out IPAddress address, out int length); i++)
            {
                ReceivedPayloadBytes += length;
                if (!peers.TryGetValue(address, out Peer peer) || !ArenaRecord.TryDecodePacket(ReceiveBuffer, length, Records, out int count)) continue;
                for (int r = 0; r < count; r++)
                {
                    ArenaRecord record = Records[r];
                    if (record.Kind == ArenaMessage.Join && record.JoinNonce != 0 && record.Session == 0 && record.PlayerId == 0)
                    {
                        if (peer.Id != 0 && peer.Nonce != record.JoinNonce) continue; // One active session per source IPv6.
                        if (peer.Id == 0)
                        {
                            peer.Id = ++nextPlayer;
                            if (peer.Id == 0) peer.Id = ++nextPlayer;
                            peer.Token = RandomToken(); peer.Nonce = record.JoinNonce; peer.Name = ArenaRecord.NormalizeName(record.Name);
                            peer.State = ArenaMotor.Spawn(peer.Id); peer.LastSeen = now; PlayerCount++;
                        }
                        SendWelcome(peer);
                        continue;
                    }
                    if (peer.Id == 0 || record.PlayerId != peer.Id || record.Session != peer.Token || record.JoinNonce != peer.Nonce) continue;
                    if (record.Kind == ArenaMessage.Leave) { peers.Remove(address); PlayerCount--; break; }
                    if (record.Kind != ArenaMessage.Input || !DhmpWire.Newer(record.Sequence, peer.AcknowledgedInput) ||
                        unchecked(record.Sequence - peer.AcknowledgedInput) > 256 || peer.Inputs.Count >= 32) continue;
                    int at = 0;
                    while (at < peer.Inputs.Count && DhmpWire.Newer(record.Sequence, peer.Inputs[at].Sequence)) at++;
                    if (at < peer.Inputs.Count && peer.Inputs[at].Sequence == record.Sequence) continue;
                    peer.Inputs.Insert(at, record); peer.LastSeen = now;
                }
            }
            expired.Clear();
            foreach (Peer peer in peers.Values) if (now - peer.LastSeen > 10) expired.Add(peer.Address);
            foreach (IPAddress address in expired) { if (peers[address].Id != 0) PlayerCount--; peers.Remove(address); }
        }

        // Caller advances on a server-owned clock, never once per received input.
        public void Tick()
        {
            EnsureOpen(); tick++;
            foreach (Peer peer in peers.Values)
            {
                if (peer.Id == 0) continue;
                if (peer.Inputs.Count == 0) peer.State = ArenaMotor.Step(peer.State, 0, 0, false, peer.State.Yaw);
                else
                {
                    ArenaRecord input = peer.Inputs[0]; peer.Inputs.RemoveAt(0);
                    peer.State = ArenaMotor.Step(peer.State, input.MoveX, input.MoveZ, input.Jump, input.State.Yaw);
                    peer.AcknowledgedInput = input.Sequence; peer.LastSentAt = input.SentAt;
                }
            }
            if (tick % 3 == 0) BroadcastStates(); // 10 snapshots/s, simulation and inputs at 30 ticks/s.
        }
        private void SendWelcome(Peer peer)
        {
            new ArenaRecord { Kind = ArenaMessage.Welcome, PlayerId = peer.Id, Session = peer.Token, JoinNonce = peer.Nonce,
                State = peer.State, Grounded = peer.State.Grounded, Name = peer.Name }.Encode(SendBuffer);
            SendData(peer.Address, SendBuffer, ArenaRecord.Size);
        }
        private void BroadcastStates()
        {
            foreach (Peer receiver in peers.Values)
            {
                if (receiver.Id == 0) continue;
                int length = 0;
                foreach (Peer player in peers.Values)
                {
                    if (player.Id == 0) continue;
                    if (length + ArenaRecord.Size > receiver.SendLimit) { SendData(receiver.Address, SendBuffer, length); length = 0; }
                    new ArenaRecord { Kind = ArenaMessage.State, PlayerId = player.Id, Sequence = tick, Session = receiver.Token,
                        JoinNonce = receiver.Nonce, AcknowledgedInput = player.AcknowledgedInput, State = player.State,
                        Grounded = player.State.Grounded, Name = player.Name, SentAt = player.LastSentAt }.Encode(SendBuffer, length);
                    length += ArenaRecord.Size;
                }
                if (length > 0) SendData(receiver.Address, SendBuffer, length);
            }
        }
    }

    public enum ArenaClientPhase { Negotiating, Joining, Connected, Closed }

    public sealed class ArenaClient : ArenaSession
    {
        private readonly IPAddress server;
        private readonly uint correlation;
        private readonly ulong nonce;
        private readonly string name;
        private readonly List<ArenaRecord> pending = new List<ArenaRecord>(128);
        private readonly Dictionary<uint, uint> generations = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, double> lastPlayerSeen = new Dictionary<uint, double>();
        private readonly List<uint> expired = new List<uint>(32);
        private readonly double deadline;
        private double nextJoin, lastServer;
        private int sendLimit = ArenaRecord.Size;
        private uint sequence;
        private ulong session;
        public uint PlayerId { get; private set; }
        public MotorState Predicted { get; private set; }
        public ArenaClientPhase Phase { get; private set; }
        public float RoundTripMilliseconds { get; private set; }
        public event Action<ArenaRecord> PlayerState;
        public event Action<uint> PlayerLeft;

        public ArenaClient(IDhmpPacketSocket data, IDhmpPacketSocket control, IPAddress serverAddress, string playerName, double now) : base(data, control)
        {
            DhmpRawIpv6Socket.ValidateAddress(serverAddress); server = serverAddress;
            name = ArenaRecord.NormalizeName(playerName); nonce = RandomToken(); correlation = (uint)(RandomToken() % uint.MaxValue) + 1;
            deadline = now + 10; lastServer = now; Phase = ArenaClientPhase.Negotiating;
            SendControl(server, DhmpWire.Control(1, correlation));
        }

        public void Poll(double now)
        {
            EnsureOpen();
            for (int i = 0; i < 64 && Control.TryReceive(ReceiveBuffer, out IPAddress address, out int length); i++)
            {
                ReceivedPayloadBytes += length;
                if (Phase != ArenaClientPhase.Negotiating || !server.Equals(address) ||
                    !DhmpWire.TryControl(ReceiveBuffer, length, out ControlMessage message) || message.Correlation != correlation) continue;
                if (message.Type == 3) throw new InvalidOperationException("DHMP compatibility rejected, reason " + message.Reason + ".");
                if (message.Type != 2) continue;
                if (DhmpWire.CompatibilityError(message) != 0) throw new InvalidOperationException("The server uses another arena wire/schema contract.");
                sendLimit = Math.Min(DhmpWire.MaximumPayload, message.MaximumPayload) / ArenaRecord.Size * ArenaRecord.Size;
                Phase = ArenaClientPhase.Joining; nextJoin = now;
            }
            if (Phase == ArenaClientPhase.Joining && now >= nextJoin)
            {
                new ArenaRecord { Kind = ArenaMessage.Join, JoinNonce = nonce, Name = name }.Encode(SendBuffer);
                SendData(server, SendBuffer, ArenaRecord.Size); nextJoin = now + 0.5;
            }
            for (int i = 0; i < 128 && Data.TryReceive(ReceiveBuffer, out IPAddress address, out int length); i++)
            {
                ReceivedPayloadBytes += length;
                if (!server.Equals(address) || !ArenaRecord.TryDecodePacket(ReceiveBuffer, length, Records, out int count)) continue;
                for (int r = 0; r < count; r++) Apply(Records[r], now);
            }
            if (Phase != ArenaClientPhase.Connected && now >= deadline) throw new TimeoutException("The arena did not accept the connection within ten seconds. Check the server, IPv6 path, and capacity.");
            if (Phase == ArenaClientPhase.Connected && now - lastServer > 10) throw new TimeoutException("The arena server stopped responding.");
            expired.Clear();
            foreach (KeyValuePair<uint, double> seen in lastPlayerSeen)
                if (seen.Key != PlayerId && now - seen.Value > 2) expired.Add(seen.Key);
            foreach (uint id in expired) { lastPlayerSeen.Remove(id); generations.Remove(id); PlayerLeft?.Invoke(id); }
        }

        private void Apply(ArenaRecord record, double now)
        {
            if (record.JoinNonce != nonce || record.PlayerId == 0) return;
            if (Phase == ArenaClientPhase.Joining && record.Kind == ArenaMessage.Welcome && record.Session != 0)
            {
                session = record.Session; PlayerId = record.PlayerId; Predicted = record.State;
                Phase = ArenaClientPhase.Connected; lastServer = now; PlayerState?.Invoke(record); return;
            }
            if (Phase != ArenaClientPhase.Connected || record.Kind != ArenaMessage.State || record.Session != session) return;
            if (generations.TryGetValue(record.PlayerId, out uint generation) && !DhmpWire.Newer(record.Sequence, generation)) return;
            if (!generations.ContainsKey(record.PlayerId) && generations.Count >= 32) return;
            if (record.PlayerId == PlayerId && DhmpWire.Newer(record.AcknowledgedInput, sequence)) return;
            generations[record.PlayerId] = record.Sequence; lastPlayerSeen[record.PlayerId] = now; lastServer = now;
            if (record.PlayerId == PlayerId)
            {
                while (pending.Count > 0 && !DhmpWire.Newer(pending[0].Sequence, record.AcknowledgedInput)) pending.RemoveAt(0);
                MotorState corrected = record.State;
                foreach (ArenaRecord input in pending) corrected = ArenaMotor.Step(corrected, input.MoveX, input.MoveZ, input.Jump, input.State.Yaw);
                Predicted = corrected;
                uint elapsed = unchecked((uint)(now * 1000) - record.SentAt);
                if (elapsed < 60000) RoundTripMilliseconds = elapsed; // Includes server processing/snapshot delay.
            }
            PlayerState?.Invoke(record);
        }

        public void SubmitInput(float x, float z, bool jump, float yaw, double now)
        {
            EnsureOpen();
            if (Phase != ArenaClientPhase.Connected) return;
            if (!DhmpWire.Finite(x) || !DhmpWire.Finite(z) || !DhmpWire.Finite(yaw) || Math.Abs(x) > 1 || Math.Abs(z) > 1)
                throw new ArgumentOutOfRangeException(nameof(x));
            if (pending.Count >= 128) throw new TimeoutException("The server is not keeping up with input acknowledgements.");
            var input = new ArenaRecord { Kind = ArenaMessage.Input, PlayerId = PlayerId, Session = session, JoinNonce = nonce,
                Sequence = ++sequence, MoveX = x, MoveZ = z, Jump = jump, State = new MotorState { Yaw = ((yaw % 360) + 360) % 360 },
                SentAt = unchecked((uint)(now * 1000)) };
            pending.Add(input); Predicted = ArenaMotor.Step(Predicted, x, z, jump, input.State.Yaw);
            // Bounded application redundancy. No DHMP ACK/retransmission framing is introduced.
            int copies = Math.Min(Math.Min(3, pending.Count), sendLimit / ArenaRecord.Size);
            for (int i = 0; i < copies; i++) pending[pending.Count - copies + i].Encode(SendBuffer, i * ArenaRecord.Size);
            SendData(server, SendBuffer, copies * ArenaRecord.Size);
        }

        public override void Dispose()
        {
            if (Disposed) return;
            try
            {
                if (Phase == ArenaClientPhase.Connected)
                {
                    new ArenaRecord { Kind = ArenaMessage.Leave, PlayerId = PlayerId, Session = session, JoinNonce = nonce }.Encode(SendBuffer);
                    SendData(server, SendBuffer, ArenaRecord.Size);
                }
            }
            finally { Phase = ArenaClientPhase.Closed; base.Dispose(); }
        }
    }
}

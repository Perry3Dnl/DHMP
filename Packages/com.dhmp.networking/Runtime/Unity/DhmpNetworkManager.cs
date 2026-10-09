using System;
using UnityEngine;

namespace DHMP.Unity
{
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public sealed class DhmpNetworkManager : MonoBehaviour
    {
        public DhmpUnitySettings settings;
        public ArenaClient Client { get; private set; }
        public ArenaServer Server { get; private set; }
        public string Status { get; private set; } = "Ready";
        public event Action<ArenaRecord> PlayerState;
        public event Action<uint> PlayerLeft;
        public event Action Stopped;
        private float inputX, inputZ, yaw;
        private bool jump;
        private double accumulator;
        public static double Now => Time.realtimeSinceStartupAsDouble;

        public void Connect(string serverIpv6, string playerName)
        {
            Stop();
            IDhmpPacketSocket data = null, control = null;
            try
            {
                Open(false, out data, out control);
                Client = new ArenaClient(data, control, DhmpUnitySettings.ParseAddress(serverIpv6), playerName, Now);
                Client.PlayerState += record => PlayerState?.Invoke(record);
                Client.PlayerLeft += id => PlayerLeft?.Invoke(id);
                Status = "Connecting…";
            }
            catch (Exception error) { data?.Dispose(); control?.Dispose(); Stop(); Status = error.Message; Debug.LogWarning("DHMP: " + error.Message); }
        }

        public void StartServer()
        {
            Stop();
            IDhmpPacketSocket data = null, control = null;
            try
            {
                Open(true, out data, out control);
                Server = new ArenaServer(data, control, settings.maximumPlayers);
                Status = "Arena server listening on " + settings.LocalAddress;
                Debug.Log("DHMP: " + Status);
            }
            catch (Exception error)
            {
                data?.Dispose(); control?.Dispose(); Stop(); Status = error.Message; Debug.LogError("DHMP: " + error.Message);
                if (Application.isBatchMode) Application.Quit(1);
            }
        }

        private void Open(bool server, out IDhmpPacketSocket data, out IDhmpPacketSocket control)
        {
            data = null; control = null;
            if (settings == null) throw new InvalidOperationException("Assign a DHMP settings asset.");
            settings.ValidateStartup(server, Application.isEditor || Debug.isDebugBuild);
            var local = settings.LocalAddress;
            data = new DhmpRawIpv6Socket(local, DhmpWire.DataProtocol, settings.enableExperimentalIpv6, settings.allowUnprotectedDemoPayloads);
            try { control = new DhmpRawIpv6Socket(local, DhmpWire.ControlProtocol, settings.enableExperimentalIpv6, settings.allowUnprotectedDemoPayloads); }
            catch { data.Dispose(); throw; }
        }

        public void SetInput(Vector2 movement, bool jumpPressed, float lookYaw)
        {
            movement = Vector2.ClampMagnitude(movement, 1); inputX = movement.x; inputZ = movement.y;
            jump |= jumpPressed; yaw = lookYaw;
        }
        private void Update()
        {
            if (Client == null && Server == null) return;
            try
            {
                double now = Now;
                Client?.Poll(now); Server?.Poll(now);
                if (Client != null) Status = Client.Phase == ArenaClientPhase.Connected ? "Connected" : "Connecting…";
                accumulator += Math.Min(Time.unscaledDeltaTime, 0.2f);
                int steps = 0;
                while (accumulator >= ArenaMotor.StepSeconds && steps++ < 4)
                {
                    accumulator -= ArenaMotor.StepSeconds;
                    Server?.Tick(); Client?.SubmitInput(inputX, inputZ, jump, yaw, now); jump = false;
                }
                if (accumulator >= ArenaMotor.StepSeconds) accumulator = 0;
            }
            catch (Exception error)
            {
                bool wasServer = Server != null; Stop(); Status = error.Message;
                Debug.LogWarning("DHMP: " + error.Message);
                if (wasServer && Application.isBatchMode) Application.Quit(1);
            }
        }
        public void Stop()
        {
            try { Client?.Dispose(); }
            finally { Client = null; Server?.Dispose(); Server = null; }
            accumulator = 0; inputX = inputZ = yaw = 0; jump = false; Status = "Disconnected"; Stopped?.Invoke();
        }
        private void OnDisable() => Stop();
    }
}

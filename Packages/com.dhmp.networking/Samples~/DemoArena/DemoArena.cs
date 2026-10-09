using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DHMP.Unity.Demo
{
    // The sold package includes this entire example. Client and dedicated server run the same motor and map.
    public sealed class DemoArena : MonoBehaviour
    {
        public DhmpUnitySettings settings;
        public Shader arenaShader;
        private DhmpNetworkManager network;
        private Camera viewCamera;
        private readonly Dictionary<uint, Avatar> avatars = new Dictionary<uint, Avatar>();
        private readonly List<Material> materials = new List<Material>();
        private string playerName = "Player", ownServer = "::1";
        private float lookYaw, lookPitch = 22;
        private bool serverMode, wasConnected;
        private double metricTime;
        private long previousSent, previousReceived;
        private float sentRate, receivedRate;

        private sealed class Avatar
        {
            public GameObject Root;
            public Material Material;
            public Transform Name;
            public Vector3 Previous, Target;
            public Quaternion PreviousRotation, Rotation;
            public double ReceivedAt;
        }

        private void Awake()
        {
            Application.runInBackground = true;
            network = gameObject.AddComponent<DhmpNetworkManager>(); network.settings = settings;
            network.PlayerState += OnPlayerState; network.PlayerLeft += RemovePlayer; network.Stopped += ClearPlayers;
            serverMode = Array.IndexOf(Environment.GetCommandLineArgs(), "--dhmp-server") >= 0;
#if UNITY_SERVER
            serverMode = true;
#endif
            if (serverMode) { network.StartServer(); return; }
            CreateArena();
        }

        private void CreateArena()
        {
            var cameraObject = new GameObject("Arena Camera");
            viewCamera = cameraObject.AddComponent<Camera>(); cameraObject.tag = "MainCamera";
            viewCamera.backgroundColor = new Color(0.055f, 0.075f, 0.12f); viewCamera.clearFlags = CameraClearFlags.SolidColor;
            viewCamera.farClipPlane = 150; viewCamera.transform.position = new Vector3(0, 16, -26);
            viewCamera.transform.LookAt(new Vector3(0, 0, 0));
            Box("Arena floor", new Vector3(0, -0.2f, 0), new Vector3(48, 0.4f, 48), new Color(0.12f, 0.17f, 0.24f));
            for (int i = 0; i < ArenaMotor.PlatformCount; i++)
            {
                ArenaPlatform p = ArenaMotor.GetPlatform(i);
                Box("Jump platform " + (i + 1), new Vector3(p.X, p.Height / 2, p.Z),
                    new Vector3(p.Width, p.Height, p.Depth), Color.Lerp(new Color(0.07f, 0.55f, 0.67f), new Color(0.26f, 0.83f, 0.72f), i / 3f));
            }
            for (int i = -24; i <= 24; i += 4)
            {
                Box("Grid X", new Vector3(i, 0.006f, 0), new Vector3(0.025f, 0.01f, 48), new Color(0.18f, 0.24f, 0.31f));
                Box("Grid Z", new Vector3(0, 0.006f, i), new Vector3(48, 0.01f, 0.025f), new Color(0.18f, 0.24f, 0.31f));
            }
            Box("North boundary", new Vector3(0, 0.15f, 24), new Vector3(48, 0.3f, 0.1f), Color.cyan);
            Box("South boundary", new Vector3(0, 0.15f, -24), new Vector3(48, 0.3f, 0.1f), Color.cyan);
            Box("East boundary", new Vector3(24, 0.15f, 0), new Vector3(0.1f, 0.3f, 48), Color.cyan);
            Box("West boundary", new Vector3(-24, 0.15f, 0), new Vector3(0.1f, 0.3f, 48), Color.cyan);
        }
        private Material Material(Color color)
        {
            var material = new Material(arenaShader); material.color = color; materials.Add(material); return material;
        }
        private GameObject Box(string label, Vector3 position, Vector3 scale, Color color)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube); box.name = label;
            box.transform.SetParent(transform, false); box.transform.position = position; box.transform.localScale = scale;
            box.GetComponent<Renderer>().sharedMaterial = Material(color);
            Destroy(box.GetComponent<Collider>()); // Shared ArenaMotor owns collision on both endpoints.
            return box;
        }
        private void OnPlayerState(ArenaRecord record)
        {
            if (serverMode) return;
            if (!avatars.TryGetValue(record.PlayerId, out Avatar avatar))
            {
                GameObject root = GameObject.CreatePrimitive(PrimitiveType.Capsule); root.name = "Player " + record.PlayerId;
                root.transform.SetParent(transform, false); root.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
                Destroy(root.GetComponent<Collider>());
                Material playerMaterial = Material(Color.HSVToRGB((record.PlayerId * 0.19f) % 1, 0.65f, 1));
                root.GetComponent<Renderer>().sharedMaterial = playerMaterial;
                var label = new GameObject("Player name"); label.transform.SetParent(root.transform, false); label.transform.localPosition = new Vector3(0, 1.4f, 0);
                TextMesh text = label.AddComponent<TextMesh>(); text.text = record.Name; text.richText = false; text.anchor = TextAnchor.MiddleCenter;
                text.characterSize = 0.04f; text.fontSize = 48; text.color = Color.white;
                avatar = new Avatar { Root = root, Material = playerMaterial, Name = label.transform, Target = Position(record.State), Rotation = Quaternion.Euler(0, record.State.Yaw, 0) };
                avatars.Add(record.PlayerId, avatar);
            }
            avatar.Previous = avatar.Target; avatar.PreviousRotation = avatar.Rotation;
            avatar.Target = Position(record.State); avatar.Rotation = Quaternion.Euler(0, record.State.Yaw, 0); avatar.ReceivedAt = DhmpNetworkManager.Now;
        }
        private static Vector3 Position(MotorState state) => new Vector3(state.X, state.Y + ArenaMotor.Height / 2, state.Z);
        private void RemovePlayer(uint id)
        {
            if (!avatars.TryGetValue(id, out Avatar avatar)) return;
            Destroy(avatar.Root); materials.Remove(avatar.Material); Destroy(avatar.Material); avatars.Remove(id);
        }
        private void ClearPlayers()
        {
            foreach (Avatar avatar in avatars.Values)
            { Destroy(avatar.Root); materials.Remove(avatar.Material); Destroy(avatar.Material); }
            avatars.Clear(); wasConnected = false; Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            previousSent = previousReceived = 0; sentRate = receivedRate = 0; metricTime = DhmpNetworkManager.Now;
        }

        private void Update()
        {
            if (serverMode || network == null) return;
            bool connected = network.Client != null && network.Client.Phase == ArenaClientPhase.Connected;
            if (connected && !wasConnected) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            wasConnected = connected;
            if (!connected) return;
            Keyboard keyboard = Keyboard.current; Mouse mouse = Mouse.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            { Cursor.lockState = Cursor.lockState == CursorLockMode.Locked ? CursorLockMode.None : CursorLockMode.Locked; Cursor.visible = Cursor.lockState != CursorLockMode.Locked; }
            bool focused = Application.isFocused && Cursor.lockState == CursorLockMode.Locked;
            if (focused && mouse != null)
            { Vector2 delta = mouse.delta.ReadValue(); lookYaw = Mathf.Repeat(lookYaw + delta.x * 0.12f, 360); lookPitch = Mathf.Clamp(lookPitch - delta.y * 0.1f, -5, 65); }
            Vector2 input = Vector2.zero; bool jump = false;
            if (focused && keyboard != null)
            {
                input.x = (keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0);
                input.y = (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0);
                jump = keyboard.spaceKey.wasPressedThisFrame;
            }
            Vector3 world = Quaternion.Euler(0, lookYaw, 0) * new Vector3(input.x, 0, input.y);
            network.SetInput(new Vector2(world.x, world.z), jump, lookYaw);
            if (DhmpNetworkManager.Now - metricTime >= 1)
            {
                double elapsed = DhmpNetworkManager.Now - metricTime;
                sentRate = (float)((network.Client.SentPayloadBytes - previousSent) / elapsed / 1024);
                receivedRate = (float)((network.Client.ReceivedPayloadBytes - previousReceived) / elapsed / 1024);
                previousSent = network.Client.SentPayloadBytes; previousReceived = network.Client.ReceivedPayloadBytes; metricTime = DhmpNetworkManager.Now;
            }
        }
        private void LateUpdate()
        {
            if (serverMode || viewCamera == null || network.Client == null) return;
            foreach (KeyValuePair<uint, Avatar> entry in avatars)
            {
                Avatar avatar = entry.Value;
                if (entry.Key == network.Client.PlayerId)
                { avatar.Root.transform.position = Position(network.Client.Predicted); avatar.Root.transform.rotation = Quaternion.Euler(0, network.Client.Predicted.Yaw, 0); }
                else
                {
                    float t = Mathf.Clamp01((float)(DhmpNetworkManager.Now - avatar.ReceivedAt) / 0.1f);
                    avatar.Root.transform.position = Vector3.Lerp(avatar.Previous, avatar.Target, t);
                    avatar.Root.transform.rotation = Quaternion.Slerp(avatar.PreviousRotation, avatar.Rotation, t);
                }
                avatar.Name.rotation = viewCamera.transform.rotation;
            }
            if (network.Client.Phase == ArenaClientPhase.Connected)
            {
                Vector3 focus = Position(network.Client.Predicted) + Vector3.up * 0.5f;
                Quaternion rotation = Quaternion.Euler(lookPitch, lookYaw, 0);
                viewCamera.transform.SetPositionAndRotation(focus + rotation * new Vector3(0, 0.5f, -5), rotation);
            }
        }

        private void OnGUI()
        {
            if (serverMode || network == null) return;
            GUILayout.BeginArea(new Rect(20, 20, 370, 470), GUI.skin.box);
            GUILayout.Label("DHMP DEMO ARENA", new GUIStyle(GUI.skin.label) { fontSize = 23, fontStyle = FontStyle.Bold });
            GUILayout.Label(network.Status, new GUIStyle(GUI.skin.label) { wordWrap = true });
            GUILayout.Space(12);
            if (network.Client == null && network.Server == null)
            {
                GUILayout.Label("Player name"); playerName = GUILayout.TextField(playerName, 32);
                GUILayout.Space(10);
                string demo = settings == null ? "" : settings.DemoEndpoint;
                GUI.enabled = !string.IsNullOrWhiteSpace(demo);
                if (GUILayout.Button("Join the DHMP demo server", GUILayout.Height(36))) network.Connect(demo, playerName);
                GUI.enabled = true;
                if (string.IsNullOrWhiteSpace(demo)) GUILayout.Label("The publisher's demo server has not been configured in this preview.", new GUIStyle(GUI.skin.label) { wordWrap = true });
                GUILayout.Space(15); GUILayout.Label("Your own server — IPv6 address"); ownServer = GUILayout.TextField(ownServer);
                if (GUILayout.Button("Connect to own server", GUILayout.Height(32))) network.Connect(ownServer, playerName);
                GUILayout.Space(10);
                if (GUILayout.Button("Start an arena server here")) network.StartServer();
            }
            else
            {
                if (network.Client != null && network.Client.Phase == ArenaClientPhase.Connected)
                {
                    GUILayout.Label(avatars.Count + " players · 30 simulation ticks/s");
                    GUILayout.Label("Input → snapshot: " + network.Client.RoundTripMilliseconds.ToString("0") + " ms");
                    GUILayout.Label("Payload out " + sentRate.ToString("0.0") + " KiB/s · in " + receivedRate.ToString("0.0") + " KiB/s");
                    GUILayout.Label("WASD — move\nMouse — look\nSpace — jump\nEsc — release / capture cursor");
                }
                if (network.Server != null) GUILayout.Label(network.Server.PlayerCount + " players connected");
                if (GUILayout.Button("Disconnect / stop")) network.Stop();
            }
            GUILayout.EndArea();
        }
        private void OnDestroy()
        {
            if (network != null) { network.PlayerState -= OnPlayerState; network.PlayerLeft -= RemovePlayer; network.Stopped -= ClearPlayers; }
            foreach (Material material in materials) if (material != null) Destroy(material);
        }
    }
}

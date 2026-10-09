using System;

namespace DHMP.Unity
{
    public struct MotorState
    {
        // Feet position. Shared simulation is independent of Unity's physics frame rate.
        public float X, Y, Z, VerticalVelocity, Yaw;
        public bool Grounded;
    }

    public readonly struct ArenaPlatform
    {
        public readonly float X, Z, Width, Depth, Height;
        public ArenaPlatform(float x, float z, float width, float depth, float height)
        { X = x; Z = z; Width = width; Depth = depth; Height = height; }
    }

    public static class ArenaMotor
    {
        public const int TickRate = 30;
        public const float StepSeconds = 1f / TickRate;
        public const float Speed = 5f, JumpSpeed = 7f, Gravity = 20f, Radius = 0.35f, Height = 1.8f, Extent = 24f;
        private static readonly ArenaPlatform[] Platforms = {
            new ArenaPlatform(-6, 3, 3, 3, 0.75f), new ArenaPlatform(-2, 3, 3, 3, 1.5f),
            new ArenaPlatform(2, 3, 3, 3, 2.25f), new ArenaPlatform(7, -4, 4, 4, 1f)
        };
        public static int PlatformCount => Platforms.Length;
        public static ArenaPlatform GetPlatform(int index) => Platforms[index];
        public static MotorState Spawn(uint playerId) => new MotorState { X = (playerId % 8) * 1.5f - 5, Z = -9, Grounded = true };

        public static MotorState Step(MotorState state, float x, float z, bool jump, float yaw)
        {
            if (!DhmpWire.Finite(x) || !DhmpWire.Finite(z) || !DhmpWire.Finite(yaw)) return state;
            float magnitude = (float)Math.Sqrt(x * x + z * z);
            if (magnitude > 1) { x /= magnitude; z /= magnitude; }
            state.Yaw = ((yaw % 360) + 360) % 360;
            if (jump && state.Grounded) { state.VerticalVelocity = JumpSpeed; state.Grounded = false; }
            float nextX = Clamp(state.X + x * Speed * StepSeconds, -Extent + Radius, Extent - Radius);
            if (!Blocked(nextX, state.Y, state.Z)) state.X = nextX;
            float nextZ = Clamp(state.Z + z * Speed * StepSeconds, -Extent + Radius, Extent - Radius);
            if (!Blocked(state.X, state.Y, nextZ)) state.Z = nextZ;
            state.VerticalVelocity -= Gravity * StepSeconds;
            float nextY = state.Y + state.VerticalVelocity * StepSeconds;
            float floor = 0;
            foreach (ArenaPlatform platform in Platforms)
                if (Overlap(state.X, state.Z, platform) && state.Y >= platform.Height - 0.001f && nextY <= platform.Height)
                    floor = Math.Max(floor, platform.Height);
            state.Grounded = nextY <= floor && state.VerticalVelocity <= 0;
            if (state.Grounded) { state.Y = floor; state.VerticalVelocity = 0; }
            else state.Y = nextY;
            return state;
        }

        private static bool Blocked(float x, float y, float z)
        {
            foreach (ArenaPlatform platform in Platforms)
                if (y < platform.Height - 0.001f && y + Height > 0 && Overlap(x, z, platform)) return true;
            return false;
        }
        private static bool Overlap(float x, float z, ArenaPlatform p)
        {
            float dx = x - Clamp(x, p.X - p.Width / 2, p.X + p.Width / 2);
            float dz = z - Clamp(z, p.Z - p.Depth / 2, p.Z + p.Depth / 2);
            return dx * dx + dz * dz < Radius * Radius;
        }
        private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
    }
}

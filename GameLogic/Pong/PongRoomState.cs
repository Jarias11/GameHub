using System;

namespace GameLogic.Pong
{
    /// <summary>
    /// Pure server-side state for a Pong room.
    /// No sockets, no ASP.NET, just game data.
    /// </summary>
    public class PongRoomState : GameLogic.IRoomState
    {
        public string RoomCode { get; }

        // --- Base speeds & scaling (units per second) ---
        // These are tuned to roughly match your old behavior at ~30 FPS.
        public float BaseBallSpeedX { get; } = 21f;  // was 0.7 * 30
        public float BaseBallSpeedY { get; } = 12f;  // was 0.4 * 30
        public float BasePaddleSpeed { get; } = 45f; // was 1.5 * 30

        /// <summary>Total number of paddle hits this match.</summary>
        public int HitCount { get; set; } = 0;

        /// <summary>Scales ball speed (no cap).</summary>
        public float BallSpeedMultiplier { get; set; } = 1f;

        /// <summary>Scales paddle speed (capped at 4×).</summary>
        public float PaddleSpeedMultiplier { get; set; } = 1f;

        public float BallX { get; set; } = 50;
        public float BallY { get; set; } = 50;
        public float VelX { get; set; } = 0f; // units/sec
        public float VelY { get; set; } = 0f; // units/sec

        public float Paddle1Y { get; set; } = 50;
        public float Paddle2Y { get; set; } = 50;

        public int Direction1 { get; set; } = 0;
        public int Direction2 { get; set; } = 0;

        public int Score1 { get; set; } = 0;
        public int Score2 { get; set; } = 0;

        public PongRoomState(string roomCode)
        {
            RoomCode = roomCode;
        }

        public void ResetBall(Random rng, int direction = 0)
        {
            BallX = 50;
            BallY = 50;

            var dirX = direction != 0
                ? direction
                : (rng.Next(2) == 0 ? -1 : 1);

            var dirY = rng.Next(2) == 0 ? 1 : -1;

            // Speeds are now per second, scaled by multiplier
            var speedX = BaseBallSpeedX * BallSpeedMultiplier;
            var speedY = BaseBallSpeedY * BallSpeedMultiplier;

            VelX = speedX * dirX;
            VelY = speedY * dirY;
        }
    }
}

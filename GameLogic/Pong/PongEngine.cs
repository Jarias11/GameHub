using System;

namespace GameLogic.Pong
{
	/// <summary>
	/// Pure Pong rules/physics. No networking, no room dictionaries.
	/// </summary>
	public static class PongEngine
	{
		public static void Update(PongRoomState s, Random rng)
		{
			const float dt = 1.0f;   // simple timestep

			const float minY = 10f;
			const float maxY = 90f;
			const float paddleReach = 12f;


			// Current paddle speed with cap 4×
			var rawPaddleSpeed = s.BasePaddleSpeed * s.PaddleSpeedMultiplier;
			var maxPaddleSpeed = s.BasePaddleSpeed * 4f;
			var paddleSpeed = rawPaddleSpeed > maxPaddleSpeed ? maxPaddleSpeed : rawPaddleSpeed;

			// Move paddles
			s.Paddle1Y = Clamp(s.Paddle1Y + s.Direction1 * paddleSpeed * dt, minY, maxY);
			s.Paddle2Y = Clamp(s.Paddle2Y + s.Direction2 * paddleSpeed * dt, minY, maxY);



			// Move ball
			s.BallX += s.VelX * dt;
			s.BallY += s.VelY * dt;

			// Top / bottom walls (clamp + bounce)
			if (s.BallY <= 0)
			{
				s.BallY = 0;
				s.VelY = -s.VelY;
			}
			else if (s.BallY >= 100)
			{
				s.BallY = 100;
				s.VelY = -s.VelY;
			}

			bool hitPaddle = false;

			// Left paddle
			if (s.VelX < 0 && s.BallX <= 5 && Math.Abs(s.BallY - s.Paddle1Y) <= paddleReach)
			{
				s.BallX = 5;
				s.VelX = -s.VelX;
				hitPaddle = true;
			}

			// Right paddle
			if (s.VelX > 0 && s.BallX >= 95 && Math.Abs(s.BallY - s.Paddle2Y) <= paddleReach)
			{
				s.BallX = 95;
				s.VelX = -s.VelX;
				hitPaddle = true;
			}

			if (hitPaddle)
			{
				s.HitCount++;

				// Every 4 hits → faster ball (no cap)
				if (s.HitCount % 4 == 0)
				{
					s.BallSpeedMultiplier += 0.30f; // ~15% faster every 4 hits

					var dirX = Math.Sign(s.VelX);
					var dirY = Math.Sign(s.VelY);
					if (dirY == 0) dirY = 1;

					var speedX = s.BaseBallSpeedX * s.BallSpeedMultiplier;
					var speedY = s.BaseBallSpeedY * s.BallSpeedMultiplier;

					s.VelX = speedX * dirX;
					s.VelY = speedY * dirY;
				}

				// Every 8 hits → faster paddles, capped at 4×
				if (s.HitCount % 8 == 0)
				{
					var newMul = s.PaddleSpeedMultiplier + 0.2f; // ~20% step
					if (newMul > 4f) newMul = 4f;
					s.PaddleSpeedMultiplier = newMul;
				}
			}

			// Scoring (keep logic the same; ball may go slightly past 0–100,
			// but we’ll clamp visually in the client so it never leaves the window)
			if (s.BallX < -5)
			{
				s.Score2++;
				s.HitCount = 0;
				s.BallSpeedMultiplier = 1f;
				s.PaddleSpeedMultiplier = 1f;
				s.ResetBall(rng, 1);
			}
			else if (s.BallX > 105)
			{
				s.Score1++;
				s.HitCount = 0;
				s.BallSpeedMultiplier = 1f;
				s.PaddleSpeedMultiplier = 1f;
				s.ResetBall(rng, -1);
				
			}
		}

		private static float Clamp(float v, float min, float max) =>
			v < min ? min : (v > max ? max : v);
	}
}

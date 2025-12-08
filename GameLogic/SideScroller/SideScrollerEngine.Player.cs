using System;

namespace GameLogic.SideScroller
{
	public sealed partial class SideScrollerEngine
	{
		// ── Player movement tuning (in BLOCKS/sec or seconds) ──────────────

		// Horizontal
		private const float MoveSpeedBlocksPerSecond = 6f;          // top speed
		private const float RunAccelBlocksPerSecondSquared = 20f;   // how fast you reach top speed
		private const float RunDecelBlocksPerSecondSquared = 25f;   // how fast you slow down with no input
		private const float AirControlMultiplier = 0.7f;            // less control in air

		// Vertical
		private const float GravityBlocksPerSecondSquared = 30f;
		private const float JumpHeightBlocks = 2.8f;                // max jump height if fully held

		// Coyote time: how long after walking off a ledge you can still jump
		private const float CoyoteTimeSeconds = 0.12f;

		// Variable jump: extra gravity when button is released early
		private const float JumpCutGravityMultiplier = 2.5f;

		// Derived pixel-based values
		private float _moveSpeed;
		private float _gravity;
		private float _jumpSpeed;
		private float _runAccel;
		private float _runDecel;

		// Player state
		private float _playerX;
		private float _playerY;
		private float _playerVelX;
		private float _playerVelY;
		private float _prevPlayerX;
		private float _prevPlayerY;
		private bool _isOnGround;

		// Coyote timer
		private float _coyoteTimer;
		private bool _wasJumpHeldLastFrame;

		// Index of the platform we’re currently standing on, or -1.
		private int _standingPlatformIndex = -1;

		/// <summary>
		/// Called from ctor to precompute movement tuning.
		/// </summary>
		private void InitializePlayerMovement()
		{
			_moveSpeed = MoveSpeedBlocksPerSecond * BlockSize;
			_gravity = GravityBlocksPerSecondSquared * BlockSize;

			_runAccel = RunAccelBlocksPerSecondSquared * BlockSize;
			_runDecel = RunDecelBlocksPerSecondSquared * BlockSize;

			// Compute jump speed so max jump height ≈ JumpHeightBlocks.
			// Physics: height = (JumpSpeed^2) / (2 * Gravity)
			// => JumpSpeed = -sqrt(2 * Gravity * height)
			float jumpHeightPixels = JumpHeightBlocks * BlockSize;
			_jumpSpeed = -MathF.Sqrt(2f * _gravity * jumpHeightPixels);
		}

		/// <summary>
		/// Reset all player-related state. Called from Reset().
		/// </summary>
		private void ResetPlayer()
		{
			_playerX = BlocksToX(2);
			_playerY = BlocksAboveGroundToY(2);
			_prevPlayerX = _playerX;
			_prevPlayerY = _playerY;

			_playerVelX = 0f;
			_playerVelY = 0f;
			_isOnGround = false;
			_standingPlatformIndex = -1;

			_coyoteTimer = 0f;
			_wasJumpHeldLastFrame = false;
		}

		/// <summary>
		/// Integrates player input + physics for this frame.
		/// Geometry / collisions / camera are handled elsewhere.
		/// </summary>
		private void SimulatePlayer(float dt, bool leftHeld, bool rightHeld, bool jumpHeld)
		{
			// Remember previous position for collision logic.
			_prevPlayerX = _playerX;
			_prevPlayerY = _playerY;

			// ───────────────── Coyote time bookkeeping ─────────────────────
			// _isOnGround here is from *last frame's* collision result.
			if (_isOnGround)
			{
				_coyoteTimer = CoyoteTimeSeconds;
			}
			else
			{
				_coyoteTimer = MathF.Max(0f, _coyoteTimer - dt);
			}

			bool jumpJustPressed = jumpHeld && !_wasJumpHeldLastFrame;

			// ───────────────── Horizontal movement (accel/decel) ────────────
			float targetDir = 0f;
			if (leftHeld) targetDir -= 1f;
			if (rightHeld) targetDir += 1f;

			float accel = _runAccel;
			float decel = _runDecel;

			// We can optionally reduce control in the air.
			if (!_isOnGround)
			{
				accel *= AirControlMultiplier;
				decel *= AirControlMultiplier;
			}

			float maxSpeed = _moveSpeed;
			float currentXVel = _playerVelX;

			if (targetDir != 0f)
			{
				float desiredVel = targetDir * maxSpeed;

				// If changing direction, use decel first to brake.
				if (MathF.Sign(desiredVel) != MathF.Sign(currentXVel))
				{
					currentXVel = MoveTowards(currentXVel, desiredVel, decel * dt);
				}
				else
				{
					currentXVel = MoveTowards(currentXVel, desiredVel, accel * dt);
				}
			}
			else
			{
				// No input: smoothly decelerate to zero.
				currentXVel = MoveTowards(currentXVel, 0f, decel * dt);
			}

			_playerVelX = currentXVel;

			// ───────────────── Vertical (jump + gravity) ────────────────────

			// 1) Start a jump if:
			//    - jump was just pressed
			//    - AND (we are on the ground OR within coyote time)
			if (jumpJustPressed && (_isOnGround || _coyoteTimer > 0f))
			{
				_playerVelY = _jumpSpeed;
				_isOnGround = false;
				_coyoteTimer = 0f;   // consume coyote time
			}

			// 2) Apply gravity.
			float gravityThisFrame = _gravity;

			// Variable jump: if player lets go of jump while still moving up,
			// increase gravity so they don't reach full height.
			if (!jumpHeld && _playerVelY < 0f)
			{
				gravityThisFrame *= JumpCutGravityMultiplier;
			}

			_playerVelY += gravityThisFrame * dt;

			// ───────────────── Integrate position ───────────────────────────
			_playerX += _playerVelX * dt;
			_playerY += _playerVelY * dt;

			// Collision step will re-set this if we land on something.
			_isOnGround = false;

			// For next frame's "just pressed" detection
			_wasJumpHeldLastFrame = jumpHeld;
		}

		/// <summary>
		/// Helper: smoothly moves value toward target by maxDelta, without overshoot.
		/// </summary>
		private static float MoveTowards(float current, float target, float maxDelta)
		{
			float delta = target - current;
			if (MathF.Abs(delta) <= maxDelta)
				return target;

			return current + MathF.Sign(delta) * maxDelta;
		}
	}
}

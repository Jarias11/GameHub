// GameLogic/JumpsEngine.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace GameLogic.Jumps
{
	public readonly struct JumpsInputState
	{
		public bool Left { get; init; }
		public bool Right { get; init; }
		public bool Down { get; init; }
		public bool JumpHeld { get; init; }
	}

	public enum JumpsPickupType
	{
		Coin,
		JumpBoost,
		SpeedBoost,
		Magnet,
		DoubleJump,
		SlowScroll
	}

	public sealed class JumpsPickupView
	{
		public JumpsPickupType Type { get; init; }
		public bool Collected { get; init; }
		public float X { get; init; }
		public float Y { get; init; }
		public bool IsMagnetPulling { get; init; }
	}

	public sealed class JumpsPlatformView
	{
		public float X { get; init; }
		public float Y { get; init; }
		public float Width { get; init; }
		public float Height { get; init; }
		public JumpsPickupView? Pickup { get; init; }
	}

	/// <summary>
	/// Pure game logic / physics for the Jumps game.
	/// No WPF, no Skia, no input system. Reusable by client & server.
	/// </summary>
	public sealed class JumpsEngine
	{
		// ==== Public constants (for rendering/layout) ======================

		public const int BaseColumns = 5;
		public const float WorldWidth = 480f;
		public const float WorldHeight = 480f;

		public const float PlayerSize = 24f;
		public const float PlatformWidth = 80f;
		public const float PlatformHeight = 8f;
		public const float RowSpacing = 80f;

		public const float Gravity = 1200f;
		public const float BaseJumpVelocity = -640f;

		public const float MoveSpeedBase = 175f;
		public const float StartSpeedFactor = 0.65f;
		public const float WarmupDuration = 3.0f;
		public const float HorizontalScale = 0.85f;

		public const float BaseScrollSpeed = 30f;
		public const float ScrollAccel = 1.01f;

		public const float PickupRadius = 6f;

		public const float LandingVerticalForgiveness = 6f;
		public const float LandingHorizontalForgiveness = 10f;

		public const float JumpBufferTime = 0.12f;

		public const float DropThroughIgnoreTime = 0.25f;
		public const float DropThroughKickSpeed = 80f;

		public const float PowerupDuration = 10f;
		public const float JumpBoostMultiplier = 1.25f;
		public const float SpeedBoostMultiplier = 1.35f;
		public const double CoinChance = 0.70;
		public const double JumpBoostChance = 0.02;
		public const double SpeedBoostChance = 0.02;
		public const double MagnetChance = 0.02;
		public const double DoubleJumpChance = 0.02;
		public const double SlowScrollChance = 0.02;

		public const float MagnetRadiusWorld = RowSpacing * 2f;
		public const float MagnetPullSpeed = 520f;
		public const float MagnetSnapDistance = 8f;

		public const int ExtraAirJumpsPerUse = 1;

		public const float SlowScrollMultiplier = 0.25f;

		public const float DeathMargin = 100f;
		public const float DeathInputLockDuration = 3f;

		// ==== Internal types ===============================================

		private sealed class Pickup
		{
			public JumpsPickupType Type;
			public bool Collected;

			public bool IsMagnetPulling;
			public float WorldX;
			public float WorldY;
		}

		private sealed class Platform
		{
			public int RowIndex;
			public float X;
			public float Y;
			public float Width;
			public float Height;
			public Pickup? Pickup;
		}

		// ==== Runtime state: player ========================================

		private float _playerX;
		private float _playerY;
		private float _playerVX;
		private float _playerVY;
		private bool _isGrounded;
		private bool _hasStarted;
		private bool _spaceWasHeldLastFrame;
		private bool _isJumping;
		private bool _jumpCutApplied;
		private float _jumpBufferRemaining;
		private int _airJumpsRemaining;

		// ==== Runtime state: world / platforms / levels ====================

		private float _groundY;
		private float _scrollSpeed;
		private float _elapsedSinceStart;
		private int _level = 1;

		private readonly List<Platform> _platforms = new();
		private int _nextRowIndex;
		private int _bufferRows = 1;
		private int _currentColumns = BaseColumns;

		// ==== Runtime state: camera ========================================

		private float _cameraTop;
		private float _cameraTargetTop;
		private float _cameraZoom = 1f;
		private float _cameraTargetZoom = 1f;

		// ==== Runtime state: power-ups =====================================

		private bool _jumpBoostActive;
		private float _jumpBoostTimeRemaining;

		private bool _speedBoostActive;
		private float _speedBoostTimeRemaining;

		private bool _magnetActive;
		private float _magnetTimeRemaining;

		private bool _doubleJumpActive;
		private float _doubleJumpTimeRemaining;

		private bool _slowScrollActive;
		private float _slowScrollTimeRemaining;

		private Platform? _currentPlatform;
		private Platform? _dropThroughPlatform;
		private float _dropThroughTimer;

		// ==== Runtime state: scoring & death ===============================

		private int _score;
		private int _highScore;
		private bool _inputLocked;
		private float _inputLockTimer;
		private bool _pendingReset;
		private bool _isDead;

		// debug
		private float _currentMoveSpeed;

		// ==== General ======================================================

		private readonly Random _rng;

		// Cached render views (avoid leaking internal mutable types)
		private readonly List<JumpsPlatformView> _platformViews = new();

		// ==== Public read-only properties ==================================

		public float PlayerX => _playerX;
		public float PlayerY => _playerY;

		public float GroundY => _groundY;

		public float CameraTop => _cameraTop;
		public float CameraZoom => _cameraZoom;

		public int Level => _level;
		public int Score => _score;
		public int HighScore => _highScore;

		public float ScrollSpeed => _scrollSpeed;
		public float CurrentMoveSpeed => _currentMoveSpeed;

		public bool IsDead => _isDead;

		public bool JumpBoostActive => _jumpBoostActive;
		public float JumpBoostTimeRemaining => _jumpBoostTimeRemaining;

		public bool SpeedBoostActive => _speedBoostActive;
		public float SpeedBoostTimeRemaining => _speedBoostTimeRemaining;

		public bool MagnetActive => _magnetActive;
		public float MagnetTimeRemaining => _magnetTimeRemaining;

		public bool DoubleJumpActive => _doubleJumpActive;
		public float DoubleJumpTimeRemaining => _doubleJumpTimeRemaining;

		public bool SlowScrollActive => _slowScrollActive;
		public float SlowScrollTimeRemaining => _slowScrollTimeRemaining;

		public IReadOnlyList<JumpsPlatformView> Platforms => _platformViews;

		// ===================================================================

		public JumpsEngine(int seed)
		{
			_rng = new Random(seed);
			Reset();
		}

		public JumpsEngine()
			: this(Environment.TickCount)
		{
		}

		// ==== Reset ========================================================

		public void Reset()
		{
			_platforms.Clear();

			_groundY = WorldHeight - 40f;

			_playerX = (WorldWidth - PlayerSize) / 2f;
			_playerY = _groundY - PlayerSize;
			_playerVX = 0f;
			_playerVY = 0f;
			_isGrounded = true;
			_hasStarted = false;
			_spaceWasHeldLastFrame = false;
			_isJumping = false;
			_jumpCutApplied = false;
			_jumpBufferRemaining = 0f;

			_currentPlatform = null;
			_dropThroughPlatform = null;
			_dropThroughTimer = 0f;

			_elapsedSinceStart = 0f;
			_scrollSpeed = 0f;
			_score = 0;

			_level = 1;

			_jumpBoostActive = false;
			_jumpBoostTimeRemaining = 0f;
			_speedBoostActive = false;
			_speedBoostTimeRemaining = 0f;

			_magnetActive = false;
			_magnetTimeRemaining = 0f;

			_doubleJumpActive = false;
			_doubleJumpTimeRemaining = 0f;
			_airJumpsRemaining = 0;

			_slowScrollActive = false;
			_slowScrollTimeRemaining = 0f;

			_nextRowIndex = 0;
			float y = _groundY - RowSpacing;
			for (int i = 0; i < 10; i++)
			{
				GeneratePlatformsRow(y);
				y -= RowSpacing;
				_nextRowIndex++;
			}

			_pendingReset = false;
			_isDead = false;
			_inputLocked = false;
			_inputLockTimer = 0f;

			_bufferRows = 1;
			_currentColumns = BaseColumns;
			_cameraTop = 0f;
			_cameraTargetTop = 0f;
			_cameraZoom = 1f;
			_cameraTargetZoom = 1f;

			UpdatePlatformViews();
		}

		// ==== Public step ===================================================

		public void Step(double dt, JumpsInputState input)
		{
			float fdt = (float)dt;

			// Input lock timer & auto-reset after death
			if (_inputLocked)
			{
				_inputLockTimer -= fdt;
				if (_inputLockTimer <= 0f)
				{
					_inputLocked = false;
					_inputLockTimer = 0f;

					if (_pendingReset)
					{
						_pendingReset = false;
						Reset();
					}
				}
			}

			// Tick jump buffer
			if (_jumpBufferRemaining > 0f)
			{
				_jumpBufferRemaining -= fdt;
				if (_jumpBufferRemaining < 0f)
					_jumpBufferRemaining = 0f;
			}

			// Tick drop-through timer
			if (_dropThroughTimer > 0f)
			{
				_dropThroughTimer -= fdt;
				if (_dropThroughTimer <= 0f)
				{
					_dropThroughTimer = 0f;
					_dropThroughPlatform = null;
				}
			}

			UpdatePhysics(fdt, input);
			UpdatePlatformViews();
		}

		// ==== Physics core ==================================================

		private void UpdatePhysics(float dt, JumpsInputState input)
		{
			if (_inputLocked && _pendingReset)
				return;

			HandleInput(dt, input);

			float effectiveScrollSpeed = UpdateScrollSpeed(dt);
			TickPowerupTimers(dt);

			float currentMoveSpeed = ComputeMoveSpeed(effectiveScrollSpeed);
			_currentMoveSpeed = currentMoveSpeed;

			ApplyHorizontalMovement(currentMoveSpeed, dt);

			if (!_hasStarted)
			{
				StickPlayerToGround();
				return;
			}

			ApplyVerticalScroll(effectiveScrollSpeed, dt);

			bool died = ApplyVerticalMovementAndLanding(dt);
			if (died)
				return;

			RecycleAndSpawnRows();
			ApplyMagnetAutoCollect(dt);
			CheckPickupCollisions();

			UpdateLevelAndCamera();
			SmoothCamera(effectiveScrollSpeed, dt);
			SmoothZoom(dt);
		}

		// ==== Input handling ===============================================

		private void HandleInput(float dt, JumpsInputState input)
		{
			if (_inputLocked)
			{
				_playerVX = 0f;
				_spaceWasHeldLastFrame = false;
				return;
			}

			bool left = input.Left;
			bool right = input.Right;
			bool down = input.Down;

			_playerVX = 0f;
			if (left && !right)
				_playerVX = -1f;
			else if (right && !left)
				_playerVX = 1f;

			bool spaceHeld = input.JumpHeld;
			bool spacePressedThisFrame = spaceHeld && !_spaceWasHeldLastFrame;

			if (spacePressedThisFrame)
			{
				if (_isGrounded && _currentPlatform != null && down)
				{
					StartDropThrough();
				}
				else if (_isGrounded)
				{
					StartJump();
				}
				else if (_doubleJumpActive && _airJumpsRemaining > 0)
				{
					UseAirJump();
				}
				else
				{
					_jumpBufferRemaining = JumpBufferTime;
				}
			}

			if (!spaceHeld && _spaceWasHeldLastFrame && _isJumping && !_jumpCutApplied && _playerVY < 0f)
			{
				_playerVY *= 0.4f;
				_jumpCutApplied = true;
			}

			_spaceWasHeldLastFrame = spaceHeld;
		}

		private void StartJump()
		{
			if (!_isGrounded)
				return;

			_hasStarted = true;
			_isGrounded = false;
			_isJumping = true;
			_jumpCutApplied = false;
			_currentPlatform = null;

			float jumpVelocity = GetJumpVelocityForCurrentLevel();

			if (_jumpBoostActive)
				jumpVelocity *= JumpBoostMultiplier;

			_playerVY = jumpVelocity;
		}

		private void UseAirJump()
		{
			if (!_doubleJumpActive || _airJumpsRemaining <= 0)
				return;

			_hasStarted = true;
			_isGrounded = false;
			_isJumping = true;
			_jumpCutApplied = false;
			_currentPlatform = null;

			float jumpVelocity = GetJumpVelocityForCurrentLevel();

			if (_jumpBoostActive)
				jumpVelocity *= JumpBoostMultiplier;

			_playerVY = jumpVelocity;
			_airJumpsRemaining--;
		}

		private float GetJumpVelocityForCurrentLevel()
		{
			float vMag = -BaseJumpVelocity;
			float baseHeight = (vMag * vMag) / (2f * Gravity);

			int extraPlatforms = Math.Max(0, (_level - 1) / 5);
			const int MaxExtraPlatforms = 4;
			if (extraPlatforms > MaxExtraPlatforms)
				extraPlatforms = MaxExtraPlatforms;

			float targetHeight = baseHeight + extraPlatforms * RowSpacing;
			float scale = MathF.Sqrt(targetHeight / baseHeight);
			float scaledMag = vMag * scale;

			return -scaledMag;
		}

		private void StartDropThrough()
		{
			if (!_isGrounded || _currentPlatform == null)
				return;

			_hasStarted = true;

			_isGrounded = false;
			_isJumping = false;
			_jumpCutApplied = false;

			_dropThroughPlatform = _currentPlatform;
			_dropThroughTimer = DropThroughIgnoreTime;
			_currentPlatform = null;

			if (_playerVY < 0f)
				_playerVY = 0f;

			_playerVY += DropThroughKickSpeed;
		}

		// ==== Scroll / powerups / movement =================================

		private float UpdateScrollSpeed(float dt)
		{
			if (_hasStarted)
			{
				_elapsedSinceStart += dt;
				_scrollSpeed = BaseScrollSpeed + ScrollAccel * _elapsedSinceStart;
			}
			else
			{
				_scrollSpeed = 0f;
			}

			float effectiveScroll = _scrollSpeed;
			if (_slowScrollActive)
				effectiveScroll *= SlowScrollMultiplier;

			return effectiveScroll;
		}

		private void TickPowerupTimers(float dt)
		{
			TickPowerup(ref _jumpBoostActive, ref _jumpBoostTimeRemaining, dt);
			TickPowerup(ref _speedBoostActive, ref _speedBoostTimeRemaining, dt);
			TickPowerup(ref _magnetActive, ref _magnetTimeRemaining, dt);
			TickPowerup(ref _slowScrollActive, ref _slowScrollTimeRemaining, dt);

			TickPowerup(ref _doubleJumpActive, ref _doubleJumpTimeRemaining, dt);
			if (!_doubleJumpActive)
				_airJumpsRemaining = 0;
		}

		private static void TickPowerup(ref bool active, ref float timeRemaining, float dt)
		{
			if (!active)
				return;

			timeRemaining -= dt;
			if (timeRemaining <= 0f)
			{
				timeRemaining = 0f;
				active = false;
			}
		}

		private float ComputeMoveSpeed(float effectiveScrollSpeed)
		{
			float rawFactor = _hasStarted
				? (effectiveScrollSpeed / BaseScrollSpeed)
				: 1f;

			float t = _elapsedSinceStart / WarmupDuration;
			if (t < 0f) t = 0f;
			if (t > 1f) t = 1f;

			float baseFactor = StartSpeedFactor + (1f - StartSpeedFactor) * t;
			float difficultyFactor = 1f + (MathF.Sqrt(rawFactor) - 1f) * HorizontalScale;

			float speedFactor = baseFactor * difficultyFactor;
			if (speedFactor < StartSpeedFactor)
				speedFactor = StartSpeedFactor;

			float currentMoveSpeed = MoveSpeedBase * speedFactor;

			if (_speedBoostActive)
				currentMoveSpeed *= SpeedBoostMultiplier;

			return currentMoveSpeed;
		}

		private void ApplyHorizontalMovement(float currentMoveSpeed, float dt)
		{
			_playerX += _playerVX * currentMoveSpeed * dt;

			if (_playerX < 0f) _playerX = 0f;
			if (_playerX > WorldWidth - PlayerSize) _playerX = WorldWidth - PlayerSize;
		}

		private void StickPlayerToGround()
		{
			_playerY = _groundY - PlayerSize;
		}

		private void ApplyVerticalScroll(float effectiveScrollSpeed, float dt)
		{
			float scrollDelta = effectiveScrollSpeed * dt;

			_playerY += scrollDelta;
			_groundY += scrollDelta;

			foreach (var p in _platforms)
				p.Y += scrollDelta;
		}

		private bool ApplyVerticalMovementAndLanding(float dt)
		{
			float prevY = _playerY;
			float prevBottom = prevY + PlayerSize;

			_playerVY += Gravity * dt;
			_playerY += _playerVY * dt;

			if (_playerVY >= 0f)
				_isJumping = false;

			float bottom = _playerY + PlayerSize;

			if (_playerVY > 0f)
				TryLandOnPlatform(prevBottom, bottom);

			if (bottom > WorldHeight + DeathMargin)
			{
				if (!_isDead)
				{
					_isDead = true;
					LockInputForDeath();
				}
				return true;
			}

			return false;
		}

		private void TryLandOnPlatform(float prevBottom, float bottom)
		{
			foreach (var p in _platforms)
			{
				if (_dropThroughPlatform == p && _dropThroughTimer > 0f)
					continue;

				float platformTop = p.Y;
				float playerCenterX = _playerX + PlayerSize / 2f;

				bool passesVertically =
					prevBottom <= platformTop + LandingVerticalForgiveness &&
					bottom >= platformTop - LandingVerticalForgiveness;

				bool withinHorizontal =
					playerCenterX >= p.X - LandingHorizontalForgiveness &&
					playerCenterX <= p.X + p.Width + LandingHorizontalForgiveness;

				if (!passesVertically || !withinHorizontal)
					continue;

				_playerY = p.Y - PlayerSize;
				_playerVY = 0f;
				_isGrounded = true;
				_isJumping = false;
				_jumpCutApplied = false;
				_currentPlatform = p;

				_airJumpsRemaining = _doubleJumpActive ? ExtraAirJumpsPerUse : 0;

				if (_jumpBufferRemaining > 0f)
				{
					_jumpBufferRemaining = 0f;
					StartJump();
				}

				break;
			}
		}

		private void LockInputForDeath()
		{
			_inputLocked = true;
			_inputLockTimer = DeathInputLockDuration;
			_pendingReset = true;

			_playerVX = 0f;
			_spaceWasHeldLastFrame = false;
			// We can't clear hardware key state here (that was WPF-specific),
			// but we do clear our internal "was held" so press logic is consistent.
		}

		// ==== World generation / recycle ===================================

		private void GeneratePlatformsRow(float y)
		{
			int platformCount = _rng.Next(1, 3); // 1 or 2

			var lanes = Enumerable.Range(0, _currentColumns)
				.OrderBy(_ => _rng.Next())
				.Take(platformCount);

			float laneWidth = WorldWidth / _currentColumns;

			foreach (int lane in lanes)
			{
				float centerX = laneWidth * (lane + 0.5f);
				float x = centerX - PlatformWidth / 2f;

				Pickup? pickup = null;
				double roll = _rng.NextDouble();

				if (roll < CoinChance)
					pickup = new Pickup { Type = JumpsPickupType.Coin, Collected = false };
				else if (roll < CoinChance + JumpBoostChance)
					pickup = new Pickup { Type = JumpsPickupType.JumpBoost, Collected = false };
				else if (roll < CoinChance + JumpBoostChance + SpeedBoostChance)
					pickup = new Pickup { Type = JumpsPickupType.SpeedBoost, Collected = false };
				else if (roll < CoinChance + JumpBoostChance + SpeedBoostChance + MagnetChance)
					pickup = new Pickup { Type = JumpsPickupType.Magnet, Collected = false };
				else if (roll < CoinChance + JumpBoostChance + SpeedBoostChance + MagnetChance + DoubleJumpChance)
					pickup = new Pickup { Type = JumpsPickupType.DoubleJump, Collected = false };
				else if (roll < CoinChance + JumpBoostChance + SpeedBoostChance + MagnetChance + DoubleJumpChance + SlowScrollChance)
					pickup = new Pickup { Type = JumpsPickupType.SlowScroll, Collected = false };

				_platforms.Add(new Platform
				{
					RowIndex = _nextRowIndex,
					X = x,
					Y = y,
					Width = PlatformWidth,
					Height = PlatformHeight,
					Pickup = pickup
				});
			}
		}

		private void RecycleAndSpawnRows()
		{
			float bottomLimit = WorldHeight + RowSpacing * _bufferRows;
			_platforms.RemoveAll(p => p.Y > bottomLimit);

			if (_platforms.Count == 0)
			{
				float y = _groundY - RowSpacing;
				for (int i = 0; i < 10; i++)
				{
					GeneratePlatformsRow(y);
					y -= RowSpacing;
					_nextRowIndex++;
				}
				return;
			}

			float minY = _platforms.Min(p => p.Y);

			while (minY > -RowSpacing * _bufferRows)
			{
				float newRowY = minY - RowSpacing;
				GeneratePlatformsRow(newRowY);
				_nextRowIndex++;
				minY = newRowY;
			}
		}

		private void UpdateLevelAndCamera()
		{
			int targetLevel = 1 + (int)(_scrollSpeed / 50f);
			if (targetLevel < 1)
				targetLevel = 1;

			if (targetLevel <= _level)
				return;

			_level = targetLevel;

			int zoomSteps = _level / 2;

			const float ZoomStep = 0.1f;
			float targetZoom = 1f - zoomSteps * ZoomStep;
			if (targetZoom < 0.6f)
				targetZoom = 0.6f;

			_cameraTargetZoom = targetZoom;
			_cameraTargetTop = 0f;

			int extraPairs = zoomSteps / 2;
			_bufferRows = 1 + extraPairs;

			int extraColumnPairs = zoomSteps / 2;
			_currentColumns = BaseColumns + extraColumnPairs * 2;
		}

		private void SmoothCamera(float effectiveScrollSpeed, float dt)
		{
			float cameraPanSpeed = MathF.Max(effectiveScrollSpeed, BaseScrollSpeed);
			float delta = _cameraTargetTop - _cameraTop;

			if (MathF.Abs(delta) <= 0.01f)
				return;

			float maxMove = cameraPanSpeed * dt;

			if (MathF.Abs(delta) <= maxMove)
				_cameraTop = _cameraTargetTop;
			else
				_cameraTop += MathF.Sign(delta) * maxMove;
		}

		private void SmoothZoom(float dt)
		{
			const float ZoomChangeSpeed = 0.5f;
			float zoomDelta = _cameraTargetZoom - _cameraZoom;

			if (MathF.Abs(zoomDelta) <= 0.001f)
				return;

			float maxChange = ZoomChangeSpeed * dt;

			if (MathF.Abs(zoomDelta) <= maxChange)
				_cameraZoom = _cameraTargetZoom;
			else
				_cameraZoom += MathF.Sign(zoomDelta) * maxChange;
		}

		// ==== Magnet & pickups =============================================

		private void ApplyMagnetAutoCollect(float dt)
		{
			float playerCenterX = _playerX + PlayerSize / 2f;
			float playerCenterY = _playerY + PlayerSize / 2f;

			float radius = MagnetRadiusWorld;
			float radiusSq = radius * radius;

			foreach (var p in _platforms)
			{
				var pickup = p.Pickup;
				if (pickup == null || pickup.Collected || pickup.Type != JumpsPickupType.Coin)
					continue;

				if (_magnetActive && !pickup.IsMagnetPulling)
				{
					float cx = p.X + p.Width / 2f;
					float cy = p.Y - PickupRadius - 2f;

					float dx0 = cx - playerCenterX;
					float dy0 = cy - playerCenterY;

					if (dx0 * dx0 + dy0 * dy0 <= radiusSq)
					{
						pickup.IsMagnetPulling = true;
						pickup.WorldX = cx;
						pickup.WorldY = cy;
					}
				}

				if (!pickup.IsMagnetPulling)
					continue;

				float dx = playerCenterX - pickup.WorldX;
				float dy = playerCenterY - pickup.WorldY;
				float distSq = dx * dx + dy * dy;

				if (distSq <= MagnetSnapDistance * MagnetSnapDistance)
				{
					pickup.Collected = true;
					pickup.IsMagnetPulling = false;
					_score++;
					if (_score > _highScore)
						_highScore = _score;
					continue;
				}

				float dist = MathF.Sqrt(distSq);
				if (dist > 0.0001f)
				{
					float nx = dx / dist;
					float ny = dy / dist;

					pickup.WorldX += nx * MagnetPullSpeed * dt;
					pickup.WorldY += ny * MagnetPullSpeed * dt;
				}
			}
		}

		private void CheckPickupCollisions()
		{
			float px1 = _playerX;
			float py1 = _playerY;
			float px2 = px1 + PlayerSize;
			float py2 = py1 + PlayerSize;

			foreach (var p in _platforms)
			{
				var pickup = p.Pickup;
				if (pickup == null || pickup.Collected)
					continue;

				float cx, cy;

				if (pickup.IsMagnetPulling)
				{
					cx = pickup.WorldX;
					cy = pickup.WorldY;
				}
				else
				{
					cx = p.X + p.Width / 2f;
					cy = p.Y - PickupRadius - 2f;
				}

				float cLeft = cx - PickupRadius;
				float cRight = cx + PickupRadius;
				float cTop = cy - PickupRadius;
				float cBottom = cy + PickupRadius;

				bool intersects =
					px1 < cRight &&
					px2 > cLeft &&
					py1 < cBottom &&
					py2 > cTop;

				if (!intersects)
					continue;

				pickup.Collected = true;

				switch (pickup.Type)
				{
					case JumpsPickupType.Coin:
						_score++;
						if (_score > _highScore)
							_highScore = _score;
						break;

					case JumpsPickupType.JumpBoost:
						ActivateJumpBoost();
						break;

					case JumpsPickupType.SpeedBoost:
						ActivateSpeedBoost();
						break;

					case JumpsPickupType.Magnet:
						ActivateMagnet();
						break;

					case JumpsPickupType.DoubleJump:
						ActivateDoubleJump();
						break;

					case JumpsPickupType.SlowScroll:
						ActivateSlowScroll();
						break;
				}
			}
		}

		private void ActivateJumpBoost()
		{
			_jumpBoostActive = true;
			_jumpBoostTimeRemaining += PowerupDuration;
		}

		private void ActivateSpeedBoost()
		{
			_speedBoostActive = true;
			_speedBoostTimeRemaining += PowerupDuration;
		}

		private void ActivateMagnet()
		{
			_magnetActive = true;
			_magnetTimeRemaining += PowerupDuration;
		}

		private void ActivateDoubleJump()
		{
			if (_doubleJumpActive)
			{
				_doubleJumpTimeRemaining += PowerupDuration;
				return;
			}

			_doubleJumpActive = true;
			_doubleJumpTimeRemaining += PowerupDuration;
			_airJumpsRemaining = ExtraAirJumpsPerUse;
		}

		private void ActivateSlowScroll()
		{
			_slowScrollActive = true;
			_slowScrollTimeRemaining += PowerupDuration;
		}

		// ==== Render view snapshot =========================================

		private void UpdatePlatformViews()
		{
			_platformViews.Clear();

			foreach (var p in _platforms)
			{
				JumpsPickupView? pickupView = null;

				if (p.Pickup is { Collected: false } pickup)
				{
					float cx, cy;

					if (pickup.IsMagnetPulling)
					{
						cx = pickup.WorldX;
						cy = pickup.WorldY;
					}
					else
					{
						cx = p.X + p.Width / 2f;
						cy = p.Y - PickupRadius - 2f;
					}

					pickupView = new JumpsPickupView
					{
						Type = pickup.Type,
						Collected = pickup.Collected,
						IsMagnetPulling = pickup.IsMagnetPulling,
						X = cx,
						Y = cy
					};
				}

				_platformViews.Add(new JumpsPlatformView
				{
					X = p.X,
					Y = p.Y,
					Width = p.Width,
					Height = p.Height,
					Pickup = pickupView
				});
			}
		}
	}
}

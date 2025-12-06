using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameClient.Wpf.ClientServices;
using GameContracts;
using SkiaSharp;
using SkiaSharp.Views.WPF;

namespace GameClient.Wpf.GameClients
{
	public partial class JumpsGameClient : UserControl, IGameClient
	{
		// ==== IGameClient plumbing ==========================================

		public GameType GameType => GameType.Jumps;

		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		public void SetConnection(Func<HubMessage, Task> sendAsync, Func<bool> isSocketOpen)
		{
			_sendAsync = sendAsync;
			_isSocketOpen = isSocketOpen;
		}

		public void OnRoomChanged(string? roomCode, string? playerId)
		{
			InputService.Clear();

			if (!string.IsNullOrEmpty(roomCode))
			{
				ResetGame();
			}
		}

		public bool TryHandleMessage(HubMessage msg) => false;

		public void OnKeyDown(KeyEventArgs e) => InputService.OnKeyDown(e.Key);

		public void OnKeyUp(KeyEventArgs e) => InputService.OnKeyUp(e.Key);

		// ==== World constants ===============================================

		private const int Columns = 5;         // platforms in 3 columns
		private const float WorldWidth = 480f;
		private const float WorldHeight = 480f;
		private int _level = 1;

		private const float PlayerSize = 24f;
		private const float PlatformWidth = 80f;
		private const float PlatformHeight = 8f;
		private const float RowSpacing = 80f;

		private const float Gravity = 1200f;      // units/s^2

		// Base jump velocity (about 2 platforms high)
		private const float BaseJumpVelocity = -640f;

		private const float MoveSpeedBase = 220f;     // horizontal units/s
		private const float StartSpeedFactor = 0.65f; // 65% of base speed at t = 0
		private const float WarmupDuration = 3.0f;    // seconds until we reach full base speed
		private float _currentMoveSpeed;

		private const float HorizontalScale = 0.85f;  // how strongly we respond to scroll speed

		private const float BaseScrollSpeed = 30f;    // how fast world scrolls at start
		private const float ScrollAccel = 1.1f;       // how much scroll speed adds per second

		private const float PickupRadius = 6f;

		// Landing & jump generosity
		private const float LandingVerticalForgiveness = 6f;    // vertical leeway
		private const float LandingHorizontalForgiveness = 10f; // horizontal leeway

		private const float JumpBufferTime = 0.12f; // how early you can press jump and still get it

		// Drop-through platform settings
		private const float DropThroughIgnoreTime = 0.25f;  // how long to ignore the platform we dropped through
		private const float DropThroughKickSpeed = 80f;     // small downward push

		// ==== Power-up settings =============================================

		private const float PowerupDuration = 10f;          // seconds for both boosts
		private const float JumpBoostMultiplier = 1.25f;    // ~3 platforms high
		private const float SpeedBoostMultiplier = 1.35f;   // faster horizontal move
		private const double CoinChance = 0.60;        // 60% chance
		private const double JumpBoostChance = 0.02;   // 2% chance
		private const double SpeedBoostChance = 0.02;  // 2% chance
		private const double MagnetChance = 0.02;      // 2% chance
		private const double DoubleJumpChance = 0.02;  // 2% chance

		// Magnet behavior: radius ~ 2 platforms in world units
		private const float MagnetRadiusWorld = RowSpacing * 2f;
		private const float MagnetPullSpeed = 520f;   // how fast coins fly toward the player
		private const float MagnetSnapDistance = 8f;  // when this close, snap & collect

		// Double jump behavior
		private const int ExtraAirJumpsPerUse = 1;

		// ==== Game state ====================================================

		private readonly DispatcherTimer _timer;
		private DateTime _lastFrameTime;
		private readonly Random _rng = new();

		private float _playerX;
		private float _playerY;
		private float _playerVX;
		private float _playerVY;
		private bool _isGrounded;
		private bool _hasStarted;
		private bool _spaceWasHeldLastFrame;
		private bool _isJumping;       // true while in the upward phase
		private bool _jumpCutApplied;  // true once we've shortened the jump

		private float _groundY;
		private float _scrollSpeed;
		private float _elapsedSinceStart;
		const float DeathMargin = 100f;

		// Jump buffer (handles pressing space slightly before landing)
		private float _jumpBufferRemaining;

		private int _score;

		private int _highScore;

		// Death input lock
		private bool _inputLocked;
		private float _inputLockTimer;
		private const float DeathInputLockDuration = 3f;

		private bool _pendingReset;
		private bool _isDead;

		// ==== Pickups / Power-ups ==========================================

		private enum PickupType
		{
			Coin,
			JumpBoost,
			SpeedBoost,
			Magnet,
			DoubleJump
		}

		private sealed class Pickup
		{
			public PickupType Type;
			public bool Collected;

			// Magnet animation state
			public bool IsMagnetPulling;
			public float WorldX;
			public float WorldY;
		}

		private sealed class Platform
		{
			public int RowIndex { get; set; }
			public float X { get; set; }
			public float Y { get; set; }
			public float Width { get; set; }
			public float Height { get; set; }

			public Pickup? Pickup { get; set; }
		}

		private readonly List<Platform> _platforms = new();
		private int _nextRowIndex;

		// Track which platform we’re standing on and which we’re dropping through
		private Platform? _currentPlatform;
		private Platform? _dropThroughPlatform;
		private float _dropThroughTimer;

		// Active power-up state
		private bool _jumpBoostActive;
		private float _jumpBoostTimeRemaining;

		private bool _speedBoostActive;
		private float _speedBoostTimeRemaining;

		private bool _magnetActive;
		private float _magnetTimeRemaining;

		private bool _doubleJumpActive;
		private float _doubleJumpTimeRemaining;

		// how many extra jumps we still have in the current airtime
		private int _airJumpsRemaining;

		// ====================================================================

		public JumpsGameClient()
		{
			InitializeComponent();

			_timer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
			};
			_timer.Tick += GameLoop;
		}

		private void UserControl_Loaded(object sender, RoutedEventArgs e)
		{
			Focus();

			InitializeGame();
			_lastFrameTime = DateTime.Now;
			_timer.Start();
		}

		// ==== Initialization / reset ========================================

		private void InitializeGame()
		{
			_platforms.Clear();

			_groundY = WorldHeight - 40f;

			// Start player on the ground in the middle.
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
			UpdateScoreText();

			_level = 1;
			UpdateLevelText();

			// reset power-ups
			_jumpBoostActive = false;
			_jumpBoostTimeRemaining = 0f;
			_speedBoostActive = false;
			_speedBoostTimeRemaining = 0f;

			_magnetActive = false;
			_magnetTimeRemaining = 0f;

			_doubleJumpActive = false;
			_doubleJumpTimeRemaining = 0f;
			_airJumpsRemaining = 0;

			// Generate an initial stack of rows above the ground.
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


			SkElement.InvalidateVisual();
		}

		private void ResetGame()
		{
			InitializeGame();
		}

		/// <summary>
		/// Generate a row of platforms at a specific Y position.
		/// </summary>
		private void GeneratePlatformsRow(float y)
		{
			// At least 1 platform, sometimes 2, never 3
			int platformCount = _rng.Next(1, 3); // 1 or 2

			var lanes = Enumerable.Range(0, Columns)
								  .OrderBy(_ => _rng.Next())
								  .Take(platformCount);

			float laneWidth = WorldWidth / Columns;

			foreach (int lane in lanes)
			{
				float centerX = laneWidth * (lane + 0.5f);
				float x = centerX - PlatformWidth / 2f;

				// Decide what pickup to spawn (if any)
				Pickup? pickup = null;
				double roll = _rng.NextDouble();

				// Chain the probabilities in order:
				// 0 - 0.60                                => Coin
				// 0.60 - 0.62                             => JumpBoost
				// 0.62 - 0.64                             => SpeedBoost
				// 0.64 - 0.66                             => Magnet
				// 0.66 - 0.68                             => DoubleJump
				// > 0.68                                  => nothing
				if (roll < CoinChance)
				{
					pickup = new Pickup { Type = PickupType.Coin, Collected = false };
				}
				else if (roll < CoinChance + JumpBoostChance)
				{
					pickup = new Pickup { Type = PickupType.JumpBoost, Collected = false };
				}
				else if (roll < CoinChance + JumpBoostChance + SpeedBoostChance)
				{
					pickup = new Pickup { Type = PickupType.SpeedBoost, Collected = false };
				}
				else if (roll < CoinChance + JumpBoostChance + SpeedBoostChance + MagnetChance)
				{
					pickup = new Pickup { Type = PickupType.Magnet, Collected = false };
				}
				else if (roll < CoinChance + JumpBoostChance + SpeedBoostChance + MagnetChance + DoubleJumpChance)
				{
					pickup = new Pickup { Type = PickupType.DoubleJump, Collected = false };
				}

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

		// ==== Input handling (via InputService) =============================

		private void HandleInput(double dt)
		{

			if (_inputLocked)
			{
				_playerVX = 0f;
				_spaceWasHeldLastFrame = false;
				return;
			}

			bool left = InputService.IsHeld(Key.Left) || InputService.IsHeld(Key.A);
			bool right = InputService.IsHeld(Key.Right) || InputService.IsHeld(Key.D);
			bool down = InputService.IsHeld(Key.Down) || InputService.IsHeld(Key.S);

			// Horizontal movement
			_playerVX = 0f;
			if (left && !right)
			{
				_playerVX = -1f;
			}
			else if (right && !left)
			{
				_playerVX = 1f;
			}

			bool spaceHeld = InputService.IsHeld(Key.Space);
			bool spacePressedThisFrame = spaceHeld && !_spaceWasHeldLastFrame;

			// If we pressed jump this frame:
			//  - If grounded and holding down on a platform -> drop through.
			//  - If grounded normally -> jump immediately.
			//  - If in the air -> buffer it.
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

			// Jump release: if we release space while still going up and haven't cut it yet,
			// shorten the jump by reducing upward velocity.
			if (!spaceHeld && _spaceWasHeldLastFrame && _isJumping && !_jumpCutApplied && _playerVY < 0f)
			{
				_playerVY *= 0.4f;   // tweak factor for how "short" a short hop is
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

			// Apply jump boost if active
			float jumpVelocity = BaseJumpVelocity;
			if (_jumpBoostActive)
			{
				jumpVelocity *= JumpBoostMultiplier;
			}

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

			float jumpVelocity = BaseJumpVelocity;
			if (_jumpBoostActive)
			{
				jumpVelocity *= JumpBoostMultiplier;
			}

			_playerVY = jumpVelocity;
			_airJumpsRemaining--; // consume the extra jump
		}

		private void StartDropThrough()
		{
			if (!_isGrounded || _currentPlatform == null)
				return;

			// Don't start the game scroll just from falling through, but keep consistency:
			_hasStarted = true;

			_isGrounded = false;
			_isJumping = false;
			_jumpCutApplied = false;

			_dropThroughPlatform = _currentPlatform;
			_dropThroughTimer = DropThroughIgnoreTime;
			_currentPlatform = null;

			// Make sure we are moving downward at least a bit
			if (_playerVY < 0f)
				_playerVY = 0f;
			_playerVY += DropThroughKickSpeed;
		}

		// ==== Game loop =====================================================

		private void GameLoop(object? sender, EventArgs e)
		{
			var now = DateTime.Now;
			if (_lastFrameTime == default)
			{
				_lastFrameTime = now;
				return;
			}

			double dt = (now - _lastFrameTime).TotalSeconds;
			_lastFrameTime = now;

			// Tick input lock timer
			if (_inputLocked)
			{
				_inputLockTimer -= (float)dt;
				if (_inputLockTimer <= 0f)
				{
					_inputLocked = false;
					_inputLockTimer = 0f;

					// Clear any stuck keys so holding space/A/D during lock doesn't instantly fire
					InputService.Clear();
					// If we were waiting to reset after death, do it now
					if (_pendingReset)
					{
						_pendingReset = false;
						ResetGame();
					}
				}
			}

			// Tick the jump buffer down
			if (_jumpBufferRemaining > 0f)
			{
				_jumpBufferRemaining -= (float)dt;
				if (_jumpBufferRemaining < 0f)
					_jumpBufferRemaining = 0f;
			}

			// Tick drop-through ignore timer
			if (_dropThroughTimer > 0f)
			{
				_dropThroughTimer -= (float)dt;
				if (_dropThroughTimer <= 0f)
				{
					_dropThroughTimer = 0f;
					_dropThroughPlatform = null;
				}
			}

			UpdatePhysics(dt);
			SkElement.InvalidateVisual();
		}

		private void UpdatePhysics(double dt)
		{
			if (_inputLocked && _pendingReset)
			{
				return;
			}
			// Always read input so they can freely move on the ground
			HandleInput(dt);

			// ==== Update scroll speed / "game speed" =========================

			if (_hasStarted)
			{
				_elapsedSinceStart += (float)dt;
				_scrollSpeed = BaseScrollSpeed + ScrollAccel * _elapsedSinceStart;
			}
			else
			{
				_scrollSpeed = 0f;
			}

			// Tick power-up timers
			if (_jumpBoostActive)
			{
				_jumpBoostTimeRemaining -= (float)dt;
				if (_jumpBoostTimeRemaining <= 0f)
				{
					_jumpBoostActive = false;
					_jumpBoostTimeRemaining = 0f;
				}
			}

			if (_speedBoostActive)
			{
				_speedBoostTimeRemaining -= (float)dt;
				if (_speedBoostTimeRemaining <= 0f)
				{
					_speedBoostActive = false;
					_speedBoostTimeRemaining = 0f;
				}
			}
			if (_magnetActive)
			{
				_magnetTimeRemaining -= (float)dt;
				if (_magnetTimeRemaining <= 0f)
				{
					_magnetActive = false;
					_magnetTimeRemaining = 0f;
				}
			}

			if (_doubleJumpActive)
			{
				_doubleJumpTimeRemaining -= (float)dt;
				if (_doubleJumpTimeRemaining <= 0f)
				{
					_doubleJumpActive = false;
					_doubleJumpTimeRemaining = 0f;
					_airJumpsRemaining = 0;
				}
			}

			// Raw difficulty factor based on how much faster we're scrolling now
			float rawFactor = _hasStarted
				? (_scrollSpeed / BaseScrollSpeed)   // 1.0 at start, grows over time
				: 1f;

			// --- Early-game warmup: gradually go from StartSpeedFactor -> 1 over WarmupDuration ---
			float t = _elapsedSinceStart / WarmupDuration;
			if (t < 0f) t = 0f;
			if (t > 1f) t = 1f;

			// Base factor handles "early game is slower, then reaches 1x"
			float baseFactor = StartSpeedFactor + (1f - StartSpeedFactor) * t;

			// Difficulty scaling on top, but with sub-linear growth so it ramps slower.
			float difficultyFactor = 1f + (MathF.Sqrt(rawFactor) - 1f) * HorizontalScale;

			// Combine them
			float speedFactor = baseFactor * difficultyFactor;

			// Keep a lower bound so we never go under our intended early speed
			if (speedFactor < StartSpeedFactor)
				speedFactor = StartSpeedFactor;

			float currentMoveSpeed = MoveSpeedBase * speedFactor;

			// Apply speed boost if active
			if (_speedBoostActive)
			{
				currentMoveSpeed *= SpeedBoostMultiplier;
			}

			_currentMoveSpeed = currentMoveSpeed;

			// ==== Horizontal move =============================================

			_playerX += _playerVX * currentMoveSpeed * (float)dt;

			// Clamp to world bounds
			if (_playerX < 0f) _playerX = 0f;
			if (_playerX > WorldWidth - PlayerSize) _playerX = WorldWidth - PlayerSize;

			// ==== Early-out before the game starts ============================

			// Before first jump: stick to ground, no scroll, no gravity.
			if (!_hasStarted)
			{
				_playerY = _groundY - PlayerSize;
				return;
			}

			// ==== Vertical scroll =============================================

			float scrollDelta = _scrollSpeed * (float)dt;

			// Scroll player, ground, and platforms downward together.
			_playerY += scrollDelta;
			_groundY += scrollDelta;

			foreach (var p in _platforms)
			{
				p.Y += scrollDelta;
			}

			// ==== Player vertical physics =====================================

			float prevY = _playerY;
			float prevBottom = prevY + PlayerSize;

			_playerVY += Gravity * (float)dt;
			_playerY += _playerVY * (float)dt;
			if (_playerVY >= 0f)
			{
				_isJumping = false;
			}

			float bottom = _playerY + PlayerSize;

			// Check landing on platforms only while falling
			if (_playerVY > 0f)
			{
				foreach (var p in _platforms)
				{
					// Ignore the platform we're currently dropping through
					if (_dropThroughPlatform == p && _dropThroughTimer > 0f)
						continue;

					float platformTop = p.Y;
					float playerCenterX = _playerX + PlayerSize / 2f;

					// More generous vertical check: allow a little over/under the platform.
					bool passesVertically =
						prevBottom <= platformTop + LandingVerticalForgiveness &&
						bottom >= platformTop - LandingVerticalForgiveness;

					// More generous horizontal check: allow feet to overlap slightly off the edge.
					bool withinHorizontal =
						playerCenterX >= p.X - LandingHorizontalForgiveness &&
						playerCenterX <= p.X + p.Width + LandingHorizontalForgiveness;

					if (passesVertically && withinHorizontal)
					{
						// Land on this platform
						_playerY = p.Y - PlayerSize;
						_playerVY = 0f;
						_isGrounded = true;
						_isJumping = false;
						_jumpCutApplied = false;
						_currentPlatform = p;

						_airJumpsRemaining = _doubleJumpActive ? ExtraAirJumpsPerUse : 0;

						// If we had a buffered jump (pressed space slightly before landing),
						// fire it immediately for a snappy feel.
						if (_jumpBufferRemaining > 0f)
						{
							_jumpBufferRemaining = 0f;
							StartJump();
						}

						break;
					}
				}
			}

			// If we fall off the bottom, reset for now
			if (bottom > WorldHeight + DeathMargin)
			{
				if (!_isDead)
				{
					_isDead = true;
					LockInputForDeath();
				}

				// Don't keep simulating while waiting for the reset
				return;
			}

			// Recycle platforms: remove those below screen, spawn new rows above
			RecycleAndSpawnRows();

			// Magnet effect: auto-collect distant coins
			ApplyMagnetAutoCollect((float)dt);

			// Check coin / power-up collisions
			CheckPickupCollisions();

			// === Level update: every +50 scroll speed = +1 level ===
			int newLevel = 1 + (int)(_scrollSpeed / 50f);
			if (newLevel < 1) newLevel = 1;

			if (newLevel != _level)
			{
				_level = newLevel;
				UpdateLevelText();
			}

			UpdateDebugText();
		}
		private void LockInputForDeath()
		{
			_inputLocked = true;
			_inputLockTimer = DeathInputLockDuration;
			_pendingReset = true;

			// Stop movement & clear current inputs
			_playerVX = 0f;
			_spaceWasHeldLastFrame = false;
			InputService.Clear();
		}
		private void ApplyMagnetAutoCollect(float dt)
		{
			if (!_magnetActive)
				return;

			float playerCenterX = _playerX + PlayerSize / 2f;
			float playerCenterY = _playerY + PlayerSize / 2f;

			float radius = MagnetRadiusWorld;
			float radiusSq = radius * radius;

			foreach (var p in _platforms)
			{
				var pickup = p.Pickup;
				if (pickup == null || pickup.Collected || pickup.Type != PickupType.Coin)
					continue;

				// --- STEP 1: If this coin is not yet being pulled, check if it should start ---
				if (!pickup.IsMagnetPulling)
				{
					// Coin's current position (anchored to platform)
					float cx = p.X + p.Width / 2f;
					float cy = p.Y - PickupRadius - 2f;

					float dx0 = cx - playerCenterX;
					float dy0 = cy - playerCenterY;

					if (dx0 * dx0 + dy0 * dy0 <= radiusSq)
					{
						// Start magnet pull: freeze current world position
						pickup.IsMagnetPulling = true;
						pickup.WorldX = cx;
						pickup.WorldY = cy;
					}
				}

				// --- STEP 2: If it's being pulled, move it toward player and collect on contact ---
				if (pickup.IsMagnetPulling)
				{
					float dx = playerCenterX - pickup.WorldX;
					float dy = playerCenterY - pickup.WorldY;
					float distSq = dx * dx + dy * dy;

					if (distSq <= MagnetSnapDistance * MagnetSnapDistance)
					{
						// Close enough: collect
						pickup.Collected = true;
						pickup.IsMagnetPulling = false;
						_score++;
						UpdateScoreText();
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
		}

		/// <summary>
		/// Removes platforms that have scrolled below the screen,
		/// and spawns new rows above the top so platforms keep coming.
		/// </summary>
		private void RecycleAndSpawnRows()
		{
			// Remove platforms that are completely below the screen + margin
			float bottomLimit = WorldHeight + RowSpacing;
			_platforms.RemoveAll(p => p.Y > bottomLimit);

			// If everything somehow got removed, regenerate a basic stack.
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

			// Find the highest (smallest Y) platform
			float minY = _platforms.Min(p => p.Y);

			// While our "topmost" row is too far down (no row above screen),
			// spawn new rows above it at fixed spacing.
			while (minY > -RowSpacing)
			{
				float newRowY = minY - RowSpacing;
				GeneratePlatformsRow(newRowY);
				_nextRowIndex++;
				minY = newRowY;
			}
		}

		private void ActivateJumpBoost()
		{
			_jumpBoostActive = true;
			_jumpBoostTimeRemaining = PowerupDuration;
		}

		private void ActivateSpeedBoost()
		{
			_speedBoostActive = true;
			_speedBoostTimeRemaining = PowerupDuration;
		}
		private void ActivateMagnet()
		{
			_magnetActive = true;
			_magnetTimeRemaining = PowerupDuration;
		}

		private void ActivateDoubleJump()
		{
			// If already active, do NOT give more jumps, just reset timer
			if (_doubleJumpActive)
			{
				_doubleJumpTimeRemaining = PowerupDuration;
				return;
			}

			_doubleJumpActive = true;
			_doubleJumpTimeRemaining = PowerupDuration;

			// Give one extra air jump for the current/next airtime
			_airJumpsRemaining = ExtraAirJumpsPerUse;
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

				// Pickup sits just above the middle of the platform
				float cx;
				float cy;

				// If it's being magnet-pulled, use its animated position
				if (pickup.IsMagnetPulling)
				{
					cx = pickup.WorldX;
					cy = pickup.WorldY;
				}
				else
				{
					// Pickup sits just above the middle of the platform
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

				if (intersects)
				{
					pickup.Collected = true;

					switch (pickup.Type)
					{
						case PickupType.Coin:
							_score++;
							UpdateScoreText();
							break;

						case PickupType.JumpBoost:
							ActivateJumpBoost();
							break;

						case PickupType.SpeedBoost:
							ActivateSpeedBoost();
							break;

						case PickupType.Magnet:
							ActivateMagnet();
							break;

						case PickupType.DoubleJump:
							ActivateDoubleJump();
							break;
					}
				}
			}
		}

		private void UpdateScoreText()
		{
			// Update high score if current score beats it
			if (_score > _highScore)
			{
				_highScore = _score;
			}

			if (ScoreText != null)
			{
				ScoreText.Text = $"Score: {_score} (Best: {_highScore})";
			}
		}

		private void UpdateLevelText()
		{
			if (LevelText != null)
			{
				LevelText.Text = $"Level {_level}";
			}
		}

		private void UpdateDebugText()
		{
			if (DebugText != null)
			{
				DebugText.Text =
					$"Scroll: {_scrollSpeed:F1}  PlayerMax: {_currentMoveSpeed:F1}";
			}
		}

		// ==== Skia rendering ================================================

		private void OnPaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			canvas.Clear(SKColors.Black);

			float canvasWidth = e.Info.Width;
			float canvasHeight = e.Info.Height;

			// Letterbox scaling so we can keep a clean 360x480 world
			float scale = Math.Min(canvasWidth / WorldWidth, canvasHeight / WorldHeight);
			float xOffset = (canvasWidth - WorldWidth * scale) / 2f;
			float yOffset = (canvasHeight - WorldHeight * scale) / 2f;

			canvas.Translate(xOffset, yOffset);
			canvas.Scale(scale);

			// Ground (scrolls with world)
			using (var groundPaint = new SKPaint
			{
				Color = new SKColor(60, 60, 60),
				IsAntialias = true
			})
			{
				canvas.DrawRect(0, _groundY, WorldWidth, 6f, groundPaint);
			}

			// Platforms + pickups
			using (var platformPaint = new SKPaint
			{
				Color = SKColors.LightGray,
				IsAntialias = true
			})
			using (var coinPaint = new SKPaint
			{
				Color = SKColors.Gold,
				IsAntialias = true
			})
			using (var jumpPaint = new SKPaint
			{
				Color = SKColors.MediumPurple,
				IsAntialias = true
			})
			using (var speedPaint = new SKPaint
			{
				Color = SKColors.LimeGreen,
				IsAntialias = true
			})
			using (var magnetPaint = new SKPaint
			{
				Color = SKColors.Red,
				IsAntialias = true
			})
			using (var doubleJumpPaint = new SKPaint
			{
				Color = SKColors.SaddleBrown,
				IsAntialias = true
			})
			{
				foreach (var p in _platforms)
				{
					canvas.DrawRect(p.X, p.Y, p.Width, p.Height, platformPaint);

					if (p.Pickup != null && !p.Pickup.Collected)
					{
						float cx;
						float cy;

						// If the coin is being magnet-pulled, use its animated world position
						if (p.Pickup.IsMagnetPulling)
						{
							cx = p.Pickup.WorldX;
							cy = p.Pickup.WorldY;
						}
						else
						{
							// Regular anchored position above the platform
							cx = p.X + p.Width / 2f;
							cy = p.Y - PickupRadius - 2f;
						}

						SKPaint paintToUse = coinPaint;
						switch (p.Pickup.Type)
						{
							case PickupType.Coin:
								paintToUse = coinPaint;
								break;
							case PickupType.JumpBoost:
								paintToUse = jumpPaint;
								break;
							case PickupType.SpeedBoost:
								paintToUse = speedPaint;
								break;
							case PickupType.Magnet:
								paintToUse = magnetPaint;
								break;
							case PickupType.DoubleJump:
								paintToUse = doubleJumpPaint;
								break;
						}

						canvas.DrawCircle(cx, cy, PickupRadius, paintToUse);
					}
				}
			}

			// Player
			using (var playerPaint = new SKPaint
			{
				Color = SKColors.DeepSkyBlue,
				IsAntialias = true
			})
			{
				var rect = new SKRect(
					_playerX,
					_playerY,
					_playerX + PlayerSize,
					_playerY + PlayerSize);

				var rrect = new SKRoundRect(rect, 4f, 4f);
				canvas.DrawRoundRect(rrect, playerPaint);
			}

			// ===== Power-up rings around the player (arc countdown) =====
			float playerCenterX = _playerX + PlayerSize / 2f;
			float playerCenterY = _playerY + PlayerSize / 2f;

			// Helper: clamp
			float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

			// Purple arc for jump boost (outer)
			if (_jumpBoostActive && _jumpBoostTimeRemaining > 0f)
			{
				float frac = Clamp01(_jumpBoostTimeRemaining / PowerupDuration);
				float sweepAngle = 360f * frac;

				using (var jumpRingPaint = new SKPaint
				{
					Color = SKColors.MediumPurple,
					IsAntialias = true,
					Style = SKPaintStyle.Stroke,
					StrokeWidth = 2.5f
				})
				{
					float radius = PlayerSize * 0.90f;
					var arcRect = new SKRect(
						playerCenterX - radius,
						playerCenterY - radius,
						playerCenterX + radius,
						playerCenterY + radius);

					canvas.DrawArc(arcRect, -90f, sweepAngle, false, jumpRingPaint);
				}
			}

			// Green arc for speed boost
			if (_speedBoostActive && _speedBoostTimeRemaining > 0f)
			{
				float frac = Clamp01(_speedBoostTimeRemaining / PowerupDuration);
				float sweepAngle = 360f * frac;

				using (var speedRingPaint = new SKPaint
				{
					Color = SKColors.LimeGreen,
					IsAntialias = true,
					Style = SKPaintStyle.Stroke,
					StrokeWidth = 2.5f
				})
				{
					float radius = PlayerSize * 0.70f;
					var arcRect = new SKRect(
						playerCenterX - radius,
						playerCenterY - radius,
						playerCenterX + radius,
						playerCenterY + radius);

					canvas.DrawArc(arcRect, -90f, sweepAngle, false, speedRingPaint);
				}
			}

			// Red arc for magnet
			if (_magnetActive && _magnetTimeRemaining > 0f)
			{
				float frac = Clamp01(_magnetTimeRemaining / PowerupDuration);
				float sweepAngle = 360f * frac;

				using (var magnetRingPaint = new SKPaint
				{
					Color = SKColors.Red,
					IsAntialias = true,
					Style = SKPaintStyle.Stroke,
					StrokeWidth = 2.5f
				})
				{
					// Use the world AoE radius so the ring matches the effect area
					float radius = MagnetRadiusWorld;
					var arcRect = new SKRect(
						playerCenterX - radius,
						playerCenterY - radius,
						playerCenterX + radius,
						playerCenterY + radius);

					// countdown from top, clockwise
					canvas.DrawArc(arcRect, -90f, sweepAngle, false, magnetRingPaint);
				}
			}

			// Brown arc for double jump
			if (_doubleJumpActive && _doubleJumpTimeRemaining > 0f)
			{
				float frac = Clamp01(_doubleJumpTimeRemaining / PowerupDuration);
				float sweepAngle = 360f * frac;

				using (var doubleJumpRingPaint = new SKPaint
				{
					Color = SKColors.SaddleBrown,
					IsAntialias = true,
					Style = SKPaintStyle.Stroke,
					StrokeWidth = 2.5f
				})
				{
					float radius = PlayerSize * 0.50f;
					var arcRect = new SKRect(
						playerCenterX - radius,
						playerCenterY - radius,
						playerCenterX + radius,
						playerCenterY + radius);

					canvas.DrawArc(arcRect, -90f, sweepAngle, false, doubleJumpRingPaint);
				}
			}
		}

	}
}

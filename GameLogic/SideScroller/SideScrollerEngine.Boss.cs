using System;
using System.Collections.Generic;

namespace GameLogic.SideScroller
{
	public sealed partial class SideScrollerEngine
	{
		private const float BossShotSpeedBlocksPerSecond = 12f;
		private const float BossFireIntervalSecondsPhase1 = 2.0f;
		private const float BossFireIntervalSecondsPhase2 = 1.2f;
		private const int BossShotsPerVolley = 2;
		private const float BossDeathShakeDuration = 1.0f;
		private const float BossDeathShakeAmplitude = 8f;
		private const float BossDeathFallSpeed = 300f;

		private const float BossHurtFlashDuration = 0.35f;

		private enum BossPhase
		{
			Inactive = 0,
			Phase1 = 1,
			Phase2 = 2,
			Phase3 = 3,
			Dead = 4
		}

		public readonly struct Boss
		{
			public Boss(float x, float footY, float width, float height)
			{
				X = x;
				FootY = footY;
				Width = width;
				Height = height;
			}

			public float X { get; }
			public float FootY { get; }
			public float Width { get; }
			public float Height { get; }
		}

		public readonly struct BossShot
		{
			public BossShot(float x, float y, float vx, float vy, float radius)
			{
				X = x;
				Y = y;
				Vx = vx;
				Vy = vy;
				Radius = radius;
			}

			public float X { get; }
			public float Y { get; }
			public float Vx { get; }
			public float Vy { get; }
			public float Radius { get; }

			public BossShot WithPosition(float x, float y) =>
				new BossShot(x, y, Vx, Vy, Radius);
		}

		// Boss state
		private bool _bossActive;
		private Boss _boss;
		private BossPhase _bossPhase = BossPhase.Inactive;
		private float _bossFireTimer;
		private readonly List<BossShot> _bossShots = new();

		// Track whether this boss has been beaten this run
		private bool _bossDefeated;

		// Arena button / arena info
		private float _arenaButtonX;
		private float _arenaButtonY;
		private float _arenaButtonRadius;
		private bool _arenaButtonAvailablePhase1;
		private float _arenaPlatformMinX;
		private float _arenaPlatformMaxX;

		// Shockwave
		private bool _arenaWaveActive;
		private float _arenaWaveRadius;
		private float _arenaWaveMaxRadius;
		private float _arenaWaveSpeed;
		private bool _arenaWaveAlreadyHitBoss;

		// Which phase the current wave will transition to (if any)
		private BossPhase _pendingPhaseAfterWave = BossPhase.Inactive;

		// Button usability flags for each phase
		private bool _arenaButtonAvailablePhase2;
		private bool _arenaButtonAvailablePhase3;

		// Death animation
		private bool _bossDying;
		private float _bossDeathElapsed;
		private float _bossBaseFootY;

		// Player safety during boss wave
		private bool _playerInvulnerableDuringWave;

		// Visual: boss hurt flash
		private float _bossHurtFlashTimer;

		// Exposed to the client
		public bool BossActive => _bossActive;
		public Boss BossData => _boss;
		public IReadOnlyList<BossShot> BossShots => _bossShots;

		public int BossPhaseIndex => (int)_bossPhase;

		public bool BossHurtFlashing => _bossHurtFlashTimer > 0f;

		public bool ArenaButtonEnabled =>
	_bossActive &&
	!_arenaWaveActive &&
	(
		(_bossPhase == BossPhase.Phase1 && _arenaButtonAvailablePhase1) ||
		(_bossPhase == BossPhase.Phase2 && _arenaButtonAvailablePhase2) ||
		(_bossPhase == BossPhase.Phase3 && _arenaButtonAvailablePhase3)
	);

		public float ArenaButtonX => _arenaButtonX;
		public float ArenaButtonY => _arenaButtonY;
		public float ArenaButtonRadius => _arenaButtonRadius;

		public bool ArenaWaveActive => _arenaWaveActive;
		public float ArenaWaveRadius => _arenaWaveRadius;

		private void ResetBoss()
		{
			_bossActive = false;
			_bossPhase = BossPhase.Inactive;
			_bossFireTimer = 0f;
			_playerInvulnerableDuringWave = false;
			_bossShots.Clear();

			// Button
			_arenaButtonX = 0f;
			_arenaButtonY = 0f;
			_arenaButtonRadius = 0f;
			_arenaButtonAvailablePhase1 = false;
			_arenaButtonAvailablePhase2 = false;
			_arenaButtonAvailablePhase3 = false;
			_arenaPlatformMinX = 0f;
			_arenaPlatformMaxX = 0f;

			// Wave
			_arenaWaveActive = false;
			_arenaWaveRadius = 0f;
			_arenaWaveMaxRadius = 0f;
			_arenaWaveSpeed = 0f;
			_arenaWaveAlreadyHitBoss = false;
			_pendingPhaseAfterWave = BossPhase.Inactive;

			// Visual
			_bossHurtFlashTimer = 0f;

			// Death anim
			_bossDying = false;
			_bossDeathElapsed = 0f;
			_bossBaseFootY = 0f;
		}


		private void SpawnBossOnPlatform(Platform platform)
		{
			// Make boss roughly 2x bigger than normal enemy
			float bossWidth = 56f;
			float bossHeight = 100f;

			// Put boss near the right end of the platform, standing on it
			float bossX = platform.X + platform.Width - bossWidth * 0.6f;
			float bossFootY = platform.Y;

			_boss = new Boss(bossX, bossFootY, bossWidth, bossHeight);
			_bossBaseFootY = bossFootY;
			_bossActive = true;
			_bossPhase = BossPhase.Phase1;

			// Setup arena button in the center of this platform
			_arenaPlatformMinX = platform.X;
			_arenaPlatformMaxX = platform.X + platform.Width;

			_arenaButtonX = platform.X + platform.Width / 2f;
			_arenaButtonY = platform.Y;
			_arenaButtonRadius = BlockSize * 0.4f;

			_arenaButtonAvailablePhase1 = true;
			_arenaButtonAvailablePhase2 = false;
			_arenaButtonAvailablePhase3 = false;

			// Reset firing + wave
			_bossFireTimer = BossFireIntervalSecondsPhase1;
			_bossShots.Clear();

			_arenaWaveActive = false;
			_arenaWaveRadius = 0f;
			_arenaWaveAlreadyHitBoss = false;
			_pendingPhaseAfterWave = BossPhase.Inactive;

			_bossHurtFlashTimer = 0f;
			_bossDying = false;
			_bossDeathElapsed = 0f;
		}


		/// <summary>
		/// Called from Update() after collisions to see if the player just
		/// landed on the button in the current boss phase.
		/// </summary>
		private void CheckArenaButtonLanding()
		{
			// Simple guard:
			if (!_bossActive || _arenaWaveActive || _arenaButtonRadius <= 0f)
				return;

			// Horizontal overlap with button area
			float playerHalfWidth = PlayerWidth / 2f;
			float playerLeft = _playerX - playerHalfWidth;
			float playerRight = _playerX + playerHalfWidth;

			float btnLeft = _arenaButtonX - _arenaButtonRadius;
			float btnRight = _arenaButtonX + _arenaButtonRadius;

			bool horizontalOverlap =
				playerRight > btnLeft &&
				playerLeft < btnRight;

			// "Jumped on it": last frame above button Y, now at/under, moving down
			bool wasAbove = _prevPlayerY < _arenaButtonY - 1f;
			bool nowOnOrBelow = _playerY >= _arenaButtonY;
			bool movingDown = _playerVelY >= 0f;

			if (!horizontalOverlap || !wasAbove || !nowOnOrBelow || !movingDown)
				return;

			// Decide what this stomp should do based on the current phase
			switch (_bossPhase)
			{
				case BossPhase.Phase1:
					if (_arenaButtonAvailablePhase1)
						ActivateArenaButtonForPhaseTransition(BossPhase.Phase2);
					break;

				case BossPhase.Phase2:
					if (_arenaButtonAvailablePhase2)
						ActivateArenaButtonForPhaseTransition(BossPhase.Phase3);
					break;

				case BossPhase.Phase3:
					if (_arenaButtonAvailablePhase3)
						ActivateArenaButtonForPhaseTransition(BossPhase.Dead); // final blow
					break;
			}
		}


		/// <summary>
		/// Starts the shockwave and sets which phase we will transition to
		/// when the wave hits the boss.
		/// </summary>
		private void ActivateArenaButtonForPhaseTransition(BossPhase targetPhase)
		{
			// Start shockwave
			_arenaWaveActive = true;
			_arenaWaveRadius = _arenaButtonRadius;
			_arenaWaveSpeed = 400f;              // px/s
			_arenaWaveMaxRadius = 1200f;         // big enough for the arena
			_arenaWaveAlreadyHitBoss = false;

			_pendingPhaseAfterWave = targetPhase;

			// 🔹 Make player invulnerable while the wave is travelling
			_playerInvulnerableDuringWave = true;

			// Immediately push player to the left side of the arena platform
			_playerX = _arenaPlatformMinX + PlayerWidth * 1.2f;
			_prevPlayerX = _playerX;
			_playerVelX = 0f;
			_playerVelY = 0f;

			// Snap camera to left edge of lock
			_cameraX = _cameraLockMinX;

			// While the wave is traveling, button is locked for all phases
			_arenaButtonAvailablePhase1 = false;
			_arenaButtonAvailablePhase2 = false;
			_arenaButtonAvailablePhase3 = false;
		}

		private void UpdateBoss(float dtSeconds, float viewWidth)
		{
			if (!_bossActive || _bossPhase == BossPhase.Inactive)
				return;

			// Hurt flash timer
			if (_bossHurtFlashTimer > 0f)
			{
				_bossHurtFlashTimer -= dtSeconds;
				if (_bossHurtFlashTimer < 0f)
					_bossHurtFlashTimer = 0f;
			}

			// --- Shockwave expansion & boss hit check -----------------
			if (_arenaWaveActive)
			{
				_arenaWaveRadius += _arenaWaveSpeed * dtSeconds;

				// Distance from button to boss center
				float bossCenterX = _boss.X;
				float bossCenterY = _boss.FootY - _boss.Height * 0.6f;
				float dx = bossCenterX - _arenaButtonX;
				float dy = bossCenterY - _arenaButtonY;
				float distToBoss = MathF.Sqrt(dx * dx + dy * dy);

				// When wave reaches boss for the first time, apply damage / phase change
				if (!_arenaWaveAlreadyHitBoss && _arenaWaveRadius >= distToBoss)
				{
					_arenaWaveAlreadyHitBoss = true;

					switch (_pendingPhaseAfterWave)
					{
						case BossPhase.Phase2:
							// Phase1 -> Phase2
							_bossPhase = BossPhase.Phase2;
							_bossHurtFlashTimer = BossHurtFlashDuration;

							_bossShots.Clear();
							MoveButtonCloserToBoss();   // Phase2: a bit closer
							SpawnPhase2Adds();          // 1 robot between player & button
							_bossFireTimer = BossFireIntervalSecondsPhase2;

							_arenaButtonAvailablePhase2 = true;   // button usable again once wave is done
							break;

						case BossPhase.Phase3:
							// Phase2 -> Phase3
							_bossPhase = BossPhase.Phase3;
							_bossHurtFlashTimer = BossHurtFlashDuration;

							_bossShots.Clear();
							MoveButtonEvenCloserToBossPhase3();   // even closer
							SpawnPhase3Adds();                    // go from 1 robot to 2
							_bossFireTimer = BossFireIntervalSecondsPhase2; // same or tweak faster if you want

							_arenaButtonAvailablePhase3 = true;
							break;

						case BossPhase.Dead:
							// Phase3 -> Death
							StartBossDeathSequence();
							break;
					}

					_pendingPhaseAfterWave = BossPhase.Inactive;
				}

				// End wave when it's far enough
				if (_arenaWaveRadius >= _arenaWaveMaxRadius)
				{
					_arenaWaveActive = false;
					_playerInvulnerableDuringWave = false;
				}
			}
			// If we are in the death phase, just animate shaking + falling.
			if (_bossPhase == BossPhase.Dead && _bossDying)
			{
				_bossDeathElapsed += dtSeconds;

				float newFootY;

				if (_bossDeathElapsed < BossDeathShakeDuration)
				{
					// Shake in place
					float shake = MathF.Sin(_bossDeathElapsed * 40f) * BossDeathShakeAmplitude;
					newFootY = _bossBaseFootY + shake;
				}
				else
				{
					// Fall off the screen
					float tFall = _bossDeathElapsed - BossDeathShakeDuration;
					newFootY = _bossBaseFootY + BossDeathFallSpeed * tFall;
				}

				_boss = new Boss(_boss.X, newFootY, _boss.Width, _boss.Height);

				// When boss is well below the ground, remove it and unlock camera
				if (newFootY - _boss.Height > GroundY + 6 * BlockSize)
				{
					_bossDying = false;
					_bossActive = false;   // DrawBoss will stop drawing
					ReleaseCameraLock();   // player can continue
				}

				// No bullets, no further logic while dying
				return;
			}


			// --- Shooting pattern based on phase ----------------------
			switch (_bossPhase)
			{
				case BossPhase.Phase1:
					UpdateBossShooting(dtSeconds, viewWidth, BossFireIntervalSecondsPhase1);
					break;

				case BossPhase.Phase2:
					UpdateBossShooting(dtSeconds, viewWidth, BossFireIntervalSecondsPhase2);
					break;

				case BossPhase.Phase3:
					// Placeholder for future: different patterns, more bullets, etc.
					UpdateBossShooting(dtSeconds, viewWidth, 3.0f);
					break;
			}
		}

		private void UpdateBossShooting(float dtSeconds, float viewWidth, float fireInterval)
		{
			// 1) Update fire timer and shoot volleys
			_bossFireTimer -= dtSeconds;
			if (_bossFireTimer <= 0f)
			{
				FireBossVolley();
				_bossFireTimer = fireInterval;
			}

			// 2) Move shots
			if (_bossShots.Count > 0)
			{
				float shotSpeed = BossShotSpeedBlocksPerSecond * BlockSize;

				for (int i = 0; i < _bossShots.Count; i++)
				{
					var s = _bossShots[i];
					float newX = s.X + s.Vx * shotSpeed * dtSeconds;
					float newY = s.Y + s.Vy * shotSpeed * dtSeconds;

					_bossShots[i] = s.WithPosition(newX, newY);
				}
			}

			// 3) Cull shots that leave camera view
			if (_bossShots.Count > 0)
			{
				float margin = 50f;
				float left = _cameraX - margin;
				float right = _cameraX + viewWidth + margin;
				float top = -margin;
				float bottom = GroundY + KillDepthBlocksBelowGround * BlockSize + margin;

				for (int i = _bossShots.Count - 1; i >= 0; i--)
				{
					var s = _bossShots[i];
					if (s.X < left || s.X > right || s.Y < top || s.Y > bottom)
					{
						_bossShots.RemoveAt(i);
					}
				}
			}
		}

		private void FireBossVolley()
		{
			if (!_bossActive ||
				_bossPhase == BossPhase.Inactive ||
				_bossPhase == BossPhase.Dead)
				return;

			// Aim at player's current center
			float playerCenterX = _playerX;
			float playerCenterY = _playerY - PlayerHeight / 2f;

			float bossCenterX = _boss.X;
			float bossCenterY = _boss.FootY - _boss.Height * 0.6f;

			float dx = playerCenterX - bossCenterX;
			float dy = playerCenterY - bossCenterY;
			float len = MathF.Sqrt(dx * dx + dy * dy);
			if (len < 0.001f)
			{
				// Avoid NaN; default to shooting toward player-side (left)
				dx = -1f;
				dy = 0f;
				len = 1f;
			}

			dx /= len;
			dy /= len;

			float shotRadius = 6f;

			// Two shots with small vertical offsets
			for (int i = 0; i < BossShotsPerVolley; i++)
			{
				float offsetY = (i == 0) ? -10f : +10f;

				var shot = new BossShot(
					x: bossCenterX,
					y: bossCenterY + offsetY,
					vx: dx,
					vy: dy,
					radius: shotRadius);

				_bossShots.Add(shot);
			}
		}

		private bool CheckBossShotCollisions()
		{
			if (_playerInvulnerableDuringWave)
				return false;
			if (!_bossActive || _bossShots.Count == 0)
				return false;

			float playerLeft = _playerX - PlayerWidth / 2f;
			float playerRight = playerLeft + PlayerWidth;
			float playerTop = _playerY - PlayerHeight;
			float playerBottom = _playerY;

			for (int i = 0; i < _bossShots.Count; i++)
			{
				var s = _bossShots[i];

				float shotLeft = s.X - s.Radius;
				float shotRight = s.X + s.Radius;
				float shotTop = s.Y - s.Radius;
				float shotBottom = s.Y + s.Radius;

				bool overlap =
					playerLeft < shotRight &&
					playerRight > shotLeft &&
					playerTop < shotBottom &&
					playerBottom > shotTop;

				if (overlap)
				{
					Reset();
					return true;
				}
			}

			return false;
		}

		// ─────────────────────────────────────────────────────────────
		// Phase 2 helpers: move button closer + spawn adds
		// ─────────────────────────────────────────────────────────────

		private void MoveButtonCloserToBoss()
		{
			// Slide button ~40% of the way from its current spot toward the boss,
			// but keep a small gap so it doesn't overlap the boss.
			float targetX = _boss.X - BlockSize * 2f; // a couple blocks left of boss
			float newX = _arenaButtonX + (targetX - _arenaButtonX) * 0.4f;

			// Clamp inside arena platform
			newX = Math.Clamp(newX, _arenaPlatformMinX + BlockSize, _arenaPlatformMaxX - BlockSize);
			_arenaButtonX = newX;
		}
		private void MoveButtonEvenCloserToBossPhase3()
		{
			// Slide button even closer than Phase2, but keep a tiny gap before the boss.
			float targetX = _boss.X - BlockSize * 1.2f;
			float newX = _arenaButtonX + (targetX - _arenaButtonX) * 0.7f; // move more aggressively

			// Clamp inside arena platform
			newX = Math.Clamp(newX, _arenaPlatformMinX + BlockSize * 0.5f, _arenaPlatformMaxX - BlockSize * 0.5f);
			_arenaButtonX = newX;
		}

		private void SpawnPhase2Adds()
		{
			// Spawn a few ground enemies between player's side and the button.
			// Player was moved to left of arena platform in ActivateArenaButtonPhase1.
			float left = _arenaPlatformMinX + BlockSize * 3f;          // a bit to the right of player
			float right = _arenaButtonX - BlockSize * 2f;              // a bit left of button

			if (right <= left)
				return;

			int count = 1;

			float enemyWidth = 28f;
			float enemyHeight = 50f;
			float enemySpeed = EnemySpeedBlocksPerSecond * BlockSize;
			float patrolRadiusBlocks = 4f;
			float patrolRadiusPixels = patrolRadiusBlocks * BlockSize;

			for (int i = 0; i < count; i++)
			{
				float t = (i + 1) / (float)(count + 1);
				float x = left + t * (right - left);

				var enemy = new Enemy(
					x: x,
					footY: GroundY,
					width: enemyWidth,
					height: enemyHeight,
					patrolMinX: x - patrolRadiusPixels,
					patrolMaxX: x + patrolRadiusPixels,
					speed: enemySpeed);

				AddEnemy(enemy);
			}
		}
		private void SpawnPhase3Adds()
		{
			// We already spawned 1 robot in Phase2. Add one more, closer to the boss.
			float left = _arenaButtonX + BlockSize;              // just to the right of the button
			float right = _boss.X - BlockSize * 2f;              // a bit left of the boss

			if (right <= left)
				return;

			float enemyWidth = 28f;
			float enemyHeight = 50f;
			float enemySpeed = EnemySpeedBlocksPerSecond * BlockSize;
			float patrolRadiusBlocks = 2f;
			float patrolRadiusPixels = patrolRadiusBlocks * BlockSize;

			// Place this one roughly midway between button and boss
			float x = (left + right) * 0.5f;

			var enemy = new Enemy(
				x: x,
				footY: GroundY,
				width: enemyWidth,
				height: enemyHeight,
				patrolMinX: x - patrolRadiusPixels,
				patrolMaxX: x + patrolRadiusPixels,
				speed: enemySpeed);

			AddEnemy(enemy);
		}
		private void StartBossDeathSequence()
		{
			_bossPhase = BossPhase.Dead;
			_bossDying = true;
			_bossDeathElapsed = 0f;
			_bossBaseFootY = _boss.FootY;

			_bossDefeated = true;

			_bossShots.Clear();
			_arenaWaveActive = false;

			_playerInvulnerableDuringWave = false;

			// Hide button so it doesn't distract
			_arenaButtonRadius = 0f;
			_arenaButtonAvailablePhase1 = false;
			_arenaButtonAvailablePhase2 = false;
			_arenaButtonAvailablePhase3 = false;
		}


	}
}

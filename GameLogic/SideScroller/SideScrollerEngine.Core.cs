using System;
using System.Collections.Generic;

namespace GameLogic.SideScroller
{
	public sealed partial class SideScrollerEngine
	{
		// ── World / grid constants ──────────────────────────────────────────
		public const float BlockSize = 40f;
		public const float GroundY = 390f;
		private const float KillDepthBlocksBelowGround = 4f;

		// Player size (used by client + collisions)
		public const float PlayerWidth = 30f;
		public const float PlayerHeight = 40f;

		// Level state
		public bool LevelCompleted { get; private set; }

		// Camera lock region
		public bool CameraLocked { get; private set; }
		private float _cameraLockMinX;
		private float _cameraLockMaxX;

		// Camera position in world space
		private float _cameraX;

		// Level geometry (arrays are built in Platforms partial)
		private Platform[] _platforms;
		private Structure[] _structures;

		// Public read-only access for the client
		public float PlayerX => _playerX;
		public float PlayerY => _playerY;
		public float CameraX => _cameraX;
		public bool IsOnGround => _isOnGround;

		public IReadOnlyList<Platform> Platforms => _platforms;
		public IReadOnlyList<Structure> Structures => _structures;

		// ── ctor ─────────────────────────────────────────────────────────────
		public SideScrollerEngine()
		{
			// Movement tuning/derived values (implemented in Player partial)
			InitializePlayerMovement();

			// Build level geometry (implemented in Platforms partial)
			_platforms = BuildPlatforms();
			_structures = BuildStructures();

			_enemies = BuildEnemies();

			Reset();
		}

		// --- shared helpers (used by all partial files) -------------------

		public static float BlocksAboveGroundToY(int blocksAboveGround) =>
			GroundY - blocksAboveGround * BlockSize;

		public static float BlocksToX(int blocksFromOrigin) =>
			blocksFromOrigin * BlockSize;

		public static float BlocksToWidth(int blocksWide) =>
			blocksWide * BlockSize;

		// --- Public API -----------------------------------------------------

		public void Reset()
		{
			// Player reset (implemented in Player partial)
			ResetPlayer();

			LevelCompleted = false;
			CameraLocked = false;
			_cameraLockMinX = 0f;
			_cameraLockMaxX = 0f;

			_cameraX = 0f;
			_bossDefeated = false;

			_enemies = BuildEnemies();
			ResetBoss();
		}

		/// <summary>
		/// Main simulation step.
		/// Returns true if the player hit the kill volume and was reset this frame.
		/// </summary>
		public bool Update(float dtSeconds, bool leftHeld, bool rightHeld, bool jumpHeld, float viewWidth)
		{
			// 1) Player movement integration (implemented in Player partial)
			SimulatePlayer(dtSeconds, leftHeld, rightHeld, jumpHeld);

			// 2) Move platforms (and carry the player if standing)
			UpdateMovingPlatforms(dtSeconds);

			// 3) Collisions with platforms + structures (Platforms partial)
			HandleCollisions();

			// 4) Finish triggers (Platforms partial)

			CheckArenaButtonLanding();
			CheckFinishPlatforms();

			// 5) Kill volume (void / fall reset)
			if (CheckKillVolume())
				return true;

			UpdateCamera(viewWidth);

			// 6) Enemy collisions

			UpdateEnemies(dtSeconds);
			if (CheckEnemyCollisions())
				return true;

			// 7) Camera follow / lock
			UpdateBoss(dtSeconds, viewWidth);
			if (CheckBossShotCollisions())
				return true;
			return false;
		}

		private bool CheckKillVolume()
		{
			if (_playerInvulnerableDuringWave)
				return false;
			float killY = GroundY + KillDepthBlocksBelowGround * BlockSize;
			if (_playerY > killY)
			{
				Reset();
				return true;
			}
			return false;
		}

		private void UpdateCamera(float viewWidth)
		{
			if (viewWidth <= 0f)
				viewWidth = 800f;

			if (!CameraLocked)
			{
				_cameraX = _playerX - (viewWidth / 2f);
				if (_cameraX < 0f)
					_cameraX = 0f;

				// This is implemented in Platforms partial
				TryActivateCameraLock(viewWidth);
			}
			else
			{
				_cameraX = Math.Clamp(_cameraX, _cameraLockMinX, _cameraLockMaxX);
			}
		}

		public void ReleaseCameraLock() => CameraLocked = false;
	}
}

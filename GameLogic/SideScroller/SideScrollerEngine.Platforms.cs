using System;
using System.Collections.Generic;

namespace GameLogic.SideScroller
{
	public enum PlatformType
	{
		Normal,
		Finish,
		CameraLock,
		Moving
	}

	public sealed partial class SideScrollerEngine
	{
		// --- structs live with the platform logic -------------------------

		public readonly struct Platform
		{
			public Platform(
				float x,
				float y,
				float width,
				float height,
				PlatformType type = PlatformType.Normal,
				float moveMinX = 0f,
				float moveMaxX = 0f,
				float moveSpeed = 0f)
			{
				X = x;
				Y = y;
				Width = width;
				Height = height;
				Type = type;
				MoveMinX = moveMinX;
				MoveMaxX = moveMaxX;
				MoveSpeed = moveSpeed;
			}

			public float X { get; }
			public float Y { get; }
			public float Width { get; }
			public float Height { get; }
			public PlatformType Type { get; }

			public float MoveMinX { get; }
			public float MoveMaxX { get; }
			public float MoveSpeed { get; }
		}

		public readonly struct Structure
		{
			public Structure(float x, float y, float width, float height)
			{
				X = x;
				Y = y;
				Width = width;
				Height = height;
			}

			public float X { get; }
			public float Y { get; }
			public float Width { get; }
			public float Height { get; }
		}

		// --- level construction -------------------------------------------

		private static Platform MakePlatform(
			int blockX,
			int heightBlocks,
			int widthBlocks,
			PlatformType type = PlatformType.Normal,
			float? heightOverride = null,
			float moveMinBlocks = 0f,
			float moveMaxBlocks = 0f,
			float moveSpeedBlocksPerSecond = 0f)
		{
			float topY = BlocksAboveGroundToY(heightBlocks);

			float moveMinX = BlocksToX((int)moveMinBlocks);
			float moveMaxX = BlocksToX((int)moveMaxBlocks);
			float moveSpeedPx = moveSpeedBlocksPerSecond * BlockSize;

			return new Platform(
				x: BlocksToX(blockX),
				y: topY,
				width: BlocksToWidth(widthBlocks),
				height: heightOverride ?? (BlockSize / 2f),
				type: type,
				moveMinX: moveMinX,
				moveMaxX: moveMaxX,
				moveSpeed: moveSpeedPx);
		}

		private static Structure MakeStructure(
			int blockX,
			int heightBlocksAboveGround,
			int widthBlocks,
			int heightBlocks)
		{
			float topY = BlocksAboveGroundToY(heightBlocksAboveGround);

			return new Structure(
				x: BlocksToX(blockX),
				y: topY,
				width: BlocksToWidth(widthBlocks),
				height: heightBlocks * BlockSize);
		}

		private static Platform[] BuildPlatforms() =>
			new[]
			{
				MakePlatform(blockX: 0,  heightBlocks: 0, widthBlocks: 800),
				MakePlatform(blockX: 5,  heightBlocks: 2, widthBlocks: 3),
				MakePlatform(blockX: 12, heightBlocks: 4, widthBlocks: 6),
				MakePlatform(blockX: 22, heightBlocks: 3, widthBlocks: 4),
				MakePlatform(blockX: 85, heightBlocks: 5, widthBlocks: 2),
				MakePlatform(blockX: 90, heightBlocks: 6, widthBlocks: 2),
				MakePlatform(blockX: 97, heightBlocks: 4, widthBlocks: 2),

				MakePlatform(blockX: 600, heightBlocks: 2, widthBlocks: 2,
					type: PlatformType.Finish),

				MakePlatform(blockX: 120, heightBlocks: 0, widthBlocks: 20,type: PlatformType.CameraLock),

				MakePlatform(
					blockX: 30,
					heightBlocks: 4,
					widthBlocks: 3,
					type: PlatformType.Moving,
					moveMinBlocks: 30,
					moveMaxBlocks: 50,
					moveSpeedBlocksPerSecond: 3f),
			};

		private static Structure[] BuildStructures() =>
			new[]
			{
				MakeStructure(blockX: 15, heightBlocksAboveGround: 3, widthBlocks: 2, heightBlocks: 4),
				MakeStructure(blockX: 25, heightBlocksAboveGround: 3, widthBlocks: 5, heightBlocks: 3),
				MakeStructure(blockX: 53, heightBlocksAboveGround: 4, widthBlocks: 30, heightBlocks: 6),
			};

		// --- platform + structure behavior ---------------------------------

		private void HandleCollisions()
		{
			HandlePlatformCollisions();
			HandleStructureCollisions();
		}

		private void HandlePlatformCollisions()
		{
			float playerHalfWidth = PlayerWidth / 2f;
			float feetLeft = _playerX - playerHalfWidth;
			float feetRight = _playerX + playerHalfWidth;

			_isOnGround = false;
			_standingPlatformIndex = -1;

			for (int i = 0; i < _platforms.Length; i++)
			{
				var p = _platforms[i];

				float platformTop = p.Y;
				float platformLeft = p.X;
				float platformRight = p.X + p.Width;

				bool wasAbove = _prevPlayerY <= platformTop;
				bool nowBelowOrOnTop = _playerY >= platformTop;
				bool horizontallyOverPlatform = feetRight > platformLeft && feetLeft < platformRight;
				bool movingDown = _playerVelY >= 0f;

				if (wasAbove && nowBelowOrOnTop && horizontallyOverPlatform && movingDown)
				{
					_playerY = platformTop;
					_playerVelY = 0f;
					_isOnGround = true;
					_standingPlatformIndex = i;
				}
			}
		}

		private void UpdateMovingPlatforms(float dt)
		{
			for (int i = 0; i < _platforms.Length; i++)
			{
				var p = _platforms[i];

				if (p.Type != PlatformType.Moving || p.MoveSpeed == 0f)
					continue;

				float oldX = p.X;
				float newX = p.X + p.MoveSpeed * dt;
				float newSpeed = p.MoveSpeed;

				if (newX < p.MoveMinX || newX + p.Width > p.MoveMaxX)
				{
					newSpeed = -p.MoveSpeed;
					newX = p.X + newSpeed * dt;
				}

				float deltaX = newX - oldX;

				_platforms[i] = new Platform(
					newX, p.Y, p.Width, p.Height,
					p.Type,
					p.MoveMinX, p.MoveMaxX,
					newSpeed);

				if (_standingPlatformIndex == i)
				{
					_playerX += deltaX;
					_prevPlayerX += deltaX;
				}
			}
		}

		private void CheckFinishPlatforms()
		{
			if (LevelCompleted)
				return;

			float playerHalfWidth = PlayerWidth / 2f;
			float playerLeft = _playerX - playerHalfWidth;
			float playerRight = _playerX + playerHalfWidth;

			foreach (var p in _platforms)
			{
				if (p.Type != PlatformType.Finish)
					continue;

				bool horizontalOverlap = playerRight > p.X &&
										 playerLeft < p.X + p.Width;
				bool onTop = Math.Abs(_playerY - p.Y) < 1f;

				if (horizontalOverlap && onTop)
				{
					LevelCompleted = true;
					break;
				}
			}
		}

		private void TryActivateCameraLock(float viewWidth)
		{
			float playerHalfWidth = PlayerWidth / 2f;
			float playerLeft = _playerX - playerHalfWidth;
			float playerRight = _playerX + playerHalfWidth;

			foreach (var p in _platforms)
			{
				if (p.Type != PlatformType.CameraLock)
					continue;

				bool overlap = playerRight > p.X && playerLeft < p.X + p.Width;
				if (overlap)
				{
					if (_bossDefeated)
						return;
					CameraLocked = true;
					_cameraLockMinX = p.X;
					_cameraLockMaxX = p.X + p.Width - viewWidth;
					_cameraX = Math.Clamp(_cameraX, _cameraLockMinX, _cameraLockMaxX);

					// Spawn the boss on the right end of this platform (once)
					if (!_bossActive)
					{
						SpawnBossOnPlatform(p);
					}
					break;
				}
			}
		}

		private void HandleStructureCollisions()
		{
			float playerHalfWidth = PlayerWidth / 2f;

			float playerLeft = _playerX - playerHalfWidth;
			float playerRight = _playerX + playerHalfWidth;
			float playerTop = _playerY - PlayerHeight;
			float playerBottom = _playerY;

			float prevPlayerLeft = _prevPlayerX - playerHalfWidth;
			float prevPlayerRight = _prevPlayerX + playerHalfWidth;

			foreach (var s in _structures)
			{
				float left = s.X;
				float right = s.X + s.Width;
				float top = s.Y;
				float bottom = s.Y + s.Height;

				bool wasAbove = _prevPlayerY <= top;
				bool nowBelowOrTop = _playerY >= top;
				bool overHorizontally = playerRight > left && playerLeft < right;
				bool movingDown = _playerVelY >= 0f;

				if (wasAbove && nowBelowOrTop && overHorizontally && movingDown)
				{
					_playerY = top;
					_playerVelY = 0f;
					_isOnGround = true;

					playerTop = _playerY - PlayerHeight;
					playerBottom = _playerY;
				}

				playerLeft = _playerX - playerHalfWidth;
				playerRight = _playerX + playerHalfWidth;

				bool verticalOverlap = playerBottom > top && playerTop < bottom;

				if (verticalOverlap)
				{
					if (_playerVelX > 0f &&
						prevPlayerRight <= left &&
						playerRight > left)
					{
						_playerX = left - playerHalfWidth;
						_playerVelX = 0f;
					}
					else if (_playerVelX < 0f &&
							 prevPlayerLeft >= right &&
							 playerLeft < right)
					{
						_playerX = right + playerHalfWidth;
						_playerVelX = 0f;
					}
				}
			}
		}
	}
}

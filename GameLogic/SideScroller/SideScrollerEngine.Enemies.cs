using System;
using System.Collections.Generic;

namespace GameLogic.SideScroller
{
	public sealed partial class SideScrollerEngine
	{
		private const float EnemySpeedBlocksPerSecond = 2f; // slower than player (6 blocks/s)

		// Vision tuning
		public const float EnemyVisionRangeBlocks = 5f;          // how far ahead
		public const float EnemyVisionHalfHeightBlocks = 1.5f;   // vertical half-size of the cone base

		// AI tuning
		private const float EnemyLostSightDelaySeconds = 5f;     // after 5s of no vision, give up
		private const float EnemyLookAroundDurationSeconds = 2f; // how long they "look around"
		private const float EnemyStopDistancePixels = 5f;        // close enough to consider reached

		private enum EnemyAIState
		{
			Patrol,
			Chase,
			GoToLastSeen,
			LookAround,
			ReturnToPatrol
		}

		public readonly struct Enemy
		{
			/// <summary>World X of the enemy's center.</summary>
			public float X { get; }

			/// <summary>World Y of the enemy's feet (bottom).</summary>
			public float FootY { get; }

			/// <summary>Width of the enemy for collisions.</summary>
			public float Width { get; }

			/// <summary>Height of the enemy for collisions.</summary>
			public float Height { get; }

			/// <summary>Min X (world) this enemy can patrol to.</summary>
			public float PatrolMinX { get; }

			/// <summary>Max X (world) this enemy can patrol to.</summary>
			public float PatrolMaxX { get; }

			/// <summary>Speed in pixels/second.</summary>
			public float Speed { get; }

			public Enemy(
				float x,
				float footY,
				float width,
				float height,
				float patrolMinX,
				float patrolMaxX,
				float speed)
			{
				X = x;
				FootY = footY;
				Width = width;
				Height = height;
				PatrolMinX = patrolMinX;
				PatrolMaxX = patrolMaxX;
				Speed = speed;
			}
		}

		private Enemy[] _enemies = Array.Empty<Enemy>();

		// Per-enemy runtime data (parallel arrays)
		private float[] _enemyDirections = Array.Empty<float>();        // +1 or -1 (which way they're facing / moving)
		private bool[] _enemySeesPlayer = Array.Empty<bool>();          // vision cone result
		private EnemyAIState[] _enemyStates = Array.Empty<EnemyAIState>();
		private float[] _enemyTimeSinceSeen = Array.Empty<float>();     // seconds since last saw player
		private float[] _enemyLastSeenX = Array.Empty<float>();         // player's last known X
		private float[] _enemyLookTimer = Array.Empty<float>();         // timer for LookAround state
		private float[] _enemyVelY = Array.Empty<float>();

		public IReadOnlyList<Enemy> Enemies => _enemies;
		public IReadOnlyList<float> EnemyDirections => _enemyDirections;
		public IReadOnlyList<bool> EnemySeesPlayerFlags => _enemySeesPlayer;

		/// <summary>
		/// Build initial enemies with simple left/right patrol ranges.
		/// </summary>
		private Enemy[] BuildEnemies()
		{
			var enemies = new List<Enemy>();

			float enemyWidth = 28f;
			float enemyHeight = 50f;
			float enemySpeed = EnemySpeedBlocksPerSecond * BlockSize;

			// How far left/right from their spawn point they walk (in blocks)
			float patrolRadiusBlocks = 5f;
			float patrolRadiusPixels = patrolRadiusBlocks * BlockSize;

			// Enemy on ground near x = 10 blocks
			enemies.Add(MakeGroundEnemyAtBlockX(10, enemyWidth, enemyHeight, enemySpeed, patrolRadiusBlocks));
			enemies.Add(MakeGroundEnemyAtBlockX(100, enemyWidth, enemyHeight, enemySpeed, 5f));
			enemies.Add(MakeGroundEnemyAtBlockX(90, enemyWidth, enemyHeight, enemySpeed, 4f));

			// Enemy on a higher area near x = 22 blocks
			float highEnemyX = BlocksToX(22);
			float highEnemyFootY = BlocksAboveGroundToY(2);
			float highMinX = highEnemyX - patrolRadiusPixels;
			float highMaxX = highEnemyX + patrolRadiusPixels;

			enemies.Add(new Enemy(
				x: highEnemyX,
				footY: highEnemyFootY,
				width: enemyWidth,
				height: enemyHeight,
				patrolMinX: highMinX,
				patrolMaxX: highMaxX,
				speed: enemySpeed));

			// Create arrays + initial AI data
			var arr = enemies.ToArray();
			int n = arr.Length;

			_enemies = arr;
			_enemyDirections = new float[n];
			_enemySeesPlayer = new bool[n];
			_enemyStates = new EnemyAIState[n];
			_enemyTimeSinceSeen = new float[n];
			_enemyLastSeenX = new float[n];
			_enemyLookTimer = new float[n];
			_enemyVelY = new float[n];

			for (int i = 0; i < n; i++)
			{
				_enemyDirections[i] = 1f; // start facing right
				_enemySeesPlayer[i] = false;
				_enemyStates[i] = EnemyAIState.Patrol;
				_enemyTimeSinceSeen[i] = float.MaxValue;
				_enemyLastSeenX[i] = arr[i].X;
				_enemyLookTimer[i] = 0f;
				_enemyVelY[i] = 0f;
			}

			return arr;
		}
		private static Enemy MakeGroundEnemyAtBlockX(
			int blockX,
			float enemyWidth,
			float enemyHeight,
			float enemySpeed,
			float patrolRadiusBlocks)
		{
			float x = BlocksToX(blockX);
			float footY = GroundY;
			float patrolRadiusPixels = patrolRadiusBlocks * BlockSize;

			return new Enemy(
				x: x,
				footY: footY,
				width: enemyWidth,
				height: enemyHeight,
				patrolMinX: x - patrolRadiusPixels,
				patrolMaxX: x + patrolRadiusPixels,
				speed: enemySpeed);
		}

		/// <summary>
		/// Update enemy vision + AI + movement.
		/// </summary>
		private void UpdateEnemies(float dt)
		{
			if (_enemies.Length == 0)
				return;

			// ── PASS 0: vertical physics (gravity + ground) ─────────────
			for (int i = 0; i < _enemies.Length; i++)
			{
				var e = _enemies[i];

				// apply gravity (reuse the same _gravity as the player)
				float velY = _enemyVelY[i];
				velY += _gravity * dt;

				// integrate
				float newFootY = e.FootY + velY * dt;

				// collide with ground
				if (newFootY > GroundY)
				{
					newFootY = GroundY;
					velY = 0f;
				}

				_enemyVelY[i] = velY;

				// write back updated enemy with new FootY (X unchanged here)
				_enemies[i] = new Enemy(
					x: e.X,
					footY: newFootY,
					width: e.Width,
					height: e.Height,
					patrolMinX: e.PatrolMinX,
					patrolMaxX: e.PatrolMaxX,
					speed: e.Speed);
			}

			// Player "head"/center for vision checks
			float playerCenterX = _playerX;
			float playerCenterY = _playerY - PlayerHeight / 2f;

			float rangePixels = EnemyVisionRangeBlocks * BlockSize;
			float halfHeightPixels = EnemyVisionHalfHeightBlocks * BlockSize;

			// ── PASS 1: Vision cone check (no movement) ─────────────────────
			for (int i = 0; i < _enemies.Length; i++)
			{
				var e = _enemies[i];
				float dir = _enemyDirections[i]; // +1 or -1

				// Approx head position in world space
				float enemyTop = e.FootY - e.Height;
				float bodyTop = enemyTop + 10f;
				float headRadius = e.Width * 0.4f;
				float headCenterX = e.X;
				float headCenterY = bodyTop - headRadius * 0.4f;

				// Triangle points in world space
				float tipX = headCenterX;
				float tipY = headCenterY;

				float baseCenterX = tipX + dir * rangePixels;
				float baseCenterY = tipY;

				float baseTopX = baseCenterX;
				float baseTopY = baseCenterY - halfHeightPixels;
				float baseBottomX = baseCenterX;
				float baseBottomY = baseCenterY + halfHeightPixels;

				bool sees =
					PointInTriangle(
						playerCenterX, playerCenterY,
						tipX, tipY,
						baseTopX, baseTopY,
						baseBottomX, baseBottomY);

				_enemySeesPlayer[i] = sees;

				if (sees)
				{
					_enemyTimeSinceSeen[i] = 0f;
					_enemyLastSeenX[i] = playerCenterX;
				}
				else
				{
					_enemyTimeSinceSeen[i] += dt;
				}
			}

			// ── PASS 2: AI state + movement ─────────────────────────────────
			for (int i = 0; i < _enemies.Length; i++)
			{
				var e = _enemies[i];
				float dir = _enemyDirections[i];
				bool sees = _enemySeesPlayer[i];
				var state = _enemyStates[i];

				// Immediate transition to Chase if we see the player
				if (sees)
				{
					state = EnemyAIState.Chase;
				}

				float newX = e.X;

				switch (state)
				{
					case EnemyAIState.Patrol:
						{
							// standard back-and-forth patrol
							newX = e.X + dir * e.Speed * dt;

							if (newX < e.PatrolMinX)
							{
								newX = e.PatrolMinX;
								dir = 1f;
							}
							else if (newX > e.PatrolMaxX)
							{
								newX = e.PatrolMaxX;
								dir = -1f;
							}
							break;
						}

					case EnemyAIState.Chase:
						{
							// while chasing:
							//   - if we see player -> move to player's current X
							//   - if we don't see player -> move to last known X
							float targetX = sees ? playerCenterX : _enemyLastSeenX[i];
							float diff = targetX - e.X;
							if (MathF.Abs(diff) > EnemyStopDistancePixels)
							{
								dir = MathF.Sign(diff);
								newX = e.X + dir * e.Speed * dt;
							}

							// If we've been blind for long enough, switch to GoToLastSeen
							if (!sees && _enemyTimeSinceSeen[i] >= EnemyLostSightDelaySeconds)
							{
								state = EnemyAIState.GoToLastSeen;
							}
							break;
						}

					case EnemyAIState.GoToLastSeen:
						{
							// If we see the player again, go back to chasing.
							if (sees)
							{
								state = EnemyAIState.Chase;
								goto case EnemyAIState.Chase;
							}

							float targetX = _enemyLastSeenX[i];
							float diff = targetX - e.X;

							if (MathF.Abs(diff) <= EnemyStopDistancePixels)
							{
								// Reached last known location -> start looking around
								newX = e.X;
								_enemyLookTimer[i] = 0f;
								state = EnemyAIState.LookAround;
							}
							else
							{
								dir = MathF.Sign(diff);
								newX = e.X + dir * e.Speed * dt;
							}
							break;
						}

					case EnemyAIState.LookAround:
						{
							// If we see the player again, chase.
							if (sees)
							{
								state = EnemyAIState.Chase;
								break;
							}

							_enemyLookTimer[i] += dt;

							// Flip facing direction back and forth while looking.
							dir = MathF.Sin(_enemyLookTimer[i] * 4f) >= 0f ? 1f : -1f;
							newX = e.X; // stand still

							if (_enemyLookTimer[i] >= EnemyLookAroundDurationSeconds)
							{
								state = EnemyAIState.ReturnToPatrol;
							}
							break;
						}

					case EnemyAIState.ReturnToPatrol:
						{
							// If we see the player again, chase.
							if (sees)
							{
								state = EnemyAIState.Chase;
								goto case EnemyAIState.Chase;
							}

							// Go back to center of patrol range.
							float patrolCenter = (e.PatrolMinX + e.PatrolMaxX) * 0.5f;
							float diff = patrolCenter - e.X;

							if (MathF.Abs(diff) <= EnemyStopDistancePixels)
							{
								newX = patrolCenter;
								state = EnemyAIState.Patrol;
							}
							else
							{
								dir = MathF.Sign(diff);
								newX = e.X + dir * e.Speed * dt;
							}
							break;
						}
				}

				// Write back updated data
				_enemyStates[i] = state;
				_enemyDirections[i] = dir;

				_enemies[i] = new Enemy(
					x: newX,
					footY: e.FootY,
					width: e.Width,
					height: e.Height,
					patrolMinX: e.PatrolMinX,
					patrolMaxX: e.PatrolMaxX,
					speed: e.Speed);
			}
		}

		/// <summary>
		/// Simple barycentric point-in-triangle test.
		/// </summary>
		private static bool PointInTriangle(
			float px, float py,
			float ax, float ay,
			float bx, float by,
			float cx, float cy)
		{
			float v0x = cx - ax;
			float v0y = cy - ay;
			float v1x = bx - ax;
			float v1y = by - ay;
			float v2x = px - ax;
			float v2y = py - ay;

			float dot00 = v0x * v0x + v0y * v0y;
			float dot01 = v0x * v1x + v0y * v1y;
			float dot02 = v0x * v2x + v0y * v2y;
			float dot11 = v1x * v1x + v1y * v1y;
			float dot12 = v1x * v2x + v1y * v2y;

			float denom = dot00 * dot11 - dot01 * dot01;
			if (denom == 0f)
				return false;

			float invDenom = 1f / denom;
			float u = (dot11 * dot02 - dot01 * dot12) * invDenom;
			float v = (dot00 * dot12 - dot01 * dot02) * invDenom;

			return (u >= 0f) && (v >= 0f) && (u + v <= 1f);
		}

		/// <summary>
		/// Simple AABB collision: if the player touches any enemy, treat as death.
		/// </summary>
		private bool CheckEnemyCollisions()
		{
			if (_playerInvulnerableDuringWave)
				return false;
			if (_enemies.Length == 0)
				return false;

			// Player AABB
			float playerLeft = _playerX - PlayerWidth / 2f;
			float playerRight = playerLeft + PlayerWidth;
			float playerTop = _playerY - PlayerHeight;
			float playerBottom = _playerY;

			foreach (var enemy in _enemies)
			{
				float enemyLeft = enemy.X - enemy.Width / 2f;
				float enemyRight = enemyLeft + enemy.Width;
				float enemyTop = enemy.FootY - enemy.Height;
				float enemyBottom = enemy.FootY;

				bool overlap =
					playerLeft < enemyRight &&
					playerRight > enemyLeft &&
					playerTop < enemyBottom &&
					playerBottom > enemyTop;

				if (overlap)
				{
					// For now: just treat like kill volume
					Reset();
					return true;
				}
			}

			return false;
		}
		private void AddEnemy(Enemy enemy)
		{
			int oldCount = _enemies.Length;
			var newEnemies = new Enemy[oldCount + 1];
			Array.Copy(_enemies, newEnemies, oldCount);
			newEnemies[oldCount] = enemy;
			_enemies = newEnemies;

			Array.Resize(ref _enemyDirections, oldCount + 1);
			Array.Resize(ref _enemySeesPlayer, oldCount + 1);
			Array.Resize(ref _enemyStates, oldCount + 1);
			Array.Resize(ref _enemyTimeSinceSeen, oldCount + 1);
			Array.Resize(ref _enemyLastSeenX, oldCount + 1);
			Array.Resize(ref _enemyLookTimer, oldCount + 1);
			Array.Resize(ref _enemyVelY, oldCount + 1);

			_enemyDirections[oldCount] = 1f;
			_enemySeesPlayer[oldCount] = false;
			_enemyStates[oldCount] = EnemyAIState.Patrol;
			_enemyTimeSinceSeen[oldCount] = float.MaxValue;
			_enemyLastSeenX[oldCount] = enemy.X;
			_enemyLookTimer[oldCount] = 0f;
			_enemyVelY[oldCount] = 0f;
		}
		private void RemoveArenaGroundEnemies()
		{
			if (_enemies.Length == 0)
				return;

			// We treat "arena ground enemies" as those on the ground, within the arena platform X range
			float minX = _arenaPlatformMinX - BlockSize;   // small padding
			float maxX = _arenaPlatformMaxX + BlockSize;
			float minY = GroundY - 1f;
			float maxY = GroundY + 1f;

			var newEnemies = new List<Enemy>(_enemies.Length);
			var newDirections = new List<float>(_enemies.Length);
			var newSees = new List<bool>(_enemies.Length);
			var newStates = new List<EnemyAIState>(_enemies.Length);
			var newTimeSinceSeen = new List<float>(_enemies.Length);
			var newLastSeenX = new List<float>(_enemies.Length);
			var newLookTimer = new List<float>(_enemies.Length);
			var newVelY = new List<float>(_enemies.Length);

			for (int i = 0; i < _enemies.Length; i++)
			{
				var e = _enemies[i];

				bool isArenaGround =
					e.FootY >= minY && e.FootY <= maxY &&
					e.X >= minX && e.X <= maxX;

				if (isArenaGround)
				{
					// Skip (we are removing this enemy)
					continue;
				}

				newEnemies.Add(e);
				newDirections.Add(_enemyDirections[i]);
				newSees.Add(_enemySeesPlayer[i]);
				newStates.Add(_enemyStates[i]);
				newTimeSinceSeen.Add(_enemyTimeSinceSeen[i]);
				newLastSeenX.Add(_enemyLastSeenX[i]);
				newLookTimer.Add(_enemyLookTimer[i]);
				newVelY.Add(_enemyVelY[i]);
			}

			_enemies = newEnemies.ToArray();
			_enemyDirections = newDirections.ToArray();
			_enemySeesPlayer = newSees.ToArray();
			_enemyStates = newStates.ToArray();
			_enemyTimeSinceSeen = newTimeSinceSeen.ToArray();
			_enemyLastSeenX = newLastSeenX.ToArray();
			_enemyLookTimer = newLookTimer.ToArray();
			_enemyVelY = newVelY.ToArray();
		}


	}
}

// GameLogic/JumpsOnlineRoomState.cs
using System;
using System.Collections.Generic;
using GameContracts;
using GameLogic.Jumps;

namespace GameLogic.JumpsOnline
{
	/// <summary>
	/// Server-side runtime state for a single Jumps Online room.
	/// Holds the shared world (platforms / pickups) and up to 3 players.
	/// Pure data – the JumpsOnlineGameHandler will own all the simulation logic.
	/// </summary>
	public sealed class JumpsOnlineRoomState : IRoomState
	{
		public const int MaxPlayers = 3;

		public string RoomCode { get; }

		/// <summary>Current high-level phase of the game.</summary>
		public JumpsOnlinePhase Phase { get; set; } = JumpsOnlinePhase.Lobby;

		/// <summary>
		/// When Phase == Countdown, remaining time in seconds (e.g. from 3 down to 0).
		/// Otherwise 0.
		/// </summary>
		public float CountdownSecondsRemaining { get; set; }

		/// <summary>
		/// Time since the round actually started (Phase == Running), in seconds.
		/// Useful if you ever want time-based effects or stats.
		/// </summary>
		public float ElapsedSinceRoundStart { get; set; }

		/// <summary>
		/// Players currently in this room (up to 3). Indexed 0..Count-1.
		/// PlayerIndex maps directly to how we color them / assign lanes on the client.
		/// </summary>
		public List<JumpsOnlinePlayerRuntime> Players { get; } = new();

		/// <summary>
		/// Quick lookup by PlayerId ("P1", "P2", "P3").
		/// </summary>
		public Dictionary<string, JumpsOnlinePlayerRuntime> PlayersById { get; } = new();

		/// <summary>
		/// Number of players that are still alive this round.
		/// When this hits 0, Phase should switch to Finished.
		/// </summary>
		public int AlivePlayerCount { get; set; }

		// ─────────────────────────────────────────────
		// Shared world: single vertical stack of platforms
		// spanning N * 3 columns, where N = Players.Count
		// ─────────────────────────────────────────────

		/// <summary>Y position of the ground in world units.</summary>
		public float GroundY { get; set; } = JumpsEngine.WorldHeight - 40f;

		/// <summary>Current scroll speed (same for everyone), units per second.</summary>
		public float ScrollSpeed { get; set; }

		/// <summary>
		/// Time the scroll has been active (for "BaseScrollSpeed + accel * t" logic).
		/// </summary>
		public float ElapsedSinceScrollStart { get; set; }

		/// <summary>
		/// Difficulty / level number derived from scroll speed,
		/// same idea as single-player Jumps.
		/// </summary>
		public int Level { get; set; } = 1;

		/// <summary>
		/// Total number of vertical lanes (columns) in the shared world.
		/// Typically = Players.Count * 3 (so 3, 6, or 9).
		/// </summary>
		public int TotalColumns { get; set; } = 3;

		/// <summary>
		/// How many extra rows we keep buffered offscreen above/below.
		/// Same role as _bufferRows in JumpsEngine.
		/// </summary>
		public int BufferRows { get; set; } = 1;

		/// <summary>
		/// Monotonically increasing row index, used when spawning new rows of platforms.
		/// </summary>
		public int NextRowIndex { get; set; }

		/// <summary>
		/// All platforms currently in the world (may be culled by the handler).
		/// </summary>
		public List<JumpsOnlinePlatformRuntime> Platforms { get; } = new();

		// ─────────────────────────────────────────────
		// RNG + results
		// ─────────────────────────────────────────────

		/// <summary>
		/// Room-scoped RNG so platform layout / pickups are stable between ticks.
		/// </summary>
		public Random Rng { get; }

		/// <summary>
		/// True once we've computed final standings & decided winner/tie.
		/// Helps the handler avoid sending results multiple times.
		/// </summary>
		public bool ResultsCalculated { get; set; }

		public string? WinnerPlayerId { get; set; }
		public bool IsTie { get; set; }

		// ─────────────────────────────────────────────
		// Construction / helpers
		// ─────────────────────────────────────────────

		public JumpsOnlineRoomState(string roomCode, int seed)
		{
			RoomCode = roomCode;
			Rng = new Random(seed);
		}

		/// <summary>
		/// Adds a new player to this room and returns the runtime slot.
		/// PlayerIndex will be 0, 1, or 2.
		/// </summary>
		public JumpsOnlinePlayerRuntime AddPlayer(string playerId)
		{
			int index = Players.Count;
			if (index >= MaxPlayers)
				throw new InvalidOperationException("Cannot add more than 3 players to a JumpsOnline room.");

			var player = new JumpsOnlinePlayerRuntime(playerId, index);
			Players.Add(player);
			PlayersById[playerId] = player;
			AlivePlayerCount = Players.Count;
			return player;
		}

		/// <summary>
		/// Removes a player by id (used when someone leaves mid-lobby).
		/// Once a round is running you may choose to just mark them dead instead.
		/// </summary>
		public void RemovePlayer(string playerId)
		{
			if (!PlayersById.TryGetValue(playerId, out var p))
				return;

			PlayersById.Remove(playerId);
			Players.Remove(p);

			AlivePlayerCount = 0;
			foreach (var player in Players)
			{
				if (player.IsAlive)
					AlivePlayerCount++;
			}
		}
	}

	/// <summary>
	/// Server-side runtime for a single player in Jumps Online.
	/// Mirrors the kind of data JumpsEngine keeps for its single player,
	/// but here we have one of these per player sharing one world.
	/// </summary>
	public sealed class JumpsOnlinePlayerRuntime
	{
		public string PlayerId { get; }
		public int PlayerIndex { get; }

		public bool IsAlive { get; set; } = true;

		// Position / velocity
		public float X { get; set; }
		public float Y { get; set; }
		public float VX { get; set; }
		public float VY { get; set; }

		// Grounding / jumping state
		public bool HasStarted { get; set; }
		public bool IsGrounded { get; set; }
		public bool IsJumping { get; set; }
		public bool JumpCutApplied { get; set; }
		public float JumpBufferRemaining { get; set; }
		public int AirJumpsRemaining { get; set; }

		/// <summary>Coins collected this round.</summary>
		public int Coins { get; set; }

		/// <summary>
		/// For player-vs-player collision you can use these with JumpsEngine.PlayerSize.
		/// </summary>
		public float Width => JumpsEngine.PlayerSize;
		public float Height => JumpsEngine.PlayerSize;

		// Powerups – used both for logic and for UI rings on the client

		public bool JumpBoostActive { get; set; }
		public float JumpBoostTimeRemaining { get; set; }

		public bool SpeedBoostActive { get; set; }
		public float SpeedBoostTimeRemaining { get; set; }

		public bool MagnetActive { get; set; }
		public float MagnetTimeRemaining { get; set; }

		public bool DoubleJumpActive { get; set; }
		public float DoubleJumpTimeRemaining { get; set; }

		public bool SlowScrollActive { get; set; }
		public float SlowScrollTimeRemaining { get; set; }

		public JumpsOnlinePlayerRuntime(string playerId, int playerIndex)
		{
			PlayerId = playerId;
			PlayerIndex = playerIndex;
		}
	}

	/// <summary>
	/// Runtime representation of a platform in the shared Jumps Online world.
	/// This is similar to JumpsEngine's internal Platform type, but without logic.
	/// </summary>
	public sealed class JumpsOnlinePlatformRuntime
	{
		public int RowIndex { get; set; }

		public float X { get; set; }
		public float Y { get; set; }

		public float Width { get; set; } = JumpsEngine.PlatformWidth;
		public float Height { get; set; } = JumpsEngine.PlatformHeight;

		public JumpsOnlinePickupRuntime? Pickup { get; set; }
	}

	/// <summary>
	/// Runtime representation of a pickup in Jumps Online.
	/// Mirrors the data you need to build JumpsOnlinePickupDto.
	/// </summary>
	public sealed class JumpsOnlinePickupRuntime
	{
		public JumpsOnlinePickupType Type { get; set; }

		public bool Collected { get; set; }

		// Magnet animation state (world coordinates)
		public bool IsMagnetPulling { get; set; }
		public float WorldX { get; set; }
		public float WorldY { get; set; }
	}
}

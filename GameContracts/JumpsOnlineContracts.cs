// GameContracts/JumpsOnlineContract.cs
using System.Collections.Generic;

namespace GameContracts
{
	/// <summary>
	/// Game phases for the online Jumps mode.
	/// </summary>
	public enum JumpsOnlinePhase
	{
		Lobby = 0,      // Waiting for players, P1 can press "Start"
		Countdown = 1,  // 3-second countdown before movement starts
		Running = 2,    // Game in progress
		Finished = 3    // All players dead, winner decided
	}

	/// <summary>
	/// Pickup types in the online Jumps mode.
	/// Mirrors GameLogic.Jumps.JumpsPickupType.
	/// </summary>
	public enum JumpsOnlinePickupType
	{
		Coin = 0,
		JumpBoost = 1,
		SpeedBoost = 2,
		Magnet = 3,
		DoubleJump = 4,
		SlowScroll = 5
	}

	// ─────────────────────────────────────────────────────────────
	// Client → Server: per-player input
	// MessageType: "JumpsOnlineInput"
	// Sent frequently (e.g. each client tick) while the game is running.
	// ─────────────────────────────────────────────────────────────
	public class JumpsOnlineInputPayload
	{
		/// <summary>True if the player is holding left.</summary>
		public bool Left { get; set; }

		/// <summary>True if the player is holding right.</summary>
		public bool Right { get; set; }

		/// <summary>True if the player is holding down (for drop-through).</summary>
		public bool Down { get; set; }

		/// <summary>True if the jump key is currently held.</summary>
		public bool JumpHeld { get; set; }

		/// <summary>
		/// Optional small integer increasing with each input send from this client.
		/// Useful later if you want latency compensation or input history.
		/// </summary>
		public int Sequence { get; set; }
	}

	// ─────────────────────────────────────────────────────────────
	// Client → Server: P1 starts the round
	// MessageType: "JumpsOnlineStartRequest"
	// Only P1 should be allowed to trigger this on the server.
	// ─────────────────────────────────────────────────────────────
	public class JumpsOnlineStartRequestPayload
	{
		public string RoomCode { get; set; } = string.Empty;
	}

	// ─────────────────────────────────────────────────────────────
	// Optional: client → server request to restart after Finished
	// MessageType: "JumpsOnlineRestartRequest"
	// ─────────────────────────────────────────────────────────────
	public class JumpsOnlineRestartRequestPayload
	{
		public string RoomCode { get; set; } = string.Empty;
	}

	// ─────────────────────────────────────────────────────────────
	// Server → Clients: per-player state inside the snapshot
	// (authoritative view of all players)
	// ─────────────────────────────────────────────────────────────
	public class JumpsOnlinePlayerStateDto
	{
		/// <summary>
		/// Room-level player id ("P1", "P2", "P3").
		/// </summary>
		public string PlayerId { get; set; } = string.Empty;

		/// <summary>
		/// Index in [0..2] to help with fixed-size arrays / colors on client.
		/// </summary>
		public int PlayerIndex { get; set; }

		/// <summary>Current X position in world units.</summary>
		public float X { get; set; }

		/// <summary>Current Y position in world units.</summary>
		public float Y { get; set; }

		/// <summary>True if this player is still alive (has not fallen past death margin).</summary>
		public bool IsAlive { get; set; }

		/// <summary>Current coin count for this player.</summary>
		public int Coins { get; set; }

		// Power-up visuals (used by client for rings / indicators)

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
	}

	// ─────────────────────────────────────────────────────────────
	// Server → Clients: platform + pickup info inside snapshot
	// This represents the shared world across all players
	// (up to 3 * 3 = 9 lanes, depending on player count).
	// ─────────────────────────────────────────────────────────────
	public class JumpsOnlinePickupDto
	{
		public JumpsOnlinePickupType Type { get; set; }

		/// <summary>True if already collected by any player.</summary>
		public bool Collected { get; set; }

		/// <summary>Pickup center X in world units.</summary>
		public float X { get; set; }

		/// <summary>Pickup center Y in world units.</summary>
		public float Y { get; set; }

		/// <summary>
		/// True if this pickup is currently being pulled towards some player
		/// by a magnet effect (used for animation).
		/// </summary>
		public bool IsMagnetPulling { get; set; }
	}

	public class JumpsOnlinePlatformDto
	{
		/// <summary>Platform rectangle X (left) in world units.</summary>
		public float X { get; set; }

		/// <summary>Platform rectangle Y (top) in world units.</summary>
		public float Y { get; set; }

		/// <summary>Platform rectangle width in world units.</summary>
		public float Width { get; set; }

		/// <summary>Platform rectangle height in world units.</summary>
		public float Height { get; set; }

		/// <summary>
		/// Optional pickup sitting on/near this platform.
		/// Null if there is no pickup on this platform.
		/// </summary>
		public JumpsOnlinePickupDto? Pickup { get; set; }
	}

	// ─────────────────────────────────────────────────────────────
	// Server → Clients: full game snapshot / tick
	// MessageType: "JumpsOnlineSnapshot"
	//
	// Sent on a regular tick from the server (e.g. 20–30 Hz). Clients
	// render based on this authoritative state, plus any prediction
	// you might eventually add on top.
	// ─────────────────────────────────────────────────────────────
	public class JumpsOnlineSnapshotPayload
	{
		public string RoomCode { get; set; } = string.Empty;

		/// <summary>Overall game phase (Lobby, Countdown, Running, Finished).</summary>
		public JumpsOnlinePhase Phase { get; set; }

		/// <summary>
		/// Time remaining in the current countdown (seconds).
		/// Only meaningful when Phase == Countdown, otherwise 0.
		/// </summary>
		public float CountdownSecondsRemaining { get; set; }

		/// <summary>
		/// Current "level" for difficulty / UI, derived from scroll speed
		/// just like the single-player version.
		/// </summary>
		public int Level { get; set; }

		/// <summary>
		/// Current scroll speed of the world (used for UI/debug on client).
		/// </summary>
		public float ScrollSpeed { get; set; }

		/// <summary>
		/// All active players in this room, up to 3.
		/// </summary>
		public List<JumpsOnlinePlayerStateDto> Players { get; set; } = new();

		/// <summary>
		/// All active platforms and their pickups in the shared world.
		/// Server may cull far-off platforms if needed.
		/// </summary>
		public List<JumpsOnlinePlatformDto> Platforms { get; set; } = new();

		/// <summary>
		/// PlayerId of the winner once Phase == Finished.
		/// Null/empty if there is a tie or game not finished yet.
		/// </summary>
		public string? WinnerPlayerId { get; set; }

		/// <summary>
		/// True if the result was a tie for top coins.
		/// </summary>
		public bool IsTie { get; set; }
	}

	// ─────────────────────────────────────────────────────────────
	// Server → Clients: final round results (optional helper)
	// MessageType: "JumpsOnlineResults"
	// Sent once when the round ends (everyone dead).
	// ─────────────────────────────────────────────────────────────
	public class JumpsOnlineResultsEntry
	{
		public string PlayerId { get; set; } = string.Empty;
		public int PlayerIndex { get; set; }
		public int Coins { get; set; }
		public bool IsWinner { get; set; }
	}

	public class JumpsOnlineResultsPayload
	{
		public string RoomCode { get; set; } = string.Empty;

		/// <summary>
		/// Sorted list of players and their final coin counts.
		/// (e.g. descending by Coins).
		/// </summary>
		public List<JumpsOnlineResultsEntry> Players { get; set; } = new();

		/// <summary>
		/// PlayerId of the winner, or empty if tie.
		/// </summary>
		public string? WinnerPlayerId { get; set; }

		public bool IsTie { get; set; }
	}
}

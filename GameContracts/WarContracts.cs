// GameContracts/WarContracts.cs
using System;

namespace GameContracts
{
	// ─────────────────────────────────────────────────────────
	// Shared enums
	// ─────────────────────────────────────────────────────────

	/// <summary>
	/// Logical side in the War game.
	/// </summary>
	public enum WarSide
	{
		Left = 0,
		Right = 1
	}

	/// <summary>
	/// High-level network-visible game phase.
	/// (Maps from GameLogic.War.WarEngine.WarState on the server.)
	/// </summary>
	public enum WarNetworkState
	{
		Lobby = 0,           // Waiting for 2 players and/or side selection
		Dealing = 1,
		WaitingForClick = 2,
		Countdown = 3,
		ShowingBattle = 4,
		WarFaceDown = 5,
		RoundResult = 6,
		GameOver = 7
	}

	/// <summary>
	/// Perspective-independent round winner.
	/// (Left / Right / Tie), so clients can map it to "You win/lose".
	/// </summary>
	public enum WarNetworkRoundWinner
	{
		None = 0,
		Left = 1,
		Right = 2,
		Tie = 3
	}

	/// <summary>
	/// Simple serializable card used in War snapshots.
	/// Server maps GameLogic.CardGames.Card -> WarCardDto.
	/// </summary>
	public struct WarCardDto
	{
		public int Rank { get; set; }  // e.g. 2-14 (if you use 2..Ace)
		public int Suit { get; set; }  // e.g. 0-3 for Clubs/Diamonds/Hearts/Spades
	}

	// ─────────────────────────────────────────────────────────
	// Client → Server payloads
	// ─────────────────────────────────────────────────────────

	/// <summary>
	/// Client requests to take a side (Left/Right) in the lobby.
	/// MessageType: "WarSelectSide"
	/// </summary>
	public class WarSelectSidePayload
	{
		/// <summary>Logical side requested by this player.</summary>
		public WarSide Side { get; set; }

		/// <summary>PlayerId sending the request (from hub).</summary>
		public string PlayerId { get; set; } = string.Empty;
	}

	/// <summary>
	/// Client indicates they clicked their deck and are ready for the next battle.
	/// MessageType: "WarReady"
	/// </summary>
	public class WarReadyPayload
	{
		public string PlayerId { get; set; } = string.Empty;
	}

	/// <summary>
	/// Client asks to shuffle their own deck (if allowed).
	/// MessageType: "WarShuffleRequest"
	/// </summary>
	public class WarShuffleRequestPayload
	{
		public string PlayerId { get; set; } = string.Empty;
	}

	// (Optional later) Rematch, emotes, etc. can go here:
	// public class WarRematchRequestPayload { ... }

	// ─────────────────────────────────────────────────────────
	// Server → Client payloads
	// ─────────────────────────────────────────────────────────

	/// <summary>
	/// Snapshot of the lobby: who is in the room, who is Left/Right, and whether the game has started.
	/// MessageType: "WarLobbyState"
	/// </summary>
	public class WarLobbyStatePayload
	{
		public string RoomCode { get; set; } = string.Empty;

		/// <summary>PlayerId in the Left slot, or null if unclaimed.</summary>
		public string? LeftPlayerId { get; set; }

		/// <summary>PlayerId in the Right slot, or null if unclaimed.</summary>
		public string? RightPlayerId { get; set; }

		/// <summary>true once both slots are occupied and the game has begun.</summary>
		public bool GameStarted { get; set; }

		public int ConnectedPlayers { get; set; }
	}

	/// <summary>
	/// Full game-state snapshot used to drive the client visuals.
	/// MessageType: "WarState"
	/// </summary>
	public class WarStatePayload
	{
		public string RoomCode { get; set; } = string.Empty;

		/// <summary>Phase of the game, from the server's perspective.</summary>
		public WarNetworkState State { get; set; }

		/// <summary>
		/// Which side the receiving player is on (Left/Right).
		/// This is filled per-recipient in the handler so the client
		/// can draw "YOUR SIDE" correctly.
		/// </summary>
		public WarSide LocalSide { get; set; }

		// existing fields...
		public bool LeftReady { get; set; }
		public bool RightReady { get; set; }

		// optional if you want smooth server-driven anim:
		public float LeftReadyProgress { get; set; }   // 0..1
		public float RightReadyProgress { get; set; }  // 0..1

		// Deck counts
		public int LeftDeckCount { get; set; }
		public int RightDeckCount { get; set; }
		public int CenterDeckCount { get; set; }

		/// <summary>
		/// Countdown value currently being shown (3,2,1...).
		/// </summary>
		public float CountdownValue { get; set; }

		/// <summary>
		/// Number of face-down "war" cards currently stacked in the center.
		/// (Used to offset drawing.)
		/// </summary>
		public int WarFaceDownPlaced { get; set; }

		/// <summary>
		/// Last round winner in Left/Right/Tie form, or None.
		/// </summary>
		public WarNetworkRoundWinner LastRoundWinner { get; set; }

		// Dealing animation
		public bool HasDealCardInFlight { get; set; }
		public bool DealToLeftNext { get; set; }
		public float DealProgress { get; set; } // 0..1

		// Battle animation
		public int BattlePhase { get; set; }    // maps from WarEngine.BattleAnimPhase (int)
		public float BattleAnimProgress { get; set; } // 0..1

		// Face-up battle cards (if any)
		public WarCardDto? LeftFaceUp { get; set; }
		public WarCardDto? RightFaceUp { get; set; }

		/// <summary>
		/// Whether the local player is allowed to shuffle their deck now.
		/// (Maps from engine.ShuffleUnlocked, but can be turned off per-player if you want.)
		/// </summary>
		public bool ShuffleUnlocked { get; set; }
	}
}

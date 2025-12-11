using System.Collections.Generic;

namespace GameContracts
{
	/// <summary>
	/// High-level phase of a Blackjack round.
	/// </summary>
	public enum BlackjackPhase
	{
		Lobby = 0,        // Waiting for players, host can start
		Dealing = 1,      // Initial deal animation / state
		PlayerTurns = 2,  // Players taking turns (Hit/Stand)
		DealerTurn = 3,   // Dealer drawing their cards
		RoundResults = 4  // Results visible; host can restart
	}

	/// <summary>
	/// Per-player result at round end.
	/// </summary>
	public enum BlackjackResult
	{
		Pending = 0,  // round still in progress
		Win = 1,
		Lose = 2,
		Push = 3,
		Blackjack = 4
	}

	/// <summary>
	/// Player actions during PlayerTurns.
	/// </summary>
	public enum BlackjackActionType
	{
		Hit = 0,
		Stand = 1,
		// Future: DoubleDown = 2, Split = 3, etc.
	}

	// ─────────────────────────────────────────────────────────────
	// Client → Server
	// ─────────────────────────────────────────────────────────────

	/// <summary>
	/// P1 (host) asks to start a round.
	/// MessageType: "BlackjackStartRequest"
	/// </summary>
	public class BlackjackStartRequestPayload
	{
		public string RoomCode { get; set; } = string.Empty;
	}

	/// <summary>
	/// Player action during their turn.
	/// MessageType: "BlackjackAction"
	/// </summary>
	public class BlackjackActionPayload
	{
		public string RoomCode { get; set; } = string.Empty;
		public string PlayerId { get; set; } = string.Empty;
		public BlackjackActionType Action { get; set; }
	}

	// ─────────────────────────────────────────────────────────────
	// Server → Client: snapshot
	// ─────────────────────────────────────────────────────────────

	/// <summary>
	/// Network-friendly representation of a card.
	/// </summary>
	public class BlackjackCardDto
	{
		public int Rank { get; set; }       // e.g. 2-14 for 2..Ace
		public int Suit { get; set; }       // 0-3 for Clubs/Diamonds/Hearts/Spades
		public bool IsFaceDown { get; set; } // true for dealer hole card when hidden
	}

	public class BlackjackPlayerStateDto
	{
		/// <summary>Room-level player id ("P1", "P2", "P3", "P4").</summary>
		public string PlayerId { get; set; } = string.Empty;

		/// <summary>Seat index [0..3] – stable ordering for UI.</summary>
		public int SeatIndex { get; set; }

		/// <summary>true if this player is currently connected.</summary>
		public bool IsConnected { get; set; }

		/// <summary>true if this player is still in the current round (not folded/busted entirely).</summary>
		public bool IsInRound { get; set; }

		/// <summary>true if this is the active player's turn.</summary>
		public bool IsCurrentTurn { get; set; }

		/// <summary>true if the player has chosen Stand.</summary>
		public bool HasStood { get; set; }

		/// <summary>true if hand is bust & they are out for this round.</summary>
		public bool IsBust { get; set; }

		/// <summary>Current best 21-value (e.g. 18, 20, 21, 22 for bust).</summary>
		public int HandValue { get; set; }

		/// <summary>Total chips or score across rounds (optional for v1).</summary>
		public int Chips { get; set; }

		/// <summary>Chips at risk for this round (could be fixed 1 for now).</summary>
		public int Bet { get; set; }

		/// <summary>Final result at round end.</summary>
		public BlackjackResult Result { get; set; }

		/// <summary>The player's cards.</summary>
		public List<BlackjackCardDto> Cards { get; set; } = new();
	}

	public class BlackjackSnapshotPayload
	{
		public string RoomCode { get; set; } = string.Empty;

		/// <summary>Overall game phase (Lobby, Dealing, etc.).</summary>
		public BlackjackPhase Phase { get; set; }

		/// <summary>Room-level id of the current turn player (e.g. "P2") or null.</summary>
		public string? CurrentPlayerId { get; set; }

		/// <summary>Dealer cards (at least one may be face-down until reveal).</summary>
		public List<BlackjackCardDto> DealerCards { get; set; } = new();

		/// <summary>Dealer's visible total (0 in Lobby / Dealing).</summary>
		public int DealerVisibleValue { get; set; }

		/// <summary>True when dealer’s hole card is revealed (DealerTurn / RoundResults).</summary>
		public bool DealerRevealed { get; set; }

		/// <summary>All seats currently in the room (up to 4).</summary>
		public List<BlackjackPlayerStateDto> Players { get; set; } = new();

		/// <summary>True if the round is fully resolved and P1 may restart.</summary>
		public bool RoundComplete { get; set; }
	}
}

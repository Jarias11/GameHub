// GameContracts/CheckersContracts.cs
using System;

namespace GameContracts
{
	/// <summary>
	/// Cell encoding for the Checkers board in network payloads.
	/// Server/GameLogic can map its own enums to these values.
	/// </summary>
	public enum CheckersCell
	{
		Empty = 0,
		RedMan = 1,
		RedKing = 2,
		BlackMan = 3,
		BlackKing = 4
	}

	// ─────────────────────────────────────────────────────────
	// Client → Server: player attempts a move
	// MessageType: "CheckersMove"
	// ─────────────────────────────────────────────────────────
	public class CheckersMovePayload
	{
		/// <summary>Source row (0–7).</summary>
		public int FromRow { get; set; }

		/// <summary>Source column (0–7).</summary>
		public int FromCol { get; set; }

		/// <summary>Destination row (0–7).</summary>
		public int ToRow { get; set; }

		/// <summary>Destination column (0–7).</summary>
		public int ToCol { get; set; }
	}

	// ─────────────────────────────────────────────────────────
	// Client → Server: player resigns
	// MessageType: "CheckersResign"
	// (payload can be empty – server infers player from connection)
	// ─────────────────────────────────────────────────────────
	public class CheckersResignPayload
	{
		// Intentionally empty for now.
		// If you ever want to log a reason, you could add:
		// public string? Reason { get; set; }
	}

	// ─────────────────────────────────────────────────────────
	// Server → Client: full board + meta state
	// MessageType: "CheckersState"
	// ─────────────────────────────────────────────────────────
	public class CheckersStatePayload
	{
		/// <summary>
		/// Flattened 8x8 board (length 64).
		/// Index = row * 8 + col.
		/// When waiting for 2nd player, server can send all Empty.
		/// </summary>
		public CheckersCell[] Cells { get; set; } = Array.Empty<CheckersCell>();

		/// <summary>
		/// Logical player IDs (matching Room.Players entries), e.g. "P1"/"P2".
		/// The server will randomly assign which is Red vs Black.
		/// </summary>
		public string RedPlayerId { get; set; } = string.Empty;
		public string BlackPlayerId { get; set; } = string.Empty;

		/// <summary>
		/// True once both players are present and the starting
		/// board has been initialized.
		/// </summary>
		public bool IsStarted { get; set; }

		/// <summary>
		/// Whose turn it is ("P1" or "P2").
		/// Empty / null means game not started or finished.
		/// </summary>
		public string? CurrentPlayerId { get; set; }

		/// <summary>Whether the game is over.</summary>
		public bool IsGameOver { get; set; }

		/// <summary>
		/// Winner logical player ID ("P1"/"P2"), or null if draw or not finished.
		/// For resign, this will be the *other* player.
		/// </summary>
		public string? WinnerPlayerId { get; set; }

		/// <summary>
		/// Optional human-readable status: "Waiting for opponent",
		/// "Red to move", "Black wins by resignation", "Draw", etc.
		/// </summary>
		public string? Message { get; set; }

		// ─────────────────────────────────────────────────────
		// Optional helpers for nicer client UX
		// ─────────────────────────────────────────────────────

		/// <summary>
		/// If a multi-capture is in progress, the server can enforce that
		/// only the piece at (ForcedFromRow, ForcedFromCol) continues moving.
		/// Null when no forced continuation.
		/// </summary>
		public int? ForcedFromRow { get; set; }
		public int? ForcedFromCol { get; set; }

		/// <summary>
		/// Info about the last successful move, so the client can
		/// highlight it or animate it.
		/// Nulls when no moves yet.
		/// </summary>
		public int? LastFromRow { get; set; }
		public int? LastFromCol { get; set; }
		public int? LastToRow { get; set; }
		public int? LastToCol { get; set; }
	}
}

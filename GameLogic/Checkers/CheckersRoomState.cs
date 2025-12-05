// GameLogic/CheckersRoomState.cs
using System;

namespace GameLogic.Checkers
{
	/// <summary>
	/// Piece types for Checkers. You can tweak names/sides later.
	/// </summary>
	public enum CheckersPiece
	{
		Empty     = 0,
		RedMan,
		RedKing,
		BlackMan,
		BlackKing
	}

	/// <summary>
	/// Server-side state for a single Checkers room.
	/// </summary>
	public sealed class CheckersRoomState : IRoomState
	{
		/// <summary>
		/// Room code this state belongs to (matches Room.RoomCode).
		/// </summary>
		public string RoomCode { get; init; }

		/// <summary>
		/// Constant board size (standard Checkers is 8x8).
		/// </summary>
		public const int BoardSize = 8;

		/// <summary>
		/// The board, indexed as [row, col].
		/// </summary>
		public CheckersPiece[,] Board { get; set; } = new CheckersPiece[BoardSize, BoardSize];

		/// <summary>
		/// Logical player IDs (usually "P1" / "P2") mapped to sides.
		/// These line up with Room.Players entries.
		/// They will be assigned randomly when the second player joins.
		/// </summary>
		public string? RedPlayerId { get; set; }
		public string? BlackPlayerId { get; set; }

		/// <summary>
		/// True once both players are present and the board has been
		/// initialized with starting pieces.
		/// </summary>
		public bool IsStarted { get; set; }

		/// <summary>
		/// Whose turn it is (player ID "P1" / "P2"), or null if not started.
		/// </summary>
		public string? CurrentTurnPlayerId { get; set; }

		/// <summary>
		/// Whether the game has finished and who won, if anyone.
		/// </summary>
		public bool IsGameOver { get; set; }
		public string? WinnerPlayerId { get; set; }

		/// <summary>
		/// Optional human-readable status for debugging / logging.
		/// (You typically format a nicer message when mapping to payload.)
		/// </summary>
		public string? StatusMessage { get; set; }

		/// <summary>
		/// If a capture move created a forced multi-capture, this tells us
		/// which piece must continue the move.
		/// Null when no forced continuation is active.
		/// </summary>
		public int? ForcedFromRow { get; set; }
		public int? ForcedFromCol { get; set; }

		/// <summary>
		/// Info about the last successful move (for animations/highlight).
		/// </summary>
		public int? LastFromRow { get; set; }
		public int? LastFromCol { get; set; }
		public int? LastToRow   { get; set; }
		public int? LastToCol   { get; set; }

		/// <summary>
		/// Incremented each time a legal move is applied, mainly for debugging.
		/// </summary>
		public int MoveNumber { get; set; }

		/// <summary>
		/// Parameterless ctor for serializers.
		/// </summary>
		public CheckersRoomState()
		{
			RoomCode = string.Empty;
		}

		/// <summary>
		/// Main ctor the handler will use when creating a room state.
		/// </summary>
		public CheckersRoomState(string roomCode)
		{
			if (string.IsNullOrWhiteSpace(roomCode))
				throw new ArgumentException("Room code cannot be null or empty.", nameof(roomCode));

			RoomCode = roomCode;
			// Board is already empty by default; we only populate once
			// both players are present and we randomize sides.
		}
	}
}

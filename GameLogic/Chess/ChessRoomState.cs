using GameLogic;

namespace GameLogic.Chess
{
	/// <summary>
	/// Server-side per-room state for online Chess.
	/// Holds the core ChessState plus which PlayerId is White / Black.
	/// </summary>
	public sealed class ChessRoomState : IRoomState
	{
		public string RoomCode { get; }

		/// <summary>Owner PlayerId (creator, P1). Only P1 may choose colors.</summary>
		public string? OwnerPlayerId { get; set; }

		public string? WhitePlayerId { get; set; }
		public string? BlackPlayerId { get; set; }

		// Draw offer state (server-authoritative)
		public string? PendingDrawOfferFromPlayerId { get; set; }
		public int PendingDrawOfferPlyIndex { get; set; } = -1;

		// Once-per-ply offer rate-limit (per player)
		public int WhiteLastOfferPlyIndex { get; set; } = -1;
		public int BlackLastOfferPlyIndex { get; set; } = -1;

		/// <summary>Core chess logic (board, moves, checkmate).</summary>
		public ChessState State { get; private set; }

		public ChessRoomState(string roomCode)
		{
			RoomCode = roomCode;
			State = new ChessState();
		}

		public void Reset()
		{
			State = new ChessState();
			PendingDrawOfferFromPlayerId = null;
		PendingDrawOfferPlyIndex = -1;
		WhiteLastOfferPlyIndex = -1;
		BlackLastOfferPlyIndex = -1;
		}
	}
}

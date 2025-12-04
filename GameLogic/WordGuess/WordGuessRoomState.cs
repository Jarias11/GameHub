using System.Collections.Generic;
using GameContracts;

namespace GameLogic.WordGuess
{
	/// <summary>
	/// Server-side state for a WordGuess room.
	/// No sockets, no ASP.NET, just game data.
	/// </summary>
	public class WordGuessRoomState : IRoomState
	{
		public string RoomCode { get; }

		// upper-case, 5 letters once set
		public string? SecretWord { get; set; }

		public int MaxAttempts { get; } = 5;
		public int AttemptsMade { get; set; }
		public bool IsGameOver { get; set; }

		// Optional: keep history for UI or reconnection
		public List<WordGuessResultPayload> History { get; } = new();

		public WordGuessRoomState(string roomCode)
		{
			RoomCode = roomCode;
		}
	}
}

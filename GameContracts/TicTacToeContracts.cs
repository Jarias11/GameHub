using System;
namespace GameContracts;

// Client → Server: player makes a move
public class TicTacToeMovePayload
{
	public int Row { get; set; }     // 0–2
	public int Col { get; set; }     // 0–2
}

// Server → Client: full board state + status
public class TicTacToeStatePayload
{
	// e.g. ' ', 'X', 'O' or whatever encoding you like
	public char[] Cells { get; set; } = Array.Empty<char>(); // length 9

	public string CurrentPlayerId { get; set; } = string.Empty; // "P1" / "P2"
	public bool IsGameOver { get; set; }
	public string? WinnerPlayerId { get; set; } // null if draw or not finished
	public bool IsDraw { get; set; }

	public string? Message { get; set; } // "P1's turn", "P2 wins!", "Draw", etc.
}

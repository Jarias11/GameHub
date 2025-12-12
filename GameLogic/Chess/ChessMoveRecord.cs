namespace GameLogic.Chess
{
	public sealed class ChessMoveRecord
	{
		/// <summary>Full move number (1, 2, 3, ...), shared by White+Black.</summary>
		public int MoveNumber { get; set; }

		/// <summary>Side that played this move.</summary>
		public ChessColor Color { get; set; }

		/// <summary>Algebraic notation of the move (e.g. "e4", "Nf3", "O-O", "exd8=Q#").</summary>
		public string San { get; set; } = string.Empty;
	}
}

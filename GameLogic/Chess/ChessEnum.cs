namespace GameLogic.Chess
{
	public enum ChessColor
	{
		White = 0,
		Black = 1
	}
	public enum ChessPieceType
	{
		Pawn,
		Knight,
		Bishop,
		Rook,
		Queen,
		King
	}
	public readonly struct ChessPiece
	{
		public ChessPieceType Type { get; }
		public ChessColor Color { get; }

		public ChessPiece(ChessPieceType type, ChessColor color)
		{
			Type = type;
			Color = color;
		}

		public override string ToString() => $"{Color} {Type}";
	}
}
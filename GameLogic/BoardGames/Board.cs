// GameLogic/BoardGames/Board.cs
using System;
using System.Collections.Generic;

namespace GameLogic.BoardGames
{
	/// <summary>
	/// Generic rectangular board that other board games (Chess, Checkers, etc.)
	/// can use for cell coordinates and helper methods.
	/// </summary>
	public sealed class Board
	{
		public int Rows { get; }
		public int Columns { get; }

		public Board(int rows, int columns)
		{
			if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
			if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));

			Rows = rows;
			Columns = columns;
		}

		/// <summary>
		/// Convenience factory for a standard 8x8 board (Chess, Checkers, etc.).
		/// </summary>
		public static Board Create8x8() => new Board(8, 8);

		public bool IsInside(int row, int col) =>
			row >= 0 && row < Rows && col >= 0 && col < Columns;

		/// <summary>
		/// Flatten (row, col) into a 0-based index.
		/// </summary>
		public int ToIndex(int row, int col)
		{
			if (!IsInside(row, col))
				throw new ArgumentOutOfRangeException($"Cell ({row},{col}) is outside the board.");
			return row * Columns + col;
		}

		/// <summary>
		/// Expand a 0-based index back to (row, col).
		/// </summary>
		public (int row, int col) FromIndex(int index)
		{
			if (index < 0 || index >= Rows * Columns)
				throw new ArgumentOutOfRangeException(nameof(index));

			int row = index / Columns;
			int col = index % Columns;
			return (row, col);
		}

		/// <summary>
		/// Enumerate all board cells as (row, col).
		/// </summary>
		public IEnumerable<(int row, int col)> AllCells()
		{
			for (int r = 0; r < Rows; r++)
				for (int c = 0; c < Columns; c++)
					yield return (r, c);
		}

		/// <summary>
		/// True if this square should be treated as the "dark" square in a check pattern.
		/// Useful for Chess/Checkers boards.
		/// </summary>
		public bool IsDarkSquare(int row, int col)
		{
			if (!IsInside(row, col))
				throw new ArgumentOutOfRangeException($"Cell ({row},{col}) is outside the board.");

			// Standard alternating pattern:
			// (0,0) light; (0,1) dark; etc.
			return (row + col) % 2 == 1;
		}
	}
}

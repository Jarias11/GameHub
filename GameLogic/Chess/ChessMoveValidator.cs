using System;
using System.Collections.Generic;

namespace GameLogic.Chess
{
	/// <summary>
	/// Contains piece-specific movement rules for Chess.
	/// This is "pseudo-legal" validation: it enforces how each piece moves,
	/// prevents capturing your own pieces, and now supports en passant movement.
	/// It does NOT enforce:
	/// - Check/checkmate
	/// - Castling (handled in ChessState)
	/// - Promotion (still TODO)
	/// </summary>
	internal static class ChessMoveValidator
	{
		/// <summary>
		/// Returns all pseudo-legal destination squares (including en passant)
		/// for the piece on (fromRow, fromCol), for the side whose turn it is.
		/// This does NOT filter out moves that leave your king in check.
		/// </summary>
		public static IEnumerable<(int row, int col)> GetLegalMovesFor(
			ChessState state,
			int fromRow,
			int fromCol)
		{
			var board = state.Board;

			for (int r = 0; r < board.Rows; r++)
			{
				for (int c = 0; c < board.Columns; c++)
				{
					if (IsLegalBasicMove(state, fromRow, fromCol, r, c, out _))
					{
						yield return (r, c);
					}
				}
			}
		}

		/// <summary>
		/// Pseudo-legal move validation for all non-castling moves.
		/// - Checks bounds
		/// - Checks it's the current side to move
		/// - Enforces piece movement rules
		/// - Prevents capturing your own piece
		/// - Supports en passant
		/// Does NOT:
		/// - Prevent leaving your king in check
		/// - Handle castling
		/// - Handle promotion
		/// </summary>
		public static bool IsLegalBasicMove(
			ChessState state,
			int fromRow,
			int fromCol,
			int toRow,
			int toCol,
			out string? error)
		{
			error = null;
			var board = state.Board;

			// Basic board bounds
			if (!board.IsInside(fromRow, fromCol) || !board.IsInside(toRow, toCol))
			{
				error = "Move outside the board.";
				return false;
			}

			if (fromRow == toRow && fromCol == toCol)
			{
				error = "Cannot move to the same square.";
				return false;
			}

			var piece = state.GetPiece(fromRow, fromCol);
			if (piece is null)
			{
				error = "No piece on the selected square.";
				return false;
			}

			// Must move your own color
			if (piece.Value.Color != state.CurrentTurn)
			{
				error = "It's not your turn.";
				return false;
			}

			var target = state.GetPiece(toRow, toCol);

			// Cannot capture your own piece
			if (target is { } t && t.Color == piece.Value.Color)
			{
				error = "You cannot capture your own piece.";
				return false;
			}

			// Dispatch based on piece type
			return piece.Value.Type switch
			{
				ChessPieceType.Pawn => IsLegalPawnMove(state, piece.Value, fromRow, fromCol, toRow, toCol, target, out error),
				ChessPieceType.Knight => IsLegalKnightMove(fromRow, fromCol, toRow, toCol, out error),
				ChessPieceType.Bishop => IsLegalSlidingMove(state, fromRow, fromCol, toRow, toCol, 1, 1, out error),
				ChessPieceType.Rook => IsLegalSlidingMove(state, fromRow, fromCol, toRow, toCol, 0, 1, out error),
				ChessPieceType.Queen => IsLegalQueenMove(state, fromRow, fromCol, toRow, toCol, out error),
				ChessPieceType.King => IsLegalKingMove(fromRow, fromCol, toRow, toCol, out error),
				_ => Fail("Unknown piece type.", out error)
			};
		}

		// ───────────────────────── PAWN ─────────────────────────

		private static bool IsLegalPawnMove(
			ChessState state,
			ChessPiece pawn,
			int fromRow,
			int fromCol,
			int toRow,
			int toCol,
			ChessPiece? target,
			out string? error)
		{
			error = null;

			int direction = pawn.Color == ChessColor.White ? -1 : 1;
			int startRow = pawn.Color == ChessColor.White ? 6 : 1;

			int rowDelta = toRow - fromRow;
			int colDelta = toCol - fromCol;
			int absCol = Math.Abs(colDelta);

			// Forward move (no capture)
			if (colDelta == 0)
			{
				// Must be empty
				if (target is not null)
				{
					error = "Pawns cannot capture straight ahead.";
					return false;
				}

				// Single step
				if (rowDelta == direction)
				{
					return true;
				}

				// Double step from starting rank
				if (fromRow == startRow && rowDelta == 2 * direction)
				{
					// Check the intermediate square is empty
					int midRow = fromRow + direction;
					if (state.GetPiece(midRow, fromCol) is null)
					{
						return true;
					}

					error = "Pawn cannot jump over pieces.";
					return false;
				}

				error = "Invalid pawn forward move.";
				return false;
			}

			// Diagonal capture (one step) or en passant
			if (absCol == 1 && rowDelta == direction)
			{
				// Normal capture
				if (target is not null && target.Value.Color != pawn.Color)
				{
					return true;
				}

				// En passant: target square empty but matches state's en passant target
				if (target is null && state.EnPassantTarget is { } ep &&
					ep.row == toRow && ep.col == toCol)
				{
					return true;
				}

				error = "Pawns capture diagonally when a piece or en-passant target is there.";
				return false;
			}

			// No weird backwards/promotion moves yet
			error = "Unsupported pawn move (promotion not implemented yet).";
			return false;
		}

		// ───────────────────────── KNIGHT ────────────────────────

		private static bool IsLegalKnightMove(
			int fromRow,
			int fromCol,
			int toRow,
			int toCol,
			out string? error)
		{
			error = null;

			int dr = Math.Abs(toRow - fromRow);
			int dc = Math.Abs(toCol - fromCol);

			if ((dr == 2 && dc == 1) || (dr == 1 && dc == 2))
			{
				return true;
			}

			error = "Invalid knight move.";
			return false;
		}

		// ────────────────── BISHOP / ROOK / QUEEN ─────────────────

		private static bool IsLegalSlidingMove(
			ChessState state,
			int fromRow,
			int fromCol,
			int toRow,
			int toCol,
			int diagStepRow,    // 1 for diagonal, 0 for straight
			int diagStepCol,    // 1 for diagonal, 1 for straight
			out string? error)
		{
			error = null;

			int dr = toRow - fromRow;
			int dc = toCol - fromCol;

			int absDr = Math.Abs(dr);
			int absDc = Math.Abs(dc);

			bool isDiagonal = absDr == absDc && absDr != 0;
			bool isStraight = (dr == 0 && dc != 0) || (dc == 0 && dr != 0);

			// diagStepRow/diagStepCol define which moves we allow:
			// - Bishop: diagStepRow=1, diagStepCol=1 => diagonal only
			// - Rook:   diagStepRow=0, diagStepCol=1 => straight only
			// (Queen will call both via IsLegalQueenMove)
			if (diagStepRow == 1 && diagStepCol == 1 && !isDiagonal)
			{
				error = "This piece moves diagonally.";
				return false;
			}

			if (diagStepRow == 0 && diagStepCol == 1 && !isStraight)
			{
				error = "This piece moves in straight lines.";
				return false;
			}

			if (!isDiagonal && !isStraight)
			{
				error = "Invalid sliding move.";
				return false;
			}

			// Direction of movement per step
			int stepRow = dr == 0 ? 0 : dr / Math.Abs(dr);
			int stepCol = dc == 0 ? 0 : dc / Math.Abs(dc);

			int currentRow = fromRow + stepRow;
			int currentCol = fromCol + stepCol;

			// Check all intermediate squares are empty
			while (currentRow != toRow || currentCol != toCol)
			{
				if (state.GetPiece(currentRow, currentCol) is not null)
				{
					error = "Path is blocked.";
					return false;
				}

				currentRow += stepRow;
				currentCol += stepCol;
			}

			return true;
		}

		private static bool IsLegalQueenMove(
			ChessState state,
			int fromRow,
			int fromCol,
			int toRow,
			int toCol,
			out string? error)
		{
			// Try diagonal first
			if (IsLegalSlidingMove(state, fromRow, fromCol, toRow, toCol, 1, 1, out error))
				return true;

			// If diagonal failed we might try straight
			if (IsLegalSlidingMove(state, fromRow, fromCol, toRow, toCol, 0, 1, out error))
				return true;

			// If both fail, error is set
			return false;
		}

		// ───────────────────────── KING ──────────────────────────

		private static bool IsLegalKingMove(
			int fromRow,
			int fromCol,
			int toRow,
			int toCol,
			out string? error)
		{
			error = null;

			int dr = Math.Abs(toRow - fromRow);
			int dc = Math.Abs(toCol - fromCol);

			if (dr <= 1 && dc <= 1 && (dr + dc > 0))
			{
				// 1-square king moves only; castling handled in ChessState.
				return true;
			}

			error = "Invalid king move (castling handled separately).";
			return false;
		}

		private static bool Fail(string message, out string? error)
		{
			error = message;
			return false;
		}
	}
}

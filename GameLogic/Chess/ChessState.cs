using System;
using System.Collections.Generic;
using GameLogic.BoardGames;

namespace GameLogic.Chess
{
	/// <summary>
	/// Core chess state: board, pieces, and whose turn it is.
	/// No UI, no networking, just logic.
	/// </summary>
	public sealed class ChessState
	{
		public Board Board { get; } = Board.Create8x8();

		// row, col -> ChessPiece or null if empty
		private readonly ChessPiece?[,] _pieces;

		public ChessColor CurrentTurn { get; private set; } = ChessColor.White;

		// En passant target square (row, col) if available this turn.
		// This is the square the capturing pawn moves TO.
		private (int row, int col)? _enPassantTarget;
		public (int row, int col)? EnPassantTarget => _enPassantTarget;

		// Castling rights tracking (simple booleans):
		// true means "this piece HAS moved" or rook was captured from that square.
		private bool _whiteKingMoved, _blackKingMoved;
		private bool _whiteRookAFileMoved, _whiteRookHFileMoved;
		private bool _blackRookAFileMoved, _blackRookHFileMoved;
		// Information about the most recent promotion (for UI purposes).
		private (int row, int col, ChessColor color)? _pendingPromotion;
		public (int row, int col, ChessColor color)? PendingPromotion => _pendingPromotion;
		public bool HasPendingPromotion => _pendingPromotion.HasValue;

		// Move history & captures
		private readonly List<ChessMoveRecord> _moveHistory = new();
		private int _fullMoveNumber = 1;

		private readonly List<ChessPiece> _capturedByWhite = new();
		private readonly List<ChessPiece> _capturedByBlack = new();

		public IReadOnlyList<ChessMoveRecord> MoveHistory => _moveHistory;
		public IReadOnlyList<ChessPiece> CapturedByWhite => _capturedByWhite;
		public IReadOnlyList<ChessPiece> CapturedByBlack => _capturedByBlack;

		public bool IsGameOver { get; private set; }
		public ChessColor? Winner { get; private set; }

		public ChessState()
		{
			_pieces = new ChessPiece?[Board.Rows, Board.Columns];
			SetupStartingPosition();
		}

		// ─────────────────────────────────────────────
		// Basic access
		// ─────────────────────────────────────────────

		public ChessPiece? GetPiece(int row, int col)
		{
			if (!Board.IsInside(row, col)) return null;
			return _pieces[row, col];
		}

		private void SetPiece(int row, int col, ChessPiece? piece)
		{
			if (!Board.IsInside(row, col))
				throw new ArgumentOutOfRangeException(nameof(row), "Cell outside board.");

			_pieces[row, col] = piece;
		}


		// ─────────────────────────────────────────────
		// Initial setup
		// ─────────────────────────────────────────────

		private void SetupStartingPosition()
		{
			// Clear board
			for (int r = 0; r < Board.Rows; r++)
				for (int c = 0; c < Board.Columns; c++)
					_pieces[r, c] = null;

			// Pawns
			for (int c = 0; c < 8; c++)
			{
				SetPiece(6, c, new ChessPiece(ChessPieceType.Pawn, ChessColor.White)); // row 6
				SetPiece(1, c, new ChessPiece(ChessPieceType.Pawn, ChessColor.Black)); // row 1
			}

			// Rooks
			SetPiece(7, 0, new ChessPiece(ChessPieceType.Rook, ChessColor.White));
			SetPiece(7, 7, new ChessPiece(ChessPieceType.Rook, ChessColor.White));
			SetPiece(0, 0, new ChessPiece(ChessPieceType.Rook, ChessColor.Black));
			SetPiece(0, 7, new ChessPiece(ChessPieceType.Rook, ChessColor.Black));

			// Knights
			SetPiece(7, 1, new ChessPiece(ChessPieceType.Knight, ChessColor.White));
			SetPiece(7, 6, new ChessPiece(ChessPieceType.Knight, ChessColor.White));
			SetPiece(0, 1, new ChessPiece(ChessPieceType.Knight, ChessColor.Black));
			SetPiece(0, 6, new ChessPiece(ChessPieceType.Knight, ChessColor.Black));

			// Bishops
			SetPiece(7, 2, new ChessPiece(ChessPieceType.Bishop, ChessColor.White));
			SetPiece(7, 5, new ChessPiece(ChessPieceType.Bishop, ChessColor.White));
			SetPiece(0, 2, new ChessPiece(ChessPieceType.Bishop, ChessColor.Black));
			SetPiece(0, 5, new ChessPiece(ChessPieceType.Bishop, ChessColor.Black));

			// Queens
			SetPiece(7, 3, new ChessPiece(ChessPieceType.Queen, ChessColor.White));
			SetPiece(0, 3, new ChessPiece(ChessPieceType.Queen, ChessColor.Black));

			// Kings
			SetPiece(7, 4, new ChessPiece(ChessPieceType.King, ChessColor.White));
			SetPiece(0, 4, new ChessPiece(ChessPieceType.King, ChessColor.Black));

			// Turn & special-state reset
			CurrentTurn = ChessColor.White;
			_enPassantTarget = null;

			_whiteKingMoved = _blackKingMoved = false;
			_whiteRookAFileMoved = _whiteRookHFileMoved = false;
			_blackRookAFileMoved = _blackRookHFileMoved = false;
			_pendingPromotion = null;

			_fullMoveNumber = 1;
			_moveHistory.Clear();
			_capturedByWhite.Clear();
			_capturedByBlack.Clear();
			IsGameOver = false;
			Winner = null;
		}

		// ─────────────────────────────────────────────
		// Moves
		// ─────────────────────────────────────────────

		/// <summary>
		/// Validates and applies a move according to chess rules:
		/// - Correct turn
		/// - Piece-specific movement (including en passant)
		/// - No capturing own pieces
		/// - Sliding pieces can't jump
		/// - No move that leaves your own king in check
		/// - Supports castling (kingside/queenside)
		/// </summary>


		public void ClearPendingPromotion()
		{
			_pendingPromotion = null;
		}
		public bool TryMove(int fromRow, int fromCol, int toRow, int toCol, out string? error)
		{
			error = null;

			if (!Board.IsInside(fromRow, fromCol) || !Board.IsInside(toRow, toCol))
			{
				error = "Move outside the board.";
				return false;
			}

			var piece = GetPiece(fromRow, fromCol);
			if (piece is null)
			{
				error = "No piece on the selected square.";
				return false;
			}

			if (piece.Value.Color != CurrentTurn)
			{
				error = "It's not your turn.";
				return false;
			}

			// ── Castling is handled as a special king move ────────────────
			if (piece.Value.Type == ChessPieceType.King &&
				fromRow == toRow &&
				Math.Abs(toCol - fromCol) == 2)
			{
				return TryCastle(piece.Value.Color, fromRow, fromCol, toRow, toCol, out error);
			}

			// ── Normal / capture / en passant move ────────────────────────

			if (!ChessMoveValidator.IsLegalBasicMove(this, fromRow, fromCol, toRow, toCol, out error))
			{
				return false;
			}

			var movingPiece = piece.Value;

			// Target before the move (for normal captures)
			var targetBefore = GetPiece(toRow, toCol);

			// Determine if this is an en passant capture
			bool isEnPassant = IsEnPassantCapture(movingPiece, fromRow, fromCol, toRow, toCol);

			// Figure out which piece is actually captured (including en passant)
			ChessPiece? capturedForHistory = targetBefore;
			if (isEnPassant)
			{
				int dir = movingPiece.Color == ChessColor.White ? 1 : -1;
				int captureRow = toRow + dir;
				capturedForHistory = GetPiece(captureRow, toCol);
			}

			// Reject moves that leave our own king in check
			if (MoveLeavesKingInCheck(movingPiece.Color, fromRow, fromCol, toRow, toCol, isEnPassant))
			{
				error = "You cannot leave your king in check.";
				return false;
			}

			// Apply the move for real and update special rights (including promotion)
			ApplyMoveAndUpdateState(movingPiece, fromRow, fromCol, toRow, toCol, isEnPassant);

			// After applying, it's now the opponent's turn
			// (we'll swap at the bottom, but we'll reason about enemy explicitly)
			var enemy = movingPiece.Color == ChessColor.White ? ChessColor.Black : ChessColor.White;

			// Check / checkmate against the opponent
			bool enemyInCheck = IsInCheck(enemy);
			bool enemyIsCheckmated = enemyInCheck && !HasAnyLegalMove(enemy);

			if (enemyIsCheckmated)
			{
				IsGameOver = true;
				Winner = movingPiece.Color; // side that delivered mate
			}

			// Promotion info for notation (we currently always auto-queen)
			bool isPromotion = false;
			ChessPieceType promotionType = ChessPieceType.Queen;
			if (movingPiece.Type == ChessPieceType.Pawn)
			{
				int lastRank = movingPiece.Color == ChessColor.White ? 0 : 7;
				if (toRow == lastRank)
				{
					isPromotion = true;
				}
			}

			// Build SAN string
			string san = BuildSan(
				movingPiece,
				fromRow,
				fromCol,
				toRow,
				toCol,
				capturedForHistory,
				isEnPassant,
				isPromotion,
				promotionType,
				enemyInCheck,
				enemyIsCheckmated);

			// Record move & captured piece
			RecordMove(movingPiece.Color, san, capturedForHistory);

			// Swap turn
			CurrentTurn = CurrentTurn == ChessColor.White ? ChessColor.Black : ChessColor.White;
			return true;

		}

		/// <summary>
		/// Public helper used by the client to draw hints:
		/// Returns only fully legal moves (no king left in check, includes castling & en passant).
		/// </summary>
		public IEnumerable<(int row, int col)> GetLegalMoves(int fromRow, int fromCol)
		{
			var piece = GetPiece(fromRow, fromCol);
			if (piece is null || piece.Value.Color != CurrentTurn)
				yield break;

			// 1) Normal / capture / en passant moves from the validator
			foreach (var (toRow, toCol) in ChessMoveValidator.GetLegalMovesFor(this, fromRow, fromCol))
			{
				bool isEnPassant = IsEnPassantCapture(piece.Value, fromRow, fromCol, toRow, toCol);
				if (!MoveLeavesKingInCheck(piece.Value.Color, fromRow, fromCol, toRow, toCol, isEnPassant))
				{
					yield return (toRow, toCol);
				}
			}

			// 2) Castling moves (if this is the king on its home square)
			if (piece.Value.Type == ChessPieceType.King)
			{
				int homeRow = piece.Value.Color == ChessColor.White ? 7 : 0;

				if (fromRow == homeRow && fromCol == 4)
				{
					// King-side castle destination: file g (6)
					if (CanCastle(piece.Value.Color, kingSide: true))
					{
						yield return (homeRow, 6);
					}

					// Queen-side castle destination: file c (2)
					if (CanCastle(piece.Value.Color, kingSide: false))
					{
						yield return (homeRow, 2);
					}
				}
			}
		}

		// ─────────────────────────────────────────────
		// En passant helpers
		// ─────────────────────────────────────────────

		private bool IsEnPassantCapture(ChessPiece pawn, int fromRow, int fromCol, int toRow, int toCol)
		{
			if (pawn.Type != ChessPieceType.Pawn)
				return false;

			// Must move diagonally onto an empty square that matches en-passant target
			var target = GetPiece(toRow, toCol);
			if (target is not null)
				return false;

			if (_enPassantTarget is not { } ep)
				return false;

			if (ep.row != toRow || ep.col != toCol)
				return false;

			int colDelta = Math.Abs(toCol - fromCol);
			int direction = pawn.Color == ChessColor.White ? -1 : 1;
			int rowDelta = toRow - fromRow;

			return colDelta == 1 && rowDelta == direction;
		}

		// Apply move to a temporary board and see if it leaves 'color' in check.
		private bool MoveLeavesKingInCheck(
			ChessColor color,
			int fromRow,
			int fromCol,
			int toRow,
			int toCol,
			bool isEnPassant)
		{
			var backup = (ChessPiece?[,])_pieces.Clone();

			var piece = GetPiece(fromRow, fromCol)!.Value;

			// Remove from origin
			_pieces[fromRow, fromCol] = null;

			// Handle en passant capture: captured pawn is behind the target square
			if (isEnPassant)
			{
				int dir = color == ChessColor.White ? 1 : -1; // pawn moves up (-1), so captured pawn is below (+1)
				int captureRow = toRow + dir;
				_pieces[captureRow, toCol] = null;
			}

			// Place piece on target
			_pieces[toRow, toCol] = piece;

			bool inCheck = IsInCheck(color);

			// Restore board
			CopyBoard(backup, _pieces);

			return inCheck;
		}

		private static void CopyBoard(ChessPiece?[,] src, ChessPiece?[,] dst)
		{
			int rows = src.GetLength(0);
			int cols = src.GetLength(1);

			for (int r = 0; r < rows; r++)
				for (int c = 0; c < cols; c++)
					dst[r, c] = src[r, c];
		}

		// Apply move to the real state, update en passant & castling rights.
		private void ApplyMoveAndUpdateState(
			ChessPiece piece,
			int fromRow,
			int fromCol,
			int toRow,
			int toCol,
			bool isEnPassant)
		{
			// Clear previous en-passant target; it'll be refreshed only on double pawn push
			_enPassantTarget = null;

			// Capture info (normal capture only; en-passant handled below)
			var captured = GetPiece(toRow, toCol);

			// Move piece from origin
			SetPiece(fromRow, fromCol, null);

			// En passant capture: remove the pawn behind the target square
			if (isEnPassant)
			{
				int dir = piece.Color == ChessColor.White ? 1 : -1;
				int captureRow = toRow + dir;
				SetPiece(captureRow, toCol, null);
			}

			// Place moving piece
			SetPiece(toRow, toCol, piece);

			// Reset promotion info for this move; we'll set it again if needed.
			_pendingPromotion = null;

			// Pawn-specific logic: double push (en passant) + promotion
			if (piece.Type == ChessPieceType.Pawn)
			{
				int startRow = piece.Color == ChessColor.White ? 6 : 1;
				int dir = piece.Color == ChessColor.White ? -1 : 1;
				int lastRank = piece.Color == ChessColor.White ? 0 : 7;

				// Double push -> set en-passant target
				if (fromRow == startRow && toRow == fromRow + 2 * dir)
				{
					_enPassantTarget = (fromRow + dir, fromCol);
				}
				else
				{
					// Any other pawn move cancels en passant target
					_enPassantTarget = null;
				}

				// Promotion: pawn reached last rank
				if (toRow == lastRank)
				{
					var queen = new ChessPiece(ChessPieceType.Queen, piece.Color);
					SetPiece(toRow, toCol, queen);

					// Record that a promotion happened this move
					_pendingPromotion = (toRow, toCol, piece.Color);
				}
			}
			else
			{
				// Non-pawn move cancels en passant target
				_enPassantTarget = null;
			}

			// Update castling rights for king / rook moves or rook captures

			// King moved
			if (piece.Type == ChessPieceType.King)
			{
				if (piece.Color == ChessColor.White) _whiteKingMoved = true;
				else _blackKingMoved = true;
			}

			// Rook moved (from starting squares)
			if (piece.Type == ChessPieceType.Rook)
			{
				if (piece.Color == ChessColor.White)
				{
					if (fromRow == 7 && fromCol == 0) _whiteRookAFileMoved = true;
					if (fromRow == 7 && fromCol == 7) _whiteRookHFileMoved = true;
				}
				else
				{
					if (fromRow == 0 && fromCol == 0) _blackRookAFileMoved = true;
					if (fromRow == 0 && fromCol == 7) _blackRookHFileMoved = true;
				}
			}

			// Captured rook on its starting square -> lose that castling right
			if (captured is { } cap && cap.Type == ChessPieceType.Rook)
			{
				if (cap.Color == ChessColor.White)
				{
					if (toRow == 7 && toCol == 0) _whiteRookAFileMoved = true;
					if (toRow == 7 && toCol == 7) _whiteRookHFileMoved = true;
				}
				else
				{
					if (toRow == 0 && toCol == 0) _blackRookAFileMoved = true;
					if (toRow == 0 && toCol == 7) _blackRookHFileMoved = true;
				}
			}
		}

		// ─────────────────────────────────────────────
		// Check / attack detection
		// ─────────────────────────────────────────────

		public bool IsInCheck(ChessColor color)
		{
			// Locate king
			int kingRow = -1, kingCol = -1;

			for (int r = 0; r < Board.Rows; r++)
			{
				for (int c = 0; c < Board.Columns; c++)
				{
					var p = _pieces[r, c];
					if (p is { } piece &&
						piece.Type == ChessPieceType.King &&
						piece.Color == color)
					{
						kingRow = r;
						kingCol = c;
						break;
					}
				}
				if (kingRow != -1) break;
			}

			if (kingRow == -1)
			{
				// No king found (should not happen in a valid game)
				return false;
			}

			var enemy = color == ChessColor.White ? ChessColor.Black : ChessColor.White;
			return IsSquareAttackedBy(kingRow, kingCol, enemy);
		}

		private bool IsSquareAttackedBy(int targetRow, int targetCol, ChessColor attacker)
		{
			for (int r = 0; r < Board.Rows; r++)
			{
				for (int c = 0; c < Board.Columns; c++)
				{
					var p = _pieces[r, c];
					if (p is null || p.Value.Color != attacker)
						continue;

					int dr = targetRow - r;
					int dc = targetCol - c;
					int absDr = Math.Abs(dr);
					int absDc = Math.Abs(dc);

					switch (p.Value.Type)
					{
						case ChessPieceType.Pawn:
							{
								int dir = attacker == ChessColor.White ? -1 : 1;
								// From pawn's point of view: it attacks (r + dir, c ± 1)
								if (dr == dir && absDc == 1)
								{
									return true;
								}
								break;
							}

						case ChessPieceType.Knight:
							{
								if ((absDr == 2 && absDc == 1) || (absDr == 1 && absDc == 2))
									return true;
								break;
							}

						case ChessPieceType.Bishop:
							{
								if (absDr == absDc && absDr > 0 && IsPathClear(r, c, targetRow, targetCol))
									return true;
								break;
							}

						case ChessPieceType.Rook:
							{
								bool isStraight = (dr == 0 && dc != 0) || (dc == 0 && dr != 0);
								if (isStraight && IsPathClear(r, c, targetRow, targetCol))
									return true;
								break;
							}

						case ChessPieceType.Queen:
							{
								bool isDiagonal = absDr == absDc && absDr > 0;
								bool isStraight = (dr == 0 && dc != 0) || (dc == 0 && dr != 0);

								if ((isDiagonal || isStraight) && IsPathClear(r, c, targetRow, targetCol))
									return true;
								break;
							}

						case ChessPieceType.King:
							{
								if (absDr <= 1 && absDc <= 1 && (absDr + absDc > 0))
									return true;
								break;
							}
					}
				}
			}

			return false;
		}

		private bool IsPathClear(int fromRow, int fromCol, int toRow, int toCol)
		{
			int dr = toRow - fromRow;
			int dc = toCol - fromCol;

			int stepRow = dr == 0 ? 0 : dr / Math.Abs(dr);
			int stepCol = dc == 0 ? 0 : dc / Math.Abs(dc);

			int currentRow = fromRow + stepRow;
			int currentCol = fromCol + stepCol;

			while (currentRow != toRow || currentCol != toCol)
			{
				if (_pieces[currentRow, currentCol] is not null)
					return false;

				currentRow += stepRow;
				currentCol += stepCol;
			}

			return true;
		}

		// ─────────────────────────────────────────────
		// Castling
		// ─────────────────────────────────────────────

		private bool CanCastle(ChessColor color, bool kingSide)
		{
			int homeRow = color == ChessColor.White ? 7 : 0;
			int kingCol = 4;
			int rookCol = kingSide ? 7 : 0;
			int newKingCol = kingSide ? 6 : 2;
			int newRookCol = kingSide ? 5 : 3;

			// King must be on home square
			var king = GetPiece(homeRow, kingCol);
			if (king is null || king.Value.Type != ChessPieceType.King || king.Value.Color != color)
				return false;

			// King/rook must not have moved
			if (color == ChessColor.White)
			{
				if (_whiteKingMoved) return false;
				if (kingSide && _whiteRookHFileMoved) return false;
				if (!kingSide && _whiteRookAFileMoved) return false;
			}
			else
			{
				if (_blackKingMoved) return false;
				if (kingSide && _blackRookHFileMoved) return false;
				if (!kingSide && _blackRookAFileMoved) return false;
			}

			// Rook must be present and of same color
			var rook = GetPiece(homeRow, rookCol);
			if (rook is null || rook.Value.Type != ChessPieceType.Rook || rook.Value.Color != color)
				return false;

			// Squares between king and rook must be empty
			int dir = kingSide ? 1 : -1;
			for (int c = kingCol + dir; c != rookCol; c += dir)
			{
				if (GetPiece(homeRow, c) is not null)
					return false;
			}

			// King cannot be in check
			if (IsInCheck(color))
				return false;

			// Squares the king passes through (including destination) must not be attacked
			var enemy = color == ChessColor.White ? ChessColor.Black : ChessColor.White;
			for (int c = kingCol + dir; ; c += dir)
			{
				if (IsSquareAttackedBy(homeRow, c, enemy))
					return false;

				if (c == newKingCol)
					break;
			}

			return true;
		}

		private bool TryCastle(
			ChessColor color,
			int fromRow,
			int fromCol,
			int toRow,
			int toCol,
			out string? error)
		{
			error = null;

			bool kingSide = toCol > fromCol;
			int homeRow = color == ChessColor.White ? 7 : 0;

			if (fromRow != homeRow || fromCol != 4 || toRow != homeRow || Math.Abs(toCol - fromCol) != 2)
			{
				error = "Invalid castling coordinates.";
				return false;
			}

			if (!CanCastle(color, kingSide))
			{
				error = "Castling is not allowed in this position.";
				return false;
			}

			int rookCol = kingSide ? 7 : 0;
			int newKingCol = kingSide ? 6 : 2;
			int newRookCol = kingSide ? 5 : 3;

			var king = GetPiece(homeRow, 4)!.Value;
			var rook = GetPiece(homeRow, rookCol)!.Value;

			// Clear previous en-passant target
			_enPassantTarget = null;

			// Move king
			SetPiece(homeRow, 4, null);
			SetPiece(homeRow, newKingCol, king);

			// Move rook
			SetPiece(homeRow, rookCol, null);
			SetPiece(homeRow, newRookCol, rook);

			// Update rights
			if (color == ChessColor.White)
			{
				_whiteKingMoved = true;
				if (kingSide) _whiteRookHFileMoved = true;
				else _whiteRookAFileMoved = true;
			}
			else
			{
				_blackKingMoved = true;
				if (kingSide) _blackRookHFileMoved = true;
				else _blackRookAFileMoved = true;
			}

			// At this point, board reflects the castling move.
			// Determine if the opponent is in check / checkmated.
			var enemy = color == ChessColor.White ? ChessColor.Black : ChessColor.White;
			bool enemyInCheck = IsInCheck(enemy);
			bool enemyIsCheckmated = enemyInCheck && !HasAnyLegalMove(enemy);

			if (enemyIsCheckmated)
			{
				IsGameOver = true;
				Winner = color;
			}


			// Basic SAN for castling
			string san = kingSide ? "O-O" : "O-O-O";
			if (enemyIsCheckmated)
				san += "#";
			else if (enemyInCheck)
				san += "+";

			// Record move (no captures in castling)
			RecordMove(color, san, null);


			// Swap turn
			CurrentTurn = CurrentTurn == ChessColor.White ? ChessColor.Black : ChessColor.White;
			return true;
		}

		private static char PieceTypeToLetter(ChessPieceType type) => type switch
		{
			ChessPieceType.King => 'K',
			ChessPieceType.Queen => 'Q',
			ChessPieceType.Rook => 'R',
			ChessPieceType.Bishop => 'B',
			ChessPieceType.Knight => 'N',
			// Pawns have no letter in SAN
			_ => '\0'
		};
		private bool HasAnyLegalMove(ChessColor color)
		{
			// We reuse ChessMoveValidator, which checks CurrentTurn, so temporarily switch it.
			var savedTurn = CurrentTurn;
			CurrentTurn = color;

			try
			{
				for (int fromRow = 0; fromRow < Board.Rows; fromRow++)
				{
					for (int fromCol = 0; fromCol < Board.Columns; fromCol++)
					{
						var p = GetPiece(fromRow, fromCol);
						if (p is null || p.Value.Color != color)
							continue;

						for (int toRow = 0; toRow < Board.Rows; toRow++)
						{
							for (int toCol = 0; toCol < Board.Columns; toCol++)
							{
								if (!ChessMoveValidator.IsLegalBasicMove(
										this, fromRow, fromCol, toRow, toCol, out _))
									continue;

								bool isEp = IsEnPassantCapture(p.Value, fromRow, fromCol, toRow, toCol);

								if (!MoveLeavesKingInCheck(color, fromRow, fromCol, toRow, toCol, isEp))
									return true;
							}
						}
					}
				}

				return false;
			}
			finally
			{
				CurrentTurn = savedTurn;
			}
		}
		private string BuildSan(
		ChessPiece piece,
		int fromRow,
		int fromCol,
		int toRow,
		int toCol,
		ChessPiece? captured,
		bool isEnPassant,
		bool isPromotion,
		ChessPieceType promotionType,
		bool givesCheck,
		bool isCheckmate)
		{
			// Destination square in algebraic: file + rank from White's perspective
			char destFile = (char)('a' + toCol);
			char destRank = (char)('1' + (7 - toRow));
			string destSquare = $"{destFile}{destRank}";

			string checkSuffix = isCheckmate ? "#" : (givesCheck ? "+" : "");

			// Pawn moves
			if (piece.Type == ChessPieceType.Pawn)
			{
				char fromFile = (char)('a' + fromCol);
				bool isCapture = captured is not null || isEnPassant;

				string san = isCapture
					? $"{fromFile}x{destSquare}"   // exd5
					: destSquare;                  // e4

				if (isPromotion)
				{
					char promoLetter = PieceTypeToLetter(promotionType);
					san += $"={promoLetter}";
				}

				return san + checkSuffix;
			}

			// Pieces K, Q, R, B, N
			char pieceLetter = PieceTypeToLetter(piece.Type);
			bool pieceCapture = captured is not null || isEnPassant;

			// NOTE: we skip full disambiguation for now (you could add it later).
			string capturePart = pieceCapture ? "x" : "";

			return $"{pieceLetter}{capturePart}{destSquare}{checkSuffix}";
		}
		private void RecordMove(
	ChessColor color,
	string san,
	ChessPiece? capturedPieceForHistory)
		{
			if (capturedPieceForHistory is ChessPiece cap)
			{
				if (color == ChessColor.White)
					_capturedByWhite.Add(cap);
				else
					_capturedByBlack.Add(cap);
			}

			_moveHistory.Add(new ChessMoveRecord
			{
				MoveNumber = _fullMoveNumber,
				Color = color,
				San = san
			});

			// Full move number increments after Black's move
			if (color == ChessColor.Black)
			{
				_fullMoveNumber++;
			}
		}



	}
}

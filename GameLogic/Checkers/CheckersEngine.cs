// GameLogic/Checkers/CheckersEngine.cs
using System;
using GameContracts;
using GameLogic;

namespace GameLogic.Checkers
{
	/// <summary>
	/// Core Checkers rules:
	/// - Random color assignment once both players join.
	/// - Standard 8x8 setup.
	/// - Men move diagonally forward; kings move both ways.
	/// - Captures are mandatory (if any capture exists, you must capture).
	/// - Multi-jump (forced continuation) support.
	/// - Game over when one side has no pieces or no legal moves.
	/// </summary>
	public static class CheckersEngine
	{
		// Directions for kings (can move both ways).
		private static readonly (int dRow, int dCol)[] KingDirections =
		{
			(-1, -1), (-1, 1),
			( 1, -1), ( 1, 1)
		};

		/// <summary>
		/// Create a new room state for a given Room.
		/// Board will be initialized once both players are present.
		/// </summary>
		public static CheckersRoomState CreateInitialState(
			string roomCode,
			Room room,
			Random rng)
		{
			if (room == null) throw new ArgumentNullException(nameof(room));
			if (rng == null) throw new ArgumentNullException(nameof(rng));

			var state = new CheckersRoomState(roomCode)
			{
				IsGameOver = false,
				WinnerPlayerId = null,
				IsStarted = false
			};

			SyncPlayersFromRoom(state, room, rng);
			return state;
		}

		/// <summary>
		/// Keep the state’s Red/Black player IDs in sync with Room.Players
		/// ("P1", "P2"), and if both players are present, randomly assign
		/// which is Red / Black and randomly choose the starting player.
		/// </summary>
		public static void SyncPlayersFromRoom(
			CheckersRoomState state,
			Room room,
			Random rng)
		{
			if (state == null) throw new ArgumentNullException(nameof(state));
			if (room == null) throw new ArgumentNullException(nameof(room));
			if (rng == null) throw new ArgumentNullException(nameof(rng));

			var hasP1 = room.Players.Contains("P1");
			var hasP2 = room.Players.Contains("P2");

			// If game already started, we don't reshuffle colors;
			// we only ensure that we don't reference players that left.
			if (state.IsStarted)
			{
				if (state.RedPlayerId == "P1" && !hasP1) state.RedPlayerId = null;
				if (state.RedPlayerId == "P2" && !hasP2) state.RedPlayerId = null;
				if (state.BlackPlayerId == "P1" && !hasP1) state.BlackPlayerId = null;
				if (state.BlackPlayerId == "P2" && !hasP2) state.BlackPlayerId = null;
				return;
			}

			// Not started yet — waiting for 2 players.
			if (!(hasP1 && hasP2))
			{
				// Only one player in room; we’ll assign colors later.
				state.RedPlayerId = null;
				state.BlackPlayerId = null;
				state.IsStarted = false;
				state.CurrentTurnPlayerId = null;
				state.StatusMessage = "Waiting for opponent...";
				return;
			}

			// Both players are present but game not started.
			// Randomly decide which is Red and which is Black.
			if (state.RedPlayerId == null || state.BlackPlayerId == null)
			{
				bool p1IsRed = rng.Next(2) == 0;
				state.RedPlayerId = p1IsRed ? "P1" : "P2";
				state.BlackPlayerId = p1IsRed ? "P2" : "P1";
			}

			// Initialize starting board and random starting player.
			InitializeStartingBoard(state);

			state.IsStarted = true;
			state.ForcedFromRow = null;
			state.ForcedFromCol = null;
			state.LastFromRow = state.LastFromCol = null;
			state.LastToRow = state.LastToCol = null;
			state.MoveNumber = 0;

			// Randomly choose who starts.
			var startIsRed = rng.Next(2) == 0;
			state.CurrentTurnPlayerId = startIsRed ? state.RedPlayerId : state.BlackPlayerId;
			state.StatusMessage = $"{state.CurrentTurnPlayerId}'s turn.";
		}

		/// <summary>
		/// Standard Checkers starting position:
		/// - Black at top rows (0–2), moving downwards.
		/// - Red at bottom rows (5–7), moving upwards.
		/// Pieces only on dark squares ((row + col) % 2 == 1).
		/// </summary>
		private static void InitializeStartingBoard(CheckersRoomState state)
		{
			var size = CheckersRoomState.BoardSize;

			// Clear board first.
			for (int r = 0; r < size; r++)
				for (int c = 0; c < size; c++)
					state.Board[r, c] = CheckersPiece.Empty;

			// Black pieces (top)
			for (int r = 0; r <= 2; r++)
			{
				for (int c = 0; c < size; c++)
				{
					if (((r + c) & 1) == 1) // dark squares
					{
						state.Board[r, c] = CheckersPiece.BlackMan;
					}
				}
			}

			// Red pieces (bottom)
			for (int r = size - 3; r < size; r++)
			{
				for (int c = 0; c < size; c++)
				{
					if (((r + c) & 1) == 1) // dark squares
					{
						state.Board[r, c] = CheckersPiece.RedMan;
					}
				}
			}
		}

		/// <summary>
		/// Convert the server-side state into a payload for clients.
		/// </summary>
		public static CheckersStatePayload ToPayload(CheckersRoomState state)
		{
			if (state == null) throw new ArgumentNullException(nameof(state));

			var size = CheckersRoomState.BoardSize;
			var cells = new CheckersCell[size * size];

			for (int row = 0; row < size; row++)
			{
				for (int col = 0; col < size; col++)
				{
					var piece = state.Board[row, col];
					cells[row * size + col] = MapPieceToCell(piece);
				}
			}

			return new CheckersStatePayload
			{
				Cells = cells,
				RedPlayerId = state.RedPlayerId ?? string.Empty,
				BlackPlayerId = state.BlackPlayerId ?? string.Empty,
				IsStarted = state.IsStarted,
				CurrentPlayerId = state.CurrentTurnPlayerId ?? string.Empty,
				IsGameOver = state.IsGameOver,
				WinnerPlayerId = state.WinnerPlayerId,
				Message = BuildStatusMessage(state),
				ForcedFromRow = state.ForcedFromRow,
				ForcedFromCol = state.ForcedFromCol,
				LastFromRow = state.LastFromRow,
				LastFromCol = state.LastFromCol,
				LastToRow = state.LastToRow,
				LastToCol = state.LastToCol
			};
		}

		/// <summary>
		/// Apply a move for the given player, enforcing Checkers rules.
		/// Returns true if the move was accepted and state mutated.
		/// </summary>
		public static bool TryApplyMove(
			CheckersRoomState state,
			string playerId,
			CheckersMovePayload move,
			out string? error)
		{
			error = null;
			if (state == null) throw new ArgumentNullException(nameof(state));
			if (move == null) throw new ArgumentNullException(nameof(move));

			if (state.IsGameOver)
			{
				error = "Game is already over.";
				return false;
			}

			if (!state.IsStarted)
			{
				error = "Game has not started yet.";
				return false;
			}

			if (string.IsNullOrWhiteSpace(playerId) ||
				playerId != state.CurrentTurnPlayerId)
			{
				error = "Not your turn.";
				return false;
			}

			bool isRedSide =
				!string.IsNullOrWhiteSpace(state.RedPlayerId) &&
				playerId == state.RedPlayerId;

			bool isBlackSide =
				!string.IsNullOrWhiteSpace(state.BlackPlayerId) &&
				playerId == state.BlackPlayerId;

			if (!isRedSide && !isBlackSide)
			{
				error = "Unknown player.";
				return false;
			}

			int fromRow = move.FromRow;
			int fromCol = move.FromCol;
			int toRow = move.ToRow;
			int toCol = move.ToCol;

			if (!IsInsideBoard(fromRow, fromCol) || !IsInsideBoard(toRow, toCol))
			{
				error = "Move is out of bounds.";
				return false;
			}

			if (fromRow == toRow && fromCol == toCol)
			{
				error = "Source and destination are the same.";
				return false;
			}

			var piece = state.Board[fromRow, fromCol];
			if (piece == CheckersPiece.Empty)
			{
				error = "No piece at the source square.";
				return false;
			}

			// Ensure the piece belongs to this player.
			if (isRedSide && !IsRedPiece(piece))
			{
				error = "You can only move red pieces.";
				return false;
			}
			if (isBlackSide && !IsBlackPiece(piece))
			{
				error = "You can only move black pieces.";
				return false;
			}

			// Forced continuation of a multi-jump?
			if (state.ForcedFromRow.HasValue && state.ForcedFromCol.HasValue)
			{
				if (state.ForcedFromRow.Value != fromRow ||
					state.ForcedFromCol.Value != fromCol)
				{
					error = "You must continue capturing with the same piece.";
					return false;
				}
			}

			// Destination must be empty.
			if (state.Board[toRow, toCol] != CheckersPiece.Empty)
			{
				error = "Destination square is not empty.";
				return false;
			}

			int dRow = toRow - fromRow;
			int dCol = toCol - fromCol;
			int absRow = Math.Abs(dRow);
			int absCol = Math.Abs(dCol);

			if (absRow != absCol)
			{
				error = "Move must be diagonal.";
				return false;
			}

			bool isCapture = absRow == 2;
			bool isSimpleMove = absRow == 1;

			if (!isCapture && !isSimpleMove)
			{
				error = "Move distance must be 1 (simple) or 2 (capture).";
				return false;
			}

			// Determine forward directions for men.
			int forwardDir = isRedSide ? -1 : +1;

			// Validate movement direction for men.
			if (piece == CheckersPiece.RedMan || piece == CheckersPiece.BlackMan)
			{
				if (isSimpleMove && dRow != forwardDir)
				{
					error = "Men can only move forward.";
					return false;
				}
				if (isCapture && dRow != 2 * forwardDir)
				{
					error = "Men can only capture forward.";
					return false;
				}
			}

			// Mandatory capture rule: if any capture exists for this side,
			// you are not allowed to make a non-capture move.
			bool anyCaptureAvailable = HasAnyCapture(state, isRedSide);
			if (anyCaptureAvailable && !isCapture)
			{
				error = "You must capture when a capture is available.";
				return false;
			}

			// For captures, ensure we are jumping an opponent piece.
			if (isCapture)
			{
				int midRow = (fromRow + toRow) / 2;
				int midCol = (fromCol + toCol) / 2;
				var midPiece = state.Board[midRow, midCol];

				if (midPiece == CheckersPiece.Empty)
				{
					error = "No piece to capture.";
					return false;
				}

				if (isRedSide && !IsBlackPiece(midPiece) ||
					isBlackSide && !IsRedPiece(midPiece))
				{
					error = "You can only capture opponent pieces.";
					return false;
				}

				// Apply capture: remove the jumped piece.
				state.Board[midRow, midCol] = CheckersPiece.Empty;
			}

			// Move the piece.
			state.Board[fromRow, fromCol] = CheckersPiece.Empty;
			var movedPiece = piece;

			// King promotion.
			if (movedPiece == CheckersPiece.RedMan && toRow == 0)
			{
				movedPiece = CheckersPiece.RedKing;
			}
			else if (movedPiece == CheckersPiece.BlackMan &&
					 toRow == CheckersRoomState.BoardSize - 1)
			{
				movedPiece = CheckersPiece.BlackKing;
			}

			state.Board[toRow, toCol] = movedPiece;

			// Update bookkeeping
			state.LastFromRow = fromRow;
			state.LastFromCol = fromCol;
			state.LastToRow = toRow;
			state.LastToCol = toCol;
			state.MoveNumber++;

			// Check for additional captures if this was a capture.
			state.ForcedFromRow = null;
			state.ForcedFromCol = null;

			if (isCapture && HasCaptureFrom(state, toRow, toCol, movedPiece, isRedSide))
			{
				// Multi-jump continuation required.
				state.ForcedFromRow = toRow;
				state.ForcedFromCol = toCol;
				state.StatusMessage = "You must continue capturing.";
				// CurrentTurnPlayerId stays the same.
			}
			else
			{
				// Turn passes to opponent.
				SwitchTurn(state);
			}

			// Check for win/loss.
			UpdateGameOver(state);

			return true;
		}

		// ─────────────────────────────────────────────────────────
		// Helpers
		// ─────────────────────────────────────────────────────────

		private static bool IsInsideBoard(int row, int col)
		{
			return row >= 0 && row < CheckersRoomState.BoardSize &&
				   col >= 0 && col < CheckersRoomState.BoardSize;
		}

		private static bool IsRedPiece(CheckersPiece p) =>
			p == CheckersPiece.RedMan || p == CheckersPiece.RedKing;

		private static bool IsBlackPiece(CheckersPiece p) =>
			p == CheckersPiece.BlackMan || p == CheckersPiece.BlackKing;

		private static CheckersCell MapPieceToCell(CheckersPiece piece) =>
			piece switch
			{
				CheckersPiece.Empty => CheckersCell.Empty,
				CheckersPiece.RedMan => CheckersCell.RedMan,
				CheckersPiece.RedKing => CheckersCell.RedKing,
				CheckersPiece.BlackMan => CheckersCell.BlackMan,
				CheckersPiece.BlackKing => CheckersCell.BlackKing,
				_ => CheckersCell.Empty
			};

		private static string BuildStatusMessage(CheckersRoomState state)
		{
			if (state.IsGameOver)
			{
				if (!string.IsNullOrWhiteSpace(state.WinnerPlayerId))
				{
					return $"{state.WinnerPlayerId} wins!";
				}
				return "Game over.";
			}

			if (!state.IsStarted)
				return "Waiting for opponent...";

			if (string.IsNullOrWhiteSpace(state.CurrentTurnPlayerId))
				return "Game not started yet.";

			return $"{state.CurrentTurnPlayerId}'s turn.";
		}

		private static void SwitchTurn(CheckersRoomState state)
		{
			if (state.RedPlayerId == null || state.BlackPlayerId == null)
				return;

			if (state.CurrentTurnPlayerId == state.RedPlayerId)
			{
				state.CurrentTurnPlayerId = state.BlackPlayerId;
			}
			else
			{
				state.CurrentTurnPlayerId = state.RedPlayerId;
			}

			state.StatusMessage = $"{state.CurrentTurnPlayerId}'s turn.";
		}

		private static void UpdateGameOver(CheckersRoomState state)
		{
			if (state.RedPlayerId == null || state.BlackPlayerId == null)
				return;

			bool redHasPieces = false;
			bool blackHasPieces = false;

			var size = CheckersRoomState.BoardSize;

			for (int r = 0; r < size; r++)
			{
				for (int c = 0; c < size; c++)
				{
					var p = state.Board[r, c];
					if (IsRedPiece(p)) redHasPieces = true;
					if (IsBlackPiece(p)) blackHasPieces = true;
				}
			}

			// If one side has no pieces, they lose.
			if (!redHasPieces || !blackHasPieces)
			{
				state.IsGameOver = true;
				state.CurrentTurnPlayerId = null;

				if (redHasPieces && !blackHasPieces)
					state.WinnerPlayerId = state.RedPlayerId;
				else if (!redHasPieces && blackHasPieces)
					state.WinnerPlayerId = state.BlackPlayerId;
				else
					state.WinnerPlayerId = null;

				state.StatusMessage = BuildStatusMessage(state);
				return;
			}

			// Check for "no legal moves" (stalemate / loss).
			bool redHasMove = HasAnyMove(state, true);
			bool blackHasMove = HasAnyMove(state, false);

			if (!redHasMove || !blackHasMove)
			{
				state.IsGameOver = true;
				state.CurrentTurnPlayerId = null;

				if (redHasMove && !blackHasMove)
					state.WinnerPlayerId = state.RedPlayerId;
				else if (!redHasMove && blackHasMove)
					state.WinnerPlayerId = state.BlackPlayerId;
				else
					state.WinnerPlayerId = null;

				state.StatusMessage = BuildStatusMessage(state);
			}
		}

		private static bool HasAnyMove(CheckersRoomState state, bool forRedSide)
		{
			var size = CheckersRoomState.BoardSize;

			for (int r = 0; r < size; r++)
			{
				for (int c = 0; c < size; c++)
				{
					var p = state.Board[r, c];
					if (p == CheckersPiece.Empty)
						continue;

					if (forRedSide && !IsRedPiece(p)) continue;
					if (!forRedSide && !IsBlackPiece(p)) continue;

					if (HasSimpleMoveFrom(state, r, c, p, forRedSide) ||
						HasCaptureFrom(state, r, c, p, forRedSide))
					{
						return true;
					}
				}
			}

			return false;
		}

		private static bool HasAnyCapture(CheckersRoomState state, bool forRedSide)
		{
			var size = CheckersRoomState.BoardSize;

			for (int r = 0; r < size; r++)
			{
				for (int c = 0; c < size; c++)
				{
					var p = state.Board[r, c];
					if (p == CheckersPiece.Empty)
						continue;

					if (forRedSide && !IsRedPiece(p)) continue;
					if (!forRedSide && !IsBlackPiece(p)) continue;

					if (HasCaptureFrom(state, r, c, p, forRedSide))
						return true;
				}
			}

			return false;
		}

		private static bool HasSimpleMoveFrom(
			CheckersRoomState state,
			int row,
			int col,
			CheckersPiece piece,
			bool forRedSide)
		{
			int forwardDir = forRedSide ? -1 : +1;

			if (piece == CheckersPiece.RedMan || piece == CheckersPiece.BlackMan)
			{
				// Men: only forward diagonals by 1.
				return CheckSimpleDir(state, row, col, forwardDir, -1) ||
					   CheckSimpleDir(state, row, col, forwardDir, +1);
			}
			else
			{
				// Kings: any diagonal by 1.
				foreach (var (dRow, dCol) in KingDirections)
				{
					int nr = row + dRow;
					int nc = col + dCol;
					if (IsInsideBoard(nr, nc) &&
						state.Board[nr, nc] == CheckersPiece.Empty)
					{
						return true;
					}
				}
			}

			return false;
		}

		private static bool CheckSimpleDir(
			CheckersRoomState state,
			int row,
			int col,
			int dRow,
			int dCol)
		{
			int nr = row + dRow;
			int nc = col + dCol;
			return IsInsideBoard(nr, nc) &&
				   state.Board[nr, nc] == CheckersPiece.Empty;
		}

		private static bool HasCaptureFrom(
			CheckersRoomState state,
			int row,
			int col,
			CheckersPiece piece,
			bool forRedSide)
		{
			int size = CheckersRoomState.BoardSize;
			int forwardDir = forRedSide ? -1 : +1;

			if (piece == CheckersPiece.RedMan || piece == CheckersPiece.BlackMan)
			{
				// Men: only forward capture directions.
				return CheckCaptureDir(state, row, col, forwardDir, -1, forRedSide) ||
					   CheckCaptureDir(state, row, col, forwardDir, +1, forRedSide);
			}
			else
			{
				// Kings: all four diagonals.
				foreach (var (dRow, dCol) in KingDirections)
				{
					if (CheckCaptureDir(state, row, col, dRow, dCol, forRedSide))
						return true;
				}
			}

			return false;
		}

		private static bool CheckCaptureDir(
			CheckersRoomState state,
			int row,
			int col,
			int dRow,
			int dCol,
			bool forRedSide)
		{
			int midRow = row + dRow;
			int midCol = col + dCol;
			int toRow = row + 2 * dRow;
			int toCol = col + 2 * dCol;

			if (!IsInsideBoard(midRow, midCol) || !IsInsideBoard(toRow, toCol))
				return false;

			var midPiece = state.Board[midRow, midCol];
			if (midPiece == CheckersPiece.Empty)
				return false;

			// Must be opponent piece.
			if (forRedSide && !IsBlackPiece(midPiece)) return false;
			if (!forRedSide && !IsRedPiece(midPiece)) return false;

			// Landing square must be empty.
			if (state.Board[toRow, toCol] != CheckersPiece.Empty)
				return false;

			return true;
		}
	}
}

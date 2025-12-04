using GameContracts;
using System;
using System.Linq;
namespace GameLogic.TicTacToe
{
	public static class TicTacToeLogic
	{
		// Up to you what helpers you want:
		// - ValidateMove
		// - ApplyMove
		// - CheckWinner
		// - BuildPayload, etc.

		public static bool IsValidMove(TicTacToeRoomState state, int row, int col, string playerId)
		{
			if (state.IsGameOver)
				return false;
			if (state.CurrentPlayerId != playerId)
				return false;
			if (row < 0 || row > 2 || col < 0 || col > 2)
				return false;
			int index = GetCellIndex(row, col);
			if (state.Cells[index] != ' ')
				return false;

			return true;
		}
		public static int GetCellIndex(int row, int col)
		{
			return row * 3 + col;
		}
		public static char GetMarkForPlayer(string playerId, TicTacToeRoomState state)
		{
			if (state.PlayerXId == playerId) return 'X';
			if (state.PlayerOId == playerId) return 'O';

			throw new InvalidOperationException("Unknown player for this TicTacToe room.");
		}
		public static bool IsBoardFull(TicTacToeRoomState state)
		{
			return state.Cells.All(c => c != ' ');
		}
		public static bool HasWinner(TicTacToeRoomState state, char playerMark)
		{
			// All 8 possible winning lines (by cell index)
			int[][] winningLines =
			{
		new[] { 0, 1, 2 }, // rows
        new[] { 3, 4, 5 },
		new[] { 6, 7, 8 },

		new[] { 0, 3, 6 }, // columns
        new[] { 1, 4, 7 },
		new[] { 2, 5, 8 },

		new[] { 0, 4, 8 }, // diagonals
        new[] { 2, 4, 6 }
	};

			foreach (var line in winningLines)
			{
				if (state.Cells[line[0]] == playerMark &&
					state.Cells[line[1]] == playerMark &&
					state.Cells[line[2]] == playerMark)
				{
					return true;
				}
			}

			return false;
		}


		public static void ApplyMove(TicTacToeRoomState state, int row, int col, string playerId)
		{
			var mark = GetMarkForPlayer(playerId, state);
			int index = GetCellIndex(row, col);
			state.Cells[index] = mark;

			if (HasWinner(state, mark))
			{
				state.IsGameOver = true;
				state.WinnerPlayerId = playerId;
				state.IsDraw = false;
			}
			else if (IsBoardFull(state))
			{
				state.IsGameOver = true;
				state.IsDraw = true;
				state.WinnerPlayerId = null;
			}
			else
			{
				// Switch current player
				if (state.CurrentPlayerId == state.PlayerXId)
					state.CurrentPlayerId = state.PlayerOId!;
				else
					state.CurrentPlayerId = state.PlayerXId!;
			}


		}

		public static TicTacToeStatePayload ToPayload(TicTacToeRoomState state, string message)
		{
			return new TicTacToeStatePayload
			{
				// copy of the board so nobody can mutate your internal array accidentally
				Cells = (char[])state.Cells.Clone(),

				CurrentPlayerId = state.CurrentPlayerId ?? string.Empty,
				IsGameOver = state.IsGameOver,
				WinnerPlayerId = state.WinnerPlayerId,
				IsDraw = state.IsDraw,

				Message = message
			};
		}
		public static void InitializeStartingPlayer(TicTacToeRoomState state)
		{
			if (state.PlayerXId is null || state.PlayerOId is null)
				return; // or throw, depending how strict you want to be
			bool xStarts = Random.Shared.Next(0, 2) == 0;


			state.CurrentPlayerId = xStarts ? state.PlayerXId : state.PlayerOId;
		}
	}
}

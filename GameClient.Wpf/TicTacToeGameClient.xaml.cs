using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameContracts;

namespace GameClient.Wpf
{
	public partial class TicTacToeGameClient : UserControl, IGameClient
	{
		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		private string? _roomCode;
		private string? _playerId;

		// Keep last state so we can decide if it's our turn, etc.
		private TicTacToeStatePayload? _lastState;

		private Button[,] _buttons = null!;

		public TicTacToeGameClient()
		{
			InitializeComponent();

			// Build a simple 2D array for easier access
			_buttons = new[,]
			{
				{ Cell00, Cell01, Cell02 },
				{ Cell10, Cell11, Cell12 },
				{ Cell20, Cell21, Cell22 }
			};

			ClearBoardUI();
		}

		public GameType GameType => GameType.TicTacToe;
		public FrameworkElement View => this;

		public void SetConnection(
			Func<HubMessage, Task> sendAsync,
			Func<bool> isSocketOpen)
		{
			_sendAsync = sendAsync;
			_isSocketOpen = isSocketOpen;
		}

		public void OnRoomChanged(string? roomCode, string? playerId)
		{
			_roomCode = roomCode;
			_playerId = playerId;

			ClearBoardUI();

			if (_roomCode == null || _playerId == null)
			{
				StatusText.Text = "Not in a room.";
			}
			else
			{
				StatusText.Text = "Joined Tic-Tac-Toe room.";
			}
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			if (msg.MessageType != "TicTacToeState")
				return false;

			var payload = JsonSerializer.Deserialize<TicTacToeStatePayload>(msg.PayloadJson);
			if (payload == null) return true;

			Dispatcher.Invoke(() =>
			{
				_lastState = payload;
				UpdateUIFromState(payload);
			});

			return true;
		}

		public void OnKeyDown(KeyEventArgs e)
		{
			// Probably not needed for TicTacToe
		}

		public void OnKeyUp(KeyEventArgs e)
		{
			// Probably not needed for TicTacToe
		}

		// ── UI helpers ──────────────────────────────────────────────────────

		private void ClearBoardUI()
		{
			_lastState = null;

			for (int r = 0; r < 3; r++)
			{
				for (int c = 0; c < 3; c++)
				{
					var btn = _buttons[r, c];
					btn.Content = string.Empty;
					btn.IsEnabled = false;
				}
			}

			StatusText.Text = "Waiting for game...";
			TurnText.Text = string.Empty;
		}

		private void UpdateUIFromState(TicTacToeStatePayload state)
		{
			// Update cells
			for (int r = 0; r < 3; r++)
			{
				for (int c = 0; c < 3; c++)
				{
					int index = r * 3 + c;
					char mark = index < state.Cells.Length ? state.Cells[index] : ' ';

					var btn = _buttons[r, c];
					btn.Content = mark == ' ' ? string.Empty : mark.ToString();

					// Button enabled only if:
					// - game not over
					// - cell empty
					// - it's this client's turn
					bool isMyTurn = (_playerId != null && state.CurrentPlayerId == _playerId);
					btn.IsEnabled = !state.IsGameOver && mark == ' ';
				}
			}

			// Status message from server
			StatusText.Text = state.Message ?? string.Empty;

			// Extra local "your turn" status
			if (state.IsGameOver)
			{
				if (state.IsDraw)
				{
					TurnText.Text = "Draw.";
				}
				else if (_playerId != null && state.WinnerPlayerId == _playerId)
				{
					TurnText.Text = "You win!";
				}
				else
				{
					TurnText.Text = "You lose.";
				}
			}
			else
			{
				if (_playerId != null && state.CurrentPlayerId == _playerId)
					TurnText.Text = "Your turn.";
				else
					TurnText.Text = "Opponent's turn.";
			}
		}

		// ── Send move when you click a cell ─────────────────────────────────

		private async Task SendMoveAsync(int row, int col)
		{
			if (_sendAsync == null || _isSocketOpen == null)
				return;
			if (!_isSocketOpen() || _roomCode == null || _playerId == null)
				return;

			// Extra safety: only send if it's our turn & game not over
			if (_lastState != null && _lastState.IsGameOver)
				return;

			var payload = new TicTacToeMovePayload
			{
				Row = row,
				Col = col
			};

			var msg = new HubMessage
			{
				MessageType = "TicTacToeMove",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}

		// Hooked to Buttons in XAML
		private async void Cell_Click(object sender, RoutedEventArgs e)
		{
			if (sender is not Button button || button.Tag is not string tag)
				return;

			// Tag is "row,col"
			var parts = tag.Split(',');
			if (parts.Length != 2)
				return;

			if (!int.TryParse(parts[0], out int row)) return;
			if (!int.TryParse(parts[1], out int col)) return;

			await SendMoveAsync(row, col);
		}
	}
}

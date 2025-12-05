using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameContracts;
using System.Collections.Generic;

namespace GameClient.Wpf.GameClients
{
	public partial class WordGuessGameClient : UserControl, IGameClient
	{
		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		private string? _roomCode;
		private string? _playerId;
		private readonly Dictionary<char, LetterResult> _keyboardState = new();
		private readonly Dictionary<char, Button> _keyButtons = new();
		private readonly char[] _currentGuess = new char[5];
		private int _currentLength = 0; // how many letters currently typed (0–5)
		private static readonly Brush UnusedKeyBrush = Brushes.LightGray;

		public WordGuessGameClient()
		{
			InitializeComponent();
			InitUiDisabled();
			ClearCurrentGuess();
			ClearKeyboardState();
		}

		public GameType GameType => GameType.WordGuess;
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

			WordGuessHistoryPanel.Items.Clear();
			WordGuessStatusText.Text = string.Empty;
			GuessBox.Text = string.Empty;
			SecretWordBox.Text = string.Empty;
			ClearCurrentGuess();

			ClearKeyboardState();

			// Only enable controls if we're actually in a WordGuess room
			bool inRoom = !string.IsNullOrEmpty(_roomCode);

			SecretWordBox.IsEnabled = inRoom && _playerId == "P1";
			SetSecretButton.IsEnabled = inRoom && _playerId == "P1";

			GuessBox.IsEnabled = inRoom && _playerId == "P2";
			GuessButton.IsEnabled = inRoom && _playerId == "P2";
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			switch (msg.MessageType)
			{
				case "WordGuessResult":
					HandleResult(msg);
					return true;

				case "WordGuessReset":
					HandleReset(msg);
					return true;
				case "WordGuessSecretSet":
					HandleSecretSet(msg);
					return true;

				default:
					return false;
			}
		}
		private void HandleResult(HubMessage msg)
		{
			var payload = JsonSerializer.Deserialize<WordGuessResultPayload>(msg.PayloadJson);
			if (payload == null) return;

			Dispatcher.Invoke(() =>
			{
				AddWordGuessRow(payload);
				UpdateKeyboardFromResult(payload);

				WordGuessStatusText.Text =
					$"{payload.Message} (Attempt {payload.AttemptNumber}/{payload.MaxAttempts})";


				if (payload.IsGameOver)
				{
					GuessBox.IsEnabled = false;
					GuessButton.IsEnabled = false;
				}
			});
		}

		private void HandleReset(HubMessage msg)
		{
			Dispatcher.Invoke(() =>
			{
				// Clear previous guesses from UI
				WordGuessHistoryPanel.Items.Clear();

				// Clear text boxes
				GuessBox.Text = string.Empty;
				SecretWordBox.Text = string.Empty;
				ClearCurrentGuess();

				// Status text
				WordGuessStatusText.Text = "Game restarted. Waiting for a new secret word.";

				bool inRoom = !string.IsNullOrEmpty(_roomCode);

				// P1 can set a new secret
				SecretWordBox.IsEnabled = inRoom && _playerId == "P1";
				SetSecretButton.IsEnabled = inRoom && _playerId == "P1";

				// P2 must wait for the secret to be set
				GuessBox.IsEnabled = false;
				GuessButton.IsEnabled = false;

				ClearKeyboardState();
			});
		}
		private void HandleSecretSet(HubMessage msg)
		{
			Dispatcher.Invoke(() =>
			{
				if (string.IsNullOrEmpty(_roomCode))
					return;

				if (_playerId == "P1")
				{
					// P1 already sets this locally, but in case both sides should sync:
					WordGuessStatusText.Text = "Secret set. Waiting for guesses...";
					// P1's controls stay as they are (still allowed to change secret or not, up to you)
					ClearCurrentGuess();
					ClearKeyboardState();
				}
				else if (_playerId == "P2")
				{
					ClearCurrentGuess();
					ClearKeyboardState();
					// THIS is the important part:
					WordGuessStatusText.Text = "Secret set. Start guessing!";

					GuessBox.IsEnabled = true;
					GuessButton.IsEnabled = true;
				}
			});
		}
		public void OnKeyDown(KeyEventArgs e)
		{
			// Only react if P2 and guessing is allowed
			if (_playerId != "P2" || !GuessButton.IsEnabled)
				return;

			if (e.Key >= Key.A && e.Key <= Key.Z)
			{
				char c = (char)('A' + (e.Key - Key.A));
				AppendLetter(c);
				e.Handled = true;
			}
			else if (e.Key == Key.Back)
			{
				Backspace();
				e.Handled = true;
			}
			else if (e.Key == Key.Enter)
			{
				// Trigger the same click as pressing the Guess button
				GuessButton_Click(this, new RoutedEventArgs());
				e.Handled = true;
			}
		}

		public void OnKeyUp(KeyEventArgs e)
		{
			// WordGuess doesn't use keyboard directly.
		}

		// ── UI init ───────────────────────────────────────────────────────────

		private void InitUiDisabled()
		{
			SecretWordBox.IsEnabled = false;
			SetSecretButton.IsEnabled = false;
			GuessBox.IsEnabled = false;
			GuessButton.IsEnabled = false;
		}

		// ── Button handlers ──────────────────────────────────────────────────

		private async void SetSecretButton_Click(object sender, RoutedEventArgs e)
		{
			if (_sendAsync == null || _isSocketOpen == null)
				return;
			if (!_isSocketOpen() || _roomCode == null || _playerId != "P1")
			{
				WordGuessStatusText.Text = "You must be P1 in a WordGuess room to set the secret.";
				return;
			}

			var word = (SecretWordBox.Text ?? string.Empty).Trim();
			if (word.Length != 5)
			{
				WordGuessStatusText.Text = "Secret must be 5 letters.";
				return;
			}

			var payload = new WordGuessSetSecretPayload
			{
				SecretWord = word
			};

			var msg = new HubMessage
			{
				MessageType = "WordGuessSetSecret",
				RoomCode = _roomCode,
				PlayerId = _playerId!,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
			WordGuessStatusText.Text = "Secret set. Waiting for guesses...";
		}

		private async void GuessButton_Click(object sender, RoutedEventArgs e)
		{
			if (_sendAsync == null || _isSocketOpen == null)
				return;
			if (!_isSocketOpen() || _roomCode == null || _playerId != "P2")
			{
				WordGuessStatusText.Text = "You must be P2 in a WordGuess room to guess.";
				return;
			}

			// 🔹 Use the current tiles, not the hidden textbox
			var guess = GetCurrentGuessString();
			if (guess.Length != 5)
			{
				WordGuessStatusText.Text = "Guess must be 5 letters.";
				return;
			}

			var payload = new WordGuessGuessPayload
			{
				Guess = guess
			};

			var msg = new HubMessage
			{
				MessageType = "WordGuessGuess",
				RoomCode = _roomCode,
				PlayerId = _playerId!,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);

			// Prepare for next attempt (server will add the row to history)
			ClearCurrentGuess();
		}

		// ── History rendering ────────────────────────────────────────────────

		private void AddWordGuessRow(WordGuessResultPayload payload)
		{
			var panel = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Margin = new Thickness(0, 2, 0, 2)
			};

			for (int i = 0; i < payload.Guess.Length && i < payload.LetterResults.Length; i++)
			{
				var letter = payload.Guess[i].ToString().ToUpperInvariant();
				Brush color = Brushes.DimGray;

				switch (payload.LetterResults[i])
				{
					case LetterResult.Green:
						color = Brushes.Green;
						break;
					case LetterResult.Yellow:
						color = Brushes.Goldenrod;
						break;
					case LetterResult.Grey:
						color = Brushes.DimGray;
						break;
				}

				var border = new Border
				{
					Width = 32,
					Height = 32,
					Margin = new Thickness(2),
					Background = color,
					CornerRadius = new CornerRadius(4),
					Child = new TextBlock
					{
						Text = letter,
						HorizontalAlignment = HorizontalAlignment.Center,
						VerticalAlignment = VerticalAlignment.Center,
						FontWeight = FontWeights.Bold,
						Foreground = Brushes.White
					}
				};

				panel.Children.Add(border);
			}

			WordGuessHistoryPanel.Items.Add(panel);
		}


		private void ClearKeyboardState()
		{
			_keyboardState.Clear();

			// reset all key buttons to "unused" look
			foreach (var kvp in _keyButtons)
			{
				var btn = kvp.Value;
				btn.Background = UnusedKeyBrush;
				btn.Foreground = Brushes.Black;
			}
		}

		private static int ResultPriority(LetterResult r) => r switch
		{
			LetterResult.Grey => 0,
			LetterResult.Yellow => 1,
			LetterResult.Green => 2,
			_ => 0
		};

		private void UpdateKeyboardFromResult(WordGuessResultPayload payload)
		{
			// 1) Merge new info into _keyboardState with priority
			for (int i = 0; i < payload.Guess.Length && i < payload.LetterResults.Length; i++)
			{
				char c = char.ToUpperInvariant(payload.Guess[i]);
				var newState = payload.LetterResults[i];

				if (!_keyboardState.TryGetValue(c, out var existing))
				{
					_keyboardState[c] = newState;
				}
				else
				{
					// Only upgrade: Grey < Yellow < Green
					if (ResultPriority(newState) > ResultPriority(existing))
						_keyboardState[c] = newState;
				}
			}

			// 2) Apply colors to the buttons
			foreach (var kvp in _keyButtons)
			{
				char letter = kvp.Key;
				var btn = kvp.Value;

				if (_keyboardState.TryGetValue(letter, out var state))
				{
					ApplyKeyColor(btn, state);
				}
				else
				{
					// still unused
					btn.Background = UnusedKeyBrush;
					btn.Foreground = Brushes.Black;
				}
			}
		}

		private void ApplyKeyColor(Button btn, LetterResult state)
		{
			switch (state)
			{
				case LetterResult.Green:
					btn.Background = Brushes.Green;
					btn.Foreground = Brushes.White;
					break;
				case LetterResult.Yellow:
					btn.Background = Brushes.Goldenrod;
					btn.Foreground = Brushes.White;
					break;
				case LetterResult.Grey:
					btn.Background = Brushes.DimGray;
					btn.Foreground = Brushes.White;
					break;
			}
		}
		private void KeyboardKey_Click(object sender, RoutedEventArgs e)
		{
			if (!GuessButton.IsEnabled)
				return; // only allow input when it's P2's turn and game is active

			if (sender is Button btn && btn.Tag is string s && s.Length == 1)
			{
				var c = s[0];
				AppendLetter(c);
			}
		}
		private void BackspaceButton_Click(object sender, RoutedEventArgs e)
		{
			if (!GuessButton.IsEnabled)
				return;

			Backspace();
		}
		private void KeyboardKey_Loaded(object sender, RoutedEventArgs e)
		{
			if (sender is Button btn && btn.Tag is string s && s.Length == 1)
			{
				char letter = char.ToUpperInvariant(s[0]);
				if (!_keyButtons.ContainsKey(letter))
				{
					_keyButtons[letter] = btn;


					btn.Background = UnusedKeyBrush;
					btn.Foreground = Brushes.Black;
				}
			}
		}
		private void RefreshCurrentGuessTiles()
		{
			SetGuessTileText(GuessTile0, 0);
			SetGuessTileText(GuessTile1, 1);
			SetGuessTileText(GuessTile2, 2);
			SetGuessTileText(GuessTile3, 3);
			SetGuessTileText(GuessTile4, 4);
		}

		private void SetGuessTileText(Border tileBorder, int index)
		{
			if (tileBorder.Child is TextBlock tb)
			{
				if (index < _currentLength)
				{
					tb.Text = _currentGuess[index].ToString();
				}
				else
				{
					tb.Text = string.Empty;
				}
			}
		}

		private void ClearCurrentGuess()
		{
			for (int i = 0; i < 5; i++)
				_currentGuess[i] = '\0';

			_currentLength = 0;
			RefreshCurrentGuessTiles();

			// keep the hidden box empty so existing code that reads it doesn't get weird surprises
			GuessBox.Text = string.Empty;
		}

		private void AppendLetter(char c)
		{
			if (_currentLength >= 5)
				return;

			c = char.ToUpperInvariant(c);
			_currentGuess[_currentLength] = c;
			_currentLength++;
			RefreshCurrentGuessTiles();
		}

		private void Backspace()
		{
			if (_currentLength == 0)
				return;

			_currentLength--;
			_currentGuess[_currentLength] = '\0';
			RefreshCurrentGuessTiles();
		}

		private string GetCurrentGuessString()
		{
			return new string(_currentGuess, 0, _currentLength);
		}
	}

}

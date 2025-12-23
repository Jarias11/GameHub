using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GameContracts;
using GameClient.Wpf.Services;


namespace GameClient.Wpf.GameClients
{
	public partial class AnagramGameClient : UserControl, IGameClient
	{
		// IGameClient plumbing

		// This client handles the Anagram game
		public GameType GameType => GameType.Anagram;

		// Root UI element for MainWindow (UserControl is a FrameworkElement)
		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		private string? _roomCode;
		private string? _playerId;

		private readonly ObservableCollection<LetterTile> _letterTiles = new();
		private readonly ObservableCollection<string> _possibleWords = new();


		private readonly ObservableCollection<string> _p1Words = new();
		private readonly ObservableCollection<string> _p2Words = new();

		private int _p1Score;
		private int _p2Score;

		private bool _roundActive;
		private DateTimeOffset? _roundEndUtc;
		private readonly DispatcherTimer _timer;

		private bool _playedCountdownTicking;
		private bool _playedAlarm;
		private bool _playedGameStart;
		private bool _playedWinLose;

		private sealed class LetterTile
		{
			public string Letter { get; set; } = "";
			public int Value { get; set; }
			public bool IsRevealed { get; set; }
		}

		private static class ScrabbleValues
		{
			// Standard English Scrabble values
			public static int GetValue(char upperLetter)
			{
				return upperLetter switch
				{
					'A' => 1,
					'E' => 1,
					'I' => 1,
					'O' => 1,
					'U' => 1,
					'L' => 1,
					'N' => 1,
					'S' => 1,
					'T' => 1,
					'R' => 1,
					'D' => 2,
					'G' => 2,
					'B' => 3,
					'C' => 3,
					'M' => 3,
					'P' => 3,
					'F' => 4,
					'H' => 4,
					'V' => 4,
					'W' => 4,
					'Y' => 4,
					'K' => 5,
					'J' => 8,
					'X' => 8,
					'Q' => 10,
					'Z' => 10,
					_ => 1
				};
			}
		}


		public AnagramGameClient()
		{
			InitializeComponent();

			LetterCards.ItemsSource = _letterTiles;


			P1WordsList.ItemsSource = _p1Words;
			P2WordsList.ItemsSource = _p2Words;
			PossibleWordsList.ItemsSource = _possibleWords;


			// Default: no room yet → hide config & input panels until we know who we are
			OptionsPanel.Visibility = Visibility.Collapsed;
			P1InputPanel.Visibility = Visibility.Collapsed;
			P2InputPanel.Visibility = Visibility.Collapsed;

			_timer = new DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(0.5)
			};
			_timer.Tick += Timer_Tick;
		}

		// ── IGameClient implementation ─────────────────────────────────────

		public void SetConnection(Func<HubMessage, Task> sendAsync, Func<bool> isSocketOpen)
		{
			_sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
			_isSocketOpen = isSocketOpen ?? throw new ArgumentNullException(nameof(isSocketOpen));
		}

		public void OnRoomChanged(string? roomCode, string? playerId)
		{
			_roomCode = roomCode;
			_playerId = playerId;

			// When room changes, reset some UI
			_roundActive = false;
			_timer.Stop();
			_roundEndUtc = null;
			StatusText.Text = string.Empty;
			UpdateTimerText(0);

			_p1Words.Clear();
			_p2Words.Clear();
			_p1Score = 0;
			_p2Score = 0;
			P1ScoreText.Text = "0";
			P2ScoreText.Text = "0";
			_letterTiles.Clear();
			_possibleWords.Clear();

			LetterCards.Items.Refresh(); // optional, but fine
			RoundInfoPanel.Visibility = Visibility.Collapsed;
			SetPossibleWordsVisible(false);


			// Decide what panels to show based on who we are.
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId))
			{
				// Not in a room at all
				OptionsPanel.Visibility = Visibility.Collapsed;
				P1InputPanel.Visibility = Visibility.Collapsed;
				P2InputPanel.Visibility = Visibility.Collapsed;
			}
			else if (_playerId == "P1")
			{
				OptionsPanel.Visibility = Visibility.Visible;
				P1InputPanel.Visibility = Visibility.Visible;
				P2InputPanel.Visibility = Visibility.Collapsed;
			}
			else if (_playerId == "P2")
			{
				OptionsPanel.Visibility = Visibility.Collapsed;
				P1InputPanel.Visibility = Visibility.Collapsed;
				P2InputPanel.Visibility = Visibility.Visible;
			}
			else
			{
				// Spectator / unknown
				OptionsPanel.Visibility = Visibility.Collapsed;
				P1InputPanel.Visibility = Visibility.Collapsed;
				P2InputPanel.Visibility = Visibility.Collapsed;
			}
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			// Only handle messages for our current room
			if (string.IsNullOrEmpty(_roomCode) || msg.RoomCode != _roomCode)
				return false;

			try
			{
				switch (msg.MessageType)
				{
					case "AnagramRoundStarted":
						HandleRoundStarted(msg);
						return true;

					case "AnagramWordResult":
						HandleWordResult(msg);
						return true;

					case "AnagramRoundSummary":
						HandleRoundSummary(msg);
						return true;

					case "AnagramReset":
						HandleReset(msg);
						return true;

					default:
						return false;
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("[AnagramClient] Error handling message: " + ex.Message);
				return false;
			}
		}

		public void OnKeyDown(KeyEventArgs e)
		{
			// Optional: if you want global shortcuts later.
			// Right now the TextBoxes already handle Enter, so nothing needed here.
		}

		public void OnKeyUp(KeyEventArgs e)
		{
			// No-op for now
		}
		private void WordInput_PreviewKeyDown(object sender, KeyEventArgs e)
		{
			// If you have any global game shortcuts you want to still work while typing,
			// handle them here (before TextBox eats the key).
			//
			// Example: allow Esc to do something, etc.
			// if (e.Key == Key.Escape) { ...; e.Handled = true; }

			// IMPORTANT:
			// Do NOT set e.Handled = true for normal letters,
			// or you’ll prevent the user from typing.
		}

		// ── Message handlers ───────────────────────────────────────────────

		private void HandleRoundStarted(HubMessage msg)
		{
			var payload = JsonSerializer.Deserialize<AnagramRoundStartedPayload>(msg.PayloadJson);
			if (payload == null) return;

			_roundActive = true;
			_roundEndUtc = payload.RoundEndUtc;

			Dispatcher.Invoke(() =>
			{
				_p1Words.Clear();
				_p2Words.Clear();
				_possibleWords.Clear();

				_p1Score = 0;
				_p2Score = 0;
				P1ScoreText.Text = "0";
				P2ScoreText.Text = "0";

				SetPossibleWordsVisible(false);

				_playedCountdownTicking = false;
				_playedAlarm = false;
				_playedGameStart = false;
				_playedWinLose = false;

				// play game start once per round
				if (!_playedGameStart)
				{
					_playedGameStart = true;
					SoundService.PlayAnagramEffect(AnagramSoundEffect.GameStart);
				}

				BuildLetterTiles(payload.Letters);
				LetterCards.UpdateLayout(); // helps generate item containers
				_ = RevealTilesOneByOneAsync(); // fire-and-forget (UI animation sequence)
				RoundInfoPanel.Visibility = Visibility.Visible;

				StatusText.Text = payload.Message ?? $"Round {payload.RoundNumber} started.";

				// Only host had the options visible before start; keep them hidden now.
				if (_playerId == "P1")
				{
					OptionsPanel.Visibility = Visibility.Collapsed;
				}
				if (_playerId == "P1")
				{
					P1WordInput.Focus();
					Keyboard.Focus(P1WordInput);
				}
				else if (_playerId == "P2")
				{
					P2WordInput.Focus();
					Keyboard.Focus(P2WordInput);
				}

				UpdateTimerText(payload.DurationSeconds);
				_timer.Start();
			});
		}
		private void BuildLetterTiles(string letters)
		{
			_letterTiles.Clear();
			if (string.IsNullOrWhiteSpace(letters))
				return;

			foreach (char ch in letters.Trim())
			{
				if (!char.IsLetter(ch)) continue;

				char upper = char.ToUpperInvariant(ch);
				_letterTiles.Add(new LetterTile
				{
					Letter = upper.ToString(),
					Value = ScrabbleValues.GetValue(upper),
					IsRevealed = false
				});
			}
		}

		private async Task RevealTilesOneByOneAsync()
		{
			// small pause so UI is visible before first flip
			await Task.Delay(150);

			for (int i = 0; i < _letterTiles.Count; i++)
			{
				await FlipRevealTileAsync(i);
				await Task.Delay(120); // spacing between flips
			}
		}

		private async Task FlipRevealTileAsync(int index)
		{
			if (index < 0 || index >= _letterTiles.Count) return;

			// Try to get the container for a bit (layout can be late)
			FrameworkElement? container = null;

			for (int attempt = 0; attempt < 20; attempt++) // ~20 frames max
			{
				container = (FrameworkElement?)LetterCards.ItemContainerGenerator.ContainerFromIndex(index);
				if (container != null) break;

				// let WPF process layout/render
				await Dispatcher.Yield(DispatcherPriority.Loaded);
			}

			// If we STILL can't animate, at least reveal the tile so it never stays "?"
			if (container == null)
			{
				_letterTiles[index].IsRevealed = true;
				LetterCards.Items.Refresh();
				return;
			}

			var border = FindVisualChild<Border>(container);
			if (border == null)
			{
				_letterTiles[index].IsRevealed = true;
				LetterCards.Items.Refresh();
				return;
			}

			if (border.RenderTransform is not ScaleTransform st)
			{
				st = new ScaleTransform(1, 1);
				border.RenderTransform = st;
				border.RenderTransformOrigin = new Point(0.5, 0.5);
			}

			var shrink = new DoubleAnimation
			{
				To = 0,
				Duration = TimeSpan.FromMilliseconds(90),
				EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
			};

			var tcs1 = new TaskCompletionSource<bool>();
			shrink.Completed += (_, __) => tcs1.TrySetResult(true);
			st.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
			await tcs1.Task;

			// reveal during "edge-on"
			_letterTiles[index].IsRevealed = true;
			LetterCards.Items.Refresh();

			var expand = new DoubleAnimation
			{
				To = 1,
				Duration = TimeSpan.FromMilliseconds(110),
				EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
			};

			var tcs2 = new TaskCompletionSource<bool>();
			expand.Completed += (_, __) => tcs2.TrySetResult(true);
			st.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
			await tcs2.Task;
		}


		private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
		{
			int count = VisualTreeHelper.GetChildrenCount(parent);
			for (int i = 0; i < count; i++)
			{
				var child = VisualTreeHelper.GetChild(parent, i);
				if (child is T typed) return typed;
				var deeper = FindVisualChild<T>(child);
				if (deeper != null) return deeper;
			}
			return null;
		}


		private void HandleWordResult(HubMessage msg)
		{
			var payload = JsonSerializer.Deserialize<AnagramWordResultPayload>(msg.PayloadJson);
			if (payload == null) return;

			bool isLocal = msg.PlayerId == _playerId;

			Dispatcher.Invoke(() =>
			{
				if (msg.PlayerId == "P1")
				{
					// Everyone sees P1's score update
					_p1Score = payload.NewScore;
					P1ScoreText.Text = _p1Score.ToString();

					// Only P1 sees their own words + error feedback
					if (isLocal)
					{
						if (payload.Accepted)
						{
							SoundService.PlayAnagramEffect(AnagramSoundEffect.Success);
							_p1Words.Add(payload.Word);
							ClearInput(P1WordInput);
						}
						else
						{
							SoundService.PlayAnagramEffect(AnagramSoundEffect.Wrong);
							ShowInvalidWordFeedback(P1WordInput, payload.Reason);
						}
					}
				}
				else if (msg.PlayerId == "P2")
				{
					// Everyone sees P2's score update
					_p2Score = payload.NewScore;
					P2ScoreText.Text = _p2Score.ToString();

					// Only P2 sees their own words + error feedback
					if (isLocal)
					{
						if (payload.Accepted)
						{
							SoundService.PlayAnagramEffect(AnagramSoundEffect.Success);
							_p2Words.Add(payload.Word);
							ClearInput(P2WordInput);
						}
						else
						{
							SoundService.PlayAnagramEffect(AnagramSoundEffect.Wrong);
							ShowInvalidWordFeedback(P2WordInput, payload.Reason);
						}
					}
				}

				if (payload.IsRoundOver)
				{
					_roundActive = false;
					_timer.Stop();
					StatusText.Text = "Round over.";
				}

				if (payload.SecondsRemaining >= 0)
				{
					UpdateTimerText(payload.SecondsRemaining);
				}
			});
		}

		private void HandleRoundSummary(HubMessage msg)
		{
			var payload = JsonSerializer.Deserialize<AnagramRoundSummaryPayload>(msg.PayloadJson);
			if (payload == null) return;

			_roundActive = false;
			_timer.Stop();

			Dispatcher.Invoke(() =>
			{
				_p1Words.Clear();
				_p2Words.Clear();
				_possibleWords.Clear();
				_p1Score = 0;
				_p2Score = 0;

				foreach (var p in payload.Players)
				{
					if (p.PlayerId == "P1")
					{
						_p1Score = p.Score;
						P1ScoreText.Text = _p1Score.ToString();
						foreach (var w in p.AcceptedWords.OrderBy(x => x))
							_p1Words.Add(w);
					}
					else if (p.PlayerId == "P2")
					{
						_p2Score = p.Score;
						P2ScoreText.Text = _p2Score.ToString();
						foreach (var w in p.AcceptedWords.OrderBy(x => x))
							_p2Words.Add(w);
					}
				}
				// Play win/lose only for real players (not spectators), and only once
				if (!_playedWinLose && (_playerId == "P1" || _playerId == "P2"))
				{
					_playedWinLose = true;

					// Determine outcome for THIS client
					int myScore = (_playerId == "P1") ? _p1Score : _p2Score;
					int otherScore = (_playerId == "P1") ? _p2Score : _p1Score;

					if (myScore > otherScore)
						SoundService.PlayAnagramEffect(AnagramSoundEffect.Winner);
					else if (myScore < otherScore)
						SoundService.PlayAnagramEffect(AnagramSoundEffect.Loser);
					// else tie → play nothing (or add Tie sound later)
				}

				var allFound = payload.Players
	.SelectMany(p => p.AcceptedWords ?? Array.Empty<string>())
	.Select(w => w.ToLowerInvariant())
	.ToHashSet();
				foreach (var w in (payload.PossibleWords ?? Array.Empty<string>()))
				{
					// If you want "all possible", remove this if-check.
					// If you want "missed", keep it:
					if (!allFound.Contains(w.ToLowerInvariant()))
						_possibleWords.Add(w);
				}
				SetPossibleWordsVisible(true);

				StatusText.Text = payload.Message ?? "Round over.";

				UpdateTimerText(0);
			});
		}
		private void SetPossibleWordsVisible(bool visible)
		{
			PossibleWordsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
			ColPossible.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
		}

		private void HandleReset(HubMessage msg)
		{
			var payload = JsonSerializer.Deserialize<AnagramResetPayload>(msg.PayloadJson);
			if (payload == null) return;

			_roundActive = false;
			_timer.Stop();
			_roundEndUtc = null;

			Dispatcher.Invoke(() =>
			{
				_p1Words.Clear();
				_p2Words.Clear();
				_possibleWords.Clear();

				_p1Score = 0;
				_p2Score = 0;
				P1ScoreText.Text = "0";
				P2ScoreText.Text = "0";

				_letterTiles.Clear();
				LetterCards.Items.Refresh();
				RoundInfoPanel.Visibility = Visibility.Collapsed;
				SetPossibleWordsVisible(false);

				_playedCountdownTicking = false;
				_playedAlarm = false;
				_playedGameStart = false;
				_playedWinLose = false;

				StatusText.Text = payload.Message;

				// Host can configure again
				if (_playerId == "P1" && !string.IsNullOrEmpty(_roomCode))
				{
					OptionsPanel.Visibility = Visibility.Visible;
				}

				UpdateTimerText(0);
			});
		}

		// ── UI events ──────────────────────────────────────────────────────

		private async void StartRoundButton_Click(object sender, RoutedEventArgs e)
		{
			if (_playerId != "P1" || _sendAsync == null || _isSocketOpen == null || !_isSocketOpen())
				return;

			if (string.IsNullOrEmpty(_roomCode))
				return;

			int letterCount = GetSelectedLetterCount();
			int duration = GetSelectedDurationSeconds();

			var payload = new AnagramConfigureRoundPayload
			{
				LetterCount = letterCount,
				DurationSeconds = duration
			};

			var msg = new HubMessage
			{
				MessageType = "AnagramConfigureRound",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}

		private int GetSelectedLetterCount()
		{
			var item = LetterCountCombo.SelectedItem as ComboBoxItem;
			if (item?.Content is string s && int.TryParse(s, out int value))
				return value;
			return 5;
		}

		private int GetSelectedDurationSeconds()
		{
			var item = TimeControlCombo.SelectedItem as ComboBoxItem;
			if (item?.Tag is string tag && int.TryParse(tag, out int seconds))
				return seconds;

			if (item?.Content is string content)
			{
				var parts = content.Split(':');
				if (parts.Length == 2 &&
					int.TryParse(parts[0], out int minutes) &&
					int.TryParse(parts[1], out int secs))
				{
					return minutes * 60 + secs;
				}
			}

			return 60;
		}

		private async void P1SubmitButton_Click(object sender, RoutedEventArgs e)
		{
			await SubmitWordFromInputAsync(P1WordInput, "P1");
		}

		private async void P2SubmitButton_Click(object sender, RoutedEventArgs e)
		{
			await SubmitWordFromInputAsync(P2WordInput, "P2");
		}

		private async void WordInput_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Enter)
			{
				if (sender == P1WordInput)
				{
					await SubmitWordFromInputAsync(P1WordInput, "P1");
				}
				else if (sender == P2WordInput)
				{
					await SubmitWordFromInputAsync(P2WordInput, "P2");
				}
			}
		}

		private async Task SubmitWordFromInputAsync(TextBox textBox, string logicalPlayerId)
		{
			if (!_roundActive || string.IsNullOrWhiteSpace(textBox.Text))
				return;

			if (_sendAsync == null || _isSocketOpen == null || !_isSocketOpen())
				return;

			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId))
				return;

			// Only send if this is our own side
			if (logicalPlayerId != _playerId)
				return;

			var word = textBox.Text.Trim();

			var payload = new AnagramSubmitWordPayload
			{
				Word = word
			};

			var msg = new HubMessage
			{
				MessageType = "AnagramSubmitWord",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			ResetInputVisual(textBox);
			await _sendAsync(msg);
		}

		// ── Timer ──────────────────────────────────────────────────────────

		private async void Timer_Tick(object? sender, EventArgs e)
		{
			if (!_roundActive || !_roundEndUtc.HasValue)
			{
				_timer.Stop();
				return;
			}

			var remaining = _roundEndUtc.Value - DateTimeOffset.UtcNow;
			int seconds = (int)Math.Max(0, remaining.TotalSeconds);

			UpdateTimerText(seconds);

			// Start ticking once when we enter the last 5 seconds (5..1)
			if (seconds > 0 && seconds <= 5 && !_playedCountdownTicking)
			{
				_playedCountdownTicking = true;
				SoundService.PlayAnagramEffect(AnagramSoundEffect.ClockTicking);
			}


			if (seconds <= 0)
			{

				if (!_playedAlarm)
				{
					_playedAlarm = true;
					SoundService.PlayAnagramEffect(AnagramSoundEffect.ClockAlarm);
				}

				_roundActive = false;
				_timer.Stop();

				// Optional: show local status while waiting for server
				StatusText.Text = "Time's up! Waiting for results...";

				// Only host (P1) asks the server to finalize the round.
				if (_playerId == "P1" &&
					_sendAsync != null &&
					_isSocketOpen != null &&
					_isSocketOpen() &&
					!string.IsNullOrEmpty(_roomCode))
				{
					var payload = new AnagramTimeUpPayload();

					var msg = new HubMessage
					{
						MessageType = "AnagramTimeUp",
						RoomCode = _roomCode,
						PlayerId = _playerId,
						PayloadJson = JsonSerializer.Serialize(payload)
					};

					// Fire this off to the server
					await _sendAsync(msg);
				}
			}
		}

		private void UpdateTimerText(int totalSeconds)
		{
			if (totalSeconds < 0) totalSeconds = 0;
			int minutes = totalSeconds / 60;
			int seconds = totalSeconds % 60;
			TimerText.Text = $"{minutes:00}:{seconds:00}";
		}

		// ── Input visual helpers ───────────────────────────────────────────

		private void ClearInput(TextBox textBox)
		{
			textBox.Text = string.Empty;
			ResetInputVisual(textBox);
		}

		private void ResetInputVisual(TextBox textBox)
		{
			var borderBrush = TryFindResource("AnagramInputBorderBrush") as Brush ?? Brushes.Gray;
			var fgBrush = TryFindResource("AnagramInputForegroundBrush") as Brush ?? Brushes.White;

			textBox.BorderBrush = borderBrush;
			textBox.Foreground = fgBrush;

			if (textBox.RenderTransform is not TranslateTransform tt)
			{
				tt = new TranslateTransform();
				textBox.RenderTransform = tt;
			}
			tt.X = 0;
		}

		private void ShowInvalidWordFeedback(TextBox textBox, string? reason)
		{
			textBox.BorderBrush = Brushes.Red;
			textBox.Foreground = Brushes.Red;
			StatusText.Text = reason ?? "Word rejected.";

			var storyboard = TryFindResource("AnagramShakeStoryboard") as Storyboard;
			if (storyboard != null)
			{
				if (textBox.RenderTransform is not TranslateTransform)
				{
					textBox.RenderTransform = new TranslateTransform();
				}

				storyboard.Begin(textBox, true);
			}
		}
	}
}

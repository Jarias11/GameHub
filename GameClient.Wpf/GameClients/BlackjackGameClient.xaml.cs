using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameClient.Wpf;
using GameContracts;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace GameClient.Wpf.GameClients
{
	public partial class BlackjackGameClient : UserControl, IGameClient
	{
		public GameType GameType => GameType.Blackjack;
		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		private string? _roomCode;
		private string? _playerId;

		private BlackjackSnapshotPayload? _snapshot;

		public BlackjackGameClient()
		{
			InitializeComponent();
		}

		public void SetConnection(Func<HubMessage, Task> sendAsync, Func<bool> isSocketOpen)
		{
			_sendAsync = sendAsync;
			_isSocketOpen = isSocketOpen;
		}

		public void OnRoomChanged(string? roomCode, string? playerId)
		{
			_roomCode = roomCode;
			_playerId = playerId;

			if (string.IsNullOrEmpty(roomCode) || string.IsNullOrEmpty(playerId))
			{
				_snapshot = null;
				PhaseText.Text = "Phase: Lobby";
				DealerText.Text = "Dealer: -";
				PlayersSummaryText.Text = "No room.";
				GameSurface.InvalidateVisual();
			}
			else
			{
				PhaseText.Text = "Lobby";
				DealerText.Text = "Dealer: ?";
				PlayersSummaryText.Text = "Waiting...";
			}
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			if (msg.MessageType == "BlackjackSnapshot")
			{
				if (!Dispatcher.CheckAccess())
				{
					Dispatcher.BeginInvoke(new Action(() =>
					{
						HandleSnapshot(msg.PayloadJson);
					}));
				}
				else
				{
					HandleSnapshot(msg.PayloadJson);
				}
				return true;
			}

			return false;
		}

		public void OnKeyDown(KeyEventArgs e) { }
		public void OnKeyUp(KeyEventArgs e) { }

		private void HandleSnapshot(string payloadJson)
		{
			BlackjackSnapshotPayload? snapshot;
			try
			{
				snapshot = JsonSerializer.Deserialize<BlackjackSnapshotPayload>(payloadJson);
			}
			catch
			{
				return;
			}

			if (snapshot == null)
				return;

			_snapshot = snapshot;

			PhaseText.Text = $"Phase: {snapshot.Phase}";
			int playerCount = snapshot.Players?.Count ?? 0;
			PlayersSummaryText.Text = $"{playerCount} player(s) in room";

			if (snapshot.DealerCards != null && snapshot.DealerCards.Count > 0)
			{
				if (snapshot.DealerRevealed)
					DealerText.Text = $"Dealer: {snapshot.DealerVisibleValue}";
				else
					DealerText.Text = "Dealer: ? + ?";
			}
			else
			{
				DealerText.Text = "Dealer: -";
			}

			// Buttons
			bool isHost = string.Equals(_playerId, "P1", StringComparison.OrdinalIgnoreCase);
			StartButton.IsEnabled = isHost && snapshot.Phase == BlackjackPhase.Lobby && playerCount > 0;

			bool myTurn = snapshot.CurrentPlayerId == _playerId &&
				snapshot.Phase == BlackjackPhase.PlayerTurns;

			HitButton.IsEnabled = myTurn;
			StandButton.IsEnabled = myTurn;

			RestartButton.IsEnabled = isHost && snapshot.RoundComplete;

			GameSurface.InvalidateVisual();
		}

		// ─────────────────────────────────────────────────────────
		// Button handlers
		// ─────────────────────────────────────────────────────────

		private async void StartButton_Click(object sender, RoutedEventArgs e)
		{
			if (_sendAsync == null || _isSocketOpen == null)
				return;
			if (!_isSocketOpen())
				return;
			if (string.IsNullOrEmpty(_roomCode))
				return;

			var payload = new BlackjackStartRequestPayload
			{
				RoomCode = _roomCode
			};

			var msg = new HubMessage
			{
				MessageType = "BlackjackStartRequest",
				RoomCode = _roomCode,
				PlayerId = _playerId ?? "",
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			try
			{
				await _sendAsync(msg);
			}
			catch { }
		}

		private async void HitButton_Click(object sender, RoutedEventArgs e)
		{
			await SendActionAsync(BlackjackActionType.Hit);
		}

		private async void StandButton_Click(object sender, RoutedEventArgs e)
		{
			await SendActionAsync(BlackjackActionType.Stand);
		}

		private async Task SendActionAsync(BlackjackActionType action)
		{
			if (_sendAsync == null || _isSocketOpen == null)
				return;
			if (!_isSocketOpen())
				return;
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId))
				return;

			var payload = new BlackjackActionPayload
			{
				RoomCode = _roomCode,
				PlayerId = _playerId,
				Action = action
			};

			var msg = new HubMessage
			{
				MessageType = "BlackjackAction",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			try
			{
				await _sendAsync(msg);
			}
			catch { }
		}

		private void RestartButton_Click(object sender, RoutedEventArgs e)
		{
			// This just uses your global RestartGameButton in MainWindow.
			// The host will click the main Restart button; this local one is optional.
		}

		// ─────────────────────────────────────────────────────────
		// Skia rendering
		// ─────────────────────────────────────────────────────────

		private void GameSurface_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			var info = e.Info;

			canvas.Clear(new SKColor(5, 10, 30));

			if (_snapshot == null)
				return;

			float width = info.Width;
			float height = info.Height;

			// Table background
			using (var tablePaint = new SKPaint { Color = new SKColor(0, 80, 50) })
			{
				var rect = new SKRect(8, 8, width - 8, height - 8);
				canvas.DrawRoundRect(rect, 16, 16, tablePaint);
			}

			// Dealer at top
			float dealerY = 40f;
			DrawHand(canvas, _snapshot.DealerCards, width / 2f, dealerY, isDealer: true);

			// Players at bottom (up to 4)
			if (_snapshot.Players != null && _snapshot.Players.Count > 0)
			{
				float baseY = height - 120f;
				float spacingX = width / 4f;
				for (int i = 0; i < _snapshot.Players.Count; i++)
				{
					var p = _snapshot.Players[i];
					float centerX = spacingX * (i + 0.5f);
					DrawPlayer(canvas, p, centerX, baseY);
				}
			}
		}

		private void DrawHand(SKCanvas canvas, System.Collections.Generic.List<BlackjackCardDto> cards, float centerX, float y, bool isDealer)
		{
			if (cards == null || cards.Count == 0)
				return;

			float cardWidth = 40f;
			float cardHeight = 60f;
			float spacing = 16f;
			float totalWidth = cards.Count * cardWidth + (cards.Count - 1) * spacing;
			float startX = centerX - totalWidth / 2f;

			using var backPaint = new SKPaint { Color = new SKColor(30, 30, 90), IsAntialias = true };
			using var backBorder = new SKPaint { Color = new SKColor(150, 200, 255), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
			using var facePaint = new SKPaint { Color = new SKColor(240, 240, 255), IsAntialias = true };
			using var faceBorder = new SKPaint { Color = new SKColor(30, 30, 40), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
			using var textPaint = new SKPaint
			{
				Color = SKColors.Black,
				TextSize = 18,
				IsAntialias = true,
				TextAlign = SKTextAlign.Center
			};

			for (int i = 0; i < cards.Count; i++)
			{
				float x = startX + i * (cardWidth + spacing);
				var cardRect = new SKRect(x, y, x + cardWidth, y + cardHeight);

				if (cards[i].IsFaceDown)
				{
					canvas.DrawRoundRect(cardRect, 6, 6, backPaint);
					canvas.DrawRoundRect(cardRect, 6, 6, backBorder);
				}
				else
				{
					canvas.DrawRoundRect(cardRect, 6, 6, facePaint);
					canvas.DrawRoundRect(cardRect, 6, 6, faceBorder);

					string rankText = RankToString(cards[i].Rank);
					string suitText = SuitToSymbol(cards[i].Suit);

					bool isRed = cards[i].Suit == 1 || cards[i].Suit == 2;
					textPaint.Color = isRed ? SKColors.Red : SKColors.Black;

					string combined = $"{rankText}{suitText}";
					canvas.DrawText(combined, cardRect.MidX, cardRect.MidY + 6, textPaint);
				}
			}
		}

		private void DrawPlayer(SKCanvas canvas, BlackjackPlayerStateDto p, float centerX, float baseY)
		{
			float nameY = baseY + 72f;

			using var labelPaint = new SKPaint
			{
				Color = SKColors.White,
				TextSize = 16,
				IsAntialias = true,
				TextAlign = SKTextAlign.Center
			};

			string label = p.PlayerId;
			if (p.Result != BlackjackResult.Pending)
			{
				label += $" ({p.Result})";
			}
			else if (_snapshot?.CurrentPlayerId == p.PlayerId)
			{
				label += " (Your turn)";
			}

			canvas.DrawText(label, centerX, nameY, labelPaint);

			// Chips / hand value line
			string bottom = $"Val: {p.HandValue}  Chips: {p.Chips}";
			canvas.DrawText(bottom, centerX, nameY + 18, labelPaint);

			// Cards
			float cardsY = baseY;
			DrawHand(canvas, p.Cards, centerX, cardsY, isDealer: false);
		}

		private static string RankToString(int rank)
		{
			return rank switch
			{
				11 => "J",
				12 => "Q",
				13 => "K",
				14 => "A",
				_ => rank.ToString()
			};
		}

		private static string SuitToSymbol(int suit)
		{
			return suit switch
			{
				0 => "♣",
				1 => "♦",
				2 => "♥",
				3 => "♠",
				_ => "?"
			};
		}
	}
}

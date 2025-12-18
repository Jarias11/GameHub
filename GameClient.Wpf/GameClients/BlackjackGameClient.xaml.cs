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

		private SKRect[] _seatRects = new SKRect[4]; // updated every draw

		private string? _roomCode;
		private string? _playerId;


		private readonly (int value, string label)[] _chipDefs =
		{
			(5, "5"),
			(25, "25"),
			(100, "100"),
			(500, "500"),
			(1000, "1000"),
			(-1, "ALL") // all-in
		};

		private SKRect[] _chipRects = new SKRect[6];
		private SKRect _submitBetRect;
		private int _pendingBet = 0;







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
				GameSurface.InvalidateVisual();
			}
			else
			{
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



			bool isHost = string.Equals(_playerId, "P1", StringComparison.OrdinalIgnoreCase);


			// Buttons
			int seatedCount = snapshot.SeatPlayerIds?.Count(pid => !string.IsNullOrEmpty(pid)) ?? 0;
			StartButton.IsEnabled = isHost && snapshot.Phase == BlackjackPhase.Lobby && seatedCount > 0;


			bool myTurn = snapshot.CurrentPlayerId == _playerId &&
				snapshot.Phase == BlackjackPhase.PlayerTurns;
			bool canSplit = false;

			var me = snapshot.Players?.FirstOrDefault(p => p.PlayerId == _playerId);
			if (me != null && me.CanBailout)
			{
				BailoutButton.Visibility = Visibility.Visible;
				BailoutButton.IsEnabled = true;
			}
			else
			{
				BailoutButton.Visibility = Visibility.Collapsed;
			}

			if (me != null) canSplit = me.CanSplit;

			SplitButton.IsEnabled = canSplit;

			HitButton.IsEnabled = myTurn;
			StandButton.IsEnabled = myTurn;

			RestartButton.IsEnabled = isHost && snapshot.RoundComplete;

			if (snapshot.Phase != BlackjackPhase.Betting)
			{
				_pendingBet = 0;
			}

			GameSurface.InvalidateVisual();
		}

		// ─────────────────────────────────────────────────────────
		// Button handlers
		// ─────────────────────────────────────────────────────────
		private void DrawCenterStatus(SKCanvas canvas, float width, float dealerY)
		{
			if (_snapshot == null) return;

			string phaseText = _snapshot.Phase.ToString();

			string dealerText;
			if (_snapshot.DealerCards == null || _snapshot.DealerCards.Count == 0)
				dealerText = "Dealer: -";
			else if (_snapshot.DealerRevealed)
				dealerText = $"Dealer: {_snapshot.DealerVisibleValue}";
			else
				dealerText = "Dealer: ?";

			float y = dealerY - 28f;

			using var bigPaint = new SKPaint
			{
				Color = SKColors.White,
				IsAntialias = true
			};

			using var smallPaint = new SKPaint
			{
				Color = new SKColor(210, 230, 255),
				IsAntialias = true
			};

			using var glowPaint = new SKPaint
			{
				Color = new SKColor(0, 0, 0, 160),
				IsAntialias = true
			};

			using var bigFont = new SKFont { Size = 34 };
			using var smallFont = new SKFont { Size = 22 };

			bigPaint.TextAlign = SKTextAlign.Center;
			smallPaint.TextAlign = SKTextAlign.Center;
			glowPaint.TextAlign = SKTextAlign.Center;

			// Phase (glow then text)
			canvas.DrawText(phaseText, width / 2f, y, bigFont, glowPaint);
			canvas.DrawText(phaseText, width / 2f, y, bigFont, bigPaint);

			// Dealer line
			canvas.DrawText(dealerText, width / 2f, y + 28f, smallFont, smallPaint);
		}


		private async void SplitButton_Click(object sender, RoutedEventArgs e)
		{
			await SendActionAsync(BlackjackActionType.Split);
		}
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

			// Dealer in center (not top)
			float dealerY = height * 0.35f;
			// draw centered status above dealer cards
			DrawCenterStatus(canvas, width, dealerY);
			DrawHand(canvas, _snapshot.DealerCards, width / 2f, dealerY, isDealer: true);



			// Seats around dealer (fixed 4)
			DrawSeatsAndPlayers(canvas, width, height);

			if (_snapshot.Phase == BlackjackPhase.Betting)
			{
				DrawBettingUi(canvas, width, height);
			}
		}

		private void DrawBettingUi(SKCanvas canvas, float width, float height)
		{
			if (_snapshot == null || string.IsNullOrEmpty(_playerId)) return;

			// only if I'm seated and not spectating
			var me = _snapshot.Players?.FirstOrDefault(p => p.PlayerId == _playerId);
			if (me == null) return;
			if (!me.IsSeated || me.IsSpectatingThisRound) return;

			// chips row near bottom-center
			float y = height * 0.52f;
			float startX = width * 0.18f;
			float gap = width * 0.11f;
			float r = 26f;

			using var textPaint = new SKPaint { Color = SKColors.White, TextSize = 14, IsAntialias = true, TextAlign = SKTextAlign.Center };

			for (int i = 0; i < _chipDefs.Length; i++)
			{
				float cx = startX + i * gap;
				float cy = y;

				_chipRects[i] = new SKRect(cx - r, cy - r, cx + r, cy + r);

				var color = i switch
				{
					0 => new SKColor(200, 40, 40),     // 5
					1 => new SKColor(40, 120, 220),    // 25
					2 => new SKColor(40, 200, 90),     // 100
					3 => new SKColor(160, 60, 200),    // 500
					4 => new SKColor(240, 180, 40),    // 1000
					_ => new SKColor(230, 230, 230),   // ALL
				};

				using var chipPaint = new SKPaint { Color = color, IsAntialias = true };
				using var border = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };

				canvas.DrawCircle(cx, cy, r, chipPaint);
				canvas.DrawCircle(cx, cy, r, border);

				canvas.DrawText(_chipDefs[i].label, cx, cy + 5, textPaint);
			}

			// Pending bet + submit button
			string pending = $"Your bet: {_pendingBet}";
			canvas.DrawText(pending, width * 0.5f, y + 60, textPaint);

			float btnW = 180, btnH = 46;
			_submitBetRect = new SKRect(width * 0.5f - btnW / 2f, y + 72, width * 0.5f + btnW / 2f, y + 72 + btnH);

			bool alreadySubmitted = me.HasSubmittedBet;

			using var btnFill = new SKPaint { Color = alreadySubmitted ? new SKColor(60, 60, 60) : new SKColor(20, 140, 120), IsAntialias = true };
			using var btnBorder = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };

			canvas.DrawRoundRect(_submitBetRect, 12, 12, btnFill);
			canvas.DrawRoundRect(_submitBetRect, 12, 12, btnBorder);

			canvas.DrawText(alreadySubmitted ? "Bet Submitted" : "Submit Bet", _submitBetRect.MidX, _submitBetRect.MidY + 6, textPaint);
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
			canvas.DrawText(bottom, centerX, nameY - 180, labelPaint);

			// Cards
			float cardsY = baseY;
			DrawHand(canvas, p.Cards, centerX, cardsY, isDealer: false);
		}
		private void DrawSeatsAndPlayers(SKCanvas canvas, float width, float height)
		{
			if (_snapshot == null) return;

			// Seat centers: top-left, top-right, bottom-left, bottom-right (around dealer)
			var centers = new (float x, float y)[]
			{
		(width * 0.25f, height * 0.20f),
		(width * 0.75f, height * 0.20f),
		(width * 0.25f, height * 0.75f),
		(width * 0.75f, height * 0.75f),
			};

			// Seat hitboxes (for clicking)
			float seatW = 180f;
			float seatH = 140f;

			for (int seat = 0; seat < 4; seat++)
			{
				var (cx, cy) = centers[seat];
				_seatRects[seat] = new SKRect(cx - seatW / 2f, cy - seatH / 2f, cx + seatW / 2f, cy + seatH / 2f);

				DrawSeat(canvas, seat, cx, cy);
			}
		}
		private void DrawSeat(SKCanvas canvas, int seatIndex, float centerX, float centerY)
		{
			if (_snapshot == null) return;

			// Seat background
			var rect = _seatRects[seatIndex];

			using var seatFill = new SKPaint { Color = new SKColor(0, 60, 40), IsAntialias = true };
			using var seatBorder = new SKPaint { Color = new SKColor(120, 220, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };

			// Highlight if this is your seat
			bool isMySeat = _snapshot.SeatPlayerIds != null
				&& seatIndex < _snapshot.SeatPlayerIds.Length
				&& string.Equals(_snapshot.SeatPlayerIds[seatIndex], _playerId, StringComparison.OrdinalIgnoreCase);

			if (isMySeat)
				seatBorder.StrokeWidth = 4;

			canvas.DrawRoundRect(rect, 16, 16, seatFill);
			canvas.DrawRoundRect(rect, 16, 16, seatBorder);

			// Who is in this seat?
			string? seatPid = null;
			if (_snapshot.SeatPlayerIds != null && seatIndex < _snapshot.SeatPlayerIds.Length)
				seatPid = _snapshot.SeatPlayerIds[seatIndex];

			// Find player DTO for this seat (if any)
			BlackjackPlayerStateDto? p = null;
			if (!string.IsNullOrEmpty(seatPid) && _snapshot.Players != null)
				p = _snapshot.Players.FirstOrDefault(x => x.PlayerId == seatPid);

			// Seat label
			using var labelPaint = new SKPaint
			{
				Color = SKColors.White,
				TextSize = 16,
				IsAntialias = true,
				TextAlign = SKTextAlign.Center
			};

			var labelY = rect.Top + 24;
			var label = string.IsNullOrEmpty(seatPid) ? $"Seat {seatIndex + 1} (Empty)" : $"{seatPid}";
			if (p != null && p.Chips <= 0) label += " - Loser";
			canvas.DrawText(label, rect.MidX, labelY, labelPaint);

			// If empty: show 2 face-down cards as placeholders (your “2 flipped down cards on 4 seats”)
			if (p == null)
			{
				var placeholder = new System.Collections.Generic.List<BlackjackCardDto>
		{
			new BlackjackCardDto { Rank = 0, Suit = 0, IsFaceDown = true },
			new BlackjackCardDto { Rank = 0, Suit = 0, IsFaceDown = true },
		};

				DrawHand(canvas, placeholder, rect.MidX, rect.MidY - 35, isDealer: false);
				return;
			}

			// Show their cards and info
			DrawHand(canvas, p.Cards, rect.MidX, rect.MidY - 35, isDealer: false);

			var info = $"Val: {p.HandValue}  Chips: {p.Chips}";
			if (p.IsSpectatingThisRound) info = "Spectating this round";
			if (p.Result != BlackjackResult.Pending) info = $"{p.Result} | {info}";

			canvas.DrawText(info, rect.MidX, rect.Bottom - 18, labelPaint);
		}
		private async void GameSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (_snapshot == null) return;
			if (_sendAsync == null || _isSocketOpen == null) return;
			if (!_isSocketOpen()) return;
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId)) return;



			// WPF mouse coords (DIPs)
			var p = e.GetPosition(GameSurface);

			// Convert to Skia pixel coords
			float sx = (float)(p.X * GameSurface.CanvasSize.Width / Math.Max(1.0, GameSurface.ActualWidth));
			float sy = (float)(p.Y * GameSurface.CanvasSize.Height / Math.Max(1.0, GameSurface.ActualHeight));


			// If betting: click chips / submit
			if (_snapshot.Phase == BlackjackPhase.Betting)
			{
				await HandleBettingClickAsync(e);
				return;
			}

			// Only allow picking seats in Lobby
			if (_snapshot.Phase != BlackjackPhase.Lobby) return;

			// Find which seat rect was clicked
			int seatIndex = -1;
			for (int i = 0; i < _seatRects.Length; i++)
			{
				if (_seatRects[i].Contains(sx, sy))
				{
					seatIndex = i;
					break;
				}
			}

			if (seatIndex < 0) return;

			// If seat occupied by someone else, ignore
			var seatPid = (_snapshot.SeatPlayerIds != null && seatIndex < _snapshot.SeatPlayerIds.Length)
				? _snapshot.SeatPlayerIds[seatIndex]
				: null;

			if (!string.IsNullOrEmpty(seatPid) && !string.Equals(seatPid, _playerId, StringComparison.OrdinalIgnoreCase))
				return;

			// Send seat select
			var payload = new BlackjackSeatSelectPayload
			{
				RoomCode = _roomCode!,
				SeatIndex = seatIndex
			};

			var msg = new HubMessage
			{
				MessageType = "BlackjackSeatSelect",
				RoomCode = _roomCode!,
				PlayerId = _playerId!,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			try { await _sendAsync(msg); } catch { }
		}

		private async Task HandleBettingClickAsync(MouseButtonEventArgs e)
		{
			if (_snapshot == null || _sendAsync == null || _isSocketOpen == null) return;
			if (!_isSocketOpen()) return;
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId)) return;

			var me = _snapshot.Players?.FirstOrDefault(p => p.PlayerId == _playerId);
			if (me == null) return;
			if (!me.IsSeated || me.IsSpectatingThisRound) return;
			if (me.HasSubmittedBet) return;

			// WPF -> Skia coords
			var p = e.GetPosition(GameSurface);
			float sx = (float)(p.X * GameSurface.CanvasSize.Width / Math.Max(1.0, GameSurface.ActualWidth));
			float sy = (float)(p.Y * GameSurface.CanvasSize.Height / Math.Max(1.0, GameSurface.ActualHeight));

			// Submit button?
			if (_submitBetRect.Contains(sx, sy))
			{
				if (_pendingBet <= 0) return;

				var payload = new BlackjackBetSubmitPayload
				{
					RoomCode = _roomCode!,
					PlayerId = _playerId!,
					Bet = _pendingBet
				};

				var msg = new HubMessage
				{
					MessageType = "BlackjackBetSubmit",
					RoomCode = _roomCode!,
					PlayerId = _playerId!,
					PayloadJson = JsonSerializer.Serialize(payload)
				};

				try { await _sendAsync(msg); } catch { }
				return;
			}

			// Chip clicks
			for (int i = 0; i < _chipRects.Length; i++)
			{
				if (!_chipRects[i].Contains(sx, sy)) continue;

				int add = _chipDefs[i].value;

				if (add == -1)
				{
					_pendingBet = me.Chips; // ALL IN
				}
				else
				{
					_pendingBet += add;
					if (_pendingBet > me.Chips) _pendingBet = me.Chips;
				}

				GameSurface.InvalidateVisual();
				return;
			}
		}
		private async void BailoutButton_Click(object sender, RoutedEventArgs e)
		{
			if (_sendAsync == null || _isSocketOpen == null) return;
			if (!_isSocketOpen()) return;
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId)) return;

			var payload = new BlackjackBailoutPayload
			{
				RoomCode = _roomCode,
				PlayerId = _playerId
			};

			var msg = new HubMessage
			{
				MessageType = "BlackjackBailout",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			try { await _sendAsync(msg); } catch { }
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

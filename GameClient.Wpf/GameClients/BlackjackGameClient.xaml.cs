using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media; // CompositionTarget
using GameClient.Wpf;
using GameContracts;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using GameClient.Wpf.Services;

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

		private int _hoverSeatIndex = -1;
		private int _hoverChipIndex = -1;
		private bool _hoverSubmit = false;

		private const float HoverScale = 1.10f;

		// Reveal timeline (pre-delay -> flip -> post-delay)
		private enum DealerRevealStage { None, PreDelay, Flipping, PostDelay }
		private DealerRevealStage _dealerRevealStage = DealerRevealStage.None;
		private float _dealerRevealTimer = 0f;

		private const float DealerFlipPreDelay = 0.35f;   // delay BEFORE flip starts
		private const float DealerFlipPostDelay = 0.30f;  // delay AFTER flip ends

		private BlackjackSnapshotPayload? _uiSnapshot;        // what UI/labels/results use
		private BlackjackSnapshotPayload? _pendingUiSnapshot; // waiting until animations end

		// Extra pacing: hold a moment after last player card lands before dealer flip starts
		private const float DealerRevealHoldAfterLastDeal = 0.15f;
		private float _dealerRevealHoldTimer = 0f;
		private bool _dealerRevealPendingStart = false;


		private bool VisualBusy =>
			_activeDeal != null ||
			_dealQueue.Count > 0 ||
			_dealerRevealStage != DealerRevealStage.None ||
	_dealerRevealPendingStart;

		// How many cards are allowed to be visible per hand.
		// Keys: "D" for dealer, and playerIds like "P1","P2",...
		private readonly Dictionary<string, int> _shownCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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

		// =========================
		// Deck + animation state
		// =========================

		private SKRect _deckRect; // computed each draw; used as animation start
		private BlackjackSnapshotPayload? _prevSnapshot;

		private sealed class DealAnim
		{
			public string OwnerKey = ""; // "D" for dealer, or "P1"/"P2"... for players
			public int CardIndex;        // index in that hand
			public BlackjackCardDto Card = new BlackjackCardDto();

			public SKRect StartRect;
			public SKRect EndRect;

			public float T;                // 0..1
			public float Duration = 0.22f; // seconds
		}

		private readonly Queue<DealAnim> _dealQueue = new Queue<DealAnim>();
		private DealAnim? _activeDeal;

		private bool _animLoopOn;
		private long _lastRenderTicks;

		// Dealer hole-card flip animation (client-side)
		private bool _dealerFlipActive;
		private float _dealerFlipT;                   // 0..1
		private const float DealerFlipDuration = 0.75f; // seconds (slower)

		public BlackjackGameClient()
		{
			InitializeComponent();
			// Hover SFX for bottom buttons
			StartButton.MouseEnter += (_, __) => SoundService.PlayBlackjackEffect(BlackjackSoundEffect.Hover);
			HitButton.MouseEnter += (_, __) => SoundService.PlayBlackjackEffect(BlackjackSoundEffect.Hover);
			StandButton.MouseEnter += (_, __) => SoundService.PlayBlackjackEffect(BlackjackSoundEffect.Hover);
			SplitButton.MouseEnter += (_, __) => SoundService.PlayBlackjackEffect(BlackjackSoundEffect.Hover);
			NextRoundButton.MouseEnter += (_, __) => SoundService.PlayBlackjackEffect(BlackjackSoundEffect.Hover);
			BailoutButton.MouseEnter += (_, __) => SoundService.PlayBlackjackEffect(BlackjackSoundEffect.Hover);

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

			_prevSnapshot = null;
			_dealQueue.Clear();
			_activeDeal = null;
			StopAnimLoop();

			if (string.IsNullOrEmpty(roomCode) || string.IsNullOrEmpty(playerId))
			{
				_snapshot = null;
				GameSurface.InvalidateVisual();
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

		// =========================================================
		// ✅ Extracted UI/button enabling logic
		// =========================================================
		private void UpdateButtonsFromUi()
		{
			var ui = _uiSnapshot ?? _snapshot;
			if (ui == null) return;

			bool isHost = string.Equals(_playerId, "P1", StringComparison.OrdinalIgnoreCase);
			int seatedCount = ui.SeatPlayerIds?.Count(pid => !string.IsNullOrEmpty(pid)) ?? 0;

			StartButton.IsEnabled = isHost && ui.Phase == BlackjackPhase.Lobby && seatedCount > 0;

			bool myTurn = ui.CurrentPlayerId == _playerId && ui.Phase == BlackjackPhase.PlayerTurns;
			var me = ui.Players?.FirstOrDefault(p => p.PlayerId == _playerId);

			// bailout
			if (me != null && me.CanBailout)
			{
				BailoutButton.Visibility = Visibility.Visible;
				BailoutButton.IsEnabled = true;
			}
			else
			{
				BailoutButton.Visibility = Visibility.Collapsed;
			}

			SplitButton.IsEnabled = (me != null && me.CanSplit);
			HitButton.IsEnabled = myTurn;
			StandButton.IsEnabled = myTurn;
			NextRoundButton.IsEnabled = isHost && ui.RoundComplete;

			if (ui.Phase != BlackjackPhase.Betting)
				_pendingBet = 0;
		}

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

			// Save previous snapshot for animation diff
			var prev = _snapshot;
			_prevSnapshot = prev;

			// ALWAYS keep latest for animations/cards
			_snapshot = snapshot;

			// Start reveal timeline if needed
			bool prevRevealed = prev?.DealerRevealed ?? false;
			bool curRevealed = snapshot.DealerRevealed;
			if (!prevRevealed && curRevealed)
			{
				// If cards are still animating (or just about to finish), delay the dealer flip start slightly
				_dealerRevealPendingStart = true;
				_dealerRevealHoldTimer = 0f;

				// Ensure render loop is running so the hold timer can count down
				StartAnimLoop();
			}


			// Build deal animations based on differences (may start queue)
			BuildDealAnimations(prev, snapshot);

			// ---- UI snapshot gating ----
			if (VisualBusy)
			{
				_pendingUiSnapshot = snapshot;
				_uiSnapshot ??= snapshot; // Ensure we have something to show
			}
			else
			{
				_uiSnapshot = snapshot;
				_pendingUiSnapshot = null;
			}

			// ✅ Buttons/controls MUST be driven off _uiSnapshot
			UpdateButtonsFromUi();

			GameSurface.InvalidateVisual();
		}

		// =========================================================
		// Hover helpers
		// =========================================================
		private (float sx, float sy) ToSkiaPoint(MouseEventArgs e)
		{
			var p = e.GetPosition(GameSurface);
			float sx = (float)(p.X * GameSurface.CanvasSize.Width / Math.Max(1.0, GameSurface.ActualWidth));
			float sy = (float)(p.Y * GameSurface.CanvasSize.Height / Math.Max(1.0, GameSurface.ActualHeight));
			return (sx, sy);
		}

		private static SKRect ScaleRect(SKRect r, float scale)
		{
			float cx = r.MidX;
			float cy = r.MidY;
			float hw = r.Width * 0.5f * scale;
			float hh = r.Height * 0.5f * scale;
			return new SKRect(cx - hw, cy - hh, cx + hw, cy + hh);
		}

		// =========================================================
		// Button handlers
		// =========================================================

		private async void SplitButton_Click(object sender, RoutedEventArgs e)
		{
			await SendActionAsync(BlackjackActionType.Split);
		}

		private async void StartButton_Click(object sender, RoutedEventArgs e)
		{
			if (_sendAsync == null || _isSocketOpen == null) return;
			if (!_isSocketOpen()) return;
			if (string.IsNullOrEmpty(_roomCode)) return;

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

			try { await _sendAsync(msg); } catch { }
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
			if (_sendAsync == null || _isSocketOpen == null) return;
			if (!_isSocketOpen()) return;
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId)) return;

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

			try { await _sendAsync(msg); } catch { }
		}

		private async void NextRoundButton_Click(object sender, RoutedEventArgs e)
		{
			if (_sendAsync == null || _isSocketOpen == null) return;
			if (!_isSocketOpen()) return;
			if (string.IsNullOrEmpty(_roomCode)) return;

			// host-only action (matches your server logic style)
			bool isHost = string.Equals(_playerId, "P1", StringComparison.OrdinalIgnoreCase);
			if (!isHost) return;

			var payload = new BlackjackNextRoundRequestPayload
			{
				RoomCode = _roomCode
			};

			var msg = new HubMessage
			{
				MessageType = "BlackjackNextRoundRequest",
				RoomCode = _roomCode,
				PlayerId = _playerId ?? "",
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			try { await _sendAsync(msg); } catch { }
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

		// =========================================================
		// Skia rendering
		// =========================================================

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

			// Seats around table
			DrawSeatsAndPlayers(canvas, width, height);

			float centerX = width * 0.5f;

			float phaseY = height * 0.30f;
			float dealerLabelY = phaseY + 34f;
			float deckY = dealerLabelY + 28f;
			float cardsY = deckY + 62f;

			DrawCenterLabels(canvas, width, phaseY, dealerLabelY);

			// Deck rectangle centered
			_deckRect = new SKRect(centerX - 32, deckY, centerX + 32, deckY + 46);
			DrawDeck(canvas, _deckRect);

			// Dealer hand (draw beneath deck)
			DrawDealerHandWithAnimations(canvas, centerX, cardsY);

			// Betting UI
			if (_snapshot.Phase == BlackjackPhase.Betting)
				DrawBettingUi(canvas, width, height);

			// Draw active flying card last (on top)
			DrawActiveDealAnim(canvas);
		}

		private void DrawCenterLabels(SKCanvas canvas, float width, float phaseY, float dealerLabelY)
		{
			var s = _uiSnapshot ?? _snapshot;
			if (s == null) return;

			string phaseText = s.Phase.ToString();

			if (s.Phase == BlackjackPhase.Lobby)
			{
				int dealerCount = s.DealerCards?.Count ?? 0;
				if (dealerCount == 0)
					phaseText = "Choose Seat";
			}

			string dealerText;
			if (s.DealerCards == null || s.DealerCards.Count == 0)
				dealerText = "Dealer: -";
			else if (s.DealerRevealed)
				dealerText = $"Dealer: {s.DealerVisibleValue}";
			else
				dealerText = "Dealer: ?";

			using var glowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 160), IsAntialias = true, TextAlign = SKTextAlign.Center };
			using var phasePaint = new SKPaint { Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
			using var dealerPaint = new SKPaint { Color = new SKColor(210, 230, 255), IsAntialias = true, TextAlign = SKTextAlign.Center };

			using var phaseFont = new SKFont { Size = 34 };
			using var dealerFont = new SKFont { Size = 22 };

			canvas.DrawText(phaseText, width / 2f, phaseY, phaseFont, glowPaint);
			canvas.DrawText(phaseText, width / 2f, phaseY, phaseFont, phasePaint);

			canvas.DrawText(dealerText, width / 2f, dealerLabelY, dealerFont, dealerPaint);
		}


		private void DrawDeck(SKCanvas canvas, SKRect deckRect)
		{
			using var backFill = new SKPaint { Color = new SKColor(30, 30, 90), IsAntialias = true };
			using var backBorder = new SKPaint { Color = new SKColor(150, 200, 255), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };

			for (int i = 2; i >= 0; i--)
			{
				float dx = i * 4f;
				float dy = i * 3f;
				var r = new SKRect(deckRect.Left + dx, deckRect.Top + dy, deckRect.Right + dx, deckRect.Bottom + dy);
				canvas.DrawRoundRect(r, 8, 8, backFill);
				canvas.DrawRoundRect(r, 8, 8, backBorder);
			}
		}

		// =========================================================
		// Hands / Cards
		// =========================================================

		private const float CardW = 40f;
		private const float CardH = 60f;
		private const float CardSpacing = 16f;

		private List<SKRect> ComputeHandCardRects(int cardCount, float centerX, float y)
		{
			var rects = new List<SKRect>(Math.Max(0, cardCount));
			if (cardCount <= 0) return rects;

			float totalWidth = cardCount * CardW + (cardCount - 1) * CardSpacing;
			float startX = centerX - totalWidth / 2f;

			for (int i = 0; i < cardCount; i++)
			{
				float x = startX + i * (CardW + CardSpacing);
				rects.Add(new SKRect(x, y, x + CardW, y + CardH));
			}

			return rects;
		}

		private static string HandKey(string playerId, int handIndex)
	=> $"{playerId}:{handIndex}"; // 0=main, 1=split

		private int GetShown(string key)
			=> _shownCounts.TryGetValue(key, out var n) ? n : 0;

		private void SetShown(string key, int n)
			=> _shownCounts[key] = Math.Max(0, n);

		private void RevealCard(string key, int cardIndex)
		{
			int want = cardIndex + 1;
			if (!_shownCounts.TryGetValue(key, out var cur) || want > cur)
				_shownCounts[key] = want;
		}


		private void DrawDealerHandWithAnimations(SKCanvas canvas, float centerX, float y)
		{
			if (_snapshot == null) return;

			var cards = _snapshot.DealerCards ?? new List<BlackjackCardDto>();
			int shown = GetShown("D");

			bool isFlyingDealer = _activeDeal != null && _activeDeal.OwnerKey == "D";
			int hideBecauseDuplicate = (isFlyingDealer && _activeDeal!.CardIndex < shown) ? 1 : 0;

			int drawCount = Math.Max(0, shown - hideBecauseDuplicate);
			if (drawCount <= 0) return;

			var rects = ComputeHandCardRects(drawCount, centerX, y);

			using var backPaint = new SKPaint { Color = new SKColor(30, 30, 90), IsAntialias = true };
			using var backBorder = new SKPaint { Color = new SKColor(150, 200, 255), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
			using var facePaint = new SKPaint { Color = new SKColor(240, 240, 255), IsAntialias = true };
			using var faceBorder = new SKPaint { Color = new SKColor(30, 30, 40), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
			using var textPaint = new SKPaint { Color = SKColors.Black, TextSize = 18, IsAntialias = true, TextAlign = SKTextAlign.Center };

			for (int i = 0; i < drawCount; i++)
			{
				var card = cards[i];
				var r = rects[i];

				if (i == 1 && _snapshot.DealerRevealed)
					DrawFlipCard(canvas, card, r, backPaint, backBorder, facePaint, faceBorder, textPaint);
				else
					DrawSingleCard(canvas, card, r, backPaint, backBorder, facePaint, faceBorder, textPaint);
			}
		}

		private bool IsDealerHitCardBlocked(DealAnim a)
		{
			if (!string.Equals(a.OwnerKey, "D", StringComparison.OrdinalIgnoreCase))
				return false;

			if (a.CardIndex < 2)
				return false;

			return _dealerRevealPendingStart || _dealerRevealStage != DealerRevealStage.None;
		}

		private void DrawFlipCard(
			SKCanvas canvas,
			BlackjackCardDto card,
			SKRect rect,
			SKPaint backPaint,
			SKPaint backBorder,
			SKPaint facePaint,
			SKPaint faceBorder,
			SKPaint textPaint)
		{
			if (!_dealerFlipActive)
			{
				// If reveal is pending OR we're in the reveal timeline,
				// keep showing the back until flip is truly finished.
				if (_dealerRevealPendingStart ||
					_dealerRevealStage == DealerRevealStage.PreDelay ||
					_dealerRevealStage == DealerRevealStage.Flipping)
				{
					DrawSingleCard(canvas, new BlackjackCardDto { IsFaceDown = true }, rect,
						backPaint, backBorder, facePaint, faceBorder, textPaint);
					return;
				}

				// Only show face once the timeline is finished (PostDelay/None AND not pending)
				DrawSingleCard(canvas, card, rect, backPaint, backBorder, facePaint, faceBorder, textPaint);
				return;
			}

			float t = _dealerFlipT; // 0..1

			// Two-phase flip so motion starts immediately:
			// 0..0.5 : shrink back side from 1 -> 0
			// 0.5..1 : expand face side from 0 -> 1
			bool showFace;
			float scaleX;

			if (t < 0.5f)
			{
				showFace = false;
				float u = t / 0.5f;          // 0..1
				scaleX = 1f - u;             // 1..0
			}
			else
			{
				showFace = true;
				float u = (t - 0.5f) / 0.5f; // 0..1
				scaleX = u;                  // 0..1
			}

			// avoid exact 0 scale (can look like a "pop" on some renderers)
			scaleX = MathF.Max(0.02f, scaleX);

			float cx = rect.MidX;
			float cy = rect.MidY;

			canvas.Save();
			canvas.Translate(cx, cy);
			canvas.Scale(scaleX, 1f);
			canvas.Translate(-cx, -cy);

			if (showFace)
				DrawSingleCard(canvas, card, rect, backPaint, backBorder, facePaint, faceBorder, textPaint);
			else
				DrawSingleCard(canvas, new BlackjackCardDto { IsFaceDown = true }, rect, backPaint, backBorder, facePaint, faceBorder, textPaint);

			canvas.Restore();

		}
		private bool HasUnblockedDealsInQueue()
		{
			foreach (var a in _dealQueue)
			{
				if (!IsDealerHitCardBlocked(a))
					return true;
			}
			return false;
		}

		private void DrawHand(SKCanvas canvas, List<BlackjackCardDto> cards, float centerX, float y)
		{
			if (cards == null || cards.Count == 0)
				return;

			float totalWidth = cards.Count * CardW + (cards.Count - 1) * CardSpacing;
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
				float x = startX + i * (CardW + CardSpacing);
				var cardRect = new SKRect(x, y, x + CardW, y + CardH);

				DrawSingleCard(canvas, cards[i], cardRect, backPaint, backBorder, facePaint, faceBorder, textPaint);
			}
		}

		private void DrawSingleCard(
			SKCanvas canvas,
			BlackjackCardDto card,
			SKRect rect,
			SKPaint backPaint,
			SKPaint backBorder,
			SKPaint facePaint,
			SKPaint faceBorder,
			SKPaint textPaint)
		{
			if (card.IsFaceDown)
			{
				canvas.DrawRoundRect(rect, 6, 6, backPaint);
				canvas.DrawRoundRect(rect, 6, 6, backBorder);
				return;
			}

			canvas.DrawRoundRect(rect, 6, 6, facePaint);
			canvas.DrawRoundRect(rect, 6, 6, faceBorder);

			string rankText = RankToString(card.Rank);
			string suitText = SuitToSymbol(card.Suit);

			bool isRed = card.Suit == 1 || card.Suit == 2;
			textPaint.Color = isRed ? SKColors.Red : SKColors.Black;

			string combined = $"{rankText}{suitText}";
			canvas.DrawText(combined, rect.MidX, rect.MidY + 6, textPaint);
		}

		// =========================================================
		// Seats / Players
		// =========================================================

		private void DrawSeatsAndPlayers(SKCanvas canvas, float width, float height)
		{
			if (_snapshot == null) return;

			var centers = new (float x, float y)[]
			{
		(width * 0.25f, height * 0.20f),
		(width * 0.75f, height * 0.20f),
		(width * 0.25f, height * 0.75f),
		(width * 0.75f, height * 0.75f),
			};

			float seatW = 180f;
			float seatH = 140f;

			for (int seat = 0; seat < 4; seat++)
			{
				var (cx, cy) = centers[seat];

				var baseRect = new SKRect(
					cx - seatW / 2f,
					cy - seatH / 2f,
					cx + seatW / 2f,
					cy + seatH / 2f);

				bool hovered = (seat == _hoverSeatIndex);
				_seatRects[seat] = hovered ? ScaleRect(baseRect, HoverScale) : baseRect;

				DrawSeat(canvas, seat);
			}
		}


		private void DrawSeat(SKCanvas canvas, int seatIndex)
		{
			if (_snapshot == null) return;

			var ui = _uiSnapshot ?? _snapshot; // gated for labels/results
			var live = _snapshot;              // ALWAYS latest for cards

			var rect = _seatRects[seatIndex];
			bool hovered = (seatIndex == _hoverSeatIndex);

			// seat ownership should follow live snapshot (so visuals match animations)
			string? seatPid = null;
			if (live.SeatPlayerIds != null && seatIndex < live.SeatPlayerIds.Length)
				seatPid = live.SeatPlayerIds[seatIndex];

			bool isMySeat =
				!string.IsNullOrEmpty(seatPid) &&
				string.Equals(seatPid, _playerId, StringComparison.OrdinalIgnoreCase);

			var baseFill = new SKColor(0, 60, 40);
			var hoverFill = new SKColor(0, 75, 50);

			using var seatFill = new SKPaint { Color = hovered ? hoverFill : baseFill, IsAntialias = true };
			using var seatBorder = new SKPaint
			{
				Color = new SKColor(120, 220, 180),
				Style = SKPaintStyle.Stroke,
				StrokeWidth = isMySeat ? 4 : 2,
				IsAntialias = true
			};

			if (hovered)
			{
				using var glow = new SKPaint { Color = new SKColor(120, 220, 180, 80), IsAntialias = true };
				var glowRect = ScaleRect(rect, 1.04f);
				canvas.DrawRoundRect(glowRect, 18, 18, glow);
			}

			canvas.DrawRoundRect(rect, 16, 16, seatFill);
			canvas.DrawRoundRect(rect, 16, 16, seatBorder);

			// get player state from live snapshot for cards
			BlackjackPlayerStateDto? liveP = null;
			if (!string.IsNullOrEmpty(seatPid) && live.Players != null)
				liveP = live.Players.FirstOrDefault(x => x.PlayerId == seatPid);

			// get player state from gated snapshot for labels/results
			BlackjackPlayerStateDto? uiP = null;
			if (!string.IsNullOrEmpty(seatPid) && ui.Players != null)
				uiP = ui.Players.FirstOrDefault(x => x.PlayerId == seatPid);

			using var labelPaint = new SKPaint
			{
				Color = SKColors.White,
				TextSize = 16,
				IsAntialias = true,
				TextAlign = SKTextAlign.Center
			};

			string label = string.IsNullOrEmpty(seatPid) ? $"Seat {seatIndex + 1} (Empty)" : $"{seatPid}";
			canvas.DrawText(label, rect.MidX, rect.Top + 24, labelPaint);

			// Empty seat placeholder
			if (liveP == null)
			{
				var placeholder = new List<BlackjackCardDto>
		{
			new BlackjackCardDto { Rank = 0, Suit = 0, IsFaceDown = true },
			new BlackjackCardDto { Rank = 0, Suit = 0, IsFaceDown = true },
		};

				DrawHand(canvas, placeholder, rect.MidX, rect.MidY - 35);
				return;
			}

			// Cards must come from LIVE snapshot for immediacy
			int mainShown = GetShown(HandKey(liveP.PlayerId, 0));
			int splitShown = GetShown(HandKey(liveP.PlayerId, 1));

			bool flyingMain =
				_activeDeal != null &&
				string.Equals(_activeDeal.OwnerKey, HandKey(liveP.PlayerId, 0), StringComparison.OrdinalIgnoreCase);

			bool flyingSplit =
				_activeDeal != null &&
				string.Equals(_activeDeal.OwnerKey, HandKey(liveP.PlayerId, 1), StringComparison.OrdinalIgnoreCase);

			// avoid duplicate draw if flying card index already “revealed”
			int hideDupMain = (flyingMain && _activeDeal!.CardIndex < mainShown) ? 1 : 0;
			int hideDupSplit = (flyingSplit && _activeDeal!.CardIndex < splitShown) ? 1 : 0;

			int mainDrawCount = Math.Max(0, mainShown - hideDupMain);
			int splitDrawCount = Math.Max(0, splitShown - hideDupSplit);

			// positions
			float mainY = rect.MidY - 48f;
			float splitY = rect.MidY + 8f;

			// draw main
			if (mainDrawCount > 0 && liveP.Cards != null)
				DrawHand(canvas, liveP.Cards.Take(mainDrawCount).ToList(), rect.MidX, mainY);

			// draw split (if exists)
			if (liveP.SplitHandCards != null && liveP.SplitHandCards.Count > 0)
			{
				if (splitDrawCount > 0)
					DrawHand(canvas, liveP.SplitHandCards.Take(splitDrawCount).ToList(), rect.MidX, splitY);

				// highlight active hand (use gated snapshot so it doesn’t flicker mid-anim)
				int activeIndex = (uiP ?? liveP).ActiveHandIndex;

				using var hi = new SKPaint
				{
					Color = new SKColor(255, 255, 255, 70),
					Style = SKPaintStyle.Stroke,
					StrokeWidth = 3,
					IsAntialias = true
				};

				var hiRect = activeIndex == 0
					? new SKRect(rect.MidX - 120, mainY - 6, rect.MidX + 120, mainY + CardH + 6)
					: new SKRect(rect.MidX - 120, splitY - 6, rect.MidX + 120, splitY + CardH + 6);

				canvas.DrawRoundRect(hiRect, 10, 10, hi);
			}

			// Info should stay gated (so results/values don’t jump early)
			var infoSrc = uiP ?? liveP;

			string info;
			if (infoSrc.IsSpectatingThisRound)
			{
				info = "Spectating this round";
			}
			else if (infoSrc.HasSplit)
			{
				// Active hand value depends on ActiveHandIndex
				int activeVal = infoSrc.ActiveHandIndex == 0 ? infoSrc.HandValue : infoSrc.SplitHandValue;

				info = $"Main: {infoSrc.HandValue}  Split: {infoSrc.SplitHandValue}  (Active: {activeVal})  Chips: {infoSrc.Chips}";
			}
			else
			{
				info = $"Val: {infoSrc.HandValue}  Chips: {infoSrc.Chips}";
			}

			if (infoSrc.Result != BlackjackResult.Pending)
				info = $"{infoSrc.Result} | {info}";

			canvas.DrawText(info, rect.MidX, rect.Bottom - 18, labelPaint);
		}

		// =========================================================
		// Betting UI
		// =========================================================

		private void DrawBettingUi(SKCanvas canvas, float width, float height)
		{
			if (_snapshot == null || string.IsNullOrEmpty(_playerId)) return;

			var me = _snapshot.Players?.FirstOrDefault(p => p.PlayerId == _playerId);
			if (me == null) return;
			if (!me.IsSeated || me.IsSpectatingThisRound) return;

			float y = height * 0.52f;
			float startX = width * 0.18f;
			float gap = width * 0.11f;
			float r = 26f;

			using var textPaint = new SKPaint
			{
				Color = SKColors.White,
				TextSize = 14,
				IsAntialias = true,
				TextAlign = SKTextAlign.Center
			};

			for (int i = 0; i < _chipDefs.Length; i++)
			{
				float cx = startX + i * gap;
				float cy = y;

				var baseRect = new SKRect(cx - r, cy - r, cx + r, cy + r);
				bool hovered = (i == _hoverChipIndex);
				_chipRects[i] = hovered ? ScaleRect(baseRect, HoverScale) : baseRect;

				var drawRect = _chipRects[i];
				float drawCx = drawRect.MidX;
				float drawCy = drawRect.MidY;
				float drawR = drawRect.Width * 0.5f;

				var color = i switch
				{
					0 => new SKColor(200, 40, 40),
					1 => new SKColor(40, 120, 220),
					2 => new SKColor(40, 200, 90),
					3 => new SKColor(160, 60, 200),
					4 => new SKColor(240, 180, 40),
					_ => new SKColor(230, 230, 230),
				};

				if (hovered)
				{
					using var chipGlow = new SKPaint { Color = new SKColor(255, 255, 255, 70), IsAntialias = true };
					canvas.DrawCircle(drawCx, drawCy, drawR + 6f, chipGlow);
				}

				using var chipPaint = new SKPaint { Color = color, IsAntialias = true };
				using var border = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = hovered ? 4 : 3, IsAntialias = true };

				canvas.DrawCircle(drawCx, drawCy, drawR, chipPaint);
				canvas.DrawCircle(drawCx, drawCy, drawR, border);
				canvas.DrawText(_chipDefs[i].label, drawCx, drawCy + 5, textPaint);
			}

			canvas.DrawText($"Your bet: {_pendingBet}", width * 0.5f, y + 60, textPaint);

			float btnW = 180, btnH = 46;
			var baseBtnRect = new SKRect(width * 0.5f - btnW / 2f, y + 72, width * 0.5f + btnW / 2f, y + 72 + btnH);
			_submitBetRect = _hoverSubmit ? ScaleRect(baseBtnRect, 1.06f) : baseBtnRect;

			bool alreadySubmitted = me.HasSubmittedBet;

			var btnColor = alreadySubmitted
				? new SKColor(60, 60, 60)
				: (_hoverSubmit ? new SKColor(30, 170, 150) : new SKColor(20, 140, 120));

			using var btnFill = new SKPaint { Color = btnColor, IsAntialias = true };
			using var btnBorder = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = _hoverSubmit ? 3 : 2, IsAntialias = true };

			if (_hoverSubmit && !alreadySubmitted)
			{
				using var btnGlow = new SKPaint { Color = new SKColor(255, 255, 255, 45), IsAntialias = true };
				var glowRect = ScaleRect(_submitBetRect, 1.03f);
				canvas.DrawRoundRect(glowRect, 14, 14, btnGlow);
			}

			canvas.DrawRoundRect(_submitBetRect, 12, 12, btnFill);
			canvas.DrawRoundRect(_submitBetRect, 12, 12, btnBorder);

			canvas.DrawText(alreadySubmitted ? "Bet Submitted" : "Submit Bet", _submitBetRect.MidX, _submitBetRect.MidY + 6, textPaint);
		}

		// =========================================================
		// Mouse handling (seats / betting)
		// =========================================================

		private async void GameSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (_snapshot == null) return;
			if (_sendAsync == null || _isSocketOpen == null) return;
			if (!_isSocketOpen()) return;
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId)) return;

			var p = e.GetPosition(GameSurface);
			float sx = (float)(p.X * GameSurface.CanvasSize.Width / Math.Max(1.0, GameSurface.ActualWidth));
			float sy = (float)(p.Y * GameSurface.CanvasSize.Height / Math.Max(1.0, GameSurface.ActualHeight));

			if (_snapshot.Phase == BlackjackPhase.Betting)
			{
				await HandleBettingClickAsync(e);
				return;
			}

			if (_snapshot.Phase != BlackjackPhase.Lobby) return;

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

			var seatPid = (_snapshot.SeatPlayerIds != null && seatIndex < _snapshot.SeatPlayerIds.Length)
				? _snapshot.SeatPlayerIds[seatIndex]
				: null;

			if (!string.IsNullOrEmpty(seatPid) && !string.Equals(seatPid, _playerId, StringComparison.OrdinalIgnoreCase))
				return;

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

		private void GameSurface_MouseMove(object sender, MouseEventArgs e)
		{
			if (_snapshot == null) return;

			var (sx, sy) = ToSkiaPoint(e);

			int newSeat = -1;
			int newChip = -1;
			bool newSubmit = false;

			for (int i = 0; i < _seatRects.Length; i++)
			{
				if (_seatRects[i].Contains(sx, sy))
				{
					newSeat = i;
					break;
				}
			}

			if (_snapshot.Phase == BlackjackPhase.Betting)
			{
				for (int i = 0; i < _chipRects.Length; i++)
				{
					if (_chipRects[i].Contains(sx, sy))
					{
						newChip = i;
						break;
					}
				}

				if (_submitBetRect.Contains(sx, sy))
					newSubmit = true;
			}

			bool changed =
				newSeat != _hoverSeatIndex ||
				newChip != _hoverChipIndex ||
				newSubmit != _hoverSubmit;

			if (!changed) return;
			if (newSeat != _hoverSeatIndex && newSeat >= 0)
				SoundService.PlayBlackjackEffect(BlackjackSoundEffect.Hover);
			_hoverSeatIndex = newSeat;
			_hoverChipIndex = newChip;
			_hoverSubmit = newSubmit;

			GameSurface.InvalidateVisual();
		}

		private void GameSurface_MouseLeave(object sender, MouseEventArgs e)
		{
			if (_hoverSeatIndex == -1 && _hoverChipIndex == -1 && !_hoverSubmit)
				return;

			_hoverSeatIndex = -1;
			_hoverChipIndex = -1;
			_hoverSubmit = false;

			GameSurface.InvalidateVisual();
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

			var p = e.GetPosition(GameSurface);
			float sx = (float)(p.X * GameSurface.CanvasSize.Width / Math.Max(1.0, GameSurface.ActualWidth));
			float sy = (float)(p.Y * GameSurface.CanvasSize.Height / Math.Max(1.0, GameSurface.ActualHeight));

			if (_submitBetRect.Contains(sx, sy))
			{
				if (_pendingBet <= 0) return;
				SoundService.PlayBlackjackEffect(BlackjackSoundEffect.PokerChips);

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

			for (int i = 0; i < _chipRects.Length; i++)
			{
				if (!_chipRects[i].Contains(sx, sy)) continue;

				int add = _chipDefs[i].value;

				if (add == -1)
				{
					_pendingBet = me.Chips;
				}
				else
				{
					_pendingBet += add;
					if (_pendingBet > me.Chips) _pendingBet = me.Chips;
				}
				SoundService.PlayBlackjackEffect(BlackjackSoundEffect.PokerChips);
				GameSurface.InvalidateVisual();
				return;
			}
		}

		// =========================================================
		// Deal animation system
		// =========================================================

		private void BuildDealAnimations(BlackjackSnapshotPayload? prev, BlackjackSnapshotPayload cur)
		{
			if (prev == null) return;

			bool AnyCountDecreased()
			{
				int prevDealer = prev.DealerCards?.Count ?? 0;
				int curDealer = cur.DealerCards?.Count ?? 0;
				if (curDealer < prevDealer) return true;

				if (prev.Players != null)
				{
					foreach (var pp in prev.Players)
					{
						var cp = cur.Players?.FirstOrDefault(x => x.PlayerId == pp.PlayerId);

						int prevMain = pp.Cards?.Count ?? 0;
						int curMain = cp?.Cards?.Count ?? 0;
						if (curMain < prevMain) return true;

						int prevSplit = pp.SplitHandCards?.Count ?? 0;
						int curSplit = cp?.SplitHandCards?.Count ?? 0;
						if (curSplit < prevSplit) return true;
					}
				}
				return false;
			}

			bool phaseLooksLikeNewDeal =
				prev.Phase != cur.Phase &&
				cur.Phase == BlackjackPhase.Dealing &&
				(prev.Phase == BlackjackPhase.Betting || prev.Phase == BlackjackPhase.Lobby || prev.Phase == BlackjackPhase.RoundResults);

			bool newDeal = AnyCountDecreased() || phaseLooksLikeNewDeal;

			if (newDeal)
			{
				_dealQueue.Clear();
				_activeDeal = null;

				_shownCounts.Clear();
				SetShown("D", 0);

				if (cur.Players != null)
				{
					foreach (var p in cur.Players)
					{
						SetShown(HandKey(p.PlayerId, 0), 0);
						SetShown(HandKey(p.PlayerId, 1), 0);
					}
				}

				// dealer first
				if (cur.DealerCards != null)
				{
					for (int i = 0; i < cur.DealerCards.Count; i++)
						_dealQueue.Enqueue(new DealAnim { OwnerKey = "D", CardIndex = i, Card = cur.DealerCards[i] });
				}

				// then players (main + split)
				if (cur.Players != null)
				{
					foreach (var p in cur.Players)
					{
						if (p.Cards != null)
						{
							for (int i = 0; i < p.Cards.Count; i++)
								_dealQueue.Enqueue(new DealAnim
								{
									OwnerKey = HandKey(p.PlayerId, 0),
									CardIndex = i,
									Card = p.Cards[i]
								});
						}

						if (p.SplitHandCards != null && p.SplitHandCards.Count > 0)
						{
							for (int i = 0; i < p.SplitHandCards.Count; i++)
								_dealQueue.Enqueue(new DealAnim
								{
									OwnerKey = HandKey(p.PlayerId, 1),
									CardIndex = i,
									Card = p.SplitHandCards[i]
								});
						}
					}
				}

				if (_dealQueue.Count > 0)
				{
					StartNextDeal();
					StartAnimLoop();
				}
				return;
			}

			// incremental adds

			int prevDealerCount = prev.DealerCards?.Count ?? 0;
			int curDealerCount = cur.DealerCards?.Count ?? 0;
			if (curDealerCount > prevDealerCount && cur.DealerCards != null)
			{
				for (int i = prevDealerCount; i < curDealerCount; i++)
					_dealQueue.Enqueue(new DealAnim { OwnerKey = "D", CardIndex = i, Card = cur.DealerCards[i] });
			}

			if (cur.Players != null)
			{
				foreach (var p in cur.Players)
				{
					var pp = prev.Players?.FirstOrDefault(x => x.PlayerId == p.PlayerId);

					int prevMain = pp?.Cards?.Count ?? 0;
					int curMain = p.Cards?.Count ?? 0;
					if (curMain > prevMain && p.Cards != null)
					{
						for (int i = prevMain; i < curMain; i++)
							_dealQueue.Enqueue(new DealAnim
							{
								OwnerKey = HandKey(p.PlayerId, 0),
								CardIndex = i,
								Card = p.Cards[i]
							});
					}

					int prevSplit = pp?.SplitHandCards?.Count ?? 0;
					int curSplit = p.SplitHandCards?.Count ?? 0;
					if (curSplit > prevSplit && p.SplitHandCards != null)
					{
						for (int i = prevSplit; i < curSplit; i++)
							_dealQueue.Enqueue(new DealAnim
							{
								OwnerKey = HandKey(p.PlayerId, 1),
								CardIndex = i,
								Card = p.SplitHandCards[i]
							});
					}
				}
			}

			if (_activeDeal == null && _dealQueue.Count > 0)
			{
				StartNextDeal();
				StartAnimLoop();
			}
		}


		private void StartNextDeal()
		{
			if (_dealQueue.Count == 0)
			{
				_activeDeal = null;
				return;
			}

			// Try to find a deal we are allowed to animate right now.
			// If the front is a blocked dealer-hit card, rotate it to the back
			// so player cards can still animate and reveal.
			int attempts = _dealQueue.Count;

			while (attempts-- > 0 && _dealQueue.Count > 0)
			{
				var next = _dealQueue.Peek();

				if (IsDealerHitCardBlocked(next))
				{
					// rotate blocked card to the back
					var blocked = _dealQueue.Dequeue();
					_dealQueue.Enqueue(blocked);
					continue;
				}

				// Found an unblocked anim
				_activeDeal = _dealQueue.Dequeue();

				_activeDeal.T = 0f;
				_activeDeal.StartRect = _deckRect;
				_activeDeal.EndRect = ComputeCardTargetRect(_activeDeal.OwnerKey, _activeDeal.CardIndex);
				SoundService.PlayBlackjackEffect(BlackjackSoundEffect.CardDealt);
				return;
			}

			// If we got here, everything remaining is blocked right now
			_activeDeal = null;
		}


		private SKRect ComputeCardTargetRect(string ownerKey, int cardIndex)
		{
			if (_snapshot == null) return _deckRect;

			float width = (float)GameSurface.CanvasSize.Width;
			float height = (float)GameSurface.CanvasSize.Height;

			float centerX = width * 0.5f;

			float phaseY = height * 0.30f;
			float dealerLabelY = phaseY + 34f;
			float deckY = dealerLabelY + 28f;
			float cardsY = deckY + 62f;

			if (ownerKey == "D")
			{
				var rects = ComputeHandCardRects((_snapshot.DealerCards?.Count ?? 0), centerX, cardsY);
				return (cardIndex >= 0 && cardIndex < rects.Count) ? rects[cardIndex] : _deckRect;
			}

			// parse "P1:0"
			string pid = ownerKey;
			int handIndex = 0;

			int colon = ownerKey.IndexOf(':');
			if (colon >= 0)
			{
				pid = ownerKey.Substring(0, colon);
				int.TryParse(ownerKey.Substring(colon + 1), out handIndex);
			}

			if (_snapshot.SeatPlayerIds != null)
			{
				for (int seat = 0; seat < _snapshot.SeatPlayerIds.Length && seat < 4; seat++)
				{
					var seatPid = _snapshot.SeatPlayerIds[seat];
					if (string.IsNullOrEmpty(seatPid)) continue;
					if (!string.Equals(seatPid, pid, StringComparison.OrdinalIgnoreCase)) continue;

					var seatRect = _seatRects[seat];

					// main hand row + split hand row
					float mainY = seatRect.MidY - 48f;
					float splitY = seatRect.MidY + 8f;

					var player = _snapshot.Players?.FirstOrDefault(x => x.PlayerId == pid);
					int count = 0;

					if (handIndex == 0)
					{
						count = player?.Cards?.Count ?? 0;
						var rects = ComputeHandCardRects(count, seatRect.MidX, mainY);
						return (cardIndex >= 0 && cardIndex < rects.Count) ? rects[cardIndex] : _deckRect;
					}
					else
					{
						count = player?.SplitHandCards?.Count ?? 0;
						var rects = ComputeHandCardRects(count, seatRect.MidX, splitY);
						return (cardIndex >= 0 && cardIndex < rects.Count) ? rects[cardIndex] : _deckRect;
					}
				}
			}

			return _deckRect;
		}


		private void DrawActiveDealAnim(SKCanvas canvas)
		{
			if (_activeDeal == null) return;

			float t = EaseOutCubic(_activeDeal.T);

			var a = _activeDeal.StartRect;
			var b = _activeDeal.EndRect;

			float l = Lerp(a.Left, b.Left, t);
			float top = Lerp(a.Top, b.Top, t);
			float r = Lerp(a.Right, b.Right, t);
			float bot = Lerp(a.Bottom, b.Bottom, t);

			var rect = new SKRect(l, top, r, bot);

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

			using (var glow = new SKPaint { Color = new SKColor(255, 255, 255, 50), IsAntialias = true })
			{
				var glowRect = ScaleRect(rect, 1.10f);
				canvas.DrawRoundRect(glowRect, 8, 8, glow);
			}

			DrawSingleCard(canvas, _activeDeal.Card, rect, backPaint, backBorder, facePaint, faceBorder, textPaint);
		}

		private void StartAnimLoop()
		{
			if (_animLoopOn) return;
			_animLoopOn = true;
			_lastRenderTicks = Stopwatch.GetTimestamp();
			CompositionTarget.Rendering += OnRenderTick;
		}

		private void StopAnimLoop()
		{
			if (!_animLoopOn) return;
			_animLoopOn = false;
			CompositionTarget.Rendering -= OnRenderTick;
		}

		private void OnRenderTick(object? sender, EventArgs e)
		{
			if (_activeDeal == null)
			{
				if (_dealQueue.Count > 0)
				{
					StartNextDeal();
				}
				else
				{
					if (!VisualBusy)
					{
						StopAnimLoop();
						return;
					}
				}
			}

			long now = Stopwatch.GetTimestamp();
			double dt = (now - _lastRenderTicks) / (double)Stopwatch.Frequency;
			_lastRenderTicks = now;

			dt = Math.Min(dt, 0.05);

			// If dealer reveal is pending, wait a tiny moment after the last deal finishes
			if (_dealerRevealPendingStart)
			{
				// "Busy" here means: any deal anim still flying or queued (dealer stage hasn't started yet)
				bool dealingBusy = (_activeDeal != null) || HasUnblockedDealsInQueue();


				if (dealingBusy)
				{
					// Still dealing -> keep waiting, timer stays at 0 so we count AFTER it finishes
					_dealerRevealHoldTimer = 0f;
				}
				else
				{
					// Deals are done -> start counting the hold
					_dealerRevealHoldTimer += (float)dt;

					if (_dealerRevealHoldTimer >= DealerRevealHoldAfterLastDeal)
					{
						_dealerRevealPendingStart = false;
						_dealerRevealHoldTimer = 0f;

						// Now begin your normal reveal timeline
						_dealerRevealStage = DealerRevealStage.PreDelay;
						_dealerRevealTimer = 0f;
						_dealerFlipActive = false;
						_dealerFlipT = 0f;
					}
				}
			}

			if (_dealerRevealStage != DealerRevealStage.None)
			{
				_dealerRevealTimer += (float)dt;

				if (_dealerRevealStage == DealerRevealStage.PreDelay)
				{
					if (_dealerRevealTimer >= DealerFlipPreDelay)
					{
						_dealerRevealStage = DealerRevealStage.Flipping;
						_dealerRevealTimer = 0f;

						_dealerFlipActive = true;
						_dealerFlipT = 0f;

						SoundService.PlayBlackjackEffect(BlackjackSoundEffect.FlipCard);
					}
				}
				else if (_dealerRevealStage == DealerRevealStage.Flipping)
				{
					_dealerFlipT += (float)(dt / DealerFlipDuration);
					if (_dealerFlipT >= 1f)
					{
						_dealerFlipT = 1f;
						_dealerFlipActive = false;

						_dealerRevealStage = DealerRevealStage.PostDelay;
						_dealerRevealTimer = 0f;
					}
				}
				else if (_dealerRevealStage == DealerRevealStage.PostDelay)
				{
					if (_dealerRevealTimer >= DealerFlipPostDelay)
					{
						_dealerRevealStage = DealerRevealStage.None;
						_dealerRevealTimer = 0f;
					}
				}
			}
			else
			{
				_dealerFlipActive = false;
			}

			if (_activeDeal != null)
			{
				_activeDeal.T += (float)(dt / Math.Max(0.0001, _activeDeal.Duration));

				if (_activeDeal.T >= 1f)
				{
					RevealCard(_activeDeal.OwnerKey, _activeDeal.CardIndex);

					_activeDeal = null;
					StartNextDeal();
				}
			}

			// ✅ When visuals end, apply the pending UI snapshot AND refresh buttons
			if (!VisualBusy && _pendingUiSnapshot != null)
			{
				_uiSnapshot = _pendingUiSnapshot;
				_pendingUiSnapshot = null;

				UpdateButtonsFromUi(); // <-- this is the missing piece
			}

			if (!VisualBusy)
			{
				StopAnimLoop();
			}

			GameSurface.InvalidateVisual();
		}

		private static float Lerp(float a, float b, float t) => a + (b - a) * t;

		private static float EaseOutCubic(float t)
		{
			if (t <= 0f) return 0f;
			if (t >= 1f) return 1f;
			float u = 1f - t;
			return 1f - (u * u * u);
		}

		// =========================================================
		// Rank/Suit
		// =========================================================

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

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameContracts;
using GameLogic.War;
using GameLogic.CardGames;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace GameClient.Wpf.GameClients
{
	public partial class WarGameClient : UserControl, IGameClient
	{
		// ==== IGameClient plumbing ==========================================

		public GameType GameType => GameType.War;
		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		public void SetConnection(Func<HubMessage, Task> sendAsync, Func<bool> isSocketOpen)
		{
			_sendAsync = sendAsync;
			_isSocketOpen = isSocketOpen;
		}

		public void OnRoomChanged(string? roomCode, string? playerId)
		{
			// Offline game: treat this as "restart"
			_engine.ResetGame();
			SyncUiFromEngine();
			SkSurface.InvalidateVisual();
		}

		public bool TryHandleMessage(HubMessage msg) => false;
		public void OnKeyDown(KeyEventArgs e) { }
		public void OnKeyUp(KeyEventArgs e) { }

		// ==== Engine + timing ===============================================

		private readonly WarEngine _engine = new();
		private readonly DispatcherTimer _timer;
		private DateTime _lastTickTime;

		// For hit-testing player deck
		private SKRect _leftDeckRect;
		private SKRect _rightDeckRect;

		// purely visual constant
		private const float WarStackOffsetY = 12f;

		public WarGameClient()
		{
			InitializeComponent();

			_timer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
			};
			_timer.Tick += Timer_Tick;

			Loaded += (_, __) =>
			{
				_lastTickTime = DateTime.UtcNow;
				_engine.ResetGame();
				SyncUiFromEngine();
				_timer.Start();
			};

			Unloaded += (_, __) => _timer.Stop();
		}

		private void Timer_Tick(object? sender, EventArgs e)
		{
			var now = DateTime.UtcNow;
			var dt = (float)(now - _lastTickTime).TotalSeconds;
			_lastTickTime = now;

			_engine.Tick(dt);
			SyncUiFromEngine();
			SkSurface.InvalidateVisual();
		}

		private void SyncUiFromEngine()
		{
			SideStatusText.Text = _engine.SideStatusText;
			BottomStatusText.Text = _engine.BottomStatusText;
			ShuffleButton.Visibility = _engine.ShuffleUnlocked
				? Visibility.Visible
				: Visibility.Collapsed;
		}

		// ==== UI events =====================================================

		private void LeftSideButton_Click(object sender, RoutedEventArgs e)
		{
			_engine.SelectSide(playerOnLeft: true);
			SyncUiFromEngine();
			SkSurface.InvalidateVisual();
		}

		private void RightSideButton_Click(object sender, RoutedEventArgs e)
		{
			_engine.SelectSide(playerOnLeft: false);
			SyncUiFromEngine();
			SkSurface.InvalidateVisual();
		}

		private void ShuffleButton_Click(object sender, RoutedEventArgs e)
		{
			if (_engine.TryShufflePlayerDeck())
			{
				SyncUiFromEngine();
				SkSurface.InvalidateVisual();
			}
		}

		private void SkSurface_MouseDown(object sender, MouseButtonEventArgs e)
		{
			var pos = e.GetPosition(SkSurface);
			float x = (float)pos.X;
			float y = (float)pos.Y;

			// Which deck is the player's?
			SKRect playerDeckRect = _engine.PlayerOnLeft ? _leftDeckRect : _rightDeckRect;

			if (playerDeckRect.Contains(x, y))
			{
				_engine.OnPlayerDeckClicked();
				SyncUiFromEngine();
				SkSurface.InvalidateVisual();
			}
		}

		// ==== Skia drawing ==================================================

		private void SkSurface_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			var info = e.Info;

			canvas.Clear(new SKColor(8, 12, 30)); // dark-ish background

			float width = info.Width;
			float height = info.Height;

			float centerX = width / 2f;
			float centerY = height / 2f;

			using var playerBorderPaint = new SKPaint
			{
				Color = SKColors.Lime,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 4,
				IsAntialias = true
			};

			using var opponentBorderPaint = new SKPaint
			{
				Color = SKColors.Red,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 4,
				IsAntialias = true
			};

			using var sideLabelPaint = new SKPaint
			{
				Color = new SKColor(230, 230, 255),
				IsAntialias = true,
				TextSize = 24,
				TextAlign = SKTextAlign.Center,
				Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold)
			};

			// Positions
			var centerDeckRect = new SKRect(centerX - 40, centerY - 55, centerX + 40, centerY + 55);

			_leftDeckRect = new SKRect(60, centerY - 55, 140, centerY + 55);
			_rightDeckRect = new SKRect(width - 140, centerY - 55, width - 60, centerY + 55);

			var leftDeckRect = _leftDeckRect;
			var rightDeckRect = _rightDeckRect;

			var leftBattleRect = new SKRect(centerX - 160, centerY - 70, centerX - 60, centerY + 70);
			var rightBattleRect = new SKRect(centerX + 60, centerY - 70, centerX + 160, centerY + 70);

			// Side borders + labels
			if (_engine.SideSelected)
			{
				float marginX = 1f;
				float marginTop = 1f;
				float marginBottom = 5f;

				var leftSideRect = new SKRect(
					marginX,
					marginTop + 30,
					width / 2f - 5f,
					height - marginBottom);

				var rightSideRect = new SKRect(
					width / 2f + 5f,
					marginTop + 30,
					width - marginX,
					height - marginBottom);

				bool playerIsLeft = _engine.PlayerOnLeft;

				canvas.DrawRect(playerIsLeft ? leftSideRect : rightSideRect, playerBorderPaint);
				canvas.DrawRect(playerIsLeft ? rightSideRect : leftSideRect, opponentBorderPaint);

				string leftLabel = playerIsLeft ? "YOUR SIDE" : "OPPONENT SIDE";
				string rightLabel = playerIsLeft ? "OPPONENT SIDE" : "YOUR SIDE";

				float labelY = marginTop + 20f;

				canvas.DrawText(leftLabel, leftSideRect.MidX, labelY, sideLabelPaint);
				canvas.DrawText(rightLabel, rightSideRect.MidX, labelY, sideLabelPaint);
			}

			using var deckBackPaint = new SKPaint
			{
				Color = new SKColor(30, 40, 80),
				IsAntialias = true
			};
			using var deckBorderPaint = new SKPaint
			{
				Color = new SKColor(120, 200, 255),
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 3,
				IsAntialias = true
			};
			using var cardFrontPaint = new SKPaint
			{
				Color = new SKColor(240, 240, 255),
				IsAntialias = true
			};
			using var cardTextPaint = new SKPaint
			{
				Color = SKColors.Black,
				IsAntialias = true,
				TextSize = 32,
				TextAlign = SKTextAlign.Center,
				Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold)
			};
			using var smallTextPaint = new SKPaint
			{
				Color = new SKColor(180, 200, 220),
				IsAntialias = true,
				TextSize = 20,
				TextAlign = SKTextAlign.Center
			};
			using var overlayPaint = new SKPaint
			{
				Color = new SKColor(255, 255, 255),
				IsAntialias = true,
				TextSize = 42,
				TextAlign = SKTextAlign.Center,
				Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold)
			};

			// Center deck while dealing
			if (_engine.State == WarEngine.WarState.Dealing && _engine.CenterDeckCount > 0)
			{
				canvas.DrawRoundRect(centerDeckRect, 8, 8, deckBackPaint);
				canvas.DrawRoundRect(centerDeckRect, 8, 8, deckBorderPaint);
				canvas.DrawText($"{_engine.CenterDeckCount}",
					centerDeckRect.MidX,
					centerDeckRect.MidY + 10,
					smallTextPaint);
			}

			// Deck backs + counts
			DrawDeckWithCount(canvas, leftDeckRect, _engine.LeftDeckCount, deckBackPaint, deckBorderPaint, smallTextPaint);
			DrawDeckWithCount(canvas, rightDeckRect, _engine.RightDeckCount, deckBackPaint, deckBorderPaint, smallTextPaint);

			// Dealing animation
			if (_engine.State == WarEngine.WarState.Dealing && _engine.HasDealCardInFlight)
			{
				float t = _engine.DealProgress;
				var targetRect = _engine.DealToLeftNext ? leftDeckRect : rightDeckRect;
				var dealRect = LerpRect(centerDeckRect, targetRect, t);
				DrawCardBack(canvas, dealRect, deckBackPaint, deckBorderPaint);
			}

			// Base positions for showdown cards (shift down in war)
			var battleBaseLeft = leftBattleRect;
			var battleBaseRight = rightBattleRect;
			if (_engine.WarFaceDownPlaced > 0)
			{
				battleBaseLeft = OffsetRect(leftBattleRect, 0, _engine.WarFaceDownPlaced * WarStackOffsetY);
				battleBaseRight = OffsetRect(rightBattleRect, 0, _engine.WarFaceDownPlaced * WarStackOffsetY);
			}

			var currentLeftCardRect = battleBaseLeft;
			var currentRightCardRect = battleBaseRight;

			var phase = _engine.CurrentBattlePhase;
			float animT = _engine.BattleAnimProgress;

			if (phase != WarEngine.BattleAnimPhase.None)
			{
				if (phase == WarEngine.BattleAnimPhase.MoveToCenter)
				{
					float t = animT;
					currentLeftCardRect = LerpRect(leftDeckRect, battleBaseLeft, t);
					currentRightCardRect = LerpRect(rightDeckRect, battleBaseRight, t);
				}
				else if (phase == WarEngine.BattleAnimPhase.FaceDownIdle
					|| phase == WarEngine.BattleAnimPhase.Flip
					|| phase == WarEngine.BattleAnimPhase.AfterFlipPause)
				{
					currentLeftCardRect = battleBaseLeft;
					currentRightCardRect = battleBaseRight;
				}
				else if (phase == WarEngine.BattleAnimPhase.MoveToWinner)
				{
					float t = animT;
					var winnerRect = _engine.PendingWinnerIsLeft ? leftDeckRect : rightDeckRect;
					currentLeftCardRect = LerpRect(battleBaseLeft, winnerRect, t);
					currentRightCardRect = LerpRect(battleBaseRight, winnerRect, t);
				}
			}

			// War face-down stacks
			if (_engine.WarFaceDownPlaced > 0)
			{
				for (int i = 0; i < _engine.WarFaceDownPlaced; i++)
				{
					float yOffset = (_engine.WarFaceDownPlaced - 1 - i) * WarStackOffsetY;
					var leftStackRect = OffsetRect(battleBaseLeft, 0, -yOffset);
					var rightStackRect = OffsetRect(battleBaseRight, 0, -yOffset);

					DrawCardBack(canvas, leftStackRect, deckBackPaint, deckBorderPaint);
					DrawCardBack(canvas, rightStackRect, deckBackPaint, deckBorderPaint);
				}
			}

			// Battle cards
			if (_engine.LeftFaceUp.HasValue && phase != WarEngine.BattleAnimPhase.None)
			{
				DrawBattleCard(
					canvas,
					phase,
					animT,
					currentLeftCardRect,
					_engine.LeftFaceUp.Value,
					deckBackPaint,
					deckBorderPaint,
					cardFrontPaint,
					cardTextPaint);
			}

			if (_engine.RightFaceUp.HasValue && phase != WarEngine.BattleAnimPhase.None)
			{
				DrawBattleCard(
					canvas,
					phase,
					animT,
					currentRightCardRect,
					_engine.RightFaceUp.Value,
					deckBackPaint,
					deckBorderPaint,
					cardFrontPaint,
					cardTextPaint);
			}

			// Countdown overlay
			if (_engine.State == WarEngine.WarState.Countdown && _engine.CountdownValue >= 1f)
			{
				string text = ((int)_engine.CountdownValue).ToString();
				canvas.DrawText(text, centerX, centerY - 180, overlayPaint);
			}

			// Round result overlay
			if (_engine.State == WarEngine.WarState.RoundResult ||
				_engine.State == WarEngine.WarState.GameOver)
			{
				DrawRoundResultOverlay(canvas, leftDeckRect, rightDeckRect, overlayPaint);
			}
		}

		// ==== drawing helpers ==============================================

		private static void DrawDeckWithCount(
			SKCanvas canvas,
			SKRect rect,
			int count,
			SKPaint backPaint,
			SKPaint borderPaint,
			SKPaint textPaint)
		{
			if (count <= 0)
			{
				canvas.DrawRoundRect(rect, 8, 8, borderPaint);
				return;
			}

			var offset = 2f;
			var shadowRect = new SKRect(rect.Left + offset, rect.Top + offset, rect.Right + offset, rect.Bottom + offset);
			canvas.DrawRoundRect(shadowRect, 8, 8, backPaint);

			canvas.DrawRoundRect(rect, 8, 8, backPaint);
			canvas.DrawRoundRect(rect, 8, 8, borderPaint);

			canvas.DrawText(count.ToString(), rect.MidX, rect.Bottom + 22, textPaint);
		}

		private static void DrawCardFace(
			SKCanvas canvas,
			SKRect rect,
			Card card,
			SKPaint fillPaint,
			SKPaint borderPaint,
			SKPaint textPaint)
		{
			canvas.DrawRoundRect(rect, 10, 10, fillPaint);
			canvas.DrawRoundRect(rect, 10, 10, borderPaint);

			string rankText = card.Rank switch
			{
				CardRank.Jack => "J",
				CardRank.Queen => "Q",
				CardRank.King => "K",
				CardRank.Ace => "A",
				_ => ((int)card.Rank).ToString()
			};

			string suitText = card.Suit switch
			{
				CardSuit.Clubs => "♣",
				CardSuit.Diamonds => "♦",
				CardSuit.Hearts => "♥",
				CardSuit.Spades => "♠",
				_ => "?"
			};

			textPaint.Color = (card.Suit == CardSuit.Hearts || card.Suit == CardSuit.Diamonds)
				? SKColors.Red
				: SKColors.Black;

			string combined = $"{rankText}{suitText}";
			canvas.DrawText(combined, rect.MidX, rect.MidY + 10, textPaint);
		}

		private static void DrawCardBack(
			SKCanvas canvas,
			SKRect rect,
			SKPaint backPaint,
			SKPaint borderPaint)
		{
			canvas.DrawRoundRect(rect, 10, 10, backPaint);
			canvas.DrawRoundRect(rect, 10, 10, borderPaint);
		}

		private static void DrawBattleCard(
			SKCanvas canvas,
			WarEngine.BattleAnimPhase phase,
			float animT,
			SKRect baseRect,
			Card card,
			SKPaint backPaint,
			SKPaint borderPaint,
			SKPaint frontPaint,
			SKPaint textPaint)
		{
			if (phase == WarEngine.BattleAnimPhase.None)
				return;

			switch (phase)
			{
				case WarEngine.BattleAnimPhase.MoveToCenter:
				case WarEngine.BattleAnimPhase.FaceDownIdle:
					DrawCardBack(canvas, baseRect, backPaint, borderPaint);
					break;

				case WarEngine.BattleAnimPhase.Flip:
					{
						float t = animT;
						float scaleX = Math.Abs(1f - 2f * t);
						float cx = baseRect.MidX;
						float halfW = (baseRect.Width / 2f) * scaleX;
						var scaledRect = new SKRect(cx - halfW, baseRect.Top, cx + halfW, baseRect.Bottom);

						if (t < 0.5f)
							DrawCardBack(canvas, scaledRect, backPaint, borderPaint);
						else
							DrawCardFace(canvas, scaledRect, card, frontPaint, borderPaint, textPaint);

						break;
					}

				case WarEngine.BattleAnimPhase.AfterFlipPause:
				case WarEngine.BattleAnimPhase.MoveToWinner:
					DrawCardFace(canvas, baseRect, card, frontPaint, borderPaint, textPaint);
					break;
			}
		}

		private void DrawRoundResultOverlay(SKCanvas canvas, SKRect leftDeckRect, SKRect rightDeckRect, SKPaint paint)
		{
			string leftText = "";
			string rightText = "";

			switch (_engine.LastRoundWinner)
			{
				case WarEngine.RoundWinner.Player:
					if (_engine.PlayerOnLeft)
					{
						leftText = "YOU WIN";
						rightText = "YOU LOSE";
					}
					else
					{
						leftText = "YOU LOSE";
						rightText = "YOU WIN";
					}
					break;

				case WarEngine.RoundWinner.Opponent:
					if (_engine.PlayerOnLeft)
					{
						leftText = "YOU LOSE";
						rightText = "YOU WIN";
					}
					else
					{
						leftText = "YOU WIN";
						rightText = "YOU LOSE";
					}
					break;

				case WarEngine.RoundWinner.Tie:
					leftText = "TIE";
					rightText = "TIE";
					break;

				case WarEngine.RoundWinner.None:
					return;
			}

			if (!string.IsNullOrEmpty(leftText))
				canvas.DrawText(leftText, leftDeckRect.MidX, leftDeckRect.Top - 20, paint);

			if (!string.IsNullOrEmpty(rightText))
				canvas.DrawText(rightText, rightDeckRect.MidX, rightDeckRect.Top - 20, paint);
		}

		private static SKRect LerpRect(SKRect a, SKRect b, float t)
		{
			return new SKRect(
				a.Left + (b.Left - a.Left) * t,
				a.Top + (b.Top - a.Top) * t,
				a.Right + (b.Right - a.Right) * t,
				a.Bottom + (b.Bottom - a.Bottom) * t
			);
		}

		private static SKRect OffsetRect(SKRect r, float dx, float dy)
		{
			return new SKRect(
				r.Left + dx,
				r.Top + dy,
				r.Right + dx,
				r.Bottom + dy
			);
		}
	}
}

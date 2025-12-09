using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameContracts;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace GameClient.Wpf.GameClients
{
	public partial class WarOnlineGameClient : UserControl, IGameClient
	{
		// ==== IGameClient plumbing ==========================================

		public GameType GameType => GameType.WarOnline;
		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		private string? _roomCode;
		private string? _playerId;

		// Last snapshots from server
		private WarLobbyStatePayload? _lobby;
		private WarStatePayload? _state;

		// For hit-testing player deck
		private SKRect _leftDeckRect;
		private SKRect _rightDeckRect;

		// purely visual constant (same as offline)
		private const float WarStackOffsetY = 12f;

		public WarOnlineGameClient()
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
			// Only care about room changes for "full reset"
			bool roomChanged = _roomCode != roomCode;

			_roomCode = roomCode;
			_playerId = playerId;

			// Same room → just recompute using whatever lobby/state we already have
			if (!roomChanged)
			{
				if (_lobby != null)
				{
					UpdateSideAndStatusText();
				}

				SkSurface.InvalidateVisual();
				return;
			}

			// Real room change (or leaving room) → full reset
			_lobby = null;
			_state = null;

			LeftSideButton.IsEnabled = false;
			RightSideButton.IsEnabled = false;

			SideStatusText.Text = "No side selected.";
			BottomStatusText.Text = "Waiting for players...";
			ShuffleButton.Visibility = Visibility.Collapsed;

			SkSurface.InvalidateVisual();
		}



		public bool TryHandleMessage(HubMessage msg)
		{
			// Ensure we're on the UI thread before touching any WPF controls
			if (!Dispatcher.CheckAccess())
			{
				Dispatcher.Invoke(() => TryHandleMessage(msg));
				return true; // We handled it via the UI thread
			}
			if(string.IsNullOrEmpty(_roomCode))
				_roomCode = msg.RoomCode;

			if (msg.RoomCode != _roomCode)
				return false;

			switch (msg.MessageType)
			{
				case "WarLobbyState":
					HandleLobbyMessage(msg.PayloadJson);
					return true;

				case "WarState":
					HandleStateMessage(msg.PayloadJson);
					return true;

				default:
					return false;
			}
		}

		public void OnKeyDown(KeyEventArgs e) { }
		public void OnKeyUp(KeyEventArgs e) { }

		// ==== Message handlers ==============================================

		private void HandleLobbyMessage(string json)
		{
			WarLobbyStatePayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<WarLobbyStatePayload>(json);
			}
			catch
			{
				return;
			}
			if (payload == null) return;

			_lobby = payload;

			UpdateSideAndStatusText();
			SkSurface.InvalidateVisual();
		}

		private void HandleStateMessage(string json)
		{
			WarStatePayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<WarStatePayload>(json);
			}
			catch
			{
				return;
			}
			if (payload == null) return;

			_state = payload;

			// Shuffle is only allowed once unlocked; for now only LEFT can shuffle,
			// matching the server-side WarGameHandler/WarEngine behavior.
			bool localIsLeft = IsLocalLeft();
			ShuffleButton.Visibility =
				_state.ShuffleUnlocked && localIsLeft
					? Visibility.Visible
					: Visibility.Collapsed;

			UpdateSideAndStatusText();
			SkSurface.InvalidateVisual();
		}

		// ==== UI helpers ====================================================

		private bool HasLocalSide()
		{
			if (_lobby == null || string.IsNullOrEmpty(_playerId))
				return false;

			return _lobby.LeftPlayerId == _playerId || _lobby.RightPlayerId == _playerId;
		}

		private bool IsLocalLeft()
		{
			if (_lobby == null || string.IsNullOrEmpty(_playerId))
				return false;

			return _lobby.LeftPlayerId == _playerId;
		}

		private bool IsGameStarted() => _lobby?.GameStarted == true;

		private void UpdateSideAndStatusText()
		{
			bool gameStarted = IsGameStarted();

			// ======================================================
			// 1) BUTTON ENABLE STATE
			//    - only depends on lobby + connected players + taken
			//    - DOES NOT depend on _playerId
			// ======================================================
			if (_lobby == null)
			{
				LeftSideButton.IsEnabled = false;
				RightSideButton.IsEnabled = false;
			}
			else
			{
				bool leftTaken = !string.IsNullOrEmpty(_lobby.LeftPlayerId);
				bool rightTaken = !string.IsNullOrEmpty(_lobby.RightPlayerId);
				int connected = _lobby.ConnectedPlayers;


				if (gameStarted || connected < 2)
				{
					// Before 2 players OR once game has started → no picking sides
					LeftSideButton.IsEnabled = false;
					RightSideButton.IsEnabled = false;
				}
				else
				{
					// Lobby, 2+ players:
					// - any free side is clickable by either client
					// - once a side is taken, that button becomes disabled for everyone
					LeftSideButton.IsEnabled = !leftTaken;
					RightSideButton.IsEnabled = !rightTaken;
				}
			}

			// ======================================================
			// 2) TEXT + "YOUR SIDE" LOGIC
			//    - this DOES depend on _playerId
			//    - if we don't know _playerId yet, just show neutral text
			// ======================================================
			if (_lobby == null || string.IsNullOrEmpty(_playerId))
			{
				SideStatusText.Text = "No side selected.";

				if (!gameStarted)
				{
					if (_lobby != null && _lobby.ConnectedPlayers < 2)
						BottomStatusText.Text = "Waiting for another player to join...";
					else
						BottomStatusText.Text = "Choose Left or Right.";
				}
				else
				{
					BottomStatusText.Text = "Spectating game.";
				}

				return;
			}

			// From here on, we know our PlayerId
			bool hasSide = HasLocalSide();
			bool isLeft = IsLocalLeft();

			if (!hasSide)
			{
				SideStatusText.Text = "No side selected.";

				if (!gameStarted)
					BottomStatusText.Text = "Choose Left or Right.";
				else
					BottomStatusText.Text = "Spectating game.";

				return;
			}

			SideStatusText.Text = isLeft ? "You are LEFT side." : "You are RIGHT side.";

			if (!gameStarted)
			{
				BottomStatusText.Text = "Waiting for other player to choose side...";
				return;
			}

			// Game started + we know our side: use _state to drive detailed text
			if (_state == null)
			{
				BottomStatusText.Text = "Game started. Waiting for first state...";
				return;
			}

			switch (_state.State)
			{
				case WarNetworkState.Dealing:
					BottomStatusText.Text = "Dealing cards...";
					break;
				case WarNetworkState.WaitingForClick:
					BottomStatusText.Text = hasSide ? "Click your deck to draw!" : "Waiting for players...";
					break;
				case WarNetworkState.Countdown:
					BottomStatusText.Text = "Battle!";
					break;
				case WarNetworkState.ShowingBattle:
					BottomStatusText.Text = "Showing battle...";
					break;
				case WarNetworkState.WarFaceDown:
					BottomStatusText.Text = "WAR! Stacking face-down cards...";
					break;
				case WarNetworkState.RoundResult:
					BottomStatusText.Text = "Round result...";
					break;
				case WarNetworkState.GameOver:
					BottomStatusText.Text = "Game over. Click Restart in GameHub to play again.";
					break;
				default:
					BottomStatusText.Text = "Waiting...";
					break;
			}
		}




		// ==== Button clicks =================================================

		private async void LeftSideButton_Click(object sender, RoutedEventArgs e)
		{
			if (_sendAsync == null || string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId))
				return;

			var payload = new WarSelectSidePayload
			{
				Side = WarSide.Left,
				PlayerId = _playerId
			};

			var msg = new HubMessage
			{
				MessageType = "WarSelectSide",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}

		private async void RightSideButton_Click(object sender, RoutedEventArgs e)
		{
			if (_sendAsync == null || string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId))
				return;

			var payload = new WarSelectSidePayload
			{
				Side = WarSide.Right,
				PlayerId = _playerId
			};

			var msg = new HubMessage
			{
				MessageType = "WarSelectSide",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}

		private async void ShuffleButton_Click(object sender, RoutedEventArgs e)
		{
			if (_sendAsync == null || string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId))
				return;

			var payload = new WarShuffleRequestPayload
			{
				PlayerId = _playerId
			};

			var msg = new HubMessage
			{
				MessageType = "WarShuffleRequest",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}

		// Deck click → WarReady
		private async void SkSurface_MouseDown(object sender, MouseButtonEventArgs e)
		{
			if (_sendAsync == null || string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId))
				return;

			if (!IsGameStarted() || _state == null)
				return;

			if (!HasLocalSide())
				return; // spectators can't ready up

			var pos = e.GetPosition(SkSurface);
			float x = (float)pos.X;
			float y = (float)pos.Y;

			bool isLeft = IsLocalLeft();
			SKRect playerDeckRect = isLeft ? _leftDeckRect : _rightDeckRect;

			if (!playerDeckRect.Contains(x, y))
				return;

			var payload = new WarReadyPayload
			{
				PlayerId = _playerId
			};

			var msg = new HubMessage
			{
				MessageType = "WarReady",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
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

			// Determine local side
			bool hasSide = HasLocalSide();
			bool localIsLeft = IsLocalLeft();

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

			// Side borders + labels (YOUR SIDE / OPPONENT SIDE) – same visuals as offline,
			// but computed from lobby & playerId so colors match across clients.
			if (hasSide)
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

				canvas.DrawRect(localIsLeft ? leftSideRect : rightSideRect, playerBorderPaint);
				canvas.DrawRect(localIsLeft ? rightSideRect : leftSideRect, opponentBorderPaint);

				string leftLabel = localIsLeft ? "YOUR SIDE" : "OPPONENT SIDE";
				string rightLabel = localIsLeft ? "OPPONENT SIDE" : "YOUR SIDE";

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

			if (_state == null)
			{
				// No state yet → just show empty decks with counts 0
				DrawDeckWithCount(canvas, leftDeckRect, 0, deckBackPaint, deckBorderPaint, smallTextPaint);
				DrawDeckWithCount(canvas, rightDeckRect, 0, deckBackPaint, deckBorderPaint, smallTextPaint);
				return;
			}

			// Convenience locals
			var s = _state;
			var networkState = s.State;

			// Center deck while dealing
			if (networkState == WarNetworkState.Dealing && s.CenterDeckCount > 0)
			{
				canvas.DrawRoundRect(centerDeckRect, 8, 8, deckBackPaint);
				canvas.DrawRoundRect(centerDeckRect, 8, 8, deckBorderPaint);
				canvas.DrawText($"{s.CenterDeckCount}",
					centerDeckRect.MidX,
					centerDeckRect.MidY + 10,
					smallTextPaint);
			}

			// Deck backs + counts
			DrawDeckWithCount(canvas, leftDeckRect, s.LeftDeckCount, deckBackPaint, deckBorderPaint, smallTextPaint);
			DrawDeckWithCount(canvas, rightDeckRect, s.RightDeckCount, deckBackPaint, deckBorderPaint, smallTextPaint);

			// Base "battle" positions (same as offline)
			var battleBaseLeft = leftBattleRect;
			var battleBaseRight = rightBattleRect;

			// Special handling while waiting for clicks:
			if (networkState == WarNetworkState.WaitingForClick)
			{
				// 1) LEFT side ready → animate only left card
				if (s.LeftReady && s.LeftFaceUp.HasValue)
				{
					float t = s.LeftReadyProgress;          // 0..1, from server
					var rect = LerpRect(leftDeckRect, battleBaseLeft, t);

					// Draw as face-down while moving to center
					DrawCardBack(canvas, rect, deckBackPaint, deckBorderPaint);
				}

				// 2) RIGHT side ready → animate only right card
				if (s.RightReady && s.RightFaceUp.HasValue)
				{
					float t = s.RightReadyProgress;         // 0..1
					var rect = LerpRect(rightDeckRect, battleBaseRight, t);

					DrawCardBack(canvas, rect, deckBackPaint, deckBorderPaint);
				}

				// 3) When not ready yet, their card just isn't visible at center.

				// We skip the normal battle card drawing in this state:
				return;
			}

			// Dealing animation
			if (networkState == WarNetworkState.Dealing && s.HasDealCardInFlight)
			{
				float t = s.DealProgress;
				var targetRect = s.DealToLeftNext ? leftDeckRect : rightDeckRect;
				var dealRect = LerpRect(centerDeckRect, targetRect, t);
				DrawCardBack(canvas, dealRect, deckBackPaint, deckBorderPaint);
			}

			// Base positions for showdown cards (shift down in war)
			if (s.WarFaceDownPlaced > 0)
			{
				battleBaseLeft = OffsetRect(leftBattleRect, 0, s.WarFaceDownPlaced * WarStackOffsetY);
				battleBaseRight = OffsetRect(rightBattleRect, 0, s.WarFaceDownPlaced * WarStackOffsetY);
			}


			var currentLeftCardRect = battleBaseLeft;
			var currentRightCardRect = battleBaseRight;

			var phase = (GameLogic.War.WarEngine.BattleAnimPhase)s.BattlePhase;
			float animT = s.BattleAnimProgress;

			if (phase != GameLogic.War.WarEngine.BattleAnimPhase.None)
			{
				if (phase == GameLogic.War.WarEngine.BattleAnimPhase.MoveToCenter)
				{
					float t = animT;
					currentLeftCardRect = LerpRect(leftDeckRect, battleBaseLeft, t);
					currentRightCardRect = LerpRect(rightDeckRect, battleBaseRight, t);
				}
				else if (phase == GameLogic.War.WarEngine.BattleAnimPhase.FaceDownIdle
					|| phase == GameLogic.War.WarEngine.BattleAnimPhase.Flip
					|| phase == GameLogic.War.WarEngine.BattleAnimPhase.AfterFlipPause)
				{
					currentLeftCardRect = battleBaseLeft;
					currentRightCardRect = battleBaseRight;
				}
				else if (phase == GameLogic.War.WarEngine.BattleAnimPhase.MoveToWinner)
				{
					// NOTE: server already decided winner in deck counts; here we just
					// animate back to left/right visually. We can't know which side
					// got the pile from payload right now, so we just move back to decks.
					// (If you want exact winner animation, we can add a bool to payload.)
					float t = animT;
					// For now, move left card back to left deck, right card to right deck
					currentLeftCardRect = LerpRect(battleBaseLeft, leftDeckRect, t);
					currentRightCardRect = LerpRect(battleBaseRight, rightDeckRect, t);
				}
			}

			// War face-down stacks
			if (s.WarFaceDownPlaced > 0)
			{
				for (int i = 0; i < s.WarFaceDownPlaced; i++)
				{
					float yOffset = (s.WarFaceDownPlaced - 1 - i) * WarStackOffsetY;
					var leftStackRect = OffsetRect(battleBaseLeft, 0, -yOffset);
					var rightStackRect = OffsetRect(battleBaseRight, 0, -yOffset);

					DrawCardBack(canvas, leftStackRect, deckBackPaint, deckBorderPaint);
					DrawCardBack(canvas, rightStackRect, deckBackPaint, deckBorderPaint);
				}
			}

			// Battle cards
			if (s.LeftFaceUp.HasValue && phase != GameLogic.War.WarEngine.BattleAnimPhase.None)
			{
				DrawBattleCard(
					canvas,
					phase,
					animT,
					currentLeftCardRect,
					s.LeftFaceUp.Value,
					deckBackPaint,
					deckBorderPaint,
					cardFrontPaint,
					cardTextPaint);
			}

			if (s.RightFaceUp.HasValue && phase != GameLogic.War.WarEngine.BattleAnimPhase.None)
			{
				DrawBattleCard(
					canvas,
					phase,
					animT,
					currentRightCardRect,
					s.RightFaceUp.Value,
					deckBackPaint,
					deckBorderPaint,
					cardFrontPaint,
					cardTextPaint);
			}

			// Countdown overlay
			if (networkState == WarNetworkState.Countdown && s.CountdownValue >= 1f)
			{
				string text = ((int)s.CountdownValue).ToString();
				canvas.DrawText(text, centerX, centerY - 180, overlayPaint);
			}

			// Round result overlay
			if (networkState == WarNetworkState.RoundResult ||
				networkState == WarNetworkState.GameOver)
			{
				DrawRoundResultOverlay(canvas, leftDeckRect, rightDeckRect, overlayPaint, s.LastRoundWinner, localIsLeft);
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
			WarCardDto card,
			SKPaint fillPaint,
			SKPaint borderPaint,
			SKPaint textPaint)
		{
			canvas.DrawRoundRect(rect, 10, 10, fillPaint);
			canvas.DrawRoundRect(rect, 10, 10, borderPaint);

			string rankText = card.Rank switch
			{
				11 => "J",
				12 => "Q",
				13 => "K",
				14 => "A",
				_ => card.Rank.ToString()
			};

			string suitText = card.Suit switch
			{
				0 => "♣",
				1 => "♦",
				2 => "♥",
				3 => "♠",
				_ => "?"
			};

			// Hearts/Diamonds red, others black
			bool isRed = card.Suit == 1 || card.Suit == 2;
			textPaint.Color = isRed ? SKColors.Red : SKColors.Black;

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
			GameLogic.War.WarEngine.BattleAnimPhase phase,
			float animT,
			SKRect baseRect,
			WarCardDto card,
			SKPaint backPaint,
			SKPaint borderPaint,
			SKPaint frontPaint,
			SKPaint textPaint)
		{
			if (phase == GameLogic.War.WarEngine.BattleAnimPhase.None)
				return;

			switch (phase)
			{
				case GameLogic.War.WarEngine.BattleAnimPhase.MoveToCenter:
				case GameLogic.War.WarEngine.BattleAnimPhase.FaceDownIdle:
					DrawCardBack(canvas, baseRect, backPaint, borderPaint);
					break;

				case GameLogic.War.WarEngine.BattleAnimPhase.Flip:
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

				case GameLogic.War.WarEngine.BattleAnimPhase.AfterFlipPause:
				case GameLogic.War.WarEngine.BattleAnimPhase.MoveToWinner:
					DrawCardFace(canvas, baseRect, card, frontPaint, borderPaint, textPaint);
					break;
			}
		}

		private static void DrawRoundResultOverlay(
			SKCanvas canvas,
			SKRect leftDeckRect,
			SKRect rightDeckRect,
			SKPaint paint,
			WarNetworkRoundWinner winner,
			bool localIsLeft)
		{
			string leftText = "";
			string rightText = "";

			switch (winner)
			{
				case WarNetworkRoundWinner.Left:
					if (localIsLeft)
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

				case WarNetworkRoundWinner.Right:
					if (localIsLeft)
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

				case WarNetworkRoundWinner.Tie:
					leftText = "TIE";
					rightText = "TIE";
					break;

				case WarNetworkRoundWinner.None:
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

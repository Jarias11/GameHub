using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameContracts;
using SkiaSharp;
using SkiaSharp.Views.WPF;

namespace GameClient.Wpf.GameClients
{
	public partial class UnoGameClient : UserControl, IGameClient
	{
		public GameType GameType => GameType.Uno;
		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		private string? _roomCode;
		private string? _playerId;

		private UnoStatePayload? _state;
		private string _lastError = "";

		// Selected card indices (client-side)
		private readonly System.Collections.Generic.List<int> _selectedOrder = new(); // preserves click order
		private readonly System.Collections.Generic.HashSet<int> _selectedSet = new(); // fast contains


		// For drawing selected outlines
		private static readonly SKColor SelectedOutline = new SKColor(255, 255, 255);


		// click-hit regions for your hand
		private SKRect[] _handRects = Array.Empty<SKRect>();

		// if we played a wild without color, we store pending play? (engine supports choose-color separately)
		private bool _awaitingColorChoice;

		private int _hoverHandIndex = -1;
		private int _lastHoverHandIndex = -1;

		public UnoGameClient()
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

			_state = null;
			_lastError = "";
			_awaitingColorChoice = false;
			_selectedSet.Clear();
			_selectedOrder.Clear();

			UpdateUi();
			Canvas.InvalidateVisual();
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			if (msg.MessageType == "UnoState")
			{
				var payload = JsonSerializer.Deserialize<UnoStatePayload>(msg.PayloadJson);
				if (payload == null) return true;

				_state = payload;
				// prevent stale selections if hand changed
				_selectedSet.Clear();
				_selectedOrder.Clear();

				//_lastError = "";

				_awaitingColorChoice =
					payload.Phase == UnoTurnPhase.AwaitingWildColorChoice &&
					payload.CurrentPlayerId == _playerId;

				Dispatcher.BeginInvoke(new Action(() =>
				{
					ErrorText.Text = "";
					UpdateUi();
					Canvas.InvalidateVisual();
				}));

				return true;
			}

			if (msg.MessageType == "UnoError")
			{
				var payload = JsonSerializer.Deserialize<UnoErrorPayload>(msg.PayloadJson);
				if (payload == null) return true;

				_lastError = payload.Message;

				Dispatcher.BeginInvoke(new Action(() =>
				{
					ErrorText.Text = _lastError;
					// optional: UpdateUi(); if error affects buttons
				}));

				return true;
			}

			return false;
		}


		public void OnKeyDown(KeyEventArgs e) { }
		public void OnKeyUp(KeyEventArgs e) { }

		// =========================
		// UI actions
		// =========================

		private async void Start_Click(object sender, RoutedEventArgs e)
		{
			if (!CanSend()) return;
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId)) return;

			var msg = new HubMessage
			{
				MessageType = "UnoStart",
				RoomCode = _roomCode!,
				PlayerId = _playerId!,
				PayloadJson = JsonSerializer.Serialize(new UnoStartGamePayload { RoomCode = _roomCode! })
			};
			await _sendAsync!(msg);
		}

		private async void Draw_Click(object sender, RoutedEventArgs e)
		{
			if (!CanSend()) return;
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId)) return;

			var msg = new HubMessage
			{
				MessageType = "UnoDraw",
				RoomCode = _roomCode!,
				PlayerId = _playerId!,
				PayloadJson = JsonSerializer.Serialize(new UnoDrawPayload())
			};
			await _sendAsync!(msg);
		}

		private async void Uno_Click(object sender, RoutedEventArgs e)
		{
			if (!CanSend()) return;
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId)) return;

			var msg = new HubMessage
			{
				MessageType = "UnoCallUno",
				RoomCode = _roomCode!,
				PlayerId = _playerId!,
				PayloadJson = JsonSerializer.Serialize(new UnoCallUnoPayload { IsSayingUno = true })
			};
			await _sendAsync!(msg);
		}

		private async Task SendPlayCardAsync(int handIndex, UnoCardColor? chosenColor)
		{
			if (!CanSend()) return;
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId)) return;

			var payload = new UnoPlayCardPayload
			{
				HandIndex = handIndex,
				ChosenColor = chosenColor
			};

			var msg = new HubMessage
			{
				MessageType = "UnoPlayCard",
				RoomCode = _roomCode!,
				PlayerId = _playerId!,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync!(msg);
		}

		private async Task SendChooseColorAsync(UnoCardColor color)
		{
			if (!CanSend()) return;
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId)) return;

			var msg = new HubMessage
			{
				MessageType = "UnoChooseColor",
				RoomCode = _roomCode!,
				PlayerId = _playerId!,
				PayloadJson = JsonSerializer.Serialize(new UnoChooseColorPayload { ChosenColor = color })
			};

			await _sendAsync!(msg);
		}

		private void PickRed_Click(object sender, RoutedEventArgs e) => _ = PickColor(UnoCardColor.Red);
		private void PickYellow_Click(object sender, RoutedEventArgs e) => _ = PickColor(UnoCardColor.Yellow);
		private void PickGreen_Click(object sender, RoutedEventArgs e) => _ = PickColor(UnoCardColor.Green);
		private void PickBlue_Click(object sender, RoutedEventArgs e) => _ = PickColor(UnoCardColor.Blue);

		private async Task PickColor(UnoCardColor c)
		{
			ColorPickerOverlay.Visibility = Visibility.Collapsed;
			_awaitingColorChoice = false;
			await SendChooseColorAsync(c);
		}

		// =========================
		// Drawing / input
		// =========================

		private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (_state == null) return;
			if (!_state.IsYourTurn) return;
			if (_awaitingColorChoice) return;

			var p = e.GetPosition(Canvas);
			float x = (float)p.X;
			float y = (float)p.Y;

			for (int i = _handRects.Length - 1; i >= 0; i--) // check topmost first
			{
				if (!_handRects[i].Contains(x, y))
					continue;
				if (_selectedSet.Contains(i))
				{
					_selectedSet.Remove(i);
					_selectedOrder.Remove(i); // removes first occurrence (there will only be one)
				}
				else
				{
					_selectedSet.Add(i);
					_selectedOrder.Add(i); // append => preserves selection order
				}
				UpdateUi();
				Canvas.InvalidateVisual();
				break;
			}
		}
		private void Canvas_MouseMove(object sender, MouseEventArgs e)
		{
			if (_state == null) return;

			var p = e.GetPosition(Canvas);
			float x = (float)p.X;
			float y = (float)p.Y;

			int hover = -1;

			// IMPORTANT: check from topmost to bottommost (rightmost last drawn)
			for (int i = _handRects.Length - 1; i >= 0; i--)
			{
				if (_handRects[i].Contains(x, y))
				{
					hover = i;
					break;
				}
			}

			_hoverHandIndex = hover;

			if (_hoverHandIndex != _lastHoverHandIndex)
			{
				_lastHoverHandIndex = _hoverHandIndex;
				Canvas.Cursor = (_hoverHandIndex >= 0) ? Cursors.Hand : Cursors.Arrow;
				Canvas.InvalidateVisual();
			}
		}

		private void Canvas_MouseLeave(object sender, MouseEventArgs e)
		{
			_hoverHandIndex = -1;
			_lastHoverHandIndex = -1;
			Canvas.Cursor = Cursors.Arrow;
			Canvas.InvalidateVisual();
		}



		private void Canvas_PaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			canvas.Clear(new SKColor(20, 20, 20));

			if (_state == null)
			{
				DrawCenteredText(canvas, e.Info.Width, e.Info.Height, "UNO: create/join a room, then press Start", 18);
				return;
			}

			int w = e.Info.Width;
			int h = e.Info.Height;

			float pad = 16f;

			// Top area: discard + info
			float topH = 140f;
			DrawTopInfo(canvas, pad, pad, w - pad * 2, topH);

			// Reserve bottom area for YOUR hand
			float handAreaH = 200f;                 // tweak to taste
			float handTop = h - pad - handAreaH;    // bottom-anchored

			// Opponents can use the space between top and your hand
			DrawOpponentsHands(canvas, pad, pad, w - pad * 2, handTop - pad);

			// Your hand at bottom
			DrawHand(canvas, pad, handTop, w - pad * 2, handAreaH);
		}

		private void DrawOpponentsHands(SKCanvas canvas, float x, float y, float width, float height)
		{
			if (_state == null) return;
			if (string.IsNullOrEmpty(_playerId)) return;

			var order = _state.PlayersInOrder;
			if (order == null || order.Count < 2) return;

			int youIndex = order.IndexOf(_playerId);
			if (youIndex < 0) return;

			// quick lookup for hand counts
			int GetHandCount(string pid)
			{
				var p = _state.PlayersPublic.FirstOrDefault(pp => pp.PlayerId == pid);
				return p?.HandCount ?? 0;
			}

			// Card back size for opponents
			float cardW = 55f;
			float cardH = cardW * 1.4f;

			var namePaint = new SKPaint { Color = SKColors.White, IsAntialias = true, TextSize = 14 };
			var smallPaint = new SKPaint { Color = new SKColor(200, 200, 200), IsAntialias = true, TextSize = 12 };

			int n = order.Count;

			for (int abs = 0; abs < n; abs++)
			{
				if (abs == youIndex) continue;

				string pid = order[abs];
				int count = GetHandCount(pid);

				// relative seat (clockwise)
				int rel = (abs - youIndex + n) % n; // 1..n-1

				// Where to draw this opponent’s hand
				float handX, handY;
				float maxSpan;

				// 2 players: opponent is "top"
				if (n == 2)
				{
					rel = 2; // force top
				}

				if (rel == 1) // left
				{
					handX = x + 10;
					handY = y + height * 0.55f;
					maxSpan = height * 0.40f;
					DrawVerticalBackStack(canvas, handX, handY, cardW, cardH, count, maxSpan);

					canvas.DrawText($"{pid} ({count})", handX, handY - 10, namePaint);
				}
				else if (rel == 2) // top
				{
					handX = x + width * 0.25f;
					handY = y + 90;                 // <-- moved DOWN
					maxSpan = width * 0.50f;

					DrawHorizontalBackStack(canvas, handX, handY, cardW, cardH, count, maxSpan);

					canvas.DrawText($"{pid} ({count})", handX, handY + cardH + 16, smallPaint);
				}

				else if (rel == 3) // right (4 players)
				{
					handX = x + width - cardW - 10;
					handY = y + height * 0.55f;
					maxSpan = height * 0.40f;
					DrawVerticalBackStack(canvas, handX, handY, cardW, cardH, count, maxSpan);

					// text to the left of the stack
					float tx = handX - 110;
					canvas.DrawText($"{pid} ({count})", tx, handY - 10, namePaint);
				}
			}
		}

		private void DrawHorizontalBackStack(SKCanvas canvas, float startX, float startY, float cardW, float cardH, int count, float maxSpan)
		{
			if (count <= 0) return;

			float step = (count <= 1) ? 0 : (maxSpan - cardW) / (count - 1);
			step = Math.Clamp(step, 10f, cardW * 0.60f);

			for (int i = 0; i < count; i++)
			{
				float cx = startX + i * step;
				DrawCardBack(canvas, cx, startY, cardW, cardH);
			}
		}

		private void DrawVerticalBackStack(SKCanvas canvas, float startX, float startY, float cardW, float cardH, int count, float maxSpan)
		{
			if (count <= 0) return;

			float step = (count <= 1) ? 0 : (maxSpan - cardH) / (count - 1);
			step = Math.Clamp(step, 10f, cardH * 0.35f);

			for (int i = 0; i < count; i++)
			{
				float cy = startY + i * step;
				DrawCardBack(canvas, startX, cy, cardW, cardH);
			}
		}

		private async void PlaySelected_Click(object sender, RoutedEventArgs e)
		{
			if (_state == null) return;
			if (!_state.IsYourTurn) return;
			if (_awaitingColorChoice) return;
			if (_selectedOrder.Count == 0) return;



			// Validate order: first must be playable vs active/top
			if (_state.TopDiscard == null)
			{
				ErrorText.Text = "No discard card to play on yet.";
				return;
			}

			var firstIdx = _selectedOrder[0];
			var first = _state.YourHand[firstIdx];

			if (!IsPlayableFirst(first, _state.TopDiscard, _state.ActiveColor))
			{
				ErrorText.Text = "Your FIRST selected card is not playable. Pick a card that matches the active color or the top value.";
				return;
			}

			// Your rule: after first, all must be same VALUE as first (other colors ok)
			for (int i = 1; i < _selectedOrder.Count; i++)
			{
				var c = _state.YourHand[_selectedOrder[i]];
				if (c.Value != first.Value)
				{
					ErrorText.Text = "After the first card, you can only stack cards with the SAME value.";
					return;
				}
			}

			// Send EXACT selection order (do NOT sort)
			var indicesToSend = _selectedOrder.ToList();

			_selectedSet.Clear();
			_selectedOrder.Clear();
			UpdateUi();
			Canvas.InvalidateVisual();

			await SendPlayCardsAsync(indicesToSend, chosenColor: null);
		}



		private void DrawTopInfo(SKCanvas canvas, float x, float y, float width, float height)
		{
			var titlePaint = new SKPaint { Color = SKColors.White, IsAntialias = true, TextSize = 22 };
			var infoPaint = new SKPaint { Color = new SKColor(220, 220, 220), IsAntialias = true, TextSize = 14 };
			var smallPaint = new SKPaint { Color = new SKColor(200, 200, 200), IsAntialias = true, TextSize = 12 };

			canvas.DrawText($"Turn: {_state!.CurrentPlayerId}  |  Active: {_state.ActiveColor}", x, y + 24, titlePaint);

			string penalty = _state.PendingDrawCount > 0
				? $"PENALTY: +{_state.PendingDrawCount} ({_state.PendingDrawType})"
				: "Penalty: none";

			canvas.DrawText(penalty, x, y + 52, infoPaint);

			string deckLine = $"Deck: {_state.DeckCount}  |  Discard: {_state.DiscardCount}  |  Dir: {(_state.Direction == 1 ? "CW" : "CCW")}";
			canvas.DrawText(deckLine, x, y + 74, smallPaint);

			if (!string.IsNullOrEmpty(_state.WinnerPlayerId))
			{
				var winPaint = new SKPaint { Color = SKColors.Gold, IsAntialias = true, TextSize = 18 };
				canvas.DrawText($"WINNER: {_state.WinnerPlayerId}", x, y + 102, winPaint);
			}

			// Card sizes
			float cardW = 110;
			float cardH = 150;

			// Positions
			float deckX = x + width - (cardW * 2) - 20; // deck left
			float deckY = y + 8;

			float discardX = x + width - cardW;         // discard right
			float discardY = y + 8;

			// Draw deck as a small stack (face down)
			int deckLayers = Math.Min(3, Math.Max(0, _state!.DeckCount)); // 0..3
			for (int i = deckLayers - 1; i >= 0; i--)
			{
				DrawCardBack(canvas, deckX + i * 3, deckY - i * 3, cardW, cardH);
			}


			// Draw discard as a stack: 2-3 shadows + top face-up
			int discLayers = Math.Min(3, Math.Max(0, _state.DiscardCount - 1));
			for (int i = discLayers; i >= 1; i--)
			{
				// draw “shadow” backs underneath just for stack feel
				DrawCardBack(canvas, discardX + i * 3, discardY - i * 3, cardW, cardH);
			}

			// Top discard face up
			DrawCard(canvas, discardX, discardY, cardW, cardH, _state.TopDiscard, isFaceUp: true);
			canvas.DrawText($"Pile ({_state.DiscardCount})", discardX, discardY + cardH + 16, smallPaint);

		}

		private void DrawHand(SKCanvas canvas, float x, float y, float width, float height)
		{
			var labelPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, TextSize = 16 };

			canvas.DrawText($"Your hand ({_state!.YourHand.Count})", x, y - 10, labelPaint);

			int count = _state.YourHand.Count;
			_handRects = new SKRect[count];

			if (count == 0) return;


			// Spread across available width (overlap only if needed)
			float cardW = MathF.Min(95f, width * 0.14f);     // slightly larger default
			float cardH = cardW * 1.4f;

			float step;
			if (count <= 1)
			{
				step = 0;
			}
			else
			{
				// This is the key: use MAX, not MIN, and use (count-1)
				step = (width - cardW) / (count - 1);

				// If hand is huge, allow overlap but keep it readable
				float minStep = 22f;
				float maxStep = cardW * 0.75f; // avoid huge gaps
				step = Math.Clamp(step, minStep, maxStep);
			}

			float startX = x;
			float cy = y + 10;


			// Build rects first (same as you already do)
			for (int i = 0; i < count; i++)
			{
				float cx = startX + i * step;
				var rect = new SKRect(cx, cy, cx + cardW, cy + cardH);
				_handRects[i] = rect;
			}

			// Draw all cards EXCEPT hovered
			for (int i = 0; i < count; i++)
			{
				if (i == _hoverHandIndex) continue;

				bool selected = _selectedSet.Contains(i);
				bool highlight = _state.IsYourTurn && !_awaitingColorChoice;

				DrawCard(canvas,
					_handRects[i].Left, _handRects[i].Top,
					_handRects[i].Width, _handRects[i].Height,
					_state.YourHand[i],
					isFaceUp: true,
					highlightClickable: highlight,
					isSelected: selected);
			}

			// Draw hovered card LAST so it’s on top (and lift it slightly)
			if (_hoverHandIndex >= 0 && _hoverHandIndex < count)
			{
				int i = _hoverHandIndex;

				bool selected = _selectedSet.Contains(i);
				bool highlight = _state.IsYourTurn && !_awaitingColorChoice;

				float lift = 18f; // tweak to taste
				var r = _handRects[i];

				DrawCard(canvas,
					r.Left, r.Top - lift,
					r.Width, r.Height,
					_state.YourHand[i],
					isFaceUp: true,
					highlightClickable: true,   // make it feel interactive
					isSelected: selected);
			}

		}
		private async Task SendPlayCardsAsync(System.Collections.Generic.List<int> indices, UnoCardColor? chosenColor)
		{
			if (!CanSend()) return;
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId)) return;

			var payload = new UnoPlayCardsPayload
			{
				HandIndices = indices,
				ChosenColor = chosenColor
			};

			var msg = new HubMessage
			{
				MessageType = "UnoPlayCards",
				RoomCode = _roomCode!,
				PlayerId = _playerId!,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync!(msg);
		}


		private static void DrawCard(SKCanvas canvas, float x, float y, float w, float h, UnoCardDto? card, bool isFaceUp, bool highlightClickable = false, bool isSelected = false)
		{
			var r = new SKRect(x, y, x + w, y + h);

			var bg = new SKPaint { Color = new SKColor(35, 35, 35), IsAntialias = true };
			var border = new SKPaint
			{
				Color = isSelected ? SelectedOutline : new SKColor(90, 90, 90),
				IsAntialias = true,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = isSelected ? 4 : 2
			};

			if (highlightClickable && !isSelected)
				border.Color = new SKColor(180, 180, 180);

			canvas.DrawRoundRect(r, 10, 10, bg);
			canvas.DrawRoundRect(r, 10, 10, border);

			if (!isFaceUp || card == null) return;

			var fill = new SKPaint { Color = ToSk(card.Color), IsAntialias = true };
			var inner = new SKRect(r.Left + 8, r.Top + 8, r.Right - 8, r.Bottom - 8);
			canvas.DrawRoundRect(inner, 8, 8, fill);

			var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, TextSize = MathF.Max(12, w * 0.18f) };

			string label = card.Value switch
			{
				UnoCardValue.DrawTwo => "+2",
				UnoCardValue.WildDrawFour => "+4",
				UnoCardValue.Reverse => "REV",
				UnoCardValue.Skip => "SKIP",
				UnoCardValue.Wild => "WILD",
				_ => ValueToShort(card.Value)
			};

			// center text
			float tx = inner.MidX;
			float ty = inner.MidY + textPaint.TextSize * 0.35f;
			textPaint.TextAlign = SKTextAlign.Center;
			canvas.DrawText(label, tx, ty, textPaint);
		}
		private static void DrawCardBack(SKCanvas canvas, float x, float y, float w, float h, bool highlight = false)
		{
			var r = new SKRect(x, y, x + w, y + h);

			var bg = new SKPaint { Color = new SKColor(30, 30, 30), IsAntialias = true };
			var border = new SKPaint
			{
				Color = highlight ? new SKColor(200, 200, 200) : new SKColor(90, 90, 90),
				IsAntialias = true,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 2
			};

			canvas.DrawRoundRect(r, 10, 10, bg);
			canvas.DrawRoundRect(r, 10, 10, border);

			// simple UNO-ish stripe
			var stripe = new SKPaint { Color = new SKColor(200, 40, 40), IsAntialias = true };
			var inner = new SKRect(r.Left + 10, r.Top + h * 0.45f, r.Right - 10, r.Top + h * 0.65f);
			canvas.DrawRoundRect(inner, 6, 6, stripe);
		}
		private bool IsPlayableFirst(UnoCardDto c, UnoCardDto top, UnoCardColor active)
		{
			if (c.Value == UnoCardValue.Wild || c.Value == UnoCardValue.WildDrawFour) return true;
			if (c.Color == active) return true;
			if (c.Value == top.Value) return true;
			return false;
		}


		private static string ValueToShort(UnoCardValue v) =>
			v switch
			{
				UnoCardValue.Zero => "0",
				UnoCardValue.One => "1",
				UnoCardValue.Two => "2",
				UnoCardValue.Three => "3",
				UnoCardValue.Four => "4",
				UnoCardValue.Five => "5",
				UnoCardValue.Six => "6",
				UnoCardValue.Seven => "7",
				UnoCardValue.Eight => "8",
				UnoCardValue.Nine => "9",
				_ => v.ToString()
			};

		private static SKColor ToSk(UnoCardColor c) =>
			c switch
			{
				UnoCardColor.Red => new SKColor(220, 60, 60),
				UnoCardColor.Yellow => new SKColor(220, 200, 60),
				UnoCardColor.Green => new SKColor(60, 200, 90),
				UnoCardColor.Blue => new SKColor(60, 120, 220),
				_ => new SKColor(120, 120, 120)
			};

		private static void DrawCenteredText(SKCanvas canvas, int w, int h, string text, float size)
		{
			var p = new SKPaint { Color = SKColors.White, IsAntialias = true, TextSize = size, TextAlign = SKTextAlign.Center };
			canvas.DrawText(text, w / 2f, h / 2f, p);
		}

		private bool CanSend() => _sendAsync != null && _isSocketOpen != null && _isSocketOpen();

		private void UpdateUi()
		{
			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId))
			{
				StatusText.Text = "Not in a room.";
				ColorPickerOverlay.Visibility = Visibility.Collapsed;
				return;
			}

			if (_state == null)
			{
				StatusText.Text = $"Room {_roomCode} as {_playerId}. Waiting for UNO state...";
				ColorPickerOverlay.Visibility = Visibility.Collapsed;
				return;
			}

			string turn = _state.CurrentPlayerId;
			string you = _playerId!;
			string phase = _state.Phase.ToString();

			StatusText.Text =
				$"Room {_state.RoomCode} | You: {you} | Turn: {turn} | Phase: {phase} | Active: {_state.ActiveColor}";

			StartButton.IsEnabled = CanSend() && (_state.PlayersInOrder.Count >= 2) && (_state.Phase == UnoTurnPhase.WaitingForStart || _state.TopDiscard == null);
			DrawButton.IsEnabled = CanSend() && _state.IsYourTurn && _state.Phase == UnoTurnPhase.NormalTurn;
			UnoButton.IsEnabled = CanSend();

			ColorPickerOverlay.Visibility = (_awaitingColorChoice ? Visibility.Visible : Visibility.Collapsed);
			PlaySelectedButton.IsEnabled = CanSend()
	&& _state.IsYourTurn
	&& _state.Phase == UnoTurnPhase.NormalTurn
	&& !_awaitingColorChoice
	&& _selectedOrder.Count > 0;
		}
	}
}

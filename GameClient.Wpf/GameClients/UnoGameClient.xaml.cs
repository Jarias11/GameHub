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

		// Visual order: slot -> handIndex (server index in _state.YourHand)
		private List<int> _handOrder = new();

		// Drag state (slot-based)
		private bool _isDragging;
		private int _dragSlot = -1;          // which SLOT we started dragging from
		private int _dragHandIndex = -1;     // which HAND INDEX is being dragged
		private int _dragCurrentSlot = -1;   // current insertion slot during drag preview
		private float _dragStartMouseX;
		private float _dragStartMouseY;
		private float _dragMouseX;
		private float _dragMouseY;
		private bool _dragExceededThreshold;

		// Layout cache for hand (needed to compute slot from mouse x)
		private float _handStartX;
		private float _handStep;
		private float _handCardW;
		private float _handCardH;
		private float _handCardTopY;

		private const float DragThresholdPx = 6f;


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

				// reset visual order to natural order whenever hand changes
				_handOrder = Enumerable.Range(0, _state.YourHand.Count).ToList();

				// reset hover/drag
				_hoverHandIndex = -1;
				_lastHoverHandIndex = -1;
				_isDragging = false;
				_dragSlot = _dragHandIndex = _dragCurrentSlot = -1;


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
			if (_awaitingColorChoice) return;

			var p = e.GetPosition(Canvas);
			float x = (float)p.X;
			float y = (float)p.Y;

			// find which SLOT we clicked (topmost slot last drawn)
			int slot = -1;
			for (int i = _handRects.Length - 1; i >= 0; i--)
			{
				if (_handRects[i].Contains(x, y))
				{
					slot = i;
					break;
				}
			}
			if (slot < 0) return;

			// start a potential drag
			_isDragging = true;
			_dragExceededThreshold = false;
			_dragSlot = slot;
			_dragCurrentSlot = slot;
			_dragHandIndex = GetHandIndexAtSlot(slot);

			_dragStartMouseX = x;
			_dragStartMouseY = y;
			_dragMouseX = x;
			_dragMouseY = y;

			Canvas.CaptureMouse();
			Canvas.InvalidateVisual();
		}
		private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (_state == null) return;

			if (_isDragging)
			{
				Canvas.ReleaseMouseCapture();

				// If we DID NOT move enough => treat as a click (toggle selection)
				if (!_dragExceededThreshold)
				{
					// Only allow selecting when it's your turn + normal turn (your original rules)
					if (_state.IsYourTurn && !_awaitingColorChoice)
					{
						int handIndex = _dragHandIndex;

						if (_selectedSet.Contains(handIndex))
						{
							_selectedSet.Remove(handIndex);
							_selectedOrder.Remove(handIndex);
						}
						else
						{
							_selectedSet.Add(handIndex);
							_selectedOrder.Add(handIndex);
						}

						UpdateUi();
					}
				}

				// End drag
				_isDragging = false;
				_dragSlot = _dragHandIndex = _dragCurrentSlot = -1;
				Canvas.InvalidateVisual();
			}
		}


		private void Canvas_MouseMove(object sender, MouseEventArgs e)
		{
			if (_state == null) return;

			var p = e.GetPosition(Canvas);
			float x = (float)p.X;
			float y = (float)p.Y;

			// ---- dragging reorder ----
			if (_isDragging)
			{
				_dragMouseX = x;
				_dragMouseY = y;

				float dx = MathF.Abs(_dragMouseX - _dragStartMouseX);
				float dy = MathF.Abs(_dragMouseY - _dragStartMouseY);
				if (!_dragExceededThreshold && (dx > DragThresholdPx || dy > DragThresholdPx))
					_dragExceededThreshold = true;

				if (_dragExceededThreshold && _handRects.Length > 0)
				{
					int newSlot = SlotFromMouseX(_dragMouseX);
					if (newSlot != _dragCurrentSlot)
					{
						MoveDraggedToSlot(newSlot);
						_dragCurrentSlot = newSlot;
					}
				}

				Canvas.Cursor = Cursors.SizeWE;
				Canvas.InvalidateVisual();
				return; // don't do hover while dragging
			}

			// ---- hover (your existing logic) ----
			int hover = -1;
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

			if (_isDragging)
			{
				_isDragging = false;
				_dragSlot = _dragHandIndex = _dragCurrentSlot = -1;
				Canvas.ReleaseMouseCapture();
			}

			Canvas.Cursor = Cursors.Arrow;
			Canvas.InvalidateVisual();
		}

		private int GetHandIndexAtSlot(int slot)
		{
			if (slot < 0 || slot >= _handOrder.Count) return -1;
			return _handOrder[slot];
		}

		private int SlotFromMouseX(float mouseX)
		{
			// use center points; clamp into [0..n-1]
			int n = _handRects.Length;
			if (n <= 1) return 0;

			float local = mouseX - _handStartX;
			float approx = local / _handStep; // slot-ish
			int slot = (int)MathF.Round(approx);
			return Math.Clamp(slot, 0, n - 1);
		}

		private void MoveDraggedToSlot(int newSlot)
		{
			if (_dragHandIndex < 0) return;

			// Remove dragged card from its current position in order, then insert at newSlot
			int oldPos = _handOrder.IndexOf(_dragHandIndex);
			if (oldPos < 0) return;

			_handOrder.RemoveAt(oldPos);

			// If removing from before the insertion point, the list shifted left.
			if (newSlot > oldPos) newSlot--;

			newSlot = Math.Clamp(newSlot, 0, _handOrder.Count);
			_handOrder.Insert(newSlot, _dragHandIndex);
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

			// --- table metrics (shared for everything) ---
			float tableCx = w * 0.5f;
			float tableCy = h * 0.52f;
			float tableR = MathF.Min(w, h) * 0.42f;

			DrawHexTable(canvas, w, h);

			float pad = 16f;

			// top info stays at the top
			float topH = 140f;
			DrawTopInfo(canvas, pad, pad, w - pad * 2, topH);

			// deck/pile stays centered on the table
			DrawCenterDeckAndPile(canvas, w, h);

			// opponents stay on hex
			DrawOpponentsHandsHex(canvas, tableCx, tableCy, tableR);
			DrawTurnArrow(canvas, w, h, tableCx, tableCy, tableR);

			// =========================
			// YOUR HAND: bottom middle of screen
			// =========================
			float handAreaH = 200f;
			float handTopY = h - pad - handAreaH;

			// pick a hand width that looks good and stays centered
			float handWidth = MathF.Min(w - pad * 2, 900f);
			float handX = (w - handWidth) * 0.5f;

			DrawHand(canvas, handX, handTopY, handWidth, handAreaH);
		}

		private void DrawOpponentsHandsHex(SKCanvas canvas, float cx, float cy, float r)
		{
			if (_state == null) return;
			if (string.IsNullOrEmpty(_playerId)) return;

			var order = _state.PlayersInOrder;
			if (order == null || order.Count < 2) return;

			int youIndex = order.IndexOf(_playerId);
			if (youIndex < 0) return;

			int GetHandCount(string pid)
			{
				var p = _state.PlayersPublic.FirstOrDefault(pp => pp.PlayerId == pid);
				return p?.HandCount ?? 0;
			}

			// opponent card backs (can stay as-is)
			float cardW = 40f;
			float cardH = cardW * 1.4f;

			using var namePaint = new SKPaint { Color = SKColors.White, IsAntialias = true, TextSize = 14 };
			using var smallPaint = new SKPaint { Color = new SKColor(200, 200, 200), IsAntialias = true, TextSize = 12 };

			// flat-top hex edges
			float flatHalfHeight = r * 0.866f; // sqrt(3)/2
			float topEdgeY = cy - flatHalfHeight;
			float bottomEdgeY = cy + flatHalfHeight;

			float inset = 18f; // keep hands inside the table edge

			int n = order.Count;

			for (int abs = 0; abs < n; abs++)
			{
				if (abs == youIndex) continue;

				string pid = order[abs];
				int count = GetHandCount(pid);

				int rel = (abs - youIndex + n) % n;

				// 2 players: opponent is top
				if (n == 2) rel = 2;

				if (rel == 2)
				{
					float maxSpan = r * 1.10f;
					float startX = cx - maxSpan * 0.5f;
					float y = topEdgeY + inset;

					DrawHorizontalBackStack(canvas, startX, y, cardW, cardH, count, maxSpan);

					var (x0, span, _) = ComputeHorizontalStack(startX, cardW, count, maxSpan);
					smallPaint.TextAlign = SKTextAlign.Center;
					canvas.DrawText($"{pid} ({count})", x0 + span * 0.5f, y + cardH + 14, smallPaint);
				}
				else if (rel == 1)
				{
					float maxSpan = r * 0.95f;
					float x = (cx - r) + inset;
					float startY = cy - maxSpan * 0.5f;

					DrawVerticalBackStack(canvas, x, startY, cardW, cardH, count, maxSpan);

					var (y0, _, _) = ComputeVerticalStack(startY, cardH, count, maxSpan);
					namePaint.TextAlign = SKTextAlign.Left;
					canvas.DrawText($"{pid} ({count})", x, y0 - 8, namePaint);
				}
				else if (rel == 3)
				{
					float maxSpan = r * 0.95f;
					float x = (cx + r) - inset - cardW;
					float startY = cy - maxSpan * 0.5f;

					DrawVerticalBackStack(canvas, x, startY, cardW, cardH, count, maxSpan);

					var (y0, _, _) = ComputeVerticalStack(startY, cardH, count, maxSpan);
					namePaint.TextAlign = SKTextAlign.Right;
					canvas.DrawText($"{pid} ({count})", x - 8, y0 - 8, namePaint);
				}

			}
		}
		private void DrawTurnArrow(SKCanvas canvas, int w, int h, float tableCx, float tableCy, float tableR)
		{
			if (_state == null) return;
			if (string.IsNullOrEmpty(_playerId)) return;

			var order = _state.PlayersInOrder;
			if (order == null || order.Count < 2) return;

			int youIndex = order.IndexOf(_playerId);
			if (youIndex < 0) return;

			// Identify current player's "relative seat" from your perspective
			int curAbs = order.IndexOf(_state.CurrentPlayerId);
			if (curAbs < 0) return;

			int n = order.Count;
			int rel = (curAbs - youIndex + n) % n;

			// For 2 players, force opponent to TOP like your existing logic
			if (n == 2 && curAbs != youIndex) rel = 2;
			if (n == 2 && curAbs == youIndex) rel = 0;

			// --- deck position (must match DrawCenterDeckAndPile) ---
			float deckCardW = 50f;
			float deckCardH = deckCardW * 1.4f;
			float gap = 18f;

			float cx = w * 0.5f;
			float cy = h * 0.52f;

			float deckX = cx - gap - deckCardW;
			float deckY = cy - deckCardH * 0.5f;

			var deckCenter = new SKPoint(deckX + deckCardW * 0.5f, deckY + deckCardH * 0.5f);

			// --- target seat anchor points (on the hex) ---
			float flatHalfHeight = tableR * 0.866f; // sqrt(3)/2
			float topEdgeY = tableCy - flatHalfHeight;
			float bottomEdgeY = tableCy + flatHalfHeight;
			float inset = 18f;

			// default target = YOU (bottom)
			SKPoint target = new SKPoint(tableCx, bottomEdgeY - inset);

			if (rel == 2) // TOP
				target = new SKPoint(tableCx, topEdgeY + inset);
			else if (rel == 1) // LEFT
				target = new SKPoint((tableCx - tableR) + inset, tableCy);
			else if (rel == 3) // RIGHT
				target = new SKPoint((tableCx + tableR) - inset, tableCy);
			else if (rel == 0) // YOU (bottom)
				target = new SKPoint(tableCx, bottomEdgeY - inset);

			// --- draw arrow from deck -> target ---
			// ---- make the arrow shorter + "floating" so it doesn't touch deck/hands ----
			var dir = Normalize(Sub(target, deckCenter));

			// tune these 3 numbers:
			float alongOffset = 70f;   // pushes arrow away from deck/hand along the line
			float sideOffset = 24f;   // sideways “float” so it doesn’t sit on top of stuff
			float arrowLen = 70f; // shorter arrow length

			var perp = new SKPoint(-dir.Y, dir.X);

			var mid = Add(deckCenter, Add(Mul(dir, alongOffset), Mul(perp, sideOffset)));
			var start = Sub(mid, Mul(dir, arrowLen * 0.5f));
			var end = Add(mid, Mul(dir, arrowLen * 0.5f));

			DrawArrow(canvas, start, end);


		}
		private static SKPoint Normalize(SKPoint v)
		{
			float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
			return (len < 0.001f) ? new SKPoint(1, 0) : new SKPoint(v.X / len, v.Y / len);
		}

		private static SKPoint Add(SKPoint a, SKPoint b) => new SKPoint(a.X + b.X, a.Y + b.Y);
		private static SKPoint Sub(SKPoint a, SKPoint b) => new SKPoint(a.X - b.X, a.Y - b.Y);
		private static SKPoint Mul(SKPoint a, float s) => new SKPoint(a.X * s, a.Y * s);


		private static SKPoint MoveTowards(SKPoint from, SKPoint to, float dist)
		{
			var dx = to.X - from.X;
			var dy = to.Y - from.Y;
			var len = MathF.Sqrt(dx * dx + dy * dy);
			if (len < 0.001f) return from;
			float t = dist / len;
			return new SKPoint(from.X + dx * t, from.Y + dy * t);
		}

		private static void DrawArrow(SKCanvas canvas, SKPoint start, SKPoint end)
		{
			// Big yellow arrow with a subtle dark shadow for visibility
			using var shadow = new SKPaint
			{
				IsAntialias = true,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 12f,
				StrokeCap = SKStrokeCap.Round,
				Color = new SKColor(0, 0, 0, 120)
			};

			using var paint = new SKPaint
			{
				IsAntialias = true,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 10f,
				StrokeCap = SKStrokeCap.Round,
				Color = new SKColor(255, 220, 0) // bright yellow
			};

			// main shaft
			canvas.DrawLine(start.X + 2, start.Y + 2, end.X + 2, end.Y + 2, shadow);
			canvas.DrawLine(start, end, paint);

			// arrow head
			float headLen = 26f;
			float headAngle = 28f * (MathF.PI / 180f);

			var dirX = end.X - start.X;
			var dirY = end.Y - start.Y;
			var len = MathF.Sqrt(dirX * dirX + dirY * dirY);
			if (len < 0.001f) return;

			dirX /= len;
			dirY /= len;

			// rotate direction by +/- headAngle
			(float lx, float ly) = Rotate(dirX, dirY, +headAngle);
			(float rx, float ry) = Rotate(dirX, dirY, -headAngle);

			var left = new SKPoint(end.X - lx * headLen, end.Y - ly * headLen);
			var right = new SKPoint(end.X - rx * headLen, end.Y - ry * headLen);

			canvas.DrawLine(left.X + 2, left.Y + 2, end.X + 2, end.Y + 2, shadow);
			canvas.DrawLine(right.X + 2, right.Y + 2, end.X + 2, end.Y + 2, shadow);

			canvas.DrawLine(left, end, paint);
			canvas.DrawLine(right, end, paint);
		}

		private static (float x, float y) Rotate(float x, float y, float radians)
		{
			float c = MathF.Cos(radians);
			float s = MathF.Sin(radians);
			return (x * c - y * s, x * s + y * c);
		}
		private int GetSelectionNumber(int handIndex)
		{
			// returns 1..N, or 0 if not selected
			int pos = _selectedOrder.IndexOf(handIndex);
			return (pos >= 0) ? (pos + 1) : 0;
		}


		private void DrawCenterDeckAndPile(SKCanvas canvas, int w, int h)
		{
			if (_state == null) return;

			// center of the “table”
			float cx = w * 0.5f;
			float cy = h * 0.52f;

			float cardW = 70;
			float cardH = cardW * 1.4f;

			// deck on left, discard on right
			float gap = 18f;
			float deckX = cx - gap - cardW;
			float deckY = cy - cardH * 0.5f;

			float discardX = cx + gap;
			float discardY = cy - cardH * 0.5f;

			using var smallPaint = new SKPaint { Color = new SKColor(220, 220, 220), IsAntialias = true, TextSize = 12 };

			// Deck stack (face down)
			int deckLayers = Math.Min(3, Math.Max(0, _state.DeckCount));
			for (int i = deckLayers - 1; i >= 0; i--)
			{
				DrawCardBack(canvas, deckX + i * 3, deckY - i * 3, cardW, cardH);
			}
			canvas.DrawText($"Deck ({_state.DeckCount})", deckX, deckY + cardH + 16, smallPaint);

			// Discard stack
			int discLayers = Math.Min(3, Math.Max(0, _state.DiscardCount - 1));
			for (int i = discLayers; i >= 1; i--)
			{
				DrawCardBack(canvas, discardX + i * 3, discardY - i * 3, cardW, cardH);
			}

			DrawCard(canvas, discardX, discardY, cardW, cardH, _state.TopDiscard, isFaceUp: true);
			canvas.DrawText($"Pile ({_state.DiscardCount})", discardX, discardY + cardH + 16, smallPaint);
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

			// NEW: center the actual span within maxSpan
			float span = (count <= 1) ? cardW : (cardW + step * (count - 1));
			float x0 = startX + MathF.Max(0f, (maxSpan - span) * 0.5f);

			for (int i = 0; i < count; i++)
			{
				float cx = x0 + i * step;
				DrawCardBack(canvas, cx, startY, cardW, cardH);
			}
		}

		private void DrawVerticalBackStack(SKCanvas canvas, float startX, float startY, float cardW, float cardH, int count, float maxSpan)
		{
			if (count <= 0) return;

			float step = (count <= 1) ? 0 : (maxSpan - cardH) / (count - 1);
			step = Math.Clamp(step, 10f, cardH * 0.35f);

			// NEW: center the actual span within maxSpan
			float span = (count <= 1) ? cardH : (cardH + step * (count - 1));
			float y0 = startY + MathF.Max(0f, (maxSpan - span) * 0.5f);

			for (int i = 0; i < count; i++)
			{
				float cy = y0 + i * step;
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

			float cy = y + 10;

			// how wide the hand actually spans with the chosen step
			float span = (count <= 1) ? cardW : (cardW + step * (count - 1));

			// center that span inside the available width
			float startX = x + (width - span) * 0.5f;


			_handStartX = startX;
			_handStep = step <= 0 ? cardW : step;
			_handCardW = cardW;
			_handCardH = cardH;
			_handCardTopY = cy;



			// Build rects first (same as you already do)
			for (int i = 0; i < count; i++)
			{
				float cx = startX + i * step;
				var rect = new SKRect(cx, cy, cx + cardW, cy + cardH);
				_handRects[i] = rect;
			}

			// draw all non-hover + non-dragged first
			for (int slot = 0; slot < count; slot++)
			{
				// if dragging, skip the dragged card slot (we'll draw it floating last)
				if (_isDragging && _dragHandIndex >= 0)
				{
					int hi = GetHandIndexAtSlot(slot);
					if (hi == _dragHandIndex) continue;
				}

				// if hovering, skip hover slot so we can draw it last (on top)
				if (!_isDragging && slot == _hoverHandIndex) continue;

				int handIndex = GetHandIndexAtSlot(slot);
				if (handIndex < 0 || handIndex >= _state.YourHand.Count) continue;

				bool selected = _selectedSet.Contains(handIndex);
				bool highlight = _state.IsYourTurn && !_awaitingColorChoice;

				var r = _handRects[slot];
				int selNum = selected ? GetSelectionNumber(handIndex) : 0;
				DrawCard(canvas, r.Left, r.Top, r.Width, r.Height,
	_state.YourHand[handIndex],
	isFaceUp: true,
	highlightClickable: highlight,
	isSelected: selected,
	selectionNumber: selNum);
			}


			// Draw hovered card LAST so it’s on top (and lift it slightly)
			if (!_isDragging && _hoverHandIndex >= 0 && _hoverHandIndex < count)
			{
				int slot = _hoverHandIndex;
				int handIndex = GetHandIndexAtSlot(slot);

				if (handIndex >= 0 && handIndex < _state.YourHand.Count)
				{
					bool selected = _selectedSet.Contains(handIndex);
					float lift = 18f;
					var r = _handRects[slot];

					int selNum = selected ? GetSelectionNumber(handIndex) : 0;

					DrawCard(canvas, r.Left, r.Top - lift, r.Width, r.Height,
						_state.YourHand[handIndex],
						isFaceUp: true,
						highlightClickable: true,
						isSelected: selected,
						selectionNumber: selNum);
				}
			}

			if (_isDragging && _dragHandIndex >= 0 && _dragHandIndex < _state.YourHand.Count)
			{
				bool selected = _selectedSet.Contains(_dragHandIndex);

				// keep it centered under mouse
				float drawX = _dragMouseX - _handCardW * 0.5f;
				float drawY = _handCardTopY - 26f; // lifted a bit

				int selNum = selected ? GetSelectionNumber(_dragHandIndex) : 0;

				DrawCard(canvas, drawX, drawY, _handCardW, _handCardH,
					_state.YourHand[_dragHandIndex],
					isFaceUp: true,
					highlightClickable: true,
					isSelected: selected,
					selectionNumber: selNum);

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
		private static SKPath BuildHexPath(float cx, float cy, float radius, float rotationRadians = 0f)
		{
			var path = new SKPath();
			for (int i = 0; i < 6; i++)
			{
				float a = rotationRadians + (float)(Math.PI / 3.0) * i; // 60 deg steps
				float px = cx + radius * MathF.Cos(a);
				float py = cy + radius * MathF.Sin(a);
				if (i == 0) path.MoveTo(px, py);
				else path.LineTo(px, py);
			}
			path.Close();
			return path;
		}
		private static (float x0, float span, float step) ComputeHorizontalStack(float startX, float cardW, int count, float maxSpan)
		{
			float step = (count <= 1) ? 0 : (maxSpan - cardW) / (count - 1);
			step = Math.Clamp(step, 10f, cardW * 0.60f);

			float span = (count <= 1) ? cardW : (cardW + step * (count - 1));
			float x0 = startX + MathF.Max(0f, (maxSpan - span) * 0.5f);
			return (x0, span, step);
		}

		private static (float y0, float span, float step) ComputeVerticalStack(float startY, float cardH, int count, float maxSpan)
		{
			float step = (count <= 1) ? 0 : (maxSpan - cardH) / (count - 1);
			step = Math.Clamp(step, 10f, cardH * 0.35f);

			float span = (count <= 1) ? cardH : (cardH + step * (count - 1));
			float y0 = startY + MathF.Max(0f, (maxSpan - span) * 0.5f);
			return (y0, span, step);
		}


		private static void DrawHexTable(SKCanvas canvas, int w, int h)
		{
			float cx = w * 0.5f;
			float cy = h * 0.52f;               // slightly below center feels “table-ish”
			float radius = MathF.Min(w, h) * 0.42f;

			using var hex = BuildHexPath(cx, cy, radius, rotationRadians: 0f);

			using var fill = new SKPaint
			{
				IsAntialias = true,
				Style = SKPaintStyle.Fill,
				Color = new SKColor(18, 55, 35)   // felt/green-ish
			};

			using var border = new SKPaint
			{
				IsAntialias = true,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 6,
				Color = new SKColor(40, 100, 70)
			};

			// soft shadow-ish “rim”
			using var rim = new SKPaint
			{
				IsAntialias = true,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 14,
				Color = new SKColor(0, 0, 0, 40)
			};

			canvas.DrawPath(hex, fill);
			canvas.DrawPath(hex, rim);
			canvas.DrawPath(hex, border);

			// subtle inner inset for depth
			using var innerHex = BuildHexPath(cx, cy, radius * 0.94f, rotationRadians: 0f);
			using var innerBorder = new SKPaint
			{
				IsAntialias = true,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 2,
				Color = new SKColor(255, 255, 255, 20)
			};
			canvas.DrawPath(innerHex, innerBorder);
		}



		private static void DrawCard(SKCanvas canvas,
	float x, float y, float w, float h,
	UnoCardDto? card,
	bool isFaceUp,
	bool highlightClickable = false,
	bool isSelected = false,
	int selectionNumber = 0)
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

			if (selectionNumber > 0)
			{
				float badgeR = MathF.Max(10f, w * 0.13f);           // scales with card
				float bx = r.Left + badgeR + 6f;                    // inset from corner
				float by = r.Top + badgeR + 6f;

				using var badgeFill = new SKPaint
				{
					IsAntialias = true,
					Style = SKPaintStyle.Fill,
					Color = new SKColor(220, 40, 40)               // red
				};

				using var badgeBorder = new SKPaint
				{
					IsAntialias = true,
					Style = SKPaintStyle.Stroke,
					StrokeWidth = 2f,
					Color = new SKColor(255, 255, 255, 220)        // light outline
				};

				using var numPaint = new SKPaint
				{
					IsAntialias = true,
					Color = SKColors.White,
					TextAlign = SKTextAlign.Center,
					TextSize = badgeR * 1.15f
				};

				canvas.DrawCircle(bx, by, badgeR, badgeFill);
				canvas.DrawCircle(bx, by, badgeR, badgeBorder);

				// vertically center text nicely
				canvas.DrawText(selectionNumber.ToString(), bx, by + numPaint.TextSize * 0.35f, numPaint);
			}
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

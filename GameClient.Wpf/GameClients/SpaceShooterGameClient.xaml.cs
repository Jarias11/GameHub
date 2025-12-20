using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameClient.Wpf.ClientServices;
using GameContracts;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using System.Windows.Media;

namespace GameClient.Wpf.GameClients;

public partial class SpaceShooterGameClient : UserControl, IGameClient
{
	public GameType GameType => GameType.SpaceShooter;
	public FrameworkElement View => this;

	private Func<HubMessage, Task>? _sendAsync;
	private Func<bool>? _isSocketOpen;

	private string? _roomCode;
	private string? _playerId;

	private SpaceShooterStatePayload? _state;
	private int _inputSeq = 0;

	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private long _lastTicks;



	// ==========================
	// Fix A: input send throttling
	// ==========================
	private const double InputSendHz = 60.0; // 30-60; start at 60
	private static readonly long InputSendIntervalTicks =
		(long)(Stopwatch.Frequency / InputSendHz);

	private long _lastInputSendTicks;

	// last input snapshot so we only send if changed
	private InputSnapshot _lastSentInput;
	private bool _hasLastSentInput;

	// optional safety: avoid piling up send tasks if network stalls briefly
	private Task? _sendInFlight;

	// simple input snapshot struct
	private readonly struct InputSnapshot
	{
		public readonly bool Thrust, Left, Right, Fire;
		public InputSnapshot(bool thrust, bool left, bool right, bool fire)
		{
			Thrust = thrust; Left = left; Right = right; Fire = fire;
		}
		public bool Equals(in InputSnapshot other) =>
			Thrust == other.Thrust && Left == other.Left && Right == other.Right && Fire == other.Fire;
	}

	// ==========================
	// Fix C: coalesce state updates
	// ==========================
	private SpaceShooterStatePayload? _latestState;  // written by receive thread
	private int _stateDirty;                         // 0/1; written by receive thread

	// ==========================
	// Fix B: reuse paints (created once)
	// ==========================
	private readonly SKPaint _astPaint = new()
	{
		IsAntialias = true,
		Style = SKPaintStyle.Stroke,
		StrokeWidth = 2,
		Color = new SKColor(180, 180, 180)
	};

	private readonly SKPaint _bulletPaint = new()
	{
		IsAntialias = true,
		Style = SKPaintStyle.Fill,
		Color = SKColors.White
	};

	private readonly SKPaint _boundaryPaint = new()
	{
		IsAntialias = true,
		Style = SKPaintStyle.Stroke,
		StrokeWidth = 3,
		Color = new SKColor(80, 80, 90)
	};

	private readonly SKPaint _shipPaintMe = new()
	{
		IsAntialias = true,
		Style = SKPaintStyle.Stroke,
		StrokeWidth = 4,
		Color = SKColors.Cyan
	};

	private readonly SKPaint _shipPaintOther = new()
	{
		IsAntialias = true,
		Style = SKPaintStyle.Stroke,
		StrokeWidth = 2,
		Color = SKColors.White
	};

	private readonly SKPaint _centerTextPaint = new()
	{
		IsAntialias = true,
		TextSize = 24,
		Color = SKColors.White
	};
	private readonly SKPaint _heartFillPaint = new()
	{
		IsAntialias = true,
		Style = SKPaintStyle.Fill,
		Color = new SKColor(220, 60, 70)
	};

	private readonly SKPaint _heartEmptyFillPaint = new()
	{
		IsAntialias = true,
		Style = SKPaintStyle.Fill,
		Color = new SKColor(60, 60, 70)
	};

	private readonly SKPaint _heartOutlinePaint = new()
	{
		IsAntialias = true,
		Style = SKPaintStyle.Stroke,
		StrokeWidth = 2,
		Color = SKColors.White
	};





	public SpaceShooterGameClient()
	{
		InitializeComponent();
		Focusable = true;
		Loaded += (_, __) =>
		{
			Focus();
			_lastTicks = _clock.ElapsedTicks;
			CompositionTarget.Rendering += OnRenderFrame;
		};
		Unloaded += (_, __) =>
		{
			CompositionTarget.Rendering -= OnRenderFrame;
			InputService.Clear();
		};
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
		_inputSeq = 0;

		AliveText.Text = "Alive: 0/0";
		StatusText.Text = "Connecting...";
		Canvas.InvalidateVisual();
	}

	public bool TryHandleMessage(HubMessage msg)
	{
		if (msg.MessageType == SpaceShooterMsg.State)
		{
			SpaceShooterStatePayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<SpaceShooterStatePayload>(msg.PayloadJson);
			}
			catch
			{
				return true;
			}
			if (payload == null) return true;

			// Coalesce: just store the latest snapshot and mark dirty.
			_latestState = payload;
			System.Threading.Interlocked.Exchange(ref _stateDirty, 1);

			return true;
		}



		if (msg.MessageType == SpaceShooterMsg.GameOver)
		{
			SpaceShooterGameOverPayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<SpaceShooterGameOverPayload>(msg.PayloadJson);
			}
			catch
			{
				return true;
			}

			if (payload != null)
			{
				Dispatcher.BeginInvoke(new Action(() =>
				{
					StatusText.Text = string.IsNullOrEmpty(payload.WinnerPlayerId)
						? "Game Over: Draw"
						: $"Game Over: {payload.WinnerPlayerId} wins";
				}));
			}

			return true;
		}

		return false;
	}

	public void OnKeyDown(KeyEventArgs e)
	{
		InputService.OnKeyDown(e.Key);
	}

	public void OnKeyUp(KeyEventArgs e)
	{
		InputService.OnKeyUp(e.Key);
	}

	private void OnRenderFrame(object? sender, EventArgs e)
	{
		// 1) Apply latest state if we got one
		if (System.Threading.Interlocked.Exchange(ref _stateDirty, 0) == 1)
		{
			var payload = _latestState;
			if (payload != null)
			{
				_state = payload;
				AliveText.Text = $"Alive: {payload.PlayersAlive}/{payload.PlayersTotal}";
				StatusText.Text = _playerId == null ? "No PlayerId" : $"You are {_playerId}";
			}
		}

		// 2) Try send input (but DO NOT early-return the whole frame)
		TrySendInput();

		// 3) ✅ Always request a draw every WPF frame
		Canvas.InvalidateVisual();
	}

	private void TrySendInput()
	{
		if (_sendAsync == null || _isSocketOpen == null) return;
		if (!_isSocketOpen()) return;
		if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId)) return;

		var nowTicks = _clock.ElapsedTicks;

		bool thrust = IsHeld(Key.W) || IsHeld(Key.Up);
		bool left = IsHeld(Key.A) || IsHeld(Key.Left);
		bool right = IsHeld(Key.D) || IsHeld(Key.Right);
		bool fire = IsHeld(Key.Space);

		var snap = new InputSnapshot(thrust, left, right, fire);

		bool changed = !_hasLastSentInput || !_lastSentInput.Equals(in snap);
		bool due = (nowTicks - _lastInputSendTicks) >= InputSendIntervalTicks;

		if (!changed && !fire && !due) return;

		_lastInputSendTicks = nowTicks;

		var input = new SpaceShooterInputPayload
		{
			Sequence = ++_inputSeq,
			ClientTicks = nowTicks,
			Thrust = thrust,
			TurnLeft = left,
			TurnRight = right,
			Fire = fire
		};

		var msgOut = new HubMessage
		{
			MessageType = SpaceShooterMsg.Input,
			RoomCode = _roomCode!,
			PlayerId = _playerId!,
			PayloadJson = JsonSerializer.Serialize(input)
		};

		_lastSentInput = snap;
		_hasLastSentInput = true;

		_sendInFlight = _sendAsync(msgOut);
	}


	private static bool IsHeld(Key k) => InputService.IsHeld(k);

	// ==========================
	// Rendering (Skia)
	// ==========================
	private void Canvas_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
	{
		var canvas = e.Surface.Canvas;
		canvas.Clear(new SKColor(12, 12, 16));

		var s = _state;
		if (s == null || string.IsNullOrEmpty(_playerId))
		{
			DrawCenteredText(canvas, e.Info.Width, e.Info.Height, "Waiting for state...");
			return;
		}

		// find local ship for camera
		SpaceShooterShipPayload? me = null;
		for (int i = 0; i < s.Ships.Count; i++)
		{
			if (s.Ships[i].PlayerId == _playerId)
			{
				me = s.Ships[i];
				break;
			}
		}
		if (me == null)
		{
			DrawCenteredText(canvas, e.Info.Width, e.Info.Height, "No ship yet...");
			return;
		}

		canvas.Save();

		// CAMERA:
		// World units -> screen pixels using “view radius” as zoom.
		float viewR = MathF.Max(50f, s.World.CameraViewRadius);
		float screenMin = MathF.Min(e.Info.Width, e.Info.Height);
		float scale = (screenMin * 0.45f) / viewR; // 90% of min dimension shows 2*viewR

		// transform: center on my ship
		canvas.Translate(e.Info.Width * 0.5f, e.Info.Height * 0.5f);
		canvas.Scale(scale, scale);
		canvas.Translate(-me.X, -me.Y);

		// draw asteroids
		for (int i = 0; i < s.Asteroids.Count; i++)
		{
			var a = s.Asteroids[i];
			canvas.DrawCircle(a.X, a.Y, a.Radius, _astPaint);
		}

		// draw bullets
		for (int i = 0; i < s.Bullets.Count; i++)
		{
			var b = s.Bullets[i];
			canvas.DrawCircle(b.X, b.Y, 3f, _bulletPaint);
		}

		// draw ships
		for (int i = 0; i < s.Ships.Count; i++)
		{
			var ship = s.Ships[i];
			if (!ship.Alive) continue;
			DrawShipTriangle(canvas, ship, ship.PlayerId == _playerId);
		}

		// boundary
		canvas.DrawCircle(0, 0, s.World.WorldRadius, _boundaryPaint);

		// draw hp hearts
		canvas.Restore(); // ✅ back to screen pixels
		DrawLivesHud(canvas, e.Info.Width, e.Info.Height, me.Hp);

	}

	private void DrawShipTriangle(SKCanvas canvas, SpaceShooterShipPayload ship, bool isMe)
	{
		float r = 18f;

		// local points
		var p0 = new SKPoint(r, 0);
		var p1 = new SKPoint(-r * 0.7f, -r * 0.55f);
		var p2 = new SKPoint(-r * 0.7f, +r * 0.55f);

		float c = MathF.Cos(ship.AngleRad);
		float s = MathF.Sin(ship.AngleRad);

		SKPoint Rot(SKPoint p) => new SKPoint(
			ship.X + (p.X * c - p.Y * s),
			ship.Y + (p.X * s + p.Y * c));

		var a = Rot(p0);
		var b = Rot(p1);
		var cpt = Rot(p2);

		var paint = isMe ? _shipPaintMe : _shipPaintOther;

		canvas.DrawLine(a, b, paint);
		canvas.DrawLine(b, cpt, paint);
		canvas.DrawLine(cpt, a, paint);
	}

	private void DrawCenteredText(SKCanvas canvas, int w, int h, string text)
	{
		var bounds = new SKRect();
		_centerTextPaint.MeasureText(text, ref bounds);

		canvas.DrawText(
			text,
			(w - bounds.Width) * 0.5f,
			(h + bounds.Height) * 0.5f,
			_centerTextPaint);
	}
	private void DrawLivesHud(SKCanvas canvas, int w, int h, int lives)
	{
		const int maxLives = 3;

		float x = 16f;
		float y = 16f;
		float size = 22f;
		float gap = 8f;

		for (int i = 0; i < maxLives; i++)
		{
			var rect = new SKRect(x + i * (size + gap), y, x + i * (size + gap) + size, y + size);

			bool filled = i < lives;
			DrawHeart(canvas, rect, filled);
		}
	}

	private void DrawHeart(SKCanvas canvas, SKRect r, bool filled)
	{
		// Simple heart shape using a path (works well at small UI sizes)
		float cx = (r.Left + r.Right) * 0.5f;
		float cy = (r.Top + r.Bottom) * 0.45f;
		float w = r.Width;
		float h = r.Height;

		float topY = r.Top + h * 0.25f;
		float bottomY = r.Bottom;
		float leftX = r.Left;
		float rightX = r.Right;

		var path = new SKPath();

		// Start at bottom point
		path.MoveTo(cx, bottomY);

		// Left curve
		path.CubicTo(
			cx - w * 0.55f, r.Top + h * 0.75f,
			leftX, topY,
			cx - w * 0.25f, r.Top + h * 0.30f);

		// Left bump -> top center
		path.CubicTo(
			cx - w * 0.10f, r.Top,
			cx - w * 0.05f, r.Top,
			cx, r.Top + h * 0.18f);

		// Right bump
		path.CubicTo(
			cx + w * 0.05f, r.Top,
			cx + w * 0.10f, r.Top,
			cx + w * 0.25f, r.Top + h * 0.30f);

		// Right curve back to bottom
		path.CubicTo(
			rightX, topY,
			cx + w * 0.55f, r.Top + h * 0.75f,
			cx, bottomY);

		path.Close();

		canvas.DrawPath(path, filled ? _heartFillPaint : _heartEmptyFillPaint);
		canvas.DrawPath(path, _heartOutlinePaint);
	}


}

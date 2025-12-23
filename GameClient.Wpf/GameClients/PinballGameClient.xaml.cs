using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media; // CompositionTarget
using GameContracts;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace GameClient.Wpf.GameClients
{
	public partial class PinballGameClient : UserControl, IGameClient
	{
		// =========================================================
		// IGameClient plumbing (offline)
		// =========================================================

		public GameType GameType => GameType.Pinball;
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
			// Offline game: ignore (but keep interface happy)
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			// Offline game: no server messages to handle
			return false;
		}

		// =========================================================
		// Input
		// =========================================================

		public void OnKeyDown(KeyEventArgs e)
		{
			if (e.Key == Key.A || e.Key == Key.Left) _leftFlipperHeld = true;
			if (e.Key == Key.D || e.Key == Key.Right) _rightFlipperHeld = true;

			if (e.Key == Key.Space)
			{
				_plungerHeld = true;
				e.Handled = true;
			}

			if (_gameOver && e.Key == Key.Enter)
			{
				RestartGame();
				e.Handled = true;
			}
		}

		public void OnKeyUp(KeyEventArgs e)
		{
			if (e.Key == Key.A || e.Key == Key.Left) _leftFlipperHeld = false;
			if (e.Key == Key.D || e.Key == Key.Right) _rightFlipperHeld = false;

			if (e.Key == Key.Space)
			{
				_plungerHeld = false;
				ReleasePlungerIfPossible();
				e.Handled = true;
			}
		}

		// =========================================================
		// Loop timing
		// =========================================================

		private readonly Stopwatch _clock = new Stopwatch();
		private long _lastTicks;
		private bool _loopRunning;

		private const float MaxDt = 0.05f; // clamp spikes

		// =========================================================
		// World units
		// =========================================================

		private int _screenW;
		private int _screenH;

		// We simulate in "table space" (0..TableW, 0..TableH) and scale to screen.
		private const float TableW = 520f; // widened
		private const float TableH = 900f;

		// Tube lane (right side) - moved right for wider table
		private const float TubeX0 = 440f;
		private const float TubeX1 = 492f;

		// Exit should be on the upper-right: a gap on the tube-left wall near the top
		private const float TubeExitY = 90f;
		private const float TubeExitHeight = 90f;

		// =========================================================
		// Game state
		// =========================================================

		private long _score;
		private int _ballsRemaining = 3;
		private bool _gameOver;

		// Input state
		private bool _leftFlipperHeld;
		private bool _rightFlipperHeld;
		private bool _plungerHeld;

		// Plunger charge
		private float _plungerCharge01; // 0..1
		private const float PlungerChargeRate = 0.9f; // per second
		private const float PlungerReleaseImpulseMin = 650f;
		private const float PlungerReleaseImpulseMax = 1550f;

		// Plunger mechanics: physically pull ball down while charging.
		private const float PlungerRestY = 850f; // where ball sits when ready
		private const float PlungerMaxPull = 42f; // how far down it can be pulled
		private const float PlungerFloorInset = 2f; // keep ball slightly above floor to avoid jitter/stuck

		// Physics constants
		private const float Gravity = 1150f;     // downwards
		private const float BallRestitution = 0.75f;
		private const float WallRestitution = 0.72f;
		private const float BallFriction = 0.05f;
		private const float MaxBallSpeed = 1700f;

		// Ball
		private Ball _ball;

		// Table collision geometry
		private readonly List<Segment> _walls = new List<Segment>();
		private readonly List<Bumper> _bumpers = new List<Bumper>();
		private readonly List<Target> _targets = new List<Target>();

		// Flippers
		private Flipper _leftFlipper;
		private Flipper _rightFlipper;

		// Flipper collision thickness (visual stroke is 12 => radius ~ 6)
		// Add a bit extra for forgiveness.
		private const float FlipperRadius = 7f;

		// Drain + Plunger lane area
		private const float DrainY = TableH + 40f;
		private SKRect _plungerLaneRect; // computed in ResetTable()

		private const float MaxMovePerSubStep = 5f; // ~ ballRadius * 0.5 (your radius is 10)
		private const int MaxSubSteps = 10;         // cap so hitches don't explode CPU

		// Wall collision thickness (stroke 6 => radius ~3) + a little forgiveness
		private const float WallRadius = 4f;

		private float _hudAccum;
		private long _lastHudScore;
		private int _lastHudBalls;
		private bool _lastHudGameOver;



		// =========================================================
		// Rendering paints
		// =========================================================

		private readonly SKPaint _bgPaint = new SKPaint { Color = new SKColor(10, 12, 22), IsAntialias = true };
		private readonly SKPaint _tableBorderPaint = new SKPaint { Color = new SKColor(40, 220, 255), Style = SKPaintStyle.Stroke, StrokeWidth = 4, IsAntialias = true };
		private readonly SKPaint _wallPaint = new SKPaint { Color = new SKColor(200, 200, 200), Style = SKPaintStyle.Stroke, StrokeWidth = 6, IsAntialias = true };
		private readonly SKPaint _bumperPaint = new SKPaint { Color = new SKColor(255, 80, 120), Style = SKPaintStyle.Fill, IsAntialias = true };
		private readonly SKPaint _targetPaint = new SKPaint { Color = new SKColor(255, 190, 60), Style = SKPaintStyle.Fill, IsAntialias = true };
		private readonly SKPaint _flipperPaint = new SKPaint { Color = new SKColor(80, 220, 170), Style = SKPaintStyle.Stroke, StrokeWidth = 12, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
		private readonly SKPaint _ballPaint = new SKPaint { Color = new SKColor(240, 240, 255), Style = SKPaintStyle.Fill, IsAntialias = true };
		private readonly SKPaint _hudPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
		private readonly SKPaint _hudShadowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 160), IsAntialias = true };

		public PinballGameClient()
		{
			InitializeComponent();
			ResetTable();
		}

		private void UserControl_Loaded(object sender, RoutedEventArgs e)
		{
			Focus(); // ensure we get keyboard
			StartLoop();
		}

		private void UserControl_Unloaded(object sender, RoutedEventArgs e)
		{
			StopLoop();
		}

		private void StartLoop()
		{
			if (_loopRunning) return;

			_loopRunning = true;
			_clock.Restart();
			_lastTicks = _clock.ElapsedTicks;
			CompositionTarget.Rendering += OnRenderTick;
		}

		private void StopLoop()
		{
			if (!_loopRunning) return;

			_loopRunning = false;
			CompositionTarget.Rendering -= OnRenderTick;
			_clock.Stop();
		}

		private void OnRenderTick(object? sender, EventArgs e)
		{
			long now = _clock.ElapsedTicks;
			long dtTicks = now - _lastTicks;
			_lastTicks = now;

			double dt = (double)dtTicks / Stopwatch.Frequency;
			float fdt = (float)dt;
			if (fdt > MaxDt) fdt = MaxDt;
			if (fdt < 0f) fdt = 0f;

			Step(fdt);

			_hudAccum += fdt;

			// Only refresh UI text at 10 Hz OR when values change
			if (_hudAccum >= 0.10f ||
				_score != _lastHudScore ||
				_ballsRemaining != _lastHudBalls ||
				_gameOver != _lastHudGameOver)
			{
				_hudAccum = 0f;

				_lastHudScore = _score;
				_lastHudBalls = _ballsRemaining;
				_lastHudGameOver = _gameOver;

				HudText.Text = _gameOver
					? $"GAME OVER | Score: {_score:N0}"
					: $"Score: {_score:N0} | Balls: {_ballsRemaining}";
			}

			Surface.InvalidateVisual();

		
		}

		// =========================================================
		// Game flow
		// =========================================================

		private void RestartGame()
		{
			_score = 0;
			_ballsRemaining = 3;
			_gameOver = false;

			ResetTable();
		}

		private void ResetTable()
		{
			_walls.Clear();
			_bumpers.Clear();
			_targets.Clear();

			// --- Outer table border (simple rounded-ish outline) ---
			// Left wall
			AddWall(30, 60, 30, 860);

			// Top wall stops before tube area (tube has its own roof)
			AddWall(30, 60, TubeX0, 60);

			// Tube roof
			AddWall(TubeX0, 60, TubeX1, 60);

			// Tube kicker: slanted segment that forces the ball left toward the exit gap
			{
				float kickerY0 = 78f;
				float kickerY1 = 108f;
				AddWall(TubeX0 + 6f, kickerY0, TubeX1 - 6f, kickerY1);
			}

			// Right tube outer wall
			AddWall(TubeX1, 60, TubeX1, 900);

			// Tube left wall: SPLIT to create an exit gap into the playfield at upper-right
			float gapTop = TubeExitY;
			float gapBottom = TubeExitY + TubeExitHeight;
			AddWall(TubeX0, 60, TubeX0, gapTop);
			AddWall(TubeX0, gapBottom, TubeX0, 900);

			// Tube bottom cap (keeps ball in tube)
			AddWall(TubeX0, 900, TubeX1, 900);

			// Right outer wall of playfield (NOT tube): only below the tube exit region
			// Wall sits just left of tube so exit opens into playfield
			float rightPlayfieldX = TubeX0 - 10f;
			AddWall(rightPlayfieldX, gapBottom, rightPlayfieldX, 860);

			// Slanted inlanes to make a drain gap
			AddWall(30, 860, 165, 900);
			AddWall(rightPlayfieldX, 860, 255, 900);

			// --- Simple interior geometry inspired by classic table feel ---
			// Left mid slant
			AddWall(65, 260, 160, 210);
			// Right mid slant (shifted right slightly for wider table feel)
			AddWall(rightPlayfieldX - 35f, 260, 300, 210);

			// Some guide rails
			AddWall(105, 740, 105, 560);
			AddWall(335, 740, 335, 560);

			// --- Bumpers (score + kick) ---
			_bumpers.Add(new Bumper(new SKPoint(240, 300), 28f, 350f, 500));
			_bumpers.Add(new Bumper(new SKPoint(180, 360), 24f, 320f, 350));
			_bumpers.Add(new Bumper(new SKPoint(300, 360), 24f, 320f, 350));

			// --- Targets (simple circle targets that "light up" briefly) ---
			_targets.Add(new Target(new SKPoint(150, 180), 14f, 800));
			_targets.Add(new Target(new SKPoint(260, 155), 14f, 800));
			_targets.Add(new Target(new SKPoint(350, 180), 14f, 800));

			// --- Flippers (lower + wider) ---
			_leftFlipper = Flipper.CreateLeft(
				pivot: new SKPoint(105, 802),
				length: 105f,
				restAngleDeg: 390f,   // 30° (slight DOWN)
				upAngleDeg: 300f);    // swings up

			_rightFlipper = Flipper.CreateRight(
				pivot: new SKPoint(335, 802),
				length: 105f,
				restAngleDeg: 150f,   // left & slight DOWN (mirrors left)
				upAngleDeg: 240f);    // swings up

			// --- Plunger lane (right side tube) ---
			_plungerLaneRect = new SKRect(TubeX0, 60, TubeX1, 900);

			SpawnBallInPlungerLane();
			_plungerCharge01 = 0f;
		}

		private void SpawnBallInPlungerLane()
		{
			_ball = new Ball
			{
				Pos = new SKPoint((TubeX0 + TubeX1) * 0.5f, PlungerRestY),
				Vel = new SKPoint(0, 0),
				Radius = 10f,
				IsInPlay = true,
				JustLaunchedTimer = 0f
			};

			_plungerCharge01 = 0f;
		}

		private void LoseBall()
		{
			_ballsRemaining--;
			if (_ballsRemaining <= 0)
			{
				_gameOver = true;
				_ball.IsInPlay = false;
				return;
			}

			SpawnBallInPlungerLane();
			_plungerCharge01 = 0f;
		}

		private void ReleasePlungerIfPossible()
		{
			if (_gameOver) return;
			if (!_ball.IsInPlay) return;

			// Only allow launch if ball is in plunger lane
			if (!_plungerLaneRect.Contains(_ball.Pos)) return;

			float impulse = Lerp(PlungerReleaseImpulseMin, PlungerReleaseImpulseMax, _plungerCharge01);
			if (impulse < 250f) impulse = 250f;

			// Put the ball just above the tube floor so it won't "stick" on release
			float tubeFloorY = _plungerLaneRect.Bottom;
			float safeY = tubeFloorY - _ball.Radius - PlungerFloorInset;
			_ball.Pos = new SKPoint(_ball.Pos.X, safeY);

			_ball.Vel = new SKPoint(0f, -impulse);
			_ball.JustLaunchedTimer = 0.35f;

			_plungerCharge01 = 0f;
		}

		// =========================================================
		// Simulation
		// =========================================================



		private void Step(float dt)
		{
			if (_gameOver) return;
			if (!_ball.IsInPlay) return;

			// Dynamic substeps to reduce tunneling through flippers/walls
			float speed = (float)Math.Sqrt(_ball.Vel.X * _ball.Vel.X + _ball.Vel.Y * _ball.Vel.Y);

			int subSteps = 1;
			if (speed > 900f) subSteps = 2;
			if (speed > 1300f) subSteps = 3;
			if (speed > 1600f) subSteps = 4;

			float h = dt / subSteps;

			for (int i = 0; i < subSteps; i++)
			{
				// If ball died mid-step (drain), stop sub-steps
				if (_gameOver || !_ball.IsInPlay) break;

				StepOne(h);
			}
		}


		private void StepOne(float dt)
		{
			// Charge plunger while held + physically pull the ball down in the tube
			if (_plungerHeld)
			{
				_plungerCharge01 += PlungerChargeRate * dt;
				if (_plungerCharge01 > 1f) _plungerCharge01 = 1f;

				// If ball is in the tube, pull it down like a spring
				if (_plungerLaneRect.Contains(_ball.Pos))
				{
					float tubeFloorY = _plungerLaneRect.Bottom;
					float minY = PlungerRestY;
					float maxY = tubeFloorY - _ball.Radius - PlungerFloorInset;

					float pulled = minY + PlungerMaxPull * _plungerCharge01;
					if (pulled > maxY) pulled = maxY;

					_ball.Pos = new SKPoint((TubeX0 + TubeX1) * 0.5f, pulled);
					_ball.Vel = new SKPoint(0f, 0f);
				}
			}

			// Flipper animation
			_leftFlipper.Step(dt, _leftFlipperHeld);
			_rightFlipper.Step(dt, _rightFlipperHeld);

			// Ball physics
			_ball.Vel = new SKPoint(_ball.Vel.X, _ball.Vel.Y + Gravity * dt);

			// Simple damping so it doesn’t go infinite
			_ball.Vel = new SKPoint(
				_ball.Vel.X * (1f - BallFriction * dt),
				_ball.Vel.Y * (1f - BallFriction * dt));

			ClampBallSpeed(ref _ball);

			_ball.Pos = new SKPoint(
				_ball.Pos.X + _ball.Vel.X * dt,
				_ball.Pos.Y + _ball.Vel.Y * dt);

			if (_ball.JustLaunchedTimer > 0f)
				_ball.JustLaunchedTimer -= dt;
			// After moving the ball (after _ball.Pos update)
			for (int iter = 0; iter < 2; iter++)
			{
				// Walls (thick)
				for (int i = 0; i < _walls.Count; i++)
					CollideBallWithCapsuleSegment(ref _ball, _walls[i], WallRadius, WallRestitution);

				// Flippers (thick)
				CollideBallWithFlipper(ref _ball, _leftFlipper);
				CollideBallWithFlipper(ref _ball, _rightFlipper);
			}



			// Collide with walls
			for (int i = 0; i < _walls.Count; i++)
				CollideBallWithCapsuleSegment(ref _ball, _walls[i], WallRadius, WallRestitution);


			// Collide with bumpers
			for (int i = 0; i < _bumpers.Count; i++)
			{
				var b = _bumpers[i];
				CollideBallWithBumper(ref _ball, ref b);
				_bumpers[i] = b;
			}

			// Collide with targets
			for (int i = 0; i < _targets.Count; i++)
			{
				var t = _targets[i];
				CollideBallWithTarget(ref _ball, ref t, dt);
				_targets[i] = t;
			}

			// Collide with flippers
			CollideBallWithFlipper(ref _ball, _leftFlipper);
			CollideBallWithFlipper(ref _ball, _rightFlipper);

			// Drain check
			if (_ball.Pos.Y > DrainY)
				LoseBall();
		}



		private static bool CollideBallWithCapsuleSegment(ref Ball ball, Segment seg, float extraRadius, float restitution)
		{
			// Capsule radius = ball radius + extraRadius
			float r = ball.Radius + extraRadius;

			SKPoint ab = new SKPoint(seg.B.X - seg.A.X, seg.B.Y - seg.A.Y);
			SKPoint ap = new SKPoint(ball.Pos.X - seg.A.X, ball.Pos.Y - seg.A.Y);

			float abLen2 = ab.X * ab.X + ab.Y * ab.Y;
			if (abLen2 <= 0.0001f) return false;

			float t = (ap.X * ab.X + ap.Y * ab.Y) / abLen2;
			if (t < 0f) t = 0f;
			if (t > 1f) t = 1f;

			SKPoint closest = new SKPoint(seg.A.X + ab.X * t, seg.A.Y + ab.Y * t);
			SKPoint delta = new SKPoint(ball.Pos.X - closest.X, ball.Pos.Y - closest.Y);

			float dist2 = delta.X * delta.X + delta.Y * delta.Y;
			if (dist2 >= r * r) return false;

			float dist = (float)Math.Sqrt(Math.Max(dist2, 0.0001f));
			SKPoint n = new SKPoint(delta.X / dist, delta.Y / dist);

			// push out
			float penetration = r - dist;
			ball.Pos = new SKPoint(ball.Pos.X + n.X * penetration, ball.Pos.Y + n.Y * penetration);

			// reflect velocity
			float vn = ball.Vel.X * n.X + ball.Vel.Y * n.Y;
			if (vn < 0f)
			{
				float rx = ball.Vel.X - (1f + restitution) * vn * n.X;
				float ry = ball.Vel.Y - (1f + restitution) * vn * n.Y;
				ball.Vel = new SKPoint(rx, ry);
			}

			return true;
		}


		// =========================================================
		// Rendering
		// =========================================================

		private void Surface_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			canvas.Clear(SKColors.Black);

			_screenW = e.Info.Width;
			_screenH = e.Info.Height;

			// Fit table into available space
			float scale = Math.Min(_screenW / TableW, _screenH / TableH);
			float ox = (_screenW - TableW * scale) * 0.5f;
			float oy = (_screenH - TableH * scale) * 0.5f;

			canvas.Translate(ox, oy);
			canvas.Scale(scale);

			// Background
			canvas.DrawRect(new SKRect(0, 0, TableW, TableH), _bgPaint);

			// Outer border (visual) - updated for wider table
			canvas.DrawRect(new SKRect(24, 54, TableW - 24, 890), _tableBorderPaint);

			// Tube visual
			using (var tubePaint = new SKPaint { Color = new SKColor(255, 255, 255, 22), IsAntialias = true })
				canvas.DrawRect(_plungerLaneRect, tubePaint);

			// Exit gap highlight (upper-right)
			using (var exitPaint = new SKPaint { Color = new SKColor(40, 220, 255, 120), IsAntialias = true })
				canvas.DrawRect(new SKRect(TubeX0 - 3f, TubeExitY, TubeX0 + 3f, TubeExitY + TubeExitHeight), exitPaint);

			// Walls
			for (int i = 0; i < _walls.Count; i++)
			{
				var w = _walls[i];
				canvas.DrawLine(w.A.X, w.A.Y, w.B.X, w.B.Y, _wallPaint);
			}

			// Plunger lane indicator
			using (var lanePaint = new SKPaint { Color = new SKColor(255, 255, 255, 35), IsAntialias = true })
				canvas.DrawRect(_plungerLaneRect, lanePaint);

			// Bumpers
			for (int i = 0; i < _bumpers.Count; i++)
			{
				var b = _bumpers[i];
				canvas.DrawCircle(b.Center, b.Radius, _bumperPaint);

				using var ring = new SKPaint { Color = new SKColor(10, 12, 22), Style = SKPaintStyle.Stroke, StrokeWidth = 4, IsAntialias = true };
				canvas.DrawCircle(b.Center, b.Radius - 6, ring);
			}

			// Targets
			for (int i = 0; i < _targets.Count; i++)
			{
				var t = _targets[i];
				byte alpha = t.HitFlash > 0f ? (byte)220 : (byte)140;
				using var tp = new SKPaint
				{
					Color = new SKColor(_targetPaint.Color.Red, _targetPaint.Color.Green, _targetPaint.Color.Blue, alpha),
					IsAntialias = true
				};
				canvas.DrawCircle(t.Center, t.Radius, tp);
			}

			// Flippers
			DrawFlipper(canvas, _leftFlipper);
			DrawFlipper(canvas, _rightFlipper);

			// Ball
			if (_ball.IsInPlay)
				canvas.DrawCircle(_ball.Pos, _ball.Radius, _ballPaint);

			// HUD (Skia as well)
			DrawHud(canvas);

			// Game Over text
			if (_gameOver)
				DrawGameOver(canvas);
		}

		private void DrawFlipper(SKCanvas canvas, Flipper f)
		{
			var tip = f.GetTip();
			canvas.DrawLine(f.Pivot.X, f.Pivot.Y, tip.X, tip.Y, _flipperPaint);

			using var cap = new SKPaint { Color = _flipperPaint.Color, IsAntialias = true };
			canvas.DrawCircle(f.Pivot, 8f, cap);
		}

		private void DrawHud(SKCanvas canvas)
		{
			string s1 = $"SCORE  {_score:N0}";
			string s2 = $"BALLS  {_ballsRemaining}";
			string s3 = _plungerHeld ? $"PLUNGER  {(int)(_plungerCharge01 * 100)}%" : "HOLD SPACE TO LAUNCH";

			_hudPaint.TextSize = 26f;
			_hudShadowPaint.TextSize = 26f;

			DrawShadowText(canvas, s1, 30, 40);
			DrawShadowText(canvas, s2, TableW - 220, 40);

			_hudPaint.TextSize = 18f;
			_hudShadowPaint.TextSize = 18f;
			DrawShadowText(canvas, s3, 30, 78);
		}

		private void DrawGameOver(SKCanvas canvas)
		{
			string msg = "GAME OVER";
			string msg2 = "Press ENTER to restart";

			using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
			using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 180), IsAntialias = true, TextAlign = SKTextAlign.Center };

			paint.TextSize = 52f;
			shadow.TextSize = 52f;
			DrawShadowTextCentered(canvas, msg, TableW * 0.5f, TableH * 0.48f, paint, shadow);

			paint.TextSize = 22f;
			shadow.TextSize = 22f;
			DrawShadowTextCentered(canvas, msg2, TableW * 0.5f, TableH * 0.55f, paint, shadow);
		}

		private void DrawShadowText(SKCanvas canvas, string text, float x, float y)
		{
			canvas.DrawText(text, x + 2, y + 2, _hudShadowPaint);
			canvas.DrawText(text, x, y, _hudPaint);
		}

		private void DrawShadowTextCentered(SKCanvas canvas, string text, float cx, float cy, SKPaint paint, SKPaint shadow)
		{
			canvas.DrawText(text, cx + 2, cy + 2, shadow);
			canvas.DrawText(text, cx, cy, paint);
		}

		// =========================================================
		// Geometry / Collisions
		// =========================================================

		private void AddWall(float ax, float ay, float bx, float by)
		{
			_walls.Add(new Segment(new SKPoint(ax, ay), new SKPoint(bx, by)));
		}

		private static void ClampBallSpeed(ref Ball ball)
		{
			float vx = ball.Vel.X;
			float vy = ball.Vel.Y;
			float sp2 = vx * vx + vy * vy;
			float max2 = MaxBallSpeed * MaxBallSpeed;
			if (sp2 <= max2) return;

			float sp = (float)Math.Sqrt(sp2);
			float s = MaxBallSpeed / sp;
			ball.Vel = new SKPoint(vx * s, vy * s);
		}

		private static void CollideBallWithSegment(ref Ball ball, Segment seg)
		{
			SKPoint ab = new SKPoint(seg.B.X - seg.A.X, seg.B.Y - seg.A.Y);
			SKPoint ap = new SKPoint(ball.Pos.X - seg.A.X, ball.Pos.Y - seg.A.Y);

			float abLen2 = ab.X * ab.X + ab.Y * ab.Y;
			if (abLen2 <= 0.0001f) return;

			float t = (ap.X * ab.X + ap.Y * ab.Y) / abLen2;
			if (t < 0f) t = 0f;
			if (t > 1f) t = 1f;

			SKPoint closest = new SKPoint(seg.A.X + ab.X * t, seg.A.Y + ab.Y * t);
			SKPoint delta = new SKPoint(ball.Pos.X - closest.X, ball.Pos.Y - closest.Y);

			float dist2 = delta.X * delta.X + delta.Y * delta.Y;
			float r = ball.Radius;
			if (dist2 >= r * r) return;

			float dist = (float)Math.Sqrt(Math.Max(dist2, 0.0001f));
			SKPoint n = new SKPoint(delta.X / dist, delta.Y / dist);

			float penetration = r - dist;
			ball.Pos = new SKPoint(ball.Pos.X + n.X * penetration, ball.Pos.Y + n.Y * penetration);

			float vn = ball.Vel.X * n.X + ball.Vel.Y * n.Y;
			if (vn < 0f)
			{
				float rx = ball.Vel.X - (1f + WallRestitution) * vn * n.X;
				float ry = ball.Vel.Y - (1f + WallRestitution) * vn * n.Y;
				ball.Vel = new SKPoint(rx, ry);
			}
		}

		private void CollideBallWithBumper(ref Ball ball, ref Bumper bumper)
		{
			SKPoint d = new SKPoint(ball.Pos.X - bumper.Center.X, ball.Pos.Y - bumper.Center.Y);
			float dist2 = d.X * d.X + d.Y * d.Y;
			float min = ball.Radius + bumper.Radius;
			if (dist2 >= min * min) return;

			float dist = (float)Math.Sqrt(Math.Max(dist2, 0.0001f));
			SKPoint n = new SKPoint(d.X / dist, d.Y / dist);

			float penetration = min - dist;
			ball.Pos = new SKPoint(ball.Pos.X + n.X * penetration, ball.Pos.Y + n.Y * penetration);

			float vn = ball.Vel.X * n.X + ball.Vel.Y * n.Y;
			if (vn < 0f)
			{
				float rx = ball.Vel.X - (1f + BallRestitution) * vn * n.X;
				float ry = ball.Vel.Y - (1f + BallRestitution) * vn * n.Y;
				ball.Vel = new SKPoint(rx, ry);
			}

			ball.Vel = new SKPoint(ball.Vel.X + n.X * bumper.Kick, ball.Vel.Y + n.Y * bumper.Kick);

			_score += bumper.Score;
		}

		private void CollideBallWithTarget(ref Ball ball, ref Target target, float dt)
		{
			if (target.HitFlash > 0f)
				target.HitFlash -= dt;

			SKPoint d = new SKPoint(ball.Pos.X - target.Center.X, ball.Pos.Y - target.Center.Y);
			float dist2 = d.X * d.X + d.Y * d.Y;
			float min = ball.Radius + target.Radius;
			if (dist2 >= min * min) return;

			float dist = (float)Math.Sqrt(Math.Max(dist2, 0.0001f));
			SKPoint n = new SKPoint(d.X / dist, d.Y / dist);

			float penetration = min - dist;
			ball.Pos = new SKPoint(ball.Pos.X + n.X * penetration, ball.Pos.Y + n.Y * penetration);

			float vn = ball.Vel.X * n.X + ball.Vel.Y * n.Y;
			if (vn < 0f)
			{
				float rx = ball.Vel.X - (1f + 0.6f) * vn * n.X;
				float ry = ball.Vel.Y - (1f + 0.6f) * vn * n.Y;
				ball.Vel = new SKPoint(rx, ry);
			}

			if (target.HitFlash <= 0f)
			{
				_score += target.Score;
				target.HitFlash = 0.2f;
			}
		}

		private void CollideBallWithFlipper(ref Ball ball, Flipper flipper)
		{
			var tip = flipper.GetTip();
			var seg = new Segment(flipper.Pivot, tip);

			// Thick collision (capsule) so it matches the drawn flipper better
			SKPoint beforeVel = ball.Vel;
			bool hit = CollideBallWithCapsuleSegment(ref ball, seg, FlipperRadius, WallRestitution);

			if (!hit) return;

			// Stronger, more reliable "bat" when swinging up
			if (flipper.IsSwingingUp)
			{
				SKPoint dir = new SKPoint(tip.X - flipper.Pivot.X, tip.Y - flipper.Pivot.Y);
				float len = (float)Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
				if (len > 0.0001f)
				{
					dir = new SKPoint(dir.X / len, dir.Y / len);

					// Perp
					SKPoint perp = new SKPoint(-dir.Y, dir.X);

					// ensure "up" (negative Y) bias
					if (perp.Y > 0f) perp = new SKPoint(-perp.X, -perp.Y);

					// If the collision barely changed velocity (glancing), still kick it.
					// Make impulse bigger than before.
					float impulse = flipper.BatImpulse;
					ball.Vel = new SKPoint(ball.Vel.X + perp.X * impulse, ball.Vel.Y + perp.Y * impulse);
				}
			}
		}


		private static float Lerp(float a, float b, float t) => a + (b - a) * t;

		// =========================================================
		// Small structs
		// =========================================================

		private struct Ball
		{
			public SKPoint Pos;
			public SKPoint Vel;
			public float Radius;
			public bool IsInPlay;
			public float JustLaunchedTimer;
		}

		private readonly struct Segment
		{
			public Segment(SKPoint a, SKPoint b) { A = a; B = b; }
			public SKPoint A { get; }
			public SKPoint B { get; }
		}

		private struct Bumper
		{
			public Bumper(SKPoint center, float radius, float kick, int score)
			{
				Center = center;
				Radius = radius;
				Kick = kick;
				Score = score;
			}
			public SKPoint Center;
			public float Radius;
			public float Kick;
			public int Score;
		}

		private struct Target
		{
			public Target(SKPoint center, float radius, int score)
			{
				Center = center;
				Radius = radius;
				Score = score;
				HitFlash = 0f;
			}
			public SKPoint Center;
			public float Radius;
			public int Score;
			public float HitFlash;
		}

		private struct Flipper
		{
			public SKPoint Pivot;
			public float Length;

			public float RestAngleRad;
			public float UpAngleRad;
			public float AngleRad;

			public float SpeedRadPerSec; // how fast it rotates
			public float BatImpulse;     // how hard it kicks when swinging up

			public bool IsSwingingUp;

			public static Flipper CreateLeft(SKPoint pivot, float length, float restAngleDeg, float upAngleDeg)
			{
				return new Flipper
				{
					Pivot = pivot,
					Length = length,
					RestAngleRad = DegToRad(restAngleDeg),
					UpAngleRad = DegToRad(upAngleDeg),
					AngleRad = DegToRad(restAngleDeg),
					SpeedRadPerSec = DegToRad(900f),
					BatImpulse = 850f,
					IsSwingingUp = false
				};
			}

			public static Flipper CreateRight(SKPoint pivot, float length, float restAngleDeg, float upAngleDeg)
			{
				return new Flipper
				{
					Pivot = pivot,
					Length = length,
					RestAngleRad = DegToRad(restAngleDeg),
					UpAngleRad = DegToRad(upAngleDeg),
					AngleRad = DegToRad(restAngleDeg),
					SpeedRadPerSec = DegToRad(900f),
					BatImpulse = 850f,
					IsSwingingUp = false
				};
			}

			public void Step(float dt, bool held)
			{
				float target = held ? UpAngleRad : RestAngleRad;
				float before = AngleRad;

				if (AngleRad < target)
				{
					AngleRad += SpeedRadPerSec * dt;
					if (AngleRad > target) AngleRad = target;
				}
				else if (AngleRad > target)
				{
					AngleRad -= SpeedRadPerSec * dt;
					if (AngleRad < target) AngleRad = target;
				}

				IsSwingingUp = held && Math.Abs(AngleRad - before) > 0.0001f;
			}

			public SKPoint GetTip()
			{
				float cx = (float)Math.Cos(AngleRad);
				float sy = (float)Math.Sin(AngleRad);
				return new SKPoint(Pivot.X + cx * Length, Pivot.Y + sy * Length);
			}

			private static float DegToRad(float deg) => deg * ((float)Math.PI / 180f);
		}
	}
}

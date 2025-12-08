using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameClient.Wpf.ClientServices;
using GameContracts;
using GameLogic.Jumps;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace GameClient.Wpf.GameClients
{
	public partial class JumpsGameClient : UserControl, IGameClient
	{
		public GameType GameType => GameType.Jumps;
		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		private readonly DispatcherTimer _timer;
		private DateTime _lastFrameTime;

		private readonly JumpsEngine _engine = new JumpsEngine();

		public JumpsGameClient()
		{
			InitializeComponent();

			_timer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(16)
			};
			_timer.Tick += GameLoop;
		}

		public void SetConnection(Func<HubMessage, Task> sendAsync, Func<bool> isSocketOpen)
		{
			_sendAsync = sendAsync;
			_isSocketOpen = isSocketOpen;
		}

		public void OnRoomChanged(string? roomCode, string? playerId)
		{
			InputService.Clear();

			if (!string.IsNullOrEmpty(roomCode))
			{
				_engine.Reset();
				UpdateScoreText();
				UpdateLevelText();
			}
		}

		public bool TryHandleMessage(HubMessage msg) => false;

		public void OnKeyDown(KeyEventArgs e) => InputService.OnKeyDown(e.Key);
		public void OnKeyUp(KeyEventArgs e) => InputService.OnKeyUp(e.Key);

		private void UserControl_Loaded(object sender, RoutedEventArgs e)
		{
			Focus();

			_engine.Reset();
			UpdateScoreText();
			UpdateLevelText();
			_lastFrameTime = DateTime.Now;
			_timer.Start();
		}

		// ==== Game loop =====================================================

		private void GameLoop(object? sender, EventArgs e)
		{
			var now = DateTime.Now;
			if (_lastFrameTime == default)
			{
				_lastFrameTime = now;
				return;
			}

			double dt = (now - _lastFrameTime).TotalSeconds;
			_lastFrameTime = now;

			var input = new JumpsInputState
			{
				Left = InputService.IsHeld(Key.Left) || InputService.IsHeld(Key.A),
				Right = InputService.IsHeld(Key.Right) || InputService.IsHeld(Key.D),
				Down = InputService.IsHeld(Key.Down) || InputService.IsHeld(Key.S),
				JumpHeld = InputService.IsHeld(Key.Space)
			};

			_engine.Step(dt, input);

			UpdateScoreText();
			UpdateLevelText();
			UpdateDebugText();

			SkElement.InvalidateVisual();
		}

		// ==== UI text helpers ===============================================

		private void UpdateScoreText()
		{
			if (ScoreText != null)
			{
				ScoreText.Text = $"Score: {_engine.Score} (Best: {_engine.HighScore})";
			}
		}

		private void UpdateLevelText()
		{
			if (LevelText != null)
			{
				LevelText.Text = $"Level {_engine.Level}";
			}
		}

		private void UpdateDebugText()
		{
			if (DebugText != null)
			{
				DebugText.Text =
					$"Scroll: {_engine.ScrollSpeed:F1}  PlayerMax: {_engine.CurrentMoveSpeed:F1}";
			}
		}

		// ==== Skia rendering ================================================

		private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			canvas.Clear(SKColors.Black);

			float canvasWidth = e.Info.Width;
			float canvasHeight = e.Info.Height;

			float baseScale = Math.Min(canvasWidth / JumpsEngine.WorldWidth, canvasHeight / JumpsEngine.WorldHeight);
			float scale = baseScale * _engine.CameraZoom;
			float xOffset = (canvasWidth - JumpsEngine.WorldWidth * scale) / 2f;
			float yOffset = (canvasHeight - JumpsEngine.WorldHeight * scale) / 2f;

			canvas.Translate(xOffset, yOffset);
			canvas.Scale(scale);

			canvas.Translate(0, -_engine.CameraTop);

			DrawGround(canvas);
			DrawPlatformsAndPickups(canvas);
			DrawPlayer(canvas);
			DrawPowerupRings(canvas);
		}

		private void DrawGround(SKCanvas canvas)
		{
			using var groundPaint = new SKPaint
			{
				Color = new SKColor(60, 60, 60),
				IsAntialias = true
			};

			canvas.DrawRect(0, _engine.GroundY, JumpsEngine.WorldWidth, 6f, groundPaint);
		}

		private void DrawPlatformsAndPickups(SKCanvas canvas)
		{
			using var platformPaint = new SKPaint
			{
				Color = SKColors.LightGray,
				IsAntialias = true
			};
			using var coinPaint = new SKPaint
			{
				Color = SKColors.Gold,
				IsAntialias = true
			};
			using var jumpPaint = new SKPaint
			{
				Color = SKColors.MediumPurple,
				IsAntialias = true
			};
			using var speedPaint = new SKPaint
			{
				Color = SKColors.LimeGreen,
				IsAntialias = true
			};
			using var magnetPaint = new SKPaint
			{
				Color = SKColors.Red,
				IsAntialias = true
			};
			using var doubleJumpPaint = new SKPaint
			{
				Color = SKColors.SaddleBrown,
				IsAntialias = true
			};
			using var slowScrollPaint = new SKPaint
			{
				Color = SKColors.Cyan,
				IsAntialias = true
			};

			foreach (var p in _engine.Platforms)
			{
				canvas.DrawRect(p.X, p.Y, p.Width, p.Height, platformPaint);

				if (p.Pickup == null || p.Pickup.Collected)
					continue;

				var pickup = p.Pickup;
				float cx = pickup.X;
				float cy = pickup.Y;

				SKPaint paintToUse = coinPaint;
				switch (pickup.Type)
				{
					case JumpsPickupType.Coin:
						paintToUse = coinPaint;
						break;
					case JumpsPickupType.JumpBoost:
						paintToUse = jumpPaint;
						break;
					case JumpsPickupType.SpeedBoost:
						paintToUse = speedPaint;
						break;
					case JumpsPickupType.Magnet:
						paintToUse = magnetPaint;
						break;
					case JumpsPickupType.DoubleJump:
						paintToUse = doubleJumpPaint;
						break;
					case JumpsPickupType.SlowScroll:
						paintToUse = slowScrollPaint;
						break;
				}

				canvas.DrawCircle(cx, cy, JumpsEngine.PickupRadius, paintToUse);
			}
		}

		private void DrawPlayer(SKCanvas canvas)
		{
			using var playerPaint = new SKPaint
			{
				Color = SKColors.DeepSkyBlue,
				IsAntialias = true
			};

			var rect = new SKRect(
				_engine.PlayerX,
				_engine.PlayerY,
				_engine.PlayerX + JumpsEngine.PlayerSize,
				_engine.PlayerY + JumpsEngine.PlayerSize);

			var rrect = new SKRoundRect(rect, 4f, 4f);
			canvas.DrawRoundRect(rrect, playerPaint);
		}

		private void DrawPowerupRings(SKCanvas canvas)
		{
			float playerCenterX = _engine.PlayerX + JumpsEngine.PlayerSize / 2f;
			float playerCenterY = _engine.PlayerY + JumpsEngine.PlayerSize / 2f;

			float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

			// Jump boost
			if (_engine.JumpBoostActive && _engine.JumpBoostTimeRemaining > 0f)
			{
				float frac = Clamp01(_engine.JumpBoostTimeRemaining / JumpsEngine.PowerupDuration);
				float sweepAngle = 360f * frac;

				using var jumpRingPaint = new SKPaint
				{
					Color = SKColors.MediumPurple,
					IsAntialias = true,
					Style = SKPaintStyle.Stroke,
					StrokeWidth = 2.5f
				};

				float radius = JumpsEngine.PlayerSize * 0.90f;
				var arcRect = new SKRect(
					playerCenterX - radius,
					playerCenterY - radius,
					playerCenterX + radius,
					playerCenterY + radius);

				canvas.DrawArc(arcRect, -90f, sweepAngle, false, jumpRingPaint);
			}

			// Speed boost
			if (_engine.SpeedBoostActive && _engine.SpeedBoostTimeRemaining > 0f)
			{
				float frac = Clamp01(_engine.SpeedBoostTimeRemaining / JumpsEngine.PowerupDuration);
				float sweepAngle = 360f * frac;

				using var speedRingPaint = new SKPaint
				{
					Color = SKColors.LimeGreen,
					IsAntialias = true,
					Style = SKPaintStyle.Stroke,
					StrokeWidth = 2.5f
				};

				float radius = JumpsEngine.PlayerSize * 0.70f;
				var arcRect = new SKRect(
					playerCenterX - radius,
					playerCenterY - radius,
					playerCenterX + radius,
					playerCenterY + radius);

				canvas.DrawArc(arcRect, -90f, sweepAngle, false, speedRingPaint);
			}

			// Magnet (big radius)
			if (_engine.MagnetActive && _engine.MagnetTimeRemaining > 0f)
			{
				float frac = Clamp01(_engine.MagnetTimeRemaining / JumpsEngine.PowerupDuration);
				float sweepAngle = 360f * frac;

				using var magnetRingPaint = new SKPaint
				{
					Color = SKColors.Red,
					IsAntialias = true,
					Style = SKPaintStyle.Stroke,
					StrokeWidth = 2.5f
				};

				float radius = JumpsEngine.MagnetRadiusWorld;
				var arcRect = new SKRect(
					playerCenterX - radius,
					playerCenterY - radius,
					playerCenterX + radius,
					playerCenterY + radius);

				canvas.DrawArc(arcRect, -90f, sweepAngle, false, magnetRingPaint);
			}

			// Double jump
			if (_engine.DoubleJumpActive && _engine.DoubleJumpTimeRemaining > 0f)
			{
				float frac = Clamp01(_engine.DoubleJumpTimeRemaining / JumpsEngine.PowerupDuration);
				float sweepAngle = 360f * frac;

				using var doubleJumpRingPaint = new SKPaint
				{
					Color = SKColors.SaddleBrown,
					IsAntialias = true,
					Style = SKPaintStyle.Stroke,
					StrokeWidth = 2.5f
				};

				float radius = JumpsEngine.PlayerSize * 0.50f;
				var arcRect = new SKRect(
					playerCenterX - radius,
					playerCenterY - radius,
					playerCenterX + radius,
					playerCenterY + radius);

				canvas.DrawArc(arcRect, -90f, sweepAngle, false, doubleJumpRingPaint);
			}

			// Slow scroll
			if (_engine.SlowScrollActive && _engine.SlowScrollTimeRemaining > 0f)
			{
				float frac = Clamp01(_engine.SlowScrollTimeRemaining / JumpsEngine.PowerupDuration);
				float sweepAngle = 360f * frac;

				using var slowRingPaint = new SKPaint
				{
					Color = SKColors.Cyan,
					IsAntialias = true,
					Style = SKPaintStyle.Stroke,
					StrokeWidth = 2.5f
				};

				float radius = JumpsEngine.PlayerSize * 1.1f;
				var arcRect = new SKRect(
					playerCenterX - radius,
					playerCenterY - radius,
					playerCenterX + radius,
					playerCenterY + radius);

				canvas.DrawArc(arcRect, -90f, sweepAngle, false, slowRingPaint);
			}
		}
	}
}

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameClient.Wpf;          // <- for IGameClient
using GameClient.Wpf.ClientServices;   // <-- NEW: for InputService
using GameContracts;
using SkiaSharp;
using SkiaSharp.Views.WPF;
using SkiaSharp.Views.Desktop;
using GameLogic.Jumps;

namespace GameClient.Wpf.GameClients
{
	public partial class JumpsOnlineGameClient : UserControl, IGameClient
	{
		// ==== IGameClient plumbing ==========================================

		public GameType GameType => GameType.JumpsOnline;

		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		// Room context
		private string? _roomCode;
		private string? _playerId;

		private bool _skiaDisposed;

		// Last input we actually sent (for change detection)
		private bool _lastLeft;
		private bool _lastRight;
		private bool _lastDown;
		private bool _lastJump;
		private DateTime _lastSentInputTime = DateTime.MinValue;

		private readonly DispatcherTimer _inputTimer;



		// ==== Skia cached paints (NO per-frame allocations) =========================

		// background/platform paints
		private readonly SKPaint _bgPaint = new() { Color = new SKColor(15, 15, 40), IsAntialias = false };
		private readonly SKPaint _platPaint = new() { IsAntialias = true, Color = new SKColor(90, 90, 130) };

		// pickups
		private readonly SKPaint _coinPaint = new() { IsAntialias = true, Color = SKColors.Gold };
		private readonly SKPaint _jumpPaint = new() { IsAntialias = true, Color = SKColors.MediumPurple };
		private readonly SKPaint _speedPaint = new() { IsAntialias = true, Color = SKColors.LimeGreen };
		private readonly SKPaint _magnetPaint = new() { IsAntialias = true, Color = SKColors.Red };
		private readonly SKPaint _doublePaint = new() { IsAntialias = true, Color = SKColors.SaddleBrown };
		private readonly SKPaint _slowPaint = new() { IsAntialias = true, Color = SKColors.Cyan };


		// ==== Cached arc rects (avoid recomputing every ring every frame) ===========
		private readonly SKRect[] _ringRectOuter = new SKRect[3];  // size * 0.9
		private readonly SKRect[] _ringRectMid = new SKRect[3];  // size * 0.7
		private readonly SKRect[] _ringRectInner = new SKRect[3];  // size * 0.5
		private readonly SKRect[] _ringRectSlow = new SKRect[3];  // size * 1.1
		private readonly SKRect[] _ringRectMagnet = new SKRect[3]; // magnet radius

		private readonly float[] _ringCxCache = new float[3];
		private readonly float[] _ringCyCache = new float[3];
		private bool _ringRectsValid;


		// ==== Cached HUD/labels to avoid per-frame string allocations ===============

		private readonly string[] _playerLabelCache = new string[3];        // "P1 (12)"
		private readonly int[] _playerCoinsCache = new int[3] { int.MinValue, int.MinValue, int.MinValue };
		private readonly string[] _playerIdCache = new string[3];           // "P1"



		// players + text


		private readonly SKPaint _playerFillPaint = new() { IsAntialias = true };
		private readonly SKPaint _outlinePaint = new()
		{
			IsAntialias = true,
			Color = SKColors.White,
			Style = SKPaintStyle.Stroke,
			StrokeWidth = 2f
		};

		private readonly SKPaint _textPaint = new()
		{
			IsAntialias = true,
			Color = SKColors.White,
			TextSize = 10
		};

		// rings (reuse one paint, just swap Color each time)
		private readonly SKPaint _ringPaint = new()
		{
			IsAntialias = true,
			Style = SKPaintStyle.Stroke,
			StrokeWidth = 2.5f
		};


		// Last snapshot from server
		private JumpsOnlineSnapshotPayload? _snapshot;

		public JumpsOnlineGameClient()
		{
			InitializeComponent();

			Loaded += OnLoaded;
			Unloaded += OnUnloaded;

			// Timer to send input + repaint at ~30 Hz
			_inputTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(16)
			};
			_inputTimer.Tick += InputTimer_Tick;
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			// Clear any stale keys from a previous screen
			InputService.Clear();

			// MainWindow will forward key events, so we don't need Focus hacks,
			// but grabbing focus doesn't hurt.
			Focus();
			_inputTimer.Start();
		}

		private void OnUnloaded(object? sender, RoutedEventArgs e)
		{
			_inputTimer.Stop();
			InputService.Clear();

			if (!_skiaDisposed)
			{
				DisposeSkiaResources();
				_skiaDisposed = true;
			}
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
				// Not in a room anymore
				_snapshot = null;

				PhaseText.Text = "Phase: Lobby";
				LevelText.Text = "Level: 1";
				ScrollSpeedText.Text = "Speed: 0";
				StatusText.Text = "Not in a room.";

				P1CoinsText.Text = "Coins: -";
				P2CoinsText.Text = "Coins: -";
				P3CoinsText.Text = "Coins: -";
				P1StateText.Text = "State: -";
				P2StateText.Text = "State: -";
				P3StateText.Text = "State: -";

				GameSurface.InvalidateVisual();
			}
			else
			{
				StatusText.Text = $"Joined as {playerId}";
			}
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			if (msg.MessageType == "JumpsOnlineSnapshot")
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

		// These now just forward to InputService.
		public void OnKeyDown(KeyEventArgs e)
		{
			InputService.OnKeyDown(e.Key);
		}

		public void OnKeyUp(KeyEventArgs e)
		{
			InputService.OnKeyUp(e.Key);
		}

		// ==== Snapshot handling =============================================

		private void HandleSnapshot(string payloadJson)
		{
			JumpsOnlineSnapshotPayload? snapshot;
			try
			{
				snapshot = JsonSerializer.Deserialize<JumpsOnlineSnapshotPayload>(payloadJson);
			}
			catch
			{
				return;
			}

			if (snapshot == null)
				return;

			_snapshot = snapshot;
			_ringRectsValid = false; // new snapshot; recalc ring rects on next draw

			PhaseText.Text = $"Phase: {snapshot.Phase}";
			LevelText.Text = $"Level: {snapshot.Level}";
			ScrollSpeedText.Text = $"Speed: {snapshot.ScrollSpeed:F1}";

			UpdatePlayerInfo(snapshot);



			switch (snapshot.Phase)
			{
				case JumpsOnlinePhase.Lobby:
					StatusText.Text = "Waiting in lobby...";
					break;
				case JumpsOnlinePhase.Countdown:
					StatusText.Text = "Get ready!";
					break;
				case JumpsOnlinePhase.Running:
					StatusText.Text = "Round in progress...";
					break;
				case JumpsOnlinePhase.Finished:
					if (snapshot.IsTie)
						StatusText.Text = "Round over: Tie!";
					else if (!string.IsNullOrEmpty(snapshot.WinnerPlayerId))
						StatusText.Text = $"Round over: {snapshot.WinnerPlayerId} wins!";
					else
						StatusText.Text = "Round over.";
					break;
			}
			// Big center countdown / GO overlay
			if (snapshot.Phase == JumpsOnlinePhase.Countdown)
			{
				CenterCountdownText.Visibility = Visibility.Visible;

				float t = snapshot.CountdownSecondsRemaining;
				string label;

				// 3.. 2.. 1.. GO!
				if (t > 2.0f)
					label = "3";
				else if (t > 1.0f)
					label = "2";
				else if (t > 0.2f)
					label = "1";
				else
					label = "GO!";

				CenterCountdownText.Text = label;
			}
			else
			{
				CenterCountdownText.Visibility = Visibility.Collapsed;
				CenterCountdownText.Text = "";
			}

			GameSurface.InvalidateVisual();
		}

		private string GetCachedPlayerLabel(JumpsOnlinePlayerStateDto p)
		{
			// assumes PlayerIndex is 0..2
			int i = p.PlayerIndex;
			if (i < 0 || i >= _playerLabelCache.Length)
			{
				// fallback for unexpected values
				return $"{p.PlayerId} ({p.Coins})";
			}

			// rebuild only if something changed
			if (!string.Equals(_playerIdCache[i], p.PlayerId, StringComparison.Ordinal) || _playerCoinsCache[i] != p.Coins)
			{
				_playerIdCache[i] = p.PlayerId;
				_playerCoinsCache[i] = p.Coins;
				_playerLabelCache[i] = string.Concat(p.PlayerId, " (", p.Coins.ToString(), ")");
			}

			return _playerLabelCache[i];
		}



		private void UpdatePlayerInfo(JumpsOnlineSnapshotPayload snapshot)
		{
			JumpsOnlinePlayerStateDto? p1 = snapshot.Players.FirstOrDefault(p => p.PlayerId == "P1");
			JumpsOnlinePlayerStateDto? p2 = snapshot.Players.FirstOrDefault(p => p.PlayerId == "P2");
			JumpsOnlinePlayerStateDto? p3 = snapshot.Players.FirstOrDefault(p => p.PlayerId == "P3");

			if (p1 != null)
			{
				P1CoinsText.Text = $"Coins: {p1.Coins}";
				P1StateText.Text = $"State: {(p1.IsAlive ? "Alive" : "Dead")}";
			}
			else
			{
				P1CoinsText.Text = "Coins: -";
				P1StateText.Text = "State: -";
			}

			if (p2 != null)
			{
				P2CoinsText.Text = $"Coins: {p2.Coins}";
				P2StateText.Text = $"State: {(p2.IsAlive ? "Alive" : "Dead")}";
			}
			else
			{
				P2CoinsText.Text = "Coins: -";
				P2StateText.Text = "State: -";
			}

			if (p3 != null)
			{
				P3CoinsText.Text = $"Coins: {p3.Coins}";
				P3StateText.Text = $"State: {(p3.IsAlive ? "Alive" : "Dead")}";
			}
			else
			{
				P3CoinsText.Text = "Coins: -";
				P3StateText.Text = "State: -";
			}
		}

		// ==== Input timer ===================================================

		private async void InputTimer_Tick(object? sender, EventArgs e)
		{
			if (_sendAsync == null || _isSocketOpen == null)
				return;

			if (!_isSocketOpen())
				return;

			if (string.IsNullOrEmpty(_roomCode))
				return;

			if (_snapshot == null)
				return;

			// Only send while countdown or running
			if (_snapshot.Phase != JumpsOnlinePhase.Running &&
				_snapshot.Phase != JumpsOnlinePhase.Countdown)
			{
				return;
			}

			// Read current input from InputService
			bool left =
				InputService.IsHeld(Key.A) ||
				InputService.IsHeld(Key.Left);

			bool right =
				InputService.IsHeld(Key.D) ||
				InputService.IsHeld(Key.Right);

			bool down =
				InputService.IsHeld(Key.S) ||
				InputService.IsHeld(Key.Down);

			bool jump =
				InputService.IsHeld(Key.Space) ||
				InputService.IsHeld(Key.W) ||
				InputService.IsHeld(Key.Up);

			// Has the input actually changed since last send?
			bool changed =
				left != _lastLeft ||
				right != _lastRight ||
				down != _lastDown ||
				jump != _lastJump;

			// Heartbeat: always send at least every 200ms while active
			var now = DateTime.UtcNow;
			bool heartbeatDue = (now - _lastSentInputTime).TotalMilliseconds >= 200;

			if (!changed && !heartbeatDue)
			{
				// Nothing new to report, skip sending
				return;
			}

			var payload = new JumpsOnlineInputPayload
			{
				Left = left,
				Right = right,
				Down = down,
				JumpHeld = jump,
				Sequence = 0
			};

			var msg = new HubMessage
			{
				MessageType = "JumpsOnlineInput",
				RoomCode = _roomCode,
				PlayerId = _playerId ?? "",
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			// update cache BEFORE sending so UI timing never depends on network
			_lastLeft = left;
			_lastRight = right;
			_lastDown = down;
			_lastJump = jump;
			_lastSentInputTime = now;

			// run send on threadpool; do not block UI thread
			var send = _sendAsync;
			_ = Task.Run(async () =>
			{
				try { await send!(msg); } catch { }
			});
		}

		// ==== Buttons: Start / Restart ======================================

		private async void StartButton_Click(object sender, RoutedEventArgs e)
		{
			if (_sendAsync == null || _isSocketOpen == null)
				return;

			if (!_isSocketOpen())
				return;

			if (string.IsNullOrEmpty(_roomCode))
				return;

			var payload = new JumpsOnlineStartRequestPayload
			{
				RoomCode = _roomCode
			};

			var msg = new HubMessage
			{
				MessageType = "JumpsOnlineStartRequest",
				RoomCode = _roomCode,
				PlayerId = _playerId ?? "",
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			try
			{
				await _sendAsync(msg);
			}
			catch
			{
			}
		}

		private async void RestartButton_Click(object sender, RoutedEventArgs e)
		{
			if (_sendAsync == null || _isSocketOpen == null)
				return;

			if (!_isSocketOpen())
				return;

			if (string.IsNullOrEmpty(_roomCode))
				return;

			var payload = new JumpsOnlineRestartRequestPayload
			{
				RoomCode = _roomCode
			};

			var msg = new HubMessage
			{
				MessageType = "JumpsOnlineRestartRequest",
				RoomCode = _roomCode,
				PlayerId = _playerId ?? "",
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			try
			{
				await _sendAsync(msg);
			}
			catch
			{
			}
		}

		// ==== Skia rendering =================================================

		private void GameSurface_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			canvas.Clear(new SKColor(10, 10, 20));

			if (_snapshot == null)
				return;

			float worldWidth = JumpsEngine.WorldWidth;
			float worldHeight = JumpsEngine.WorldHeight;

			float canvasWidth = e.Info.Width;
			float canvasHeight = e.Info.Height;

			float scale = Math.Min(canvasWidth / worldWidth, canvasHeight / worldHeight);
			float offsetX = (canvasWidth - worldWidth * scale) / 2f;
			float offsetY = (canvasHeight - worldHeight * scale) / 2f;

			canvas.Save();
			canvas.Translate(offsetX, offsetY);
			canvas.Scale(scale, scale);

			// Background (cached paint)
			canvas.DrawRect(0, 0, worldWidth, worldHeight, _bgPaint);

			DrawPlatforms(canvas, _snapshot);
			DrawPlayers(canvas, _snapshot);
			DrawPowerupRings(canvas, _snapshot);

			canvas.Restore();
		}

		private void DrawPlatforms(SKCanvas canvas, JumpsOnlineSnapshotPayload snapshot)
		{
			if (snapshot.Platforms == null || snapshot.Platforms.Count == 0)
				return;

			foreach (var plat in snapshot.Platforms)
			{
				// draw platform (cached)
				canvas.DrawRect(plat.X, plat.Y, plat.Width, plat.Height, _platPaint);

				var pickup = plat.Pickup;
				if (pickup == null || pickup.Collected)
					continue;

				float cx, cy;

				if (pickup.IsMagnetPulling && (pickup.X != 0f || pickup.Y != 0f))
				{
					cx = pickup.X;
					cy = pickup.Y;
				}
				else
				{
					cx = plat.X + plat.Width / 2f;
					cy = plat.Y - JumpsEngine.PickupRadius - 2f;
				}

				// choose cached paint
				SKPaint paintToUse = pickup.Type switch
				{
					JumpsOnlinePickupType.Coin => _coinPaint,
					JumpsOnlinePickupType.JumpBoost => _jumpPaint,
					JumpsOnlinePickupType.SpeedBoost => _speedPaint,
					JumpsOnlinePickupType.Magnet => _magnetPaint,
					JumpsOnlinePickupType.DoubleJump => _doublePaint,
					JumpsOnlinePickupType.SlowScroll => _slowPaint,
					_ => _coinPaint
				};

				canvas.DrawCircle(cx, cy, JumpsEngine.PickupRadius, paintToUse);
			}
		}


		private void DrawPlayers(SKCanvas canvas, JumpsOnlineSnapshotPayload snapshot)
		{
			if (snapshot.Players == null || snapshot.Players.Count == 0)
				return;

			float size = JumpsEngine.PlayerSize;

			foreach (var p in snapshot.Players)
			{
				// reuse a single fill paint, just swap its color
				_playerFillPaint.Color = GetPlayerColor(p.PlayerIndex);
				canvas.DrawRect(p.X, p.Y, size, size, _playerFillPaint);

				// Outline "me" (cached)
				if (!string.IsNullOrEmpty(_playerId) && p.PlayerId == _playerId)
				{
					canvas.DrawRect(p.X, p.Y, size, size, _outlinePaint);
				}

				// Label (cached text paint)
				string label = GetCachedPlayerLabel(p);
				float textWidth = _textPaint.MeasureText(label);
				canvas.DrawText(label, p.X + size / 2f - textWidth / 2f, p.Y - 4f, _textPaint);

			}
		}


		private void DrawPowerupRings(SKCanvas canvas, JumpsOnlineSnapshotPayload snapshot)
		{
			if (snapshot.Players == null || snapshot.Players.Count == 0)
				return;

			UpdateRingRectsIfNeeded(snapshot);

			static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

			foreach (var p in snapshot.Players)
			{
				int i = p.PlayerIndex;
				if (i < 0 || i >= 3) continue;

				// Jump boost
				if (p.JumpBoostActive && p.JumpBoostTimeRemaining > 0f)
				{
					float sweep = 360f * Clamp01(p.JumpBoostTimeRemaining / JumpsEngine.PowerupDuration);
					_ringPaint.Color = SKColors.MediumPurple;
					canvas.DrawArc(_ringRectOuter[i], -90f, sweep, false, _ringPaint);
				}

				// Speed boost
				if (p.SpeedBoostActive && p.SpeedBoostTimeRemaining > 0f)
				{
					float sweep = 360f * Clamp01(p.SpeedBoostTimeRemaining / JumpsEngine.PowerupDuration);
					_ringPaint.Color = SKColors.LimeGreen;
					canvas.DrawArc(_ringRectMid[i], -90f, sweep, false, _ringPaint);
				}

				// Magnet
				if (p.MagnetActive && p.MagnetTimeRemaining > 0f)
				{
					float sweep = 360f * Clamp01(p.MagnetTimeRemaining / JumpsEngine.PowerupDuration);
					_ringPaint.Color = SKColors.Red;
					canvas.DrawArc(_ringRectMagnet[i], -90f, sweep, false, _ringPaint);
				}

				// Double jump
				if (p.DoubleJumpActive && p.DoubleJumpTimeRemaining > 0f)
				{
					float sweep = 360f * Clamp01(p.DoubleJumpTimeRemaining / JumpsEngine.PowerupDuration);
					_ringPaint.Color = SKColors.SaddleBrown;
					canvas.DrawArc(_ringRectInner[i], -90f, sweep, false, _ringPaint);
				}

				// Slow scroll
				if (p.SlowScrollActive && p.SlowScrollTimeRemaining > 0f)
				{
					float sweep = 360f * Clamp01(p.SlowScrollTimeRemaining / JumpsEngine.PowerupDuration);
					_ringPaint.Color = SKColors.Cyan;
					canvas.DrawArc(_ringRectSlow[i], -90f, sweep, false, _ringPaint);
				}
			}
		}

		private void UpdateRingRectsIfNeeded(JumpsOnlineSnapshotPayload snapshot)
		{
			float size = JumpsEngine.PlayerSize;

			foreach (var p in snapshot.Players)
			{
				int i = p.PlayerIndex;
				if (i < 0 || i >= 3) continue;

				float cx = p.X + size / 2f;
				float cy = p.Y + size / 2f;

				// only recompute if center moved (or first time)
				if (!_ringRectsValid || cx != _ringCxCache[i] || cy != _ringCyCache[i])
				{
					_ringCxCache[i] = cx;
					_ringCyCache[i] = cy;

					float rOuter = size * 0.9f;
					float rMid = size * 0.7f;
					float rInner = size * 0.5f;
					float rSlow = size * 1.1f;
					float rMag = JumpsEngine.MagnetRadiusWorld;

					_ringRectOuter[i] = new SKRect(cx - rOuter, cy - rOuter, cx + rOuter, cy + rOuter);
					_ringRectMid[i] = new SKRect(cx - rMid, cy - rMid, cx + rMid, cy + rMid);
					_ringRectInner[i] = new SKRect(cx - rInner, cy - rInner, cx + rInner, cy + rInner);
					_ringRectSlow[i] = new SKRect(cx - rSlow, cy - rSlow, cx + rSlow, cy + rSlow);
					_ringRectMagnet[i] = new SKRect(cx - rMag, cy - rMag, cx + rMag, cy + rMag);
				}
			}

			_ringRectsValid = true;
		}


		private SKColor GetPlayerColor(int index)
		{
			return index switch
			{
				0 => new SKColor(102, 163, 255), // bluish
				1 => new SKColor(125, 255, 125), // greenish
				2 => new SKColor(255, 128, 255), // magenta
				_ => SKColors.Gray
			};
		}

		private void DisposeSkiaResources()
		{
			_bgPaint.Dispose();
			_platPaint.Dispose();

			_coinPaint.Dispose();
			_jumpPaint.Dispose();
			_speedPaint.Dispose();
			_magnetPaint.Dispose();
			_doublePaint.Dispose();
			_slowPaint.Dispose();

			_playerFillPaint.Dispose();
			_outlinePaint.Dispose();
			_textPaint.Dispose();
			_ringPaint.Dispose();
		}

	}
}

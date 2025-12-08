using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameClient.Wpf;          // <- for IGameClient
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

		// Input state (driven by MainWindow → OnKeyDown/OnKeyUp)
		private bool _leftHeld;
		private bool _rightHeld;
		private bool _downHeld;
		private bool _jumpHeld;

		private readonly DispatcherTimer _inputTimer;

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
				Interval = TimeSpan.FromMilliseconds(33)
			};
			_inputTimer.Tick += InputTimer_Tick;
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			// MainWindow will forward key events, so we don't need Focus hacks,
			// but grabbing focus doesn't hurt.
			Focus();
			_inputTimer.Start();
		}

		private void OnUnloaded(object? sender, RoutedEventArgs e)
		{
			_inputTimer.Stop();
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

				RoomText.Text = "Room: -";
				PhaseText.Text = "Phase: Lobby";
				LevelText.Text = "Level: 1";
				ScrollSpeedText.Text = "Speed: 0";
				CountdownText.Text = "Countdown: -";
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
				RoomText.Text = $"Room: {roomCode}";
				StatusText.Text = $"Joined as {playerId}";
			}
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			if (msg.MessageType == "JumpsOnlineSnapshot")
			{
				HandleSnapshot(msg.PayloadJson);
				return true;
			}

			return false;
		}

		public void OnKeyDown(KeyEventArgs e)
		{
			switch (e.Key)
			{
				case Key.Left:
				case Key.A:
					_leftHeld = true;
					break;
				case Key.Right:
				case Key.D:
					_rightHeld = true;
					break;
				case Key.Down:
				case Key.S:
					_downHeld = true;
					break;
				case Key.Space:
				case Key.W:
				case Key.Up:
					_jumpHeld = true;
					break;
			}
		}

		public void OnKeyUp(KeyEventArgs e)
		{
			switch (e.Key)
			{
				case Key.Left:
				case Key.A:
					_leftHeld = false;
					break;
				case Key.Right:
				case Key.D:
					_rightHeld = false;
					break;
				case Key.Down:
				case Key.S:
					_downHeld = false;
					break;
				case Key.Space:
				case Key.W:
				case Key.Up:
					_jumpHeld = false;
					break;
			}
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

			PhaseText.Text = $"Phase: {snapshot.Phase}";
			LevelText.Text = $"Level: {snapshot.Level}";
			ScrollSpeedText.Text = $"Speed: {snapshot.ScrollSpeed:F1}";
			CountdownText.Text = snapshot.Phase == JumpsOnlinePhase.Countdown
				? $"Countdown: {snapshot.CountdownSecondsRemaining:F1}"
				: "Countdown: -";

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

			GameSurface.InvalidateVisual();
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

			var payload = new JumpsOnlineInputPayload
			{
				Left = _leftHeld,
				Right = _rightHeld,
				Down = _downHeld,
				JumpHeld = _jumpHeld,
				Sequence = 0 // you can increment this later
			};

			var msg = new HubMessage
			{
				MessageType = "JumpsOnlineInput",
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
				// ignore for now
			}
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

			// Background
			using (var bgPaint = new SKPaint { Color = new SKColor(15, 15, 40) })
			{
				canvas.DrawRect(0, 0, worldWidth, worldHeight, bgPaint);
			}

			DrawPlatforms(canvas, _snapshot);
			DrawPlayers(canvas, _snapshot);

			canvas.Restore();
		}

		private void DrawPlatforms(SKCanvas canvas, JumpsOnlineSnapshotPayload snapshot)
		{
			if (snapshot.Platforms == null || snapshot.Platforms.Count == 0)
				return;

			using var platPaint = new SKPaint
			{
				IsAntialias = true,
				Color = new SKColor(90, 90, 130)
			};

			using var coinPaint = new SKPaint
			{
				IsAntialias = true,
				Color = new SKColor(255, 215, 0)
			};

			foreach (var plat in snapshot.Platforms)
			{
				canvas.DrawRect(plat.X, plat.Y, plat.Width, plat.Height, platPaint);

				if (plat.Pickup is { Collected: false } pickup &&
					pickup.Type == JumpsOnlinePickupType.Coin)
				{
					float r = JumpsEngine.PickupRadius;
					canvas.DrawCircle(pickup.X, pickup.Y, r, coinPaint);
				}
			}
		}

		private void DrawPlayers(SKCanvas canvas, JumpsOnlineSnapshotPayload snapshot)
		{
			if (snapshot.Players == null || snapshot.Players.Count == 0)
				return;

			foreach (var p in snapshot.Players)
			{
				var color = GetPlayerColor(p.PlayerIndex);

				using var paint = new SKPaint
				{
					IsAntialias = true,
					Color = color
				};

				float size = JumpsEngine.PlayerSize;
				canvas.DrawRect(p.X, p.Y, size, size, paint);

				// Outline "me"
				if (!string.IsNullOrEmpty(_playerId) && p.PlayerId == _playerId)
				{
					using var outline = new SKPaint
					{
						IsAntialias = true,
						Color = SKColors.White,
						Style = SKPaintStyle.Stroke,
						StrokeWidth = 2f
					};
					canvas.DrawRect(p.X, p.Y, size, size, outline);
				}

				// Label (P1/P2/P3 + coins)
				using var textPaint = new SKPaint
				{
					Color = SKColors.White,
					TextSize = 10,
					IsAntialias = true
				};

				string label = $"{p.PlayerId} ({p.Coins})";
				float textWidth = textPaint.MeasureText(label);
				canvas.DrawText(label, p.X + size / 2f - textWidth / 2f, p.Y - 4f, textPaint);
			}
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
	}
}

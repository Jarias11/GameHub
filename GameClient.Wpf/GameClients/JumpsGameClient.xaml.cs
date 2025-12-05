using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GameContracts;

namespace GameClient.Wpf.GameClients
{
	public partial class JumpsGameClient : UserControl, IGameClient
	{
		// ==== IGameClient plumbing ==========================================

		public GameType GameType => GameType.Jumps;

		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		public void SetConnection(Func<HubMessage, Task> sendAsync, Func<bool> isSocketOpen)
		{
			_sendAsync = sendAsync;
			_isSocketOpen = isSocketOpen;
			// Offline-only for now — no usage yet.
		}

		public void OnRoomChanged(string? roomCode, string? playerId)
		{
			// For this offline arcade game we just reset whenever you "enter" the game.
			if (!string.IsNullOrEmpty(roomCode))
			{
				ResetGame();
			}
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			// Jumps is offline-only — no messages handled.
			return false;
		}

		public void OnKeyDown(KeyEventArgs e)
		{
			switch (e.Key)
			{
				case Key.Left:
				case Key.A:
					_pendingDirection = -1;
					break;

				case Key.Right:
				case Key.D:
					_pendingDirection = 1;
					break;

				case Key.Space:
					HandleJumpInput();
					break;

				case Key.R:
					ResetGame();
					break;
			}
		}

		public void OnKeyUp(KeyEventArgs e)
		{
			// If you ever want "direction only while held", you can clear _pendingDirection here.
			// For now we leave it sticky so you can tap a direction then hit Space.
		}

		// ==== Game constants ================================================

		private const int Columns = 3;

		private const double PlayerSize = 24;
		private const double PlatformWidth = 80;
		private const double PlatformHeight = 8;
		private const double RowSpacing = 80;

		private const double Gravity = 1200;      // px/s^2
		private const double JumpVelocity = -650; // px/s (negative = up)

		// ==== Game state ====================================================

		private readonly DispatcherTimer _timer;
		private DateTime _lastFrameTime;

		private readonly Random _rng = new();

		private Rectangle? _playerRect;
		private double _playerX;
		private double _playerY;
		private double _playerVY;
		private bool _isGrounded;

		// Lanes 0 (left), 1 (middle), 2 (right)
		private int _currentLane;
		private int _targetLane;
		private int _pendingDirection; // -1, 0, +1

		// Ground visual Y position (top of ground bar)
		private double _groundY;

		private sealed class Platform
		{
			public int RowIndex { get; set; }
			public int LaneIndex { get; set; }
			public Rectangle Rect { get; set; } = null!;
		}

		private readonly List<Platform> _platforms = new();

		// ====================================================================

		public JumpsGameClient()
		{
			InitializeComponent();

			_timer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
			};
			_timer.Tick += GameLoop;
		}

		private void UserControl_Loaded(object sender, RoutedEventArgs e)
		{
			// Focus isn't strictly required since MainWindow forwards keys,
			// but it doesn't hurt if this control ever needs focus.
			Focus();

			InitializeGame();
			_lastFrameTime = DateTime.Now;
			_timer.Start();
		}

		// ==== Initialization / reset ========================================

		private void InitializeGame()
		{
			_platforms.Clear();
			GameCanvas.Children.Clear();

			_groundY = GameCanvas.Height - 40;

			var ground = new Rectangle
			{
				Width = GameCanvas.Width,
				Height = 6,
				Fill = new SolidColorBrush(Color.FromRgb(60, 60, 60))
			};
			Canvas.SetLeft(ground, 0);
			Canvas.SetTop(ground, _groundY);
			GameCanvas.Children.Add(ground);

			// Player
			_playerRect = new Rectangle
			{
				Width = PlayerSize,
				Height = PlayerSize,
				Fill = Brushes.DeepSkyBlue,
				RadiusX = 4,
				RadiusY = 4
			};
			GameCanvas.Children.Add(_playerRect);

			_currentLane = 1; // middle
			_targetLane = _currentLane;
			_pendingDirection = 0;
			_isGrounded = true;
			_playerVY = 0;

			_playerX = GetLaneCenterX(_currentLane) - PlayerSize / 2;
			_playerY = _groundY - PlayerSize;
			UpdatePlayerVisual();

			// 3x3-ish feel (3 columns, multiple rows); start with 10 rows above.
			for (int row = 0; row < 10; row++)
			{
				GeneratePlatformsForRow(row);
			}
		}

		private void ResetGame()
		{
			InitializeGame();
		}

		private void GeneratePlatformsForRow(int rowIndex)
		{
			// At least 1 platform, sometimes 2, never 3
			int platformCount = _rng.Next(1, 3); // 1 or 2
			var lanes = Enumerable.Range(0, Columns)
				.OrderBy(_ => _rng.Next())
				.Take(platformCount);

			foreach (int lane in lanes)
			{
				var rect = new Rectangle
				{
					Width = PlatformWidth,
					Height = PlatformHeight,
					Fill = Brushes.LightGray
				};

				double x = GetLaneCenterX(lane) - PlatformWidth / 2;
				double y = GetRowY(rowIndex);

				Canvas.SetLeft(rect, x);
				Canvas.SetTop(rect, y);

				GameCanvas.Children.Add(rect);

				_platforms.Add(new Platform
				{
					RowIndex = rowIndex,
					LaneIndex = lane,
					Rect = rect
				});
			}
		}

		private double GetLaneCenterX(int laneIndex)
		{
			double laneWidth = GameCanvas.Width / Columns;
			return laneWidth * (laneIndex + 0.5);
		}

		private double GetRowY(int rowIndex)
		{
			// Rows go upward from the ground
			return _groundY - (rowIndex + 1) * RowSpacing;
		}

		// ==== Input helpers ================================================

		private void HandleJumpInput()
		{
			if (!_isGrounded)
				return;

			// Decide which lane to aim for based on the pending direction
			int newLane = _currentLane + _pendingDirection;
			newLane = Math.Max(0, Math.Min(Columns - 1, newLane));

			_targetLane = newLane;
			StartJump();
		}

		private void StartJump()
		{
			if (!_isGrounded)
				return;

			_isGrounded = false;
			_playerVY = JumpVelocity;
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

			UpdatePhysics(dt);
			UpdatePlayerVisual();
		}

		private void UpdatePhysics(double dt)
		{
			if (_playerRect == null)
				return;

			// Horizontal lane interpolation (smoothly move toward target lane)
			double targetX = GetLaneCenterX(_targetLane) - PlayerSize / 2;
			if (Math.Abs(targetX - _playerX) > 1)
			{
				double lerpSpeed = 10.0; // higher = faster
				_playerX += (targetX - _playerX) * Math.Min(1.0, lerpSpeed * dt);
			}
			else
			{
				_playerX = targetX;
			}

			// Vertical motion (simple physics)
			double prevY = _playerY;
			_playerVY += Gravity * dt;
			_playerY += _playerVY * dt;

			// Check landing on platforms only when falling
			if (_playerVY > 0)
			{
				double prevBottom = prevY + PlayerSize;
				double bottom = _playerY + PlayerSize;

				foreach (var p in _platforms)
				{
					double platformY = Canvas.GetTop(p.Rect);

					// Did we cross this platform between frames?
					if (prevBottom <= platformY && bottom >= platformY)
					{
						double playerCenterX = _playerX + PlayerSize / 2;
						double platformLeft = Canvas.GetLeft(p.Rect);
						double platformRight = platformLeft + PlatformWidth;

						if (playerCenterX >= platformLeft && playerCenterX <= platformRight)
						{
							// Land on this platform
							_playerY = platformY - PlayerSize;
							_playerVY = 0;
							_isGrounded = true;
							_currentLane = p.LaneIndex;
							_pendingDirection = 0;
							return;
						}
					}
				}
			}

			// If we fall off the bottom, just reset for now
			if (_playerY > GameCanvas.Height)
			{
				ResetGame();
			}
		}

		private void UpdatePlayerVisual()
		{
			if (_playerRect == null)
				return;

			Canvas.SetLeft(_playerRect, _playerX);
			Canvas.SetTop(_playerRect, _playerY);
		}
	}
}

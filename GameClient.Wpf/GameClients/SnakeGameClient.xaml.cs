using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameContracts;
using GameClient.Wpf.ClientServices;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace GameClient.Wpf.GameClients
{
	public partial class SnakeGameClient : UserControl, IGameClient
	{
		// ==== IGameClient plumbing ==========================================

		public GameType GameType => GameType.Snake;

		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		public SnakeGameClient()
		{
			InitializeComponent();

			// ✅ Give Skia a real surface size in WPF DIPs
			GameSurface.Width = Cols * CellSize;   // 40 * 20 = 800
			GameSurface.Height = Rows * CellSize;   // 20 * 20 = 400

			_timer.Interval = TimeSpan.FromMilliseconds(120); // tweak for speed
			_timer.Tick += OnTimerTick;

			ResetGame();
		}

		private void OnTimerTick(object? sender, EventArgs e) => Tick();

		public void SetConnection(Func<HubMessage, Task> sendAsync, Func<bool> isSocketOpen)
		{
			_sendAsync = sendAsync;   // unused (offline) but kept for interface consistency
			_isSocketOpen = isSocketOpen;
		}

		public void OnRoomChanged(string? roomCode, string? playerId)
		{
			// For offline Snake, just restart whenever we become the active view
			ResetGame();
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			// Offline game: never handles any server messages
			return false;
		}

		/// <summary>
		/// Forwarded KeyDown from MainWindow.
		/// We now route keys through InputService + still handle R and direction queuing.
		/// </summary>
		public void OnKeyDown(KeyEventArgs e)
		{
			// Global tracking of held keys
			InputService.OnKeyDown(e.Key);

			// Always allow restart
			if (e.Key == Key.R)
			{
				ResetGame();
				return;
			}

			if (!_isAlive) return;

			switch (e.Key)
			{
				case Key.Up:
				case Key.W:
					QueueDirection(0, -1);
					break;

				case Key.Down:
				case Key.S:
					QueueDirection(0, 1);
					break;

				case Key.Left:
				case Key.A:
					QueueDirection(-1, 0);
					break;

				case Key.Right:
				case Key.D:
					QueueDirection(1, 0);
					break;
			}
		}

		/// <summary>
		/// Forwarded KeyUp from MainWindow.
		/// We update InputService and then use it to decide whether to "fallback"
		/// to a remaining held direction (Right after releasing Up, etc.).
		/// </summary>
		public void OnKeyUp(KeyEventArgs e)
		{
			// Update global held-keys state
			InputService.OnKeyUp(e.Key);

			if (!_isAlive) return;

			switch (e.Key)
			{
				case Key.Up:
				case Key.W:
					HandleKeyReleased(verticalReleased: true);
					break;

				case Key.Down:
				case Key.S:
					HandleKeyReleased(verticalReleased: true);
					break;

				case Key.Left:
				case Key.A:
					HandleKeyReleased(verticalReleased: false);
					break;

				case Key.Right:
				case Key.D:
					HandleKeyReleased(verticalReleased: false);
					break;
			}
		}

		// ==== Snake game state ===============================================

		private readonly DispatcherTimer _timer = new();

		private const int Rows = 20;
		private const int Cols = 40;
		private const double CellSize = 20.0; // logical size; Skia will scale

		private readonly LinkedList<(int x, int y)> _snake = new();
		private (int x, int y) _direction = (1, 0); // moving right
		private (int x, int y) _food;
		private bool _isAlive;
		private readonly Random _rng = new();

		private int _score;

		private (int x, int y)? _nextDirection; // buffered direction for the next tick

		private void ResetGame()
		{
			_timer.Stop();

			_snake.Clear();

			// Start roughly middle of board
			var startX = Cols / 2;
			var startY = Rows / 2;

			_snake.AddLast((startX - 1, startY));
			_snake.AddLast((startX, startY));
			_snake.AddLast((startX + 1, startY));

			_direction = (1, 0);
			_nextDirection = null;

			_isAlive = true;
			_score = 0;
			ScoreText.Text = "0";

			// Clear any stale key state when restarting
			InputService.Clear();

			SpawnFood();
			_timer.Start();

			// Trigger Skia redraw
			GameSurface.InvalidateVisual();
		}

		/// <summary>
		/// Called when a vertical or horizontal key is released.
		/// Uses InputService to check what other keys are still held and
		/// potentially "fallback" to a remaining direction (e.g. still holding Right).
		/// </summary>
		private void HandleKeyReleased(bool verticalReleased)
		{
			if (!_isAlive) return;

			// Helper: current movement (pending or active)
			var current = _nextDirection ?? _direction;

			// Read current held state from InputService (arrow + WASD equivalents).
			bool upHeld =
				InputService.IsHeld(Key.Up) || InputService.IsHeld(Key.W);
			bool downHeld =
				InputService.IsHeld(Key.Down) || InputService.IsHeld(Key.S);
			bool leftHeld =
				InputService.IsHeld(Key.Left) || InputService.IsHeld(Key.A);
			bool rightHeld =
				InputService.IsHeld(Key.Right) || InputService.IsHeld(Key.D);

			// If we released a vertical key (Up/Down), see if we should fall back to a horizontal one.
			if (verticalReleased)
			{
				// Only do this if we're currently moving vertically (or have a vertical queued).
				if (current.y != 0)
				{
					// Prefer whichever horizontal key is still held (if only one).
					if (rightHeld && !leftHeld)
					{
						ForceQueueDirection(1, 0);
					}
					else if (leftHeld && !rightHeld)
					{
						ForceQueueDirection(-1, 0);
					}
				}
			}
			else // released a horizontal key (Left/Right)
			{
				if (current.x != 0)
				{
					if (upHeld && !downHeld)
					{
						ForceQueueDirection(0, -1);
					}
					else if (downHeld && !upHeld)
					{
						ForceQueueDirection(0, 1);
					}
				}
			}
		}

		private void ForceQueueDirection(int dx, int dy)
		{
			if (!_isAlive) return;

			var current = _nextDirection ?? _direction;

			// Still respect the "no instant reverse" rule
			if (current.x == -dx && current.y == -dy)
				return;

			_nextDirection = (dx, dy);
		}

		private void SpawnFood()
		{
			while (true)
			{
				var x = _rng.Next(0, Cols);
				var y = _rng.Next(0, Rows);

				if (_snake.All(seg => seg.x != x || seg.y != y))
				{
					_food = (x, y);
					break;
				}
			}
		}

		private void QueueDirection(int dx, int dy)
		{
			if (!_isAlive)
				return;

			// Use the most up-to-date direction (pending one if exists, else current)
			var current = _nextDirection ?? _direction;

			// 1) If this input is the SAME as the current direction, ignore it.
			if (current.x == dx && current.y == dy)
				return;

			// 2) Prevent instant reverse (e.g. going right -> left)
			if (current.x == -dx && current.y == -dy)
				return;

			// 3) If we already queued a NEW direction for this tick, ignore extras
			if (_nextDirection.HasValue)
				return;

			_nextDirection = (dx, dy);
		}

		private void Tick()
		{
			if (!_isAlive) return;

			// Apply any queued direction change for this tick
			if (_nextDirection.HasValue)
			{
				_direction = _nextDirection.Value;
				_nextDirection = null;
			}

			var head = _snake.Last!.Value;
			var newHead = (x: head.x + _direction.x, y: head.y + _direction.y);

			// Hit wall?
			if (newHead.x < 0 || newHead.x >= Cols || newHead.y < 0 || newHead.y >= Rows)
			{
				GameOver();
				return;
			}

			// Hit self?
			if (_snake.Any(seg => seg.x == newHead.x && seg.y == newHead.y))
			{
				GameOver();
				return;
			}

			// Add new head
			_snake.AddLast(newHead);

			// Check food
			if (newHead.x == _food.x && newHead.y == _food.y)
			{
				_score += 10;
				ScoreText.Text = _score.ToString();
				SpawnFood();
			}
			else
			{
				// Move forward: remove tail
				_snake.RemoveFirst();
			}

			// Ask Skia to redraw the scene with updated state
			GameSurface.InvalidateVisual();
		}

		private void GameOver()
		{
			_isAlive = false;
			_timer.Stop();

			// Simple "game over" status
			ScoreText.Text = $"{_score} (Game Over - press R to restart)";

			// Still redraw so we see final state
			GameSurface.InvalidateVisual();
		}

		// ==== Skia drawing ===================================================

		private void GameSurface_OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			var info = e.Info;

			// Clear with a dark background similar to your WPF color
			canvas.Clear(new SKColor(0x02, 0x06, 0x17)); // #020617

			// Compute cell size based on surface size
			float cellWidth = (float)info.Width / Cols;
			float cellHeight = (float)info.Height / Rows;
			float cellSize = Math.Min(cellWidth, cellHeight);

			// Center the board if the container isn't exactly aspect-matched
			float boardWidth = cellSize * Cols;
			float boardHeight = cellSize * Rows;

			float offsetX = (info.Width - boardWidth) / 2f;
			float offsetY = (info.Height - boardHeight) / 2f;

			// Helper to convert grid coords -> Skia rect
			SKRect CellRect(int x, int y)
			{
				float left = offsetX + x * cellSize + 1;
				float top = offsetY + y * cellSize + 1;
				float size = cellSize - 2; // small padding like before
				return new SKRect(left, top, left + size, top + size);
			}

			using var foodPaint = new SKPaint
			{
				Color = SKColors.OrangeRed,
				IsAntialias = true
			};

			using var snakePaint = new SKPaint
			{
				Color = SKColors.LimeGreen,
				IsAntialias = true
			};

			// Draw food
			var foodRect = CellRect(_food.x, _food.y);
			canvas.DrawRect(foodRect, foodPaint);

			// Draw snake
			foreach (var seg in _snake)
			{
				var segRect = CellRect(seg.x, seg.y);
				canvas.DrawRect(segRect, snakePaint);
			}
		}
	}
}

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

namespace GameClient.Wpf
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

			// We can safely touch GameCanvas after InitializeComponent
			GameCanvas.Width = Cols * CellSize;
			GameCanvas.Height = Rows * CellSize;

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

		public void OnKeyDown(KeyEventArgs e)
		{
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
					_upHeld = true;
					QueueDirection(0, -1);
					break;

				case Key.Down:
				case Key.S:
					_downHeld = true;
					QueueDirection(0, 1);
					break;

				case Key.Left:
				case Key.A:
					_leftHeld = true;
					QueueDirection(-1, 0);
					break;

				case Key.Right:
				case Key.D:
					_rightHeld = true;
					QueueDirection(1, 0);
					break;
			}
		}

		public void OnKeyUp(KeyEventArgs e)
		{
			if (!_isAlive) return;

			switch (e.Key)
			{
				case Key.Up:
				case Key.W:
					_upHeld = false;
					HandleKeyReleased(verticalReleased: true);
					break;

				case Key.Down:
				case Key.S:
					_downHeld = false;
					HandleKeyReleased(verticalReleased: true);
					break;

				case Key.Left:
				case Key.A:
					_leftHeld = false;
					HandleKeyReleased(verticalReleased: false);
					break;

				case Key.Right:
				case Key.D:
					_rightHeld = false;
					HandleKeyReleased(verticalReleased: false);
					break;
			}
		}

		// ==== Snake game state ===============================================

		private readonly DispatcherTimer _timer = new();

		private const int Rows = 20;
		private const int Cols = 40;
		private const double CellSize = 20.0;

		private bool _upHeld, _downHeld, _leftHeld, _rightHeld;

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

			_upHeld = _downHeld = _leftHeld = _rightHeld = false;

			_isAlive = true;
			_score = 0;
			ScoreText.Text = "0";

			SpawnFood();
			_timer.Start();
			Redraw();
		}

		private void HandleKeyReleased(bool verticalReleased)
		{
			if (!_isAlive) return;

			// If we released a vertical key (Up/Down), see if we should fall back to a horizontal one.
			if (verticalReleased)
			{
				// Only do this if we're currently moving vertically (or have a vertical queued).
				var current = _nextDirection ?? _direction;
				if (current.y != 0)
				{
					// Prefer whichever horizontal key is still held (if only one).
					if (_rightHeld && !_leftHeld)
					{
						ForceQueueDirection(1, 0);
					}
					else if (_leftHeld && !_rightHeld)
					{
						ForceQueueDirection(-1, 0);
					}
				}
			}
			else // released a horizontal key (Left/Right)
			{
				var current = _nextDirection ?? _direction;
				if (current.x != 0)
				{
					if (_upHeld && !_downHeld)
					{
						ForceQueueDirection(0, -1);
					}
					else if (_downHeld && !_upHeld)
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

			Redraw();
		}

		private void GameOver()
		{
			_isAlive = false;
			_timer.Stop();

			// Simple "game over" status
			ScoreText.Text = $"{_score} (Game Over - press R to restart)";
		}

		private void Redraw()
		{
			GameCanvas.Children.Clear();

			// Draw food
			DrawCell(_food.x, _food.y, Brushes.OrangeRed);

			// Draw snake
			foreach (var segment in _snake)
			{
				DrawCell(segment.x, segment.y, Brushes.LimeGreen);
			}
		}

		private void DrawCell(int x, int y, Brush brush)
		{
			var rect = new Rectangle
			{
				Width = CellSize - 2,
				Height = CellSize - 2,
				Fill = brush,
				RadiusX = 3,
				RadiusY = 3
			};

			Canvas.SetLeft(rect, x * CellSize + 1);
			Canvas.SetTop(rect, y * CellSize + 1);
			GameCanvas.Children.Add(rect);
		}
	}
}

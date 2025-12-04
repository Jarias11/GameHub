using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GameContracts;

namespace GameClient.Wpf
{
	public partial class NumberTilesGameClient : UserControl, IGameClient
	{
		private const int Size = 4;
		private readonly int[,] _board = new int[Size, Size];
		private readonly Border[,] _tileBorders = new Border[Size, Size];
		private readonly TextBlock[,] _tileTexts = new TextBlock[Size, Size];

		private readonly Random _rng = new();
		private int _score;
		private bool _isAnimating;

		// ==== IGameClient plumbing =====================================

		public GameType GameType => GameType.NumberTiles;
		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		public NumberTilesGameClient()
		{
			InitializeComponent();
			BuildBoardVisuals();
			NewGame();
		}

		public void SetConnection(Func<HubMessage, Task> sendAsync, Func<bool> isSocketOpen)
		{
			_sendAsync = sendAsync;
			_isSocketOpen = isSocketOpen;
			// This game is offline-only right now; no messages needed.
		}

		public void OnRoomChanged(string? roomCode, string? playerId)
		{
			// Single-player offline game; nothing special for rooms.
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			// No server messages for this offline game.
			return false;
		}

		public void OnKeyDown(KeyEventArgs e)
		{
			if (_isAnimating) return;

			bool moved = false;

			switch (e.Key)
			{
				case Key.Left:
				case Key.A:
					moved = MoveLeft();
					break;
				case Key.Right:
				case Key.D:
					moved = MoveRight();
					break;
				case Key.Up:
				case Key.W:
					moved = MoveUp();
					break;
				case Key.Down:
				case Key.S:
					moved = MoveDown();
					break;
			}

			if (moved)
			{
				SpawnRandomTile();
				UpdateBoardVisuals();
				CheckGameOver();
			}
		}

		public void OnKeyUp(KeyEventArgs e)
		{
			// Not needed for this game.
		}

		// ==== UI construction ==========================================

		private void BuildBoardVisuals()
		{
			BoardGrid.Children.Clear();

			for (int r = 0; r < Size; r++)
			{
				for (int c = 0; c < Size; c++)
				{
					var border = new Border
					{
						Margin = new Thickness(2),
						CornerRadius = new CornerRadius(6),
						Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
						HorizontalAlignment = HorizontalAlignment.Stretch,
						VerticalAlignment = VerticalAlignment.Stretch
					};

					// Enable simple scale animation on each tile
					var transform = new ScaleTransform(1.0, 1.0);
					border.RenderTransformOrigin = new Point(0.5, 0.5);
					border.RenderTransform = transform;

					var text = new TextBlock
					{
						Text = string.Empty,
						HorizontalAlignment = HorizontalAlignment.Center,
						VerticalAlignment = VerticalAlignment.Center,
						FontSize = 20,
						FontWeight = FontWeights.Bold
					};

					border.Child = text;

					BoardGrid.Children.Add(border);
					_tileBorders[r, c] = border;
					_tileTexts[r, c] = text;
				}
			}
		}

		// ==== Game lifecycle ===========================================

		private void NewGameButton_Click(object sender, RoutedEventArgs e)
		{
			NewGame();
		}

		private void NewGame()
		{
			Array.Clear(_board, 0, _board.Length);
			_score = 0;
			ScoreText.Text = "0";

			SpawnRandomTile();
			SpawnRandomTile();
			UpdateBoardVisuals();
		}
		

		// ==== Game logic: movement / merging ===========================

		private bool MoveLeft()
		{
			bool moved = false;

			for (int r = 0; r < Size; r++)
			{
				int[] line = new int[Size];
				for (int c = 0; c < Size; c++)
					line[c] = _board[r, c];

				if (CompressAndMergeLine(line))
					moved = true;

				for (int c = 0; c < Size; c++)
					_board[r, c] = line[c];
			}

			return moved;
		}

		private bool MoveRight()
		{
			bool moved = false;

			for (int r = 0; r < Size; r++)
			{
				int[] line = new int[Size];
				for (int c = 0; c < Size; c++)
					line[c] = _board[r, Size - 1 - c];

				if (CompressAndMergeLine(line))
					moved = true;

				for (int c = 0; c < Size; c++)
					_board[r, Size - 1 - c] = line[c];
			}

			return moved;
		}

		private bool MoveUp()
		{
			bool moved = false;

			for (int c = 0; c < Size; c++)
			{
				int[] line = new int[Size];
				for (int r = 0; r < Size; r++)
					line[r] = _board[r, c];

				if (CompressAndMergeLine(line))
					moved = true;

				for (int r = 0; r < Size; r++)
					_board[r, c] = line[r];
			}

			return moved;
		}

		private bool MoveDown()
		{
			bool moved = false;

			for (int c = 0; c < Size; c++)
			{
				int[] line = new int[Size];
				for (int r = 0; r < Size; r++)
					line[r] = _board[Size - 1 - r, c];

				if (CompressAndMergeLine(line))
					moved = true;

				for (int r = 0; r < Size; r++)
					_board[Size - 1 - r, c] = line[r];
			}

			return moved;
		}

		/// <summary>
		/// Classic 2048 logic for a 1D line:
		/// 1) Compress non-zero tiles to the left.
		/// 2) Merge equal neighbors once.
		/// 3) Compress again.
		/// Returns true if anything changed.
		/// </summary>
		private bool CompressAndMergeLine(int[] line)
		{
			bool changed = false;

			// 1) compress
			int[] original = (int[])line.Clone();
			int[] compressed = line.Where(v => v != 0).ToArray();
			Array.Clear(line, 0, line.Length);
			for (int i = 0; i < compressed.Length; i++)
				line[i] = compressed[i];

			// 2) merge
			for (int i = 0; i < Size - 1; i++)
			{
				if (line[i] != 0 && line[i] == line[i + 1])
				{
					int mergedValue = line[i] * 2;
					line[i] = mergedValue;
					line[i + 1] = 0;

					// score = sum of merged tile values
					_score += mergedValue;
				}
			}

			// 3) compress again
			compressed = line.Where(v => v != 0).ToArray();
			Array.Clear(line, 0, line.Length);
			for (int i = 0; i < compressed.Length; i++)
				line[i] = compressed[i];

			changed = !line.SequenceEqual(original);
			if (changed)
				ScoreText.Text = _score.ToString();

			return changed;
		}

		// ==== Spawning and game over ===================================

		private void SpawnRandomTile()
		{
			var empties = Enumerable.Range(0, Size * Size)
				.Select(i => (Row: i / Size, Col: i % Size))
				.Where(rc => _board[rc.Row, rc.Col] == 0)
				.ToList();

			if (empties.Count == 0)
				return;

			var (r, c) = empties[_rng.Next(empties.Count)];
			int value = _rng.Next(0, 10) == 0 ? 4 : 2; // ~10% chance of 4
			_board[r, c] = value;

			// Animate spawn
			AnimateTileSpawn(r, c);
		}

		private void CheckGameOver()
		{
			// If there is any empty cell -> not game over
			for (int r = 0; r < Size; r++)
				for (int c = 0; c < Size; c++)
				{
					if (_board[r, c] == 0)
						return;
				}

			// Check for any possible merge horizontally or vertically
			for (int r = 0; r < Size; r++)
				for (int c = 0; c < Size; c++)
				{
					int v = _board[r, c];
					if ((r < Size - 1 && _board[r + 1, c] == v) ||
						(c < Size - 1 && _board[r, c + 1] == v))
						return;
				}

			MessageBox.Show("No more moves! Game over.", "2048", MessageBoxButton.OK,
				MessageBoxImage.Information);
		}

		// ==== Visual update ============================================

		private void UpdateBoardVisuals()
		{
			for (int r = 0; r < Size; r++)
			{
				for (int c = 0; c < Size; c++)
				{
					int value = _board[r, c];
					var border = _tileBorders[r, c];
					var text = _tileTexts[r, c];

					if (value == 0)
					{
						text.Text = string.Empty;
						text.Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180));
						border.Background = new SolidColorBrush(Color.FromRgb(40, 40, 40));
					}
					else
					{
						text.Text = value.ToString();

						// dark grey to white based on log2(value)
						var bg = GetTileBrush(value);
						border.Background = bg;

						// switch text color based on brightness
						byte brightness = ((SolidColorBrush)bg).Color.R;
						text.Foreground = brightness > 180
							? new SolidColorBrush(Color.FromRgb(40, 40, 40))
							: new SolidColorBrush(Color.FromRgb(245, 245, 245));
					}
				}
			}
		}

		private Brush GetTileBrush(int value)
		{
			// Empty or invalid tile
			if (value <= 0)
				return new SolidColorBrush(Color.FromRgb(40, 40, 40));

			// value is power of 2: 2,4,8,16,...
			// merges = how many times a 2-tile has been doubled to reach this value
			int exp = (int)Math.Log(value, 2); // 2 -> 1, 4 -> 2, 8 -> 3, etc.
			int merges = Math.Max(0, exp - 1); // 2 => 0 merges, 4 => 1 merge, etc.

			// Clamp to 0..15 (you wanted full RGB at 15 merges)
			if (merges > 15) merges = 15;

			// Each color has 5 "steps" (0..5)
			// Green ramps first (merges 0..5)
			int greenStep = Math.Min(5, merges);

			// Blue ramps next (merges 5..10)
			int blueStep = 0;
			if (merges > 5)
				blueStep = Math.Min(5, merges - 5);

			// Red ramps last (merges 10..15)
			int redStep = 0;
			if (merges > 10)
				redStep = Math.Min(5, merges - 10);

			// Convert steps to 0..255 channel values
			// 0, 51, 102, 153, 204, 255  (6 levels including 0)
			byte channelStep = 51;

			byte g = (byte)(greenStep * channelStep);
			byte b = (byte)(blueStep * channelStep);
			byte r = (byte)(redStep * channelStep);

			var color = Color.FromRgb(r, g, b);
			return new SolidColorBrush(color);
		}


		// ==== Simple animations (spawn / merge-style pop) ==============

		private void AnimateTileSpawn(int row, int col)
		{
			var border = _tileBorders[row, col];
			if (border.RenderTransform is not ScaleTransform scale)
			{
				scale = new ScaleTransform(1, 1);
				border.RenderTransformOrigin = new Point(0.5, 0.5);
				border.RenderTransform = scale;
			}

			var animIn = new DoubleAnimation
			{
				From = 0.3,
				To = 1.0,
				Duration = TimeSpan.FromMilliseconds(120),
				EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
			};

			scale.BeginAnimation(ScaleTransform.ScaleXProperty, animIn);
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, animIn);
		}
	}
}

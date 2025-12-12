using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameContracts;
using GameClient.Wpf.ClientServices;   // <-- InputService
using GameClient.Wpf.Services;        // <-- SoundService
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace GameClient.Wpf.GameClients
{
	public partial class TetrisGameClient : UserControl, IGameClient
	{
		// ==== IGameClient plumbing ==========================================

		public GameType GameType => GameType.Tetris;
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
			StartNewGame();
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			// No online messages for now
			return false;
		}

		public void OnKeyDown(KeyEventArgs e)
		{
			// Global tracking (for held keys like Left/Right/Down)
			bool isNewPress = InputService.OnKeyDown(e.Key);

			switch (e.Key)
			{
				case Key.Left:
					if (isNewPress)
					{
						MovePieceLeft();
						_leftWasHeldLastTick = true;
						_leftHeldTimeMs = 0;
					}
					break;

				case Key.Right:
					if (isNewPress)
					{
						MovePieceRight();
						_rightWasHeldLastTick = true;
						_rightHeldTimeMs = 0;
					}
					break;

				case Key.Down:
					SoftDrop();
					break;

				case Key.A:
					MovePieceLeft();
					break;

				case Key.D:
					MovePieceRight();
					break;

				case Key.S:
					SoftDrop();
					break;

				case Key.Up:
				case Key.W:
					HardDrop();
					break;

				case Key.LeftShift:
				case Key.RightShift:
					RotateClockwise();
					break;

				case Key.CapsLock:
					RotateCounterClockwise();
					break;

				case Key.Tab:
					HoldCurrentPiece();
					break;

				case Key.P:
				case Key.Escape:
					TogglePause();
					break;

				case Key.R:
					StartNewGame();
					break;
			}
		}

		public void OnKeyUp(KeyEventArgs e)
		{
			InputService.OnKeyUp(e.Key);
		}

		// ==== Tetris configuration =========================================

		private static readonly Dictionary<(int pieceId, int rotation), (int row, int col)[]> PieceCells
			= new Dictionary<(int pieceId, int rotation), (int row, int col)[]>
			{
				// I-piece (id = 1)
				[(1, 0)] = new (int, int)[] { (1, 0), (1, 1), (1, 2), (1, 3) },
				[(1, 1)] = new (int, int)[] { (0, 1), (1, 1), (2, 1), (3, 1) },
				[(1, 2)] = new (int, int)[] { (1, 0), (1, 1), (1, 2), (1, 3) },
				[(1, 3)] = new (int, int)[] { (0, 1), (1, 1), (2, 1), (3, 1) },

				// O-piece (id = 4)
				[(4, 0)] = new (int, int)[] { (1, 1), (1, 2), (2, 1), (2, 2) },
				[(4, 1)] = new (int, int)[] { (1, 1), (1, 2), (2, 1), (2, 2) },
				[(4, 2)] = new (int, int)[] { (1, 1), (1, 2), (2, 1), (2, 2) },
				[(4, 3)] = new (int, int)[] { (1, 1), (1, 2), (2, 1), (2, 2) },

				// J-piece (id = 2)
				[(2, 0)] = new (int, int)[] { (1, 0), (1, 1), (1, 2), (2, 2) },
				[(2, 1)] = new (int, int)[] { (0, 1), (1, 1), (2, 1), (2, 0) },
				[(2, 2)] = new (int, int)[] { (1, 0), (2, 0), (2, 1), (2, 2) },
				[(2, 3)] = new (int, int)[] { (0, 1), (0, 2), (1, 1), (2, 1) },

				// L-piece (id = 3)
				[(3, 0)] = new (int, int)[] { (1, 0), (1, 1), (1, 2), (2, 0) },
				[(3, 1)] = new (int, int)[] { (0, 0), (0, 1), (1, 1), (2, 1) },
				[(3, 2)] = new (int, int)[] { (1, 2), (2, 0), (2, 1), (2, 2) },
				[(3, 3)] = new (int, int)[] { (0, 1), (1, 1), (2, 1), (2, 2) },

				// S, T, Z left as in your original…
				[(5, 0)] = new (int, int)[] { (1, 1), (1, 2), (2, 0), (2, 1) },
				[(5, 1)] = new (int, int)[] { (0, 0), (1, 0), (1, 1), (2, 1) },
				[(5, 2)] = new (int, int)[] { (1, 1), (1, 2), (2, 0), (2, 1) },
				[(5, 3)] = new (int, int)[] { (0, 1), (1, 1), (1, 2), (2, 2) },

				[(6, 0)] = new (int, int)[] { (1, 0), (1, 1), (1, 2), (2, 1) },
				[(6, 1)] = new (int, int)[] { (0, 1), (1, 1), (1, 2), (2, 1) },
				[(6, 2)] = new (int, int)[] { (1, 1), (2, 0), (2, 1), (2, 2) },
				[(6, 3)] = new (int, int)[] { (0, 1), (1, 0), (1, 1), (2, 1) },

				[(7, 0)] = new (int, int)[] { (1, 0), (1, 1), (2, 1), (2, 2) },
				[(7, 1)] = new (int, int)[] { (0, 2), (1, 1), (1, 2), (2, 1) },
				[(7, 2)] = new (int, int)[] { (1, 0), (1, 1), (2, 1), (2, 2) },
				[(7, 3)] = new (int, int)[] { (0, 1), (1, 0), (1, 1), (2, 0) },
			};

		private readonly Random _rng = new();
		private readonly Queue<int> _pieceQueue = new();
		private static readonly (int row, int col)[] EmptyCells = Array.Empty<(int, int)>();

		// Gravity timing (in milliseconds)
		private const double MaxFallIntervalMs = 1000.0;
		private const double MinFallIntervalMs = 80.0;
		private double _fallTimerMs = 0;
		private double _fallIntervalMs = MaxFallIntervalMs;

		private const double LockDelayMs = 300.0;
		private double _lockTimerMs = 0.0;
		private bool _isGrounded = false;

		// Horizontal movement repeat (DAS + ARR)
		private double _moveRepeatDelayMs = 0.0;
		private double _moveRepeatIntervalMs = 80.0;

		private double _leftHeldTimeMs = 0;
		private double _rightHeldTimeMs = 0;
		private bool _leftWasHeldLastTick = false;
		private bool _rightWasHeldLastTick = false;

		private const int BoardCols = 10;
		private const int BoardRows = 20;

		private readonly int[,] _board = new int[BoardRows, BoardCols];

		// 🔹 Line clear flash state
		private readonly bool[] _flashRows = new bool[BoardRows];
		private double _rowFlashTimerMs = 0.0;
		private const double RowFlashDurationMs = 220.0; // ms

		private int _activePieceId;
		private int _activeRotation;
		private int _activeRow;
		private int _activeCol;

		private int _ghostRow;

		private int _heldPieceId;
		private bool _hasHeldThisTurn;
		private bool _isPaused;
		private bool _isGameOver;
		private bool _pieceJustLockedThisTick;

		private int _score;
		private int _linesCleared;
		private int _level = 1;

		private readonly DispatcherTimer _timer;
		private TimeSpan _elapsedTime = TimeSpan.Zero;

		public TetrisGameClient()
		{
			InitializeComponent();

			_timer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(16.0) // ~60fps
			};
			_timer.Tick += OnTimerTick;

			Loaded += OnLoaded;
			Unloaded += OnUnloaded;

			UpdateStatsUI();
			UpdateOverlays();
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			InputService.Clear();
			_isPaused = false;

			// Start BGM for Tetris when we enter the game
			SoundService.PlayTetrisBgm();

			_timer.Start();
			StartNewGame();
		}

		private void OnUnloaded(object sender, RoutedEventArgs e)
		{
			_timer.Stop();
			InputService.Clear();

			// Stop Tetris BGM when leaving this game
			SoundService.StopBgm();
		}

		private void OnTimerTick(object? sender, EventArgs e)
		{
			StepGame();

			_elapsedTime += _timer.Interval;
			UpdateStatsUI();

			GameCanvas.InvalidateVisual();
			HoldCanvas.InvalidateVisual();
			Next1Canvas.InvalidateVisual();
			Next2Canvas.InvalidateVisual();
		}

		private void UpdateStatsUI()
		{
			ScoreTextBlock.Text = _score.ToString();
			LinesTextBlock.Text = _linesCleared.ToString();
			TimeTextBlock.Text = _elapsedTime.ToString(@"mm\:ss");
			LevelTextBlock.Text = $"Level {_level}";
		}

		private void UpdateOverlays()
		{
			if (_isGameOver)
			{
				GameOverOverlay.Visibility = Visibility.Visible;
				PauseOverlay.Visibility = Visibility.Collapsed;

				GameOverLevelTextBlock.Text = $"Level {_level}";
				GameOverScoreTextBlock.Text = $"Score {_score}";
			}
			else if (_isPaused)
			{
				GameOverOverlay.Visibility = Visibility.Collapsed;
				PauseOverlay.Visibility = Visibility.Visible;
			}
			else
			{
				GameOverOverlay.Visibility = Visibility.Collapsed;
				PauseOverlay.Visibility = Visibility.Collapsed;
			}
		}

		// ==== Skia drawing (unchanged logic) ================================

		private void GameCanvas_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			var info = e.Info;

			canvas.Clear(new SKColor(10, 10, 20));

			var (cellSize, origin) = GetBoardLayout(info.Width, info.Height);

			DrawBoardBackground(canvas, info.Width, info.Height);
			DrawBoardGrid(canvas, info.Width, info.Height, cellSize, origin);
			DrawBoardCells(canvas, cellSize, origin);
			DrawGhostPiece(canvas, cellSize, origin);
			DrawActivePiece(canvas, cellSize, origin);
		}

		private (float cellSize, SKPoint origin) GetBoardLayout(int pixelWidth, int pixelHeight)
		{
			var cellSize = Math.Min(
				pixelWidth / (float)BoardCols,
				pixelHeight / (float)BoardRows);

			var boardWidth = cellSize * BoardCols;
			var boardHeight = cellSize * BoardRows;

			var originX = (pixelWidth - boardWidth) / 2f;
			var originY = (pixelHeight - boardHeight) / 2f;

			return (cellSize, new SKPoint(originX, originY));
		}

		private void DrawBoardBackground(SKCanvas canvas, int pixelWidth, int pixelHeight)
		{
			using var paint = new SKPaint
			{
				IsAntialias = true,
				Shader = SKShader.CreateLinearGradient(
					new SKPoint(0, 0),
					new SKPoint(pixelWidth, pixelHeight),
					new[]
					{
						new SKColor(16, 16, 32),
						new SKColor(8, 8, 20)
					},
					null,
					SKShaderTileMode.Clamp)
			};

			canvas.DrawRect(new SKRect(0, 0, pixelWidth, pixelHeight), paint);
		}

		private void DrawBoardGrid(SKCanvas canvas, int pixelWidth, int pixelHeight, float cellSize, SKPoint origin)
		{
			using var gridPaint = new SKPaint
			{
				Color = new SKColor(255, 255, 255, 40),
				StrokeWidth = 1,
				IsAntialias = false,
				Style = SKPaintStyle.Stroke
			};

			float left = origin.X;
			float top = origin.Y;
			float right = origin.X + cellSize * BoardCols;
			float bottom = origin.Y + cellSize * BoardRows;

			for (int x = 0; x <= BoardCols; x++)
			{
				float px = left + x * cellSize;
				canvas.DrawLine(px, top, px, bottom, gridPaint);
			}

			for (int y = 0; y <= BoardRows; y++)
			{
				float py = top + y * cellSize;
				canvas.DrawLine(left, py, right, py, gridPaint);
			}

			using var borderPaint = new SKPaint
			{
				Color = new SKColor(255, 255, 255, 80),
				StrokeWidth = 2,
				Style = SKPaintStyle.Stroke,
				IsAntialias = true
			};

			canvas.DrawRect(left, top, cellSize * BoardCols, cellSize * BoardRows, borderPaint);
		}

		private void DrawBoardCells(SKCanvas canvas, float cellSize, SKPoint origin)
		{
			for (int row = 0; row < BoardRows; row++)
			{
				// First draw the normal cells in this row
				for (int col = 0; col < BoardCols; col++)
				{
					int cellId = _board[row, col];
					if (cellId == 0)
						continue;

					var color = GetPieceColor(cellId);
					DrawCell(canvas, row, col, cellSize, origin, color);
				}

				// Then overlay flash/expansion if this row was just cleared
				float intensity = GetRowFlashIntensity(row);
				if (intensity <= 0f)
					continue;

				// Expansion factor: 1.0 = normal height, 1.15 = slightly taller
				float scale = 1.15f;
				float expandedHeight = cellSize * scale;
				float extra = (expandedHeight - cellSize) / 2f;

				float x = origin.X;
				float y = origin.Y + row * cellSize - extra;
				float width = BoardCols * cellSize;
				float height = expandedHeight;

				using var flashPaint = new SKPaint
				{
					Color = new SKColor(255, 255, 255, (byte)(180 * intensity)),
					Style = SKPaintStyle.Fill,
					IsAntialias = true
				};

				canvas.DrawRect(new SKRect(x, y, x + width, y + height), flashPaint);
			}
		}

		private void DrawCell(SKCanvas canvas, int row, int col, float cellSize, SKPoint origin, SKColor color)
		{
			float x = origin.X + col * cellSize;
			float y = origin.Y + row * cellSize;

			var rect = new SKRect(x + 1, y + 1, x + cellSize - 1, y + cellSize - 1);

			using var fill = new SKPaint
			{
				Color = color,
				IsAntialias = true,
				Style = SKPaintStyle.Fill
			};

			using var border = new SKPaint
			{
				Color = new SKColor(0, 0, 0, 140),
				StrokeWidth = 2,
				Style = SKPaintStyle.Stroke,
				IsAntialias = true
			};

			canvas.DrawRoundRect(rect, cellSize * 0.2f, cellSize * 0.2f, fill);
			canvas.DrawRoundRect(rect, cellSize * 0.2f, cellSize * 0.2f, border);
		}

		private void DrawActivePiece(SKCanvas canvas, float cellSize, SKPoint origin)
		{
			if (_activePieceId == 0)
				return;

			var color = GetPieceColor(_activePieceId);
			var cells = GetCellsForPiece(_activePieceId, _activeRotation);
			if (cells.Length == 0)
				return;

			foreach (var (rOffset, cOffset) in cells)
			{
				int r = _activeRow + rOffset;
				int c = _activeCol + cOffset;

				if (r < 0 || r >= BoardRows || c < 0 || c >= BoardCols)
					continue;

				DrawCell(canvas, r, c, cellSize, origin, color);
			}
		}

		private void DrawPiecePreview(SKCanvas canvas, int pixelWidth, int pixelHeight, int pieceId)
		{
			canvas.Clear(new SKColor(12, 12, 24));

			using var borderPaint = new SKPaint
			{
				Color = new SKColor(255, 255, 255, 80),
				StrokeWidth = 2,
				Style = SKPaintStyle.Stroke,
				IsAntialias = true
			};

			var rect = new SKRect(4, 4, pixelWidth - 4, pixelHeight - 4);
			canvas.DrawRoundRect(rect, 12, 12, borderPaint);

			if (pieceId == 0)
				return;

			int previewCols = 4;
			int previewRows = 4;

			float cellSize = Math.Min(
				(pixelWidth - 16) / (float)previewCols,
				(pixelHeight - 16) / (float)previewRows);

			float width = cellSize * previewCols;
			float height = cellSize * previewRows;

			float originX = (pixelWidth - width) / 2f;
			float originY = (pixelHeight - height) / 2f;

			using (var gridPaint = new SKPaint
			{
				Color = new SKColor(255, 255, 255, 30),
				StrokeWidth = 1,
				Style = SKPaintStyle.Stroke
			})
			{
				for (int x = 0; x <= previewCols; x++)
				{
					float px = originX + x * cellSize;
					canvas.DrawLine(px, originY, px, originY + height, gridPaint);
				}

				for (int y = 0; y <= previewRows; y++)
				{
					float py = originY + y * cellSize;
					canvas.DrawLine(originX, py, originX + width, py, gridPaint);
				}
			}

			var color = GetPieceColor(pieceId);
			var cells = GetCellsForPiece(pieceId, 0);
			if (cells.Length == 0)
				return;

			var origin = new SKPoint(originX, originY);

			foreach (var (rOffset, cOffset) in cells)
			{
				DrawCell(canvas, rOffset, cOffset, cellSize, origin, color);
			}
		}

		private int GetUpcomingPieceId(int index)
		{
			if (index < 0 || _pieceQueue.Count == 0)
				return 0;

			var array = _pieceQueue.ToArray();
			if (index >= array.Length)
				return 0;

			return array[index];
		}

		private void DrawGhostPiece(SKCanvas canvas, float cellSize, SKPoint origin)
		{
			if (_activePieceId == 0)
				return;

			var cells = GetCellsForPiece(_activePieceId, _activeRotation);
			if (cells.Length == 0)
				return;

			var ghostColor = GetPieceColor(_activePieceId).WithAlpha(80);

			foreach (var (rOffset, cOffset) in cells)
			{
				int r = _ghostRow + rOffset;
				int c = _activeCol + cOffset;

				if (r < 0 || r >= BoardRows || c < 0 || c >= BoardCols)
					continue;

				DrawCell(canvas, r, c, cellSize, origin, ghostColor);
			}
		}

		private void HoldCanvas_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			var info = e.Info;
			DrawPiecePreview(canvas, info.Width, info.Height, _heldPieceId);
		}

		private void Next1Canvas_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			var info = e.Info;
			int nextId = GetUpcomingPieceId(0);
			DrawPiecePreview(canvas, info.Width, info.Height, nextId);
		}

		private void Next2Canvas_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			var info = e.Info;
			int nextId = GetUpcomingPieceId(1);
			DrawPiecePreview(canvas, info.Width, info.Height, nextId);
		}

		private SKColor GetPieceColor(int id)
		{
			return id switch
			{
				1 => new SKColor(0, 240, 240),
				2 => new SKColor(0, 0, 240),
				3 => new SKColor(240, 160, 0),
				4 => new SKColor(240, 240, 0),
				5 => new SKColor(0, 240, 0),
				6 => new SKColor(160, 0, 240),
				7 => new SKColor(240, 0, 0),
				_ => new SKColor(200, 200, 200)
			};
		}

		// ==== Input helper wrappers =========================================

		private bool IsLeftHeld()
			=> Keyboard.IsKeyDown(Key.Left) || Keyboard.IsKeyDown(Key.A);

		private bool IsRightHeld()
			=> Keyboard.IsKeyDown(Key.Right) || Keyboard.IsKeyDown(Key.D);

		private bool IsSoftDropHeld()
			=> Keyboard.IsKeyDown(Key.Down) || Keyboard.IsKeyDown(Key.S);

		// ==== Game logic ====================================================

		private void StartNewGame()
		{
			_isGameOver = false;
			_isPaused = false;

			ClearBoard();
			ResetStats();
			EnsureQueueFilled();
			SpawnPiece();

			if (!_timer.IsEnabled) _timer.Start();
			UpdateOverlays();
		}

		private void StepGame()
		{
			if (_isGameOver)
				return;

			if (_activePieceId == 0)
				return;

			_pieceJustLockedThisTick = false;

			double dt = _timer.Interval.TotalMilliseconds;

			UpdateHorizontalMovement(dt);
			UpdateGravity(dt);
			UpdateGhostRow();
			UpdateRowFlash(dt);
		}

		private void UpdateHorizontalMovement(double dt)
		{
			bool leftHeld = IsLeftHeld();
			bool rightHeld = IsRightHeld();

			if (leftHeld && !rightHeld)
			{
				HandleSingleDirectionHeld(
					isHeld: leftHeld,
					wasHeldLastTick: ref _leftWasHeldLastTick,
					heldTimeMs: ref _leftHeldTimeMs,
					dt: dt,
					moveAction: MovePieceLeft);

				_rightHeldTimeMs = 0;
				_rightWasHeldLastTick = false;
			}
			else if (rightHeld && !leftHeld)
			{
				HandleSingleDirectionHeld(
					isHeld: rightHeld,
					wasHeldLastTick: ref _rightWasHeldLastTick,
					heldTimeMs: ref _rightHeldTimeMs,
					dt: dt,
					moveAction: MovePieceRight);

				_leftHeldTimeMs = 0;
				_leftWasHeldLastTick = false;
			}
			else
			{
				_leftHeldTimeMs = 0;
				_rightHeldTimeMs = 0;
				_leftWasHeldLastTick = leftHeld;
				_rightWasHeldLastTick = rightHeld;
			}
		}

		private double ComputeFallIntervalMs(int totalLines)
		{
			double t = totalLines / 6.0;
			double interval = MaxFallIntervalMs * Math.Pow(0.90, t);

			if (interval < MinFallIntervalMs)
				interval = MinFallIntervalMs;

			return interval;
		}

		private void HandleSingleDirectionHeld(
			bool isHeld,
			ref bool wasHeldLastTick,
			ref double heldTimeMs,
			double dt,
			Action moveAction)
		{
			if (!isHeld)
			{
				heldTimeMs = 0;
				wasHeldLastTick = false;
				return;
			}

			if (!wasHeldLastTick)
			{
				heldTimeMs = 0;
				wasHeldLastTick = true;
				return;
			}

			heldTimeMs += dt;

			if (heldTimeMs >= _moveRepeatIntervalMs)
			{
				moveAction();
				heldTimeMs -= _moveRepeatIntervalMs;
			}
		}

		private void UpdateGravity(double dt)
		{
			double gravityMultiplier = IsSoftDropHeld() ? 4.0 : 1.0;

			_fallTimerMs += dt * gravityMultiplier;
			bool movedDownThisFrame = false;

			while (_fallTimerMs >= _fallIntervalMs)
			{
				_fallTimerMs -= _fallIntervalMs;

				int newRow = _activeRow + 1;

				if (!CheckCollisionForActivePiece(newRow, _activeCol, _activeRotation))
				{
					_activeRow = newRow;
					movedDownThisFrame = true;
					_isGrounded = false;
				}
				else
				{
					_isGrounded = true;
					break;
				}
			}

			if (_isGrounded)
			{
				_lockTimerMs += dt;

				if (_lockTimerMs >= LockDelayMs)
				{
					LockCurrentPiece();
					ClearCompletedLines();
					SpawnPiece();

					_fallTimerMs = 0;
					_lockTimerMs = 0;
					_isGrounded = false;
					_pieceJustLockedThisTick = true;
				}
			}
			else if (movedDownThisFrame)
			{
				_lockTimerMs = 0;
			}
		}

		private void ResetLockDelayAfterMovement()
		{
			if (_activePieceId == 0)
				return;

			if (CheckCollisionForActivePiece(_activeRow + 1, _activeCol, _activeRotation))
			{
				_isGrounded = true;
				_lockTimerMs = 0;
			}
			else
			{
				_isGrounded = false;
				_lockTimerMs = 0;
			}
		}

		private int ComputeGhostRow()
		{
			if (_activePieceId == 0)
				return _activeRow;

			int testRow = _activeRow;

			while (!CheckCollisionForActivePiece(testRow + 1, _activeCol, _activeRotation))
			{
				testRow++;
			}

			return testRow;
		}

		private void UpdateGhostRow()
		{
			if (_activePieceId == 0)
				return;

			_ghostRow = ComputeGhostRow();
		}
		private void UpdateRowFlash(double dt)
		{
			// If no rows are flashing, nothing to do
			bool any = false;
			for (int r = 0; r < BoardRows; r++)
			{
				if (_flashRows[r])
				{
					any = true;
					break;
				}
			}

			if (!any)
				return;

			_rowFlashTimerMs += dt;
			if (_rowFlashTimerMs >= RowFlashDurationMs)
			{
				// End of flash: clear all flags
				Array.Clear(_flashRows, 0, _flashRows.Length);
			}
		}

		private void TriggerRowFlash(bool[] rowsToFlash)
		{
			Array.Clear(_flashRows, 0, _flashRows.Length);

			bool any = false;
			for (int r = 0; r < BoardRows; r++)
			{
				if (rowsToFlash[r])
				{
					_flashRows[r] = true;
					any = true;
				}
			}

			_rowFlashTimerMs = 0.0;

			if (!any)
			{
				// nothing actually flashing
				Array.Clear(_flashRows, 0, _flashRows.Length);
			}
		}

		// 0–1 intensity for a given row based on time
		private float GetRowFlashIntensity(int row)
		{
			if (!_flashRows[row])
				return 0f;

			double t = _rowFlashTimerMs / RowFlashDurationMs;
			if (t <= 0.0 || t >= 1.0)
				return 0f;

			// Simple “pulse” shape: 0 → 1 → 0
			float intensity = (float)(1.0 - Math.Abs(2 * t - 1));
			return intensity;
		}


		private void ClearBoard()
		{
			Array.Clear(_board, 0, _board.Length);
		}

		private void ResetStats()
		{
			_heldPieceId = 0;
			_hasHeldThisTurn = false;
			_score = 0;
			_linesCleared = 0;
			_level = 1;
			_fallIntervalMs = MaxFallIntervalMs;
			_elapsedTime = TimeSpan.Zero;
		}

		private void RefillBag()
		{
			int[] bag = { 1, 2, 3, 4, 5, 6, 7 };

			for (int i = bag.Length - 1; i > 0; i--)
			{
				int j = _rng.Next(i + 1);
				(bag[i], bag[j]) = (bag[j], bag[i]);
			}

			foreach (int id in bag)
			{
				_pieceQueue.Enqueue(id);
			}
		}

		private void EnsureQueueFilled()
		{
			while (_pieceQueue.Count < 3)
			{
				RefillBag();
			}
		}

		private void SpawnPiece()
		{
			EnsureQueueFilled();

			_activePieceId = _pieceQueue.Dequeue();
			_activeRotation = 0;
			_activeRow = -1;
			_activeCol = (BoardCols / 2) - 2;

			_hasHeldThisTurn = false;
			_ghostRow = ComputeGhostRow();

			_isGrounded = false;
			_lockTimerMs = 0;
			_fallTimerMs = 0;

			if (CheckCollisionForActivePiece(_activeRow, _activeCol, _activeRotation))
			{
				_isGameOver = true;
				_timer.Stop();
				LevelTextBlock.Text = "Game Over";
				UpdateOverlays();
				SoundService.FadeOutBgm(TimeSpan.FromSeconds(2.0));
			}
		}

		private static (int rowOffset, int colOffset)[] GetCellsForPiece(int pieceId, int rotation)
		{
			int normalizedRotation = ((rotation % 4) + 4) % 4;

			if (PieceCells.TryGetValue((pieceId, normalizedRotation), out var cells))
			{
				return cells;
			}

			return EmptyCells;
		}

		private void MovePieceLeft()
		{
			if (_activePieceId == 0)
				return;

			int newCol = _activeCol - 1;

			if (!CheckCollisionForActivePiece(_activeRow, newCol, _activeRotation))
			{
				_activeCol = newCol;
				ResetLockDelayAfterMovement();
				SoundService.PlayTetrisEffect(TetrisSoundEffect.MovePiece);
			}
		}

		private void MovePieceRight()
		{
			if (_activePieceId == 0)
				return;

			int newCol = _activeCol + 1;

			if (!CheckCollisionForActivePiece(_activeRow, newCol, _activeRotation))
			{
				_activeCol = newCol;
				ResetLockDelayAfterMovement();
				SoundService.PlayTetrisEffect(TetrisSoundEffect.MovePiece);
			}
		}

		private void SoftDrop()
		{
			if (_activePieceId == 0)
				return;

			int newRow = _activeRow + 1;

			if (!CheckCollisionForActivePiece(newRow, _activeCol, _activeRotation))
			{
				_activeRow = newRow;
				ResetLockDelayAfterMovement();
			}
		}

		private void HardDrop()
		{
			if (_activePieceId == 0)
				return;

			int targetRow = ComputeGhostRow();
			int rowsDropped = targetRow - _activeRow;

			_activeRow = targetRow;

			if (rowsDropped > 0)
			{
				_score += rowsDropped * 2;
			}

			LockCurrentPiece();
			ClearCompletedLines();
			SpawnPiece();

			_fallTimerMs = 0;
		}

		private void RotateClockwise()
		{
			if (_activePieceId == 0)
				return;

			int fromRot = _activeRotation;
			int toRot = (fromRot + 1) & 3;

			var kickOffsets = new (int dRow, int dCol)[]
			{
				(0, 0),
				(0, -1),
				(0, 1),
				(0, -2),
				(0, 2),
				(-1, 0),
			};

			foreach (var (dRow, dCol) in kickOffsets)
			{
				int testRow = _activeRow + dRow;
				int testCol = _activeCol + dCol;

				if (!CheckCollisionForActivePiece(testRow, testCol, toRot))
				{
					_activeRow = testRow;
					_activeCol = testCol;
					_activeRotation = toRot;

					// 🔊 rotation sound
					SoundService.PlayTetrisEffect(TetrisSoundEffect.RotatePiece);
					ResetLockDelayAfterMovement();

					UpdateGhostRow();
					return;
				}
			}
		}

		private void RotateCounterClockwise()
		{
			if (_activePieceId == 0)
				return;

			int fromRot = _activeRotation;
			int toRot = (fromRot + 3) & 3;

			var kickOffsets = new (int dRow, int dCol)[]
			{
				(0, 0),
				(0, -1),
				(0, 1),
				(0, -2),
				(0, 2),
				(-1, 0),
			};

			foreach (var (dRow, dCol) in kickOffsets)
			{
				int testRow = _activeRow + dRow;
				int testCol = _activeCol + dCol;

				if (!CheckCollisionForActivePiece(testRow, testCol, toRot))
				{
					_activeRow = testRow;
					_activeCol = testCol;
					_activeRotation = toRot;

					// 🔊 rotation sound
					SoundService.PlayTetrisEffect(TetrisSoundEffect.RotatePiece);
					ResetLockDelayAfterMovement();

					UpdateGhostRow();
					return;
				}
			}
		}

		private void HoldCurrentPiece()
		{
			if (_activePieceId == 0)
				return;

			if (_hasHeldThisTurn)
				return;
			if (_isGrounded)
				return;
			// 👇 Prevent weird double-spawn if a lock just occurred this frame
			if (_pieceJustLockedThisTick)
				return;

			_hasHeldThisTurn = true;
			_fallTimerMs = 0;

			if (_heldPieceId == 0)
			{
				_heldPieceId = _activePieceId;
				_activePieceId = 0;
				SpawnPiece();
			}
			else
			{
				int temp = _activePieceId;
				_activePieceId = _heldPieceId;
				_heldPieceId = temp;

				_activeRotation = 0;
				_activeRow = 0;
				_activeCol = (BoardCols / 2) - 2;
			}

			UpdateGhostRow();
		}

		private void LockCurrentPiece()
		{
			if (_activePieceId == 0)
				return;

			var cells = GetCellsForPiece(_activePieceId, _activeRotation);
			if (cells.Length == 0)
				return;

			foreach (var (rOffset, cOffset) in cells)
			{
				int r = _activeRow + rOffset;
				int c = _activeCol + cOffset;

				if (r < 0 || r >= BoardRows || c < 0 || c >= BoardCols)
				{
					continue;
				}

				_board[r, c] = _activePieceId;
			}

			_activePieceId = 0;

			// 🔊 piece placed
			SoundService.PlayTetrisEffect(TetrisSoundEffect.PlacePiece);
		}

		private void TogglePause()
		{
			if (_isGameOver)
			{
				// If you later want P to restart, you can hook StartNewGame() here
				// StartNewGame();
				// return;
			}

			_isPaused = !_isPaused;

			if (_isPaused)
			{
				_timer.Stop();
				LevelTextBlock.Text = $"Level {_level} (Paused)";
			}
			else
			{
				_timer.Start();
				LevelTextBlock.Text = $"Level {_level}";
			}

			UpdateOverlays();
		}

		private void ClearCompletedLines()
		{
			int linesClearedThisStep = 0;
			int writeRow = BoardRows - 1;

			// 🔹 Track which rows are being cleared for the flash effect
			bool[] rowsToClear = new bool[BoardRows];

			for (int r = BoardRows - 1; r >= 0; r--)
			{
				bool isFull = true;

				for (int c = 0; c < BoardCols; c++)
				{
					if (_board[r, c] == 0)
					{
						isFull = false;
						break;
					}
				}

				if (!isFull)
				{
					if (writeRow != r)
					{
						for (int c = 0; c < BoardCols; c++)
						{
							_board[writeRow, c] = _board[r, c];
						}
					}

					writeRow--;
				}
				else
				{
					linesClearedThisStep++;
					rowsToClear[r] = true;
				}
			}

			for (int r = writeRow; r >= 0; r--)
			{
				for (int c = 0; c < BoardCols; c++)
				{
					_board[r, c] = 0;
				}
			}

			if (linesClearedThisStep <= 0)
				return;

			// 🔹 Trigger flash animation for the cleared rows
			TriggerRowFlash(rowsToClear);

			_linesCleared += linesClearedThisStep;

			// 🔊 line clear vs tetris
			if (linesClearedThisStep >= 4)
			{
				SoundService.PlayTetrisEffect(TetrisSoundEffect.TetrisClear);
			}
			else
			{
				SoundService.PlayTetrisEffect(TetrisSoundEffect.RowClear);
			}

			int baseScore = linesClearedThisStep switch
			{
				1 => 40,
				2 => 100,
				3 => 300,
				4 => 1200,
				_ => 0
			};

			_score += baseScore * _level;

			int oldLevel = _level;
			_level = 1 + (_linesCleared / 10);

			_fallIntervalMs = ComputeFallIntervalMs(_linesCleared);

			if (_level > oldLevel)
			{
				// 🔊 level up
				SoundService.PlayTetrisEffect(TetrisSoundEffect.LevelUp);
			}
		}


		private bool CheckCollisionForActivePiece(int newRow, int newCol, int newRotation)
		{
			if (_activePieceId == 0)
				return false;

			int rotation = ((newRotation % 4) + 4) % 4;
			var cells = GetCellsForPiece(_activePieceId, rotation);
			if (cells.Length == 0)
				return true;

			foreach (var (rOffset, cOffset) in cells)
			{
				int r = newRow + rOffset;
				int c = newCol + cOffset;

				if (c < 0 || c >= BoardCols || r >= BoardRows)
					return true;

				if (r < 0)
					continue;

				if (_board[r, c] != 0)
					return true;
			}

			return false;
		}
		private void BgmVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			// Avoid noisy calls during XAML initialization if you want:
			// if (!IsLoaded) return;

			SoundService.SetBgmVolume(e.NewValue);
		}

		private void SfxVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			// if (!IsLoaded) return;

			SoundService.SetSfxVolume(e.NewValue);
		}
	}
}

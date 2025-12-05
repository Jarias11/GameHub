using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameContracts;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace GameClient.Wpf.GameClients
{
	public partial class CheckersGameClient : UserControl, IGameClient
	{
		private const int BoardSize = 8;

		// ==== IGameClient plumbing ==========================================

		public GameType GameType => GameType.Checkers;
		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		private string? _roomCode;
		private string? _playerId;

		// Server state snapshot
		private CheckersCell[] _cells = new CheckersCell[BoardSize * BoardSize];
		private string _redPlayerId = string.Empty;
		private string _blackPlayerId = string.Empty;
		private string? _currentPlayerId;
		private bool _isStarted;
		private bool _isGameOver;
		private string? _winnerPlayerId;
		private string? _statusMessage;
		private int? _forcedFromRow;
		private int? _forcedFromCol;
		private int? _lastFromRow;
		private int? _lastFromCol;
		private int? _lastToRow;
		private int? _lastToCol;

		// Local UI state
		private int? _hoverRow;
		private int? _hoverCol;

		private int? _selectedRow;
		private int? _selectedCol;
		private readonly List<(int row, int col)> _candidateMoves = new();

		// Simple shaking animation for the selected piece
		private readonly DispatcherTimer _renderTimer;
		private DateTime _shakeStart;
		private bool _isShaking;

		public CheckersGameClient()
		{
			InitializeComponent();

			_renderTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
			};
			_renderTimer.Tick += (_, _) => BoardSurface.InvalidateVisual();
			_renderTimer.Start();
		}

		public void SetConnection(
			Func<HubMessage, Task> sendAsync,
			Func<bool> isSocketOpen)
		{
			_sendAsync = sendAsync;
			_isSocketOpen = isSocketOpen;
		}

		public void OnRoomChanged(string? roomCode, string? playerId)
		{
			_roomCode = roomCode;
			_playerId = playerId;

			// Reset local selection state when switching rooms.
			_selectedRow = _selectedCol = null;
			_candidateMoves.Clear();
			_hoverRow = _hoverCol = null;
			_isShaking = false;
			StatusText.Text = "Waiting for server...";
			SideText.Text = "Side: -";
			BoardSurface.InvalidateVisual();
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			if (msg.MessageType != "CheckersState")
				return false;

			if (_roomCode != null && msg.RoomCode != _roomCode)
				return false;

			CheckersStatePayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<CheckersStatePayload>(msg.PayloadJson);
			}
			catch (Exception ex)
			{
				Console.WriteLine("[CheckersClient] Failed to deserialize CheckersState: " + ex);
				return true;
			}
			if (payload == null)
				return true;

			ApplyPayload(payload);
			return true;
		}

		public void OnKeyDown(KeyEventArgs e) { }
		public void OnKeyUp(KeyEventArgs e) { }

		// ==== Payload handling ===============================================

		private void ApplyPayload(CheckersStatePayload payload)
		{
			if (payload.Cells.Length == BoardSize * BoardSize)
			{
				_cells = payload.Cells.ToArray();
			}

			_redPlayerId = payload.RedPlayerId ?? string.Empty;
			_blackPlayerId = payload.BlackPlayerId ?? string.Empty;

			_isStarted = payload.IsStarted;
			_isGameOver = payload.IsGameOver;
			_currentPlayerId = payload.CurrentPlayerId;
			_winnerPlayerId = payload.WinnerPlayerId;
			_statusMessage = payload.Message;

			_forcedFromRow = payload.ForcedFromRow;
			_forcedFromCol = payload.ForcedFromCol;
			_lastFromRow = payload.LastFromRow;
			_lastFromCol = payload.LastFromCol;
			_lastToRow = payload.LastToRow;
			_lastToCol = payload.LastToCol;

			// Reset local selection if the game ended or turn changed.
			if (_isGameOver ||
				(_playerId != null && _currentPlayerId != _playerId))
			{
				_selectedRow = _selectedCol = null;
				_candidateMoves.Clear();
			}

			// Update labels
			var sideText = "Side: -";
			if (!string.IsNullOrEmpty(_playerId))
			{
				if (_playerId == _redPlayerId)
					sideText = "Side: Red";
				else if (_playerId == _blackPlayerId)
					sideText = "Side: Black";
				else
					sideText = "Spectator";
			}

			SideText.Text = sideText;
			StatusText.Text = _statusMessage ?? string.Empty;

			BoardSurface.InvalidateVisual();
		}

		// ==== Mouse → selections/moves ======================================

		private void BoardSurface_OnMouseMove(object sender, MouseEventArgs e)
		{
			var pt = e.GetPosition(BoardSurface);
			if (!TryMapPointToCell(pt.X, pt.Y, out var row, out var col))
			{
				if (_hoverRow != null || _hoverCol != null)
				{
					_hoverRow = _hoverCol = null;
					BoardSurface.InvalidateVisual();
				}
				return;
			}

			if (_hoverRow != row || _hoverCol != col)
			{
				_hoverRow = row;
				_hoverCol = col;
				BoardSurface.InvalidateVisual();
			}
		}

		private async void BoardSurface_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			var pt = e.GetPosition(BoardSurface);
			if (!TryMapPointToCell(pt.X, pt.Y, out var row, out var col))
				return;

			if (_isGameOver || !_isStarted)
				return;

			if (_isSocketOpen?.Invoke() != true || _sendAsync == null || _roomCode == null)
				return;

			if (string.IsNullOrWhiteSpace(_playerId) ||
				_playerId != _currentPlayerId)
			{
				// Not our turn
				return;
			}

			bool isRedSide = _playerId == _redPlayerId;
			bool isBlackSide = _playerId == _blackPlayerId;

			if (!isRedSide && !isBlackSide)
			{
				// spectator
				return;
			}

			int index = row * BoardSize + col;
			var cell = _cells[index];

			// If clicking on one of our pieces -> select / reselect
			if (IsMyPiece(cell, isRedSide, isBlackSide))
			{
				// Multi-capture forced piece?
				if (_forcedFromRow.HasValue && _forcedFromCol.HasValue &&
					(_forcedFromRow.Value != row || _forcedFromCol.Value != col))
				{
					// Can't select other pieces during forced capture.
					return;
				}

				_selectedRow = row;
				_selectedCol = col;
				_candidateMoves.Clear();
				_candidateMoves.AddRange(GetCandidateMovesForPiece(row, col, isRedSide));

				// Trigger shake
				_shakeStart = DateTime.UtcNow;
				_isShaking = true;

				HintText.Text = _candidateMoves.Count > 0
					? "Click one of the highlighted squares to move."
					: "No legal moves for this piece.";

				BoardSurface.InvalidateVisual();
				return;
			}

			// Otherwise: if we already have a selected piece, see if this is a destination.
			if (_selectedRow.HasValue && _selectedCol.HasValue && _candidateMoves.Count > 0)
			{
				if (_candidateMoves.Any(m => m.row == row && m.col == col))
				{
					var movePayload = new CheckersMovePayload
					{
						FromRow = _selectedRow.Value,
						FromCol = _selectedCol.Value,
						ToRow = row,
						ToCol = col
					};

					var msg = new HubMessage
					{
						MessageType = "CheckersMove",
						RoomCode = _roomCode,
						PlayerId = _playerId ?? string.Empty,
						PayloadJson = JsonSerializer.Serialize(movePayload)
					};

					await _sendAsync(msg);

					// Let server authoritative state come back.
					_candidateMoves.Clear();
					_selectedRow = _selectedCol = null;
					BoardSurface.InvalidateVisual();
				}
				else
				{
					// Clicking elsewhere cancels selection.
					_selectedRow = _selectedCol = null;
					_candidateMoves.Clear();
					BoardSurface.InvalidateVisual();
				}
			}
		}

		private async void ResignButton_Click(object sender, RoutedEventArgs e)
		{
			if (_isSocketOpen?.Invoke() != true || _sendAsync == null || _roomCode == null)
				return;

			if (string.IsNullOrWhiteSpace(_playerId))
				return;

			var payload = new CheckersResignPayload();

			var msg = new HubMessage
			{
				MessageType = "CheckersResign",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}

		// ==== Drawing ========================================================

		private void BoardSurface_OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			canvas.Clear(new SKColor(10, 10, 20));

			var info = e.Info;
			float width = info.Width;
			float height = info.Height;
			float padding = 20f;

			float boardSizePx = Math.Min(width, height) - 2 * padding;
			if (boardSizePx <= 0) return;

			float cellSize = boardSizePx / BoardSize;
			float boardX = (width - boardSizePx) / 2f;
			float boardY = (height - boardSizePx) / 2f;

			// Draw squares
			using var lightPaint = new SKPaint { Color = new SKColor(40, 40, 60) };
			using var darkPaint = new SKPaint { Color = new SKColor(20, 20, 35) };
			using var highlightPaint = new SKPaint
			{
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 3,
				Color = new SKColor(255, 215, 0)
			};
			using var lastMovePaint = new SKPaint
			{
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 2,
				Color = new SKColor(0, 200, 255)
			};
			using var candidatePaint = new SKPaint
			{
				Style = SKPaintStyle.Fill,
				Color = new SKColor(0, 255, 0, 80)
			};

			for (int row = 0; row < BoardSize; row++)
			{
				for (int col = 0; col < BoardSize; col++)
				{
					float x = boardX + col * cellSize;
					float y = boardY + row * cellSize;
					var rect = new SKRect(x, y, x + cellSize, y + cellSize);

					bool isDark = ((row + col) & 1) == 1;
					canvas.DrawRect(rect, isDark ? darkPaint : lightPaint);

					// Candidate move marker
					if (_candidateMoves.Any(m => m.row == row && m.col == col))
					{
						float cx = x + cellSize / 2f;
						float cy = y + cellSize / 2f;
						canvas.DrawCircle(cx, cy, cellSize * 0.17f, candidatePaint);
					}

					// Last move highlight
					if ((_lastFromRow == row && _lastFromCol == col) ||
						(_lastToRow == row && _lastToCol == col))
					{
						canvas.DrawRect(rect, lastMovePaint);
					}

					// Hover highlight
					if (_hoverRow == row && _hoverCol == col)
					{
						canvas.DrawRect(rect, highlightPaint);
					}
				}
			}

			// Draw pieces
			using var redPaint = new SKPaint { Color = new SKColor(220, 60, 60), IsAntialias = true };
			using var redStroke = new SKPaint
			{
				Color = new SKColor(255, 200, 200),
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 2,
				IsAntialias = true
			};
			using var blackPaint = new SKPaint { Color = new SKColor(35, 35, 35), IsAntialias = true };
			using var blackStroke = new SKPaint
			{
				Color = new SKColor(200, 200, 200),
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 2,
				IsAntialias = true
			};
			using var kingPaint = new SKPaint
			{
				Color = new SKColor(255, 255, 255),
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 3,
				IsAntialias = true
			};

			var now = DateTime.UtcNow;
			double shakeElapsedMs = (now - _shakeStart).TotalMilliseconds;
			if (shakeElapsedMs > 250) _isShaking = false;

			for (int row = 0; row < BoardSize; row++)
			{
				for (int col = 0; col < BoardSize; col++)
				{
					var cell = _cells[row * BoardSize + col];
					if (cell == CheckersCell.Empty)
						continue;

					float x = boardX + col * cellSize;
					float y = boardY + row * cellSize;
					float cx = x + cellSize / 2f;
					float cy = y + cellSize / 2f;
					float radius = cellSize * 0.38f;

					// Shake offset for selected piece
					float offsetX = 0;
					float offsetY = 0;
					if (_isShaking &&
						_selectedRow == row &&
						_selectedCol == col)
					{
						double t = shakeElapsedMs / 40.0;
						offsetX = (float)(Math.Sin(t) * 3.0);
						offsetY = (float)(Math.Cos(t) * 3.0);
					}

					var fillPaint = IsRedPiece(cell) ? redPaint : blackPaint;
					var strokePaint = IsRedPiece(cell) ? redStroke : blackStroke;

					canvas.DrawCircle(cx + offsetX, cy + offsetY, radius, fillPaint);
					canvas.DrawCircle(cx + offsetX, cy + offsetY, radius, strokePaint);

					// King ring
					if (cell == CheckersCell.RedKing || cell == CheckersCell.BlackKing)
					{
						canvas.DrawCircle(cx + offsetX, cy + offsetY, radius * 0.6f, kingPaint);
					}

					// Selected outline
					if (_selectedRow == row && _selectedCol == col)
					{
						using var selectedPaint = new SKPaint
						{
							Color = new SKColor(0, 255, 255),
							Style = SKPaintStyle.Stroke,
							StrokeWidth = 3,
							IsAntialias = true
						};
						canvas.DrawCircle(cx + offsetX, cy + offsetY, radius * 1.05f, selectedPaint);
					}
				}
			}
		}

		// ==== Board math & local move helper ================================

		private bool TryMapPointToCell(double px, double py, out int row, out int col)
		{
			row = -1; col = -1;

			double width = BoardSurface.ActualWidth;
			double height = BoardSurface.ActualHeight;
			if (width <= 0 || height <= 0)
				return false;

			double padding = 20;
			double boardSizePx = Math.Min(width, height) - 2 * padding;
			if (boardSizePx <= 0) return false;

			double cellSize = boardSizePx / BoardSize;
			double boardX = (width - boardSizePx) / 2.0;
			double boardY = (height - boardSizePx) / 2.0;

			if (px < boardX || py < boardY ||
				px >= boardX + boardSizePx ||
				py >= boardY + boardSizePx)
			{
				return false;
			}

			col = (int)((px - boardX) / cellSize);
			row = (int)((py - boardY) / cellSize);

			if (row < 0 || row >= BoardSize || col < 0 || col >= BoardSize)
				return false;

			return true;
		}

		private bool IsRedPiece(CheckersCell c) =>
			c == CheckersCell.RedMan || c == CheckersCell.RedKing;

		private bool IsBlackPiece(CheckersCell c) =>
			c == CheckersCell.BlackMan || c == CheckersCell.BlackKing;

		private bool IsMyPiece(CheckersCell c, bool isRedSide, bool isBlackSide)
		{
			if (c == CheckersCell.Empty) return false;
			if (isRedSide && IsRedPiece(c)) return true;
			if (isBlackSide && IsBlackPiece(c)) return true;
			return false;
		}

		private IEnumerable<(int row, int col)> GetCandidateMovesForPiece(
			int row,
			int col,
			bool isRedSide)
		{
			var cell = _cells[row * BoardSize + col];
			if (cell == CheckersCell.Empty)
				yield break;

			bool isKing = cell == CheckersCell.RedKing || cell == CheckersCell.BlackKing;
			int forwardDir = isRedSide ? -1 : +1;

			// Mandatory capture preview: if any capture exists for our side,
			// we only show capture moves.
			bool anyCapture = AnyCaptureForSide(isRedSide);

			if (!anyCapture)
			{
				// Simple moves (distance 1)
				if (!isKing)
				{
					foreach (var (dr, dc) in new[] { (forwardDir, -1), (forwardDir, +1) })
					{
						int nr = row + dr;
						int nc = col + dc;
						if (IsInsideBoard(nr, nc) && _cells[nr * BoardSize + nc] == CheckersCell.Empty)
							yield return (nr, nc);
					}
				}
				else
				{
					foreach (var (dr, dc) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
					{
						int nr = row + dr;
						int nc = col + dc;
						if (IsInsideBoard(nr, nc) && _cells[nr * BoardSize + nc] == CheckersCell.Empty)
							yield return (nr, nc);
					}
				}
			}

			// Capture moves (distance 2)
			if (!isKing)
			{
				foreach (var (dr, dc) in new[] { (forwardDir, -1), (forwardDir, +1) })
				{
					if (TryCaptureTarget(row, col, dr, dc, isRedSide, out var target))
						yield return target;
				}
			}
			else
			{
				foreach (var (dr, dc) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
				{
					if (TryCaptureTarget(row, col, dr, dc, isRedSide, out var target))
						yield return target;
				}
			}
		}

		private bool AnyCaptureForSide(bool isRedSide)
		{
			for (int r = 0; r < BoardSize; r++)
			{
				for (int c = 0; c < BoardSize; c++)
				{
					var cell = _cells[r * BoardSize + c];
					if (cell == CheckersCell.Empty)
						continue;

					if (isRedSide && !IsRedPiece(cell)) continue;
					if (!isRedSide && !IsBlackPiece(cell)) continue;

					bool isKing = cell == CheckersCell.RedKing || cell == CheckersCell.BlackKing;
					int forwardDir = isRedSide ? -1 : +1;

					if (!isKing)
					{
						foreach (var (dr, dc) in new[] { (forwardDir, -1), (forwardDir, +1) })
						{
							if (TryCaptureTarget(r, c, dr, dc, isRedSide, out _))
								return true;
						}
					}
					else
					{
						foreach (var (dr, dc) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
						{
							if (TryCaptureTarget(r, c, dr, dc, isRedSide, out _))
								return true;
						}
					}
				}
			}

			return false;
		}

		private bool TryCaptureTarget(
			int row,
			int col,
			int dRow,
			int dCol,
			bool isRedSide,
			out (int row, int col) target)
		{
			target = default;

			int midRow = row + dRow;
			int midCol = col + dCol;
			int toRow = row + 2 * dRow;
			int toCol = col + 2 * dCol;

			if (!IsInsideBoard(midRow, midCol) || !IsInsideBoard(toRow, toCol))
				return false;

			var midCell = _cells[midRow * BoardSize + midCol];
			var destCell = _cells[toRow * BoardSize + toCol];

			if (destCell != CheckersCell.Empty)
				return false;

			if (midCell == CheckersCell.Empty)
				return false;

			// Must be opponent piece.
			if (isRedSide && !IsBlackPiece(midCell)) return false;
			if (!isRedSide && !IsRedPiece(midCell)) return false;

			target = (toRow, toCol);
			return true;
		}

		private bool IsInsideBoard(int row, int col) =>
			row >= 0 && row < BoardSize && col >= 0 && col < BoardSize;
	}
}

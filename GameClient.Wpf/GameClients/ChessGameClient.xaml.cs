using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameContracts;
using GameLogic.BoardGames;
using GameLogic.Chess;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace GameClient.Wpf.GameClients
{
	public partial class ChessGameClient : UserControl, IGameClient
	{
		// ==== IGameClient plumbing ==========================================

		public GameType GameType => GameType.Chess;
		public FrameworkElement View => this;

		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		private string? _roomCode;
		private string? _playerId;

		// My assigned color (from server)
		private ChessColor? _myColor;
		private bool _colorsAssigned;

		public void SetConnection(Func<HubMessage, Task> sendAsync, Func<bool> isSocketOpen)
		{
			_sendAsync = sendAsync;
			_isSocketOpen = isSocketOpen;
		}

		public void OnRoomChanged(string? roomCode, string? playerId)
		{
			_roomCode = roomCode;
			_playerId = playerId;

			ResetGame();

			if (_roomCode != null && _playerId != null)
			{
				ShowColorChoiceOverlay();
			}
			else
			{
				ColorChoiceOverlay.Visibility = Visibility.Collapsed;
			}
		}

		public void OnMessageReceived(HubMessage msg) { }
		public bool TryHandleMessage(HubMessage msg)
		{
			// Quick filter (safe on any thread)
			if (msg.RoomCode != _roomCode)
				return false;

			// If we're not on the UI thread, hop to it and handle there.
			if (!Dispatcher.CheckAccess())
			{
				_ = Dispatcher.InvokeAsync(() => TryHandleMessage(msg));
				return true; // we ARE handling it (async)
			}

			switch (msg.MessageType)
			{
				case "ChessColorAssigned":
					{
						var payload = JsonSerializer.Deserialize<ChessColorAssignedPayload>(msg.PayloadJson);
						if (payload != null && payload.RoomCode == _roomCode)
						{
							ApplyColorAssignment(payload); // touches UI -> now safe
							return true;
						}
						break;
					}

				case "ChessMoveApplied":
					{
						var payload = JsonSerializer.Deserialize<ChessMovePayload>(msg.PayloadJson);
						if (payload != null && payload.RoomCode == _roomCode)
						{
							ApplyRemoteMove(payload); // calls AfterSuccessfulMove -> UI safe now
							return true;
						}
						break;
					}

				case "ChessRestarted":
					{
						var payload = JsonSerializer.Deserialize<ChessRestartedPayload>(msg.PayloadJson);
						if (payload != null && payload.RoomCode == _roomCode)
						{
							ResetGame();

							// IMPORTANT: go back to the pre-game flow
							ShowColorChoiceOverlay();

							return true;
						}
						break;
					}
				case "ChessResigned":
					{
						var payload = JsonSerializer.Deserialize<ChessResignedPayload>(msg.PayloadJson);
						if (payload != null && payload.RoomCode == _roomCode)
						{
							ApplyResign(payload);
							return true;
						}
						break;
					}
				case "ChessDrawOffered":
					{
						var payload = JsonSerializer.Deserialize<ChessDrawOfferedPayload>(msg.PayloadJson);
						if (payload != null && payload.RoomCode == _roomCode)
						{
							ApplyDrawOffered(payload);
							return true;
						}
						break;
					}
				case "ChessDrawDeclined":
					{
						var payload = JsonSerializer.Deserialize<ChessDrawDeclinedPayload>(msg.PayloadJson);
						if (payload != null && payload.RoomCode == _roomCode)
						{
							ApplyDrawDeclined(payload);
							return true;
						}
						break;
					}
				case "ChessDrawAgreed":
					{
						var payload = JsonSerializer.Deserialize<ChessDrawAgreedPayload>(msg.PayloadJson);
						if (payload != null && payload.RoomCode == _roomCode)
						{
							ApplyDrawAgreed(payload);
							return true;
						}
						break;
					}
			}

			return false;
		}


		public void OnDisconnected()
		{
			_roomCode = null;
			_playerId = null;
			_myColor = null;
			_colorsAssigned = false;
			StatusText.Text = "Disconnected.";
			ColorChoiceOverlay.Visibility = Visibility.Collapsed;
		}

		public void OnKeyDown(KeyEventArgs e) { }
		public void OnKeyUp(KeyEventArgs e) { }

		// ==== Local state ===================================================

		private ChessState _state = new ChessState();
		private Board Board => _state.Board;

		private (int row, int col)? _selectedCell;
		private readonly List<(int row, int col)> _legalMoves = new();

		private bool _isDragging;
		private (int row, int col)? _dragFromCell;
		private Point _dragCurrentPosition;

		private static readonly Dictionary<string, SKBitmap> _pieceBitmaps = new();
		private readonly Dictionary<string, ImageSource> _pieceImageSources = new();

		// false = White at bottom; true = Black at bottom
		private bool _isFlipped = false;

		public ChessGameClient()
		{
			InitializeComponent();

			Loaded += (_, _) =>
			{
				SkiaView.Focus();
				SkiaView.MouseLeftButtonDown += OnMouseLeftButtonDown;
				SkiaView.MouseMove += OnMouseMove;
				SkiaView.MouseLeftButtonUp += OnMouseLeftButtonUp;
			};

			SizeChanged += (_, _) => SkiaView.InvalidateVisual();
		}

		// ==== UI helpers ====================================================

		private void ShowColorChoiceOverlay()
		{
			if (_playerId == "P1")
			{
				ColorChoiceOverlay.Visibility = Visibility.Visible;
				ColorChoiceSubText.Text = "You are Player 1. Pick White or Black.";
				ChooseWhiteButton.IsEnabled = true;
				ChooseBlackButton.IsEnabled = true;
			}
			else
			{
				ColorChoiceOverlay.Visibility = Visibility.Visible;
				ColorChoiceSubText.Text = "Waiting for Player 1 to choose colors...";
				ChooseWhiteButton.IsEnabled = false;
				ChooseBlackButton.IsEnabled = false;
			}

			StatusText.Text = "Waiting for color choice...";
		}

		private void ApplyColorAssignment(ChessColorAssignedPayload payload)
		{
			_colorsAssigned = true;

			if (_playerId == null)
				return;

			if (_playerId == payload.WhitePlayerId)
			{
				_myColor = ChessColor.White;
			}
			else if (_playerId == payload.BlackPlayerId)
			{
				_myColor = ChessColor.Black;
			}
			else
			{
				_myColor = null; // spectator
			}

			if (_myColor.HasValue)
			{
				StatusText.Text = _myColor.Value == ChessColor.White ? "You are White." : "You are Black.";
				_isFlipped = _myColor.Value == ChessColor.Black;
			}
			else
			{
				StatusText.Text = "Spectating.";
				_isFlipped = false;
			}

			ColorChoiceOverlay.Visibility = Visibility.Collapsed;
			SkiaView.InvalidateVisual();
		}

		// ==== Networking helpers ===========================================

		private async Task SendColorChoiceAsync(ChessColorDto color)
		{
			if (_sendAsync == null || _isSocketOpen == null || !_isSocketOpen() || _roomCode == null || _playerId == null)
				return;

			var payload = new ChessColorChoicePayload
			{
				RoomCode = _roomCode,
				ChosenColor = color
			};

			var msg = new HubMessage
			{
				MessageType = "ChessColorChoice",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}

		private async Task SendMoveAsync(int fromRow, int fromCol, int toRow, int toCol)
		{
			if (_sendAsync == null || _isSocketOpen == null || !_isSocketOpen() || _roomCode == null || _playerId == null)
				return;

			var payload = new ChessMovePayload
			{
				RoomCode = _roomCode,
				FromRow = fromRow,
				FromCol = fromCol,
				ToRow = toRow,
				ToCol = toCol,
				PlayerId = _playerId
			};

			var msg = new HubMessage
			{
				MessageType = "ChessMove",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}

		private async Task SendRestartAsync()
		{
			if (_sendAsync == null || _isSocketOpen == null || !_isSocketOpen() || _roomCode == null || _playerId == null)
				return;

			var payload = new RestartGamePayload
			{
				RoomCode = _roomCode,
				GameType = GameType.Chess
			};

			var msg = new HubMessage
			{
				MessageType = "RestartGame",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}

		private void ApplyRemoteMove(ChessMovePayload payload)
		{
			// Ignore my own echo; I've already applied this move locally
			if (_playerId != null && payload.PlayerId == _playerId)
				return;

			if (_state.IsGameOver)
				return;

			if (_state.TryMove(payload.FromRow, payload.FromCol, payload.ToRow, payload.ToCol, out var error))
			{
				AfterSuccessfulMove();
			}
			else
			{
				// Desync; in practice this shouldn't happen.
				Console.WriteLine($"[ChessClient] Failed to apply remote move: {error}");
			}
		}

		// ==== Flip button ===================================================

		private void OnFlipBoardClicked(object sender, RoutedEventArgs e)
		{
			_isFlipped = !_isFlipped;
			SkiaView.InvalidateVisual();
		}


		private void ApplyDrawOffered(ChessDrawOfferedPayload payload)
		{
			bool iOffered = _playerId != null && payload.OfferingPlayerId == _playerId;

			if (iOffered)
			{
				StatusText.Text = "Draw offered. Waiting for opponent...";
				DrawResponsePanel.Visibility = Visibility.Collapsed;
				OfferDrawButton.IsEnabled = false; // already offered this ply
			}
			else
			{
				StatusText.Text = "Opponent offered a draw.";
				DrawResponsePanel.Visibility = Visibility.Visible;
				OfferDrawButton.IsEnabled = false; // can't offer while responding
			}
		}

		private void ApplyDrawDeclined(ChessDrawDeclinedPayload payload)
		{
			DrawResponsePanel.Visibility = Visibility.Collapsed;

			// Re-enable offer button only when it becomes valid again.
			// (Server enforces, but this keeps UX reasonable.)
			OfferDrawButton.IsEnabled = _myColor.HasValue && !_state.IsGameOver;

			StatusText.Text = "Draw declined.";
		}

		private void ApplyDrawAgreed(ChessDrawAgreedPayload payload)
		{
			DrawResponsePanel.Visibility = Visibility.Collapsed;
			OfferDrawButton.IsEnabled = false;
			ResignButton.IsEnabled = false;

			GameOverText.Text = "Draw agreed.";
			GameOverOverlay.Visibility = Visibility.Visible;

			StatusText.Text = "Game ended in a draw.";

			_selectedCell = null;
			_legalMoves.Clear();
			_isDragging = false;
			_dragFromCell = null;

			SkiaView.InvalidateVisual();
		}


		// ==== Coordinate helpers ============================================

		private (int row, int col) ViewToModel(int viewRow, int viewCol)
		{
			if (!_isFlipped)
			{
				return (viewRow, viewCol);
			}
			else
			{
				return (Board.Rows - 1 - viewRow, Board.Columns - 1 - viewCol);
			}
		}

		private (int viewRow, int viewCol) ModelToView(int row, int col)
		{
			if (!_isFlipped)
			{
				return (row, col);
			}
			else
			{
				return (Board.Rows - 1 - row, Board.Columns - 1 - col);
			}
		}

		// ==== Mouse input ====================================================

		private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (_state.IsGameOver)
				return;

			// Must have colors assigned and be a player
			if (!_colorsAssigned || !_myColor.HasValue)
				return;

			// Only interact when it's your turn
			if (_state.CurrentTurn != _myColor.Value)
				return;

			var pos = e.GetPosition(SkiaView);

			if (!TryMapPointToCell((float)pos.X, (float)pos.Y, out var viewRow, out var viewCol))
				return;

			var (row, col) = ViewToModel(viewRow, viewCol);
			var piece = _state.GetPiece(row, col);

			// Case 1: no selection yet
			if (_selectedCell is null)
			{
				if (piece is null || piece.Value.Color != _state.CurrentTurn || piece.Value.Color != _myColor.Value)
					return;

				_selectedCell = (row, col);
				_legalMoves.Clear();
				_legalMoves.AddRange(_state.GetLegalMoves(row, col));

				StartDragFromCell(row, col, pos);
				return;
			}

			// Case 2: already selected
			var (selRow, selCol) = _selectedCell.Value;

			if (piece is not null &&
				piece.Value.Color == _state.CurrentTurn &&
				piece.Value.Color == _myColor.Value)
			{
				_selectedCell = (row, col);
				_legalMoves.Clear();
				_legalMoves.AddRange(_state.GetLegalMoves(row, col));

				StartDragFromCell(row, col, pos);
				return;
			}

			// Click-to-move
			if (_state.TryMove(selRow, selCol, row, col, out var error))
			{
				_selectedCell = null;
				_legalMoves.Clear();

				if (_state.HasPendingPromotion && _state.PendingPromotion is { })
				{
					_state.ClearPendingPromotion();
				}

				AfterSuccessfulMove();

				// Notify server
				_ = SendMoveAsync(selRow, selCol, row, col);
			}
			else
			{
				// Move failed; keep selection
			}

			SkiaView.InvalidateVisual();
		}

		private void StartDragFromCell(int row, int col, Point mousePos)
		{
			_isDragging = true;
			_dragFromCell = (row, col);
			_dragCurrentPosition = mousePos;
			SkiaView.CaptureMouse();
			SkiaView.InvalidateVisual();
		}

		private void OnMouseMove(object sender, MouseEventArgs e)
		{
			if (_state.IsGameOver)
				return;

			if (!_isDragging)
				return;

			if (e.LeftButton != MouseButtonState.Pressed)
			{
				_isDragging = false;
				_dragFromCell = null;
				SkiaView.ReleaseMouseCapture();
				SkiaView.InvalidateVisual();
				return;
			}

			_dragCurrentPosition = e.GetPosition(SkiaView);
			SkiaView.InvalidateVisual();
		}

		private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (_state.IsGameOver)
				return;

			if (!_isDragging)
				return;

			_isDragging = false;
			SkiaView.ReleaseMouseCapture();

			if (_dragFromCell is not { } fromCell)
			{
				SkiaView.InvalidateVisual();
				return;
			}

			var pos = e.GetPosition(SkiaView);

			if (!TryMapPointToCell((float)pos.X, (float)pos.Y, out var viewRow, out var viewCol))
			{
				_dragFromCell = null;
				SkiaView.InvalidateVisual();
				return;
			}

			var (toRow, toCol) = ViewToModel(viewRow, viewCol);
			var (fromRow, fromCol) = fromCell;

			if (_state.TryMove(fromRow, fromCol, toRow, toCol, out var error))
			{
				_selectedCell = null;
				_legalMoves.Clear();
				_dragFromCell = null;

				if (_state.HasPendingPromotion && _state.PendingPromotion is { })
				{
					_state.ClearPendingPromotion();
				}

				AfterSuccessfulMove();

				// Notify server
				_ = SendMoveAsync(fromRow, fromCol, toRow, toCol);
			}
			else
			{
				_selectedCell = fromCell;
				_legalMoves.Clear();
				_legalMoves.AddRange(_state.GetLegalMoves(fromRow, fromCol));
				_dragFromCell = null;

				SkiaView.InvalidateVisual();
			}
		}

		private bool TryMapPointToCell(float x, float y, out int row, out int col)
		{
			row = col = -1;

			float margin = 20f;
			float width = (float)SkiaView.ActualWidth;
			float height = (float)SkiaView.ActualHeight;

			if (width <= 0 || height <= 0)
				return false;

			float boardSize = MathF.Min(width, height) - margin * 2f;
			if (boardSize <= 0) return false;

			float startX = (width - boardSize) / 2f;
			float startY = (height - boardSize) / 2f;

			if (x < startX || x > startX + boardSize ||
				y < startY || y > startY + boardSize)
				return false;

			float cellSize = boardSize / Math.Max(Board.Rows, Board.Columns);

			col = (int)((x - startX) / cellSize);
			row = (int)((y - startY) / cellSize);

			if (col < 0 || col >= Board.Columns || row < 0 || row >= Board.Rows)
				return false;

			return true;
		}

		// ==== Skia rendering ================================================

		private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
		{
			var canvas = e.Surface.Canvas;
			var info = e.Info;

			canvas.Clear(new SKColor(18, 18, 18));

			float margin = 20f;
			float boardSize = MathF.Min(info.Width, info.Height) - margin * 2f;
			if (boardSize <= 0) return;

			float startX = (info.Width - boardSize) / 2f;
			float startY = (info.Height - boardSize) / 2f;
			float cellSize = boardSize / Math.Max(Board.Rows, Board.Columns);

			using var lightPaint = new SKPaint { Color = new SKColor(238, 238, 210), IsAntialias = true };
			using var darkPaint = new SKPaint { Color = new SKColor(118, 150, 86), IsAntialias = true };
			using var borderPaint = new SKPaint { Color = new SKColor(200, 200, 200), StrokeWidth = 3f, IsStroke = true, IsAntialias = true };
			using var selectedPaint = new SKPaint { Color = new SKColor(255, 255, 0, 80), IsAntialias = true };
			using var legalMovePaint = new SKPaint { Color = new SKColor(100, 100, 100, 200), IsAntialias = true };
			using var labelPaint = new SKPaint { Color = new SKColor(220, 220, 220), IsAntialias = true, TextAlign = SKTextAlign.Center };

			labelPaint.TextSize = MathF.Min(14f, cellSize * 0.35f);

			// 1) Squares
			for (int viewRow = 0; viewRow < Board.Rows; viewRow++)
			{
				for (int viewCol = 0; viewCol < Board.Columns; viewCol++)
				{
					var (modelRow, modelCol) = ViewToModel(viewRow, viewCol);

					float x = startX + viewCol * cellSize;
					float y = startY + viewRow * cellSize;

					var rect = new SKRect(x, y, x + cellSize, y + cellSize);
					var paint = Board.IsDarkSquare(modelRow, modelCol) ? darkPaint : lightPaint;

					canvas.DrawRect(rect, paint);
				}
			}

			// 2) Selected cell overlay
			if (_selectedCell is { } sel)
			{
				var (selViewRow, selViewCol) = ModelToView(sel.row, sel.col);
				float x = startX + selViewCol * cellSize;
				float y = startY + selViewRow * cellSize;
				var rect = new SKRect(x, y, x + cellSize, y + cellSize);
				canvas.DrawRect(rect, selectedPaint);
			}

			// 3) Legal move hints
			foreach (var (targetRow, targetCol) in _legalMoves)
			{
				var (viewRow, viewCol) = ModelToView(targetRow, targetCol);
				float centerX = startX + (viewCol + 0.5f) * cellSize;
				float centerY = startY + (viewRow + 0.5f) * cellSize;
				float radius = cellSize * 0.18f;
				canvas.DrawCircle(centerX, centerY, radius, legalMovePaint);
			}

			// 4) Pieces (non-drag)
			float padding = cellSize * 0.08f;
			for (int viewRow = 0; viewRow < Board.Rows; viewRow++)
			{
				for (int viewCol = 0; viewCol < Board.Columns; viewCol++)
				{
					var (modelRow, modelCol) = ViewToModel(viewRow, viewCol);
					var piece = _state.GetPiece(modelRow, modelCol);
					if (piece is null) continue;

					if (_isDragging && _dragFromCell is { } dragCell &&
						dragCell.row == modelRow && dragCell.col == modelCol)
					{
						continue;
					}

					var bitmap = GetPieceBitmap(piece.Value);
					if (bitmap is null) continue;

					float x = startX + viewCol * cellSize + padding;
					float y = startY + viewRow * cellSize + padding;
					float size = cellSize - padding * 2f;

					var destRect = new SKRect(x, y, x + size, y + size);
					canvas.DrawBitmap(bitmap, destRect);
				}
			}

			// 4.5) Dragged piece following mouse
			if (_isDragging && _dragFromCell is { } dragFrom)
			{
				var draggedPiece = _state.GetPiece(dragFrom.row, dragFrom.col);
				if (draggedPiece is not null)
				{
					var bitmap = GetPieceBitmap(draggedPiece.Value);
					if (bitmap is not null)
					{
						double wpfWidth = SkiaView.ActualWidth;
						double wpfHeight = SkiaView.ActualHeight;

						if (wpfWidth > 0 && wpfHeight > 0)
						{
							float sx = (float)(_dragCurrentPosition.X / wpfWidth) * info.Width;
							float sy = (float)(_dragCurrentPosition.Y / wpfHeight) * info.Height;

							float size = cellSize - padding * 2f;
							var destRect = new SKRect(
								sx - size / 2f,
								sy - size / 2f,
								sx + size / 2f,
								sy + size / 2f);

							canvas.DrawBitmap(bitmap, destRect);
						}
					}
				}
			}

			// 5) Coordinate labels
			string files = "ABCDEFGH";

			for (int viewCol = 0; viewCol < Board.Columns; viewCol++)
			{
				char fileChar = _isFlipped
					? files[Board.Columns - 1 - viewCol]
					: files[viewCol];

				float centerX = startX + (viewCol + 0.5f) * cellSize;

				float bottomY = startY + boardSize + labelPaint.TextSize + 2f;
				canvas.DrawText(fileChar.ToString(), centerX, bottomY, labelPaint);

				float topY = startY - 4f;
				canvas.DrawText(fileChar.ToString(), centerX, topY, labelPaint);
			}

			for (int viewRow = 0; viewRow < Board.Rows; viewRow++)
			{
				int rank = _isFlipped ? (viewRow + 1) : (Board.Rows - viewRow);
				string rankText = rank.ToString();

				float centerY = startY + (viewRow + 0.5f) * cellSize + (labelPaint.TextSize * 0.35f);

				float leftX = startX - labelPaint.TextSize;
				canvas.DrawText(rankText, leftX, centerY, labelPaint);

				float rightX = startX + boardSize + labelPaint.TextSize;
				canvas.DrawText(rankText, rightX, centerY, labelPaint);
			}

			var boardRect = new SKRect(startX, startY, startX + boardSize, startY + boardSize);
			canvas.DrawRect(boardRect, borderPaint);
		}

		// ==== Piece bitmap loading ==========================================

		private static SKBitmap? GetPieceBitmap(ChessPiece piece)
		{
			string key = $"{piece.Color}{piece.Type}";

			if (_pieceBitmaps.TryGetValue(key, out var cached))
				return cached;

			string fileName = key + ".png";
			string baseDir = AppContext.BaseDirectory;
			string path = Path.Combine(baseDir, "Assets", "Images", "Chess", fileName);

			if (!File.Exists(path))
				return null;

			var bitmap = SKBitmap.Decode(path);
			_pieceBitmaps[key] = bitmap;
			return bitmap;
		}

		private ImageSource? GetWpfPieceImage(ChessPiece piece)
		{
			string key = $"{piece.Color}{piece.Type}";
			if (_pieceImageSources.TryGetValue(key, out var src))
				return src;

			string fileName = key + ".png";
			string baseDir = AppContext.BaseDirectory;
			string path = Path.Combine(baseDir, "Assets", "Images", "Chess", fileName);

			if (!File.Exists(path))
				return null;

			var bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.UriSource = new Uri(path);
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.EndInit();
			bmp.Freeze();

			_pieceImageSources[key] = bmp;
			return bmp;
		}

		private void RefreshCapturedPanels()
		{
			if (WhiteCapturedPanel is null || BlackCapturedPanel is null)
				return;

			WhiteCapturedPanel.Children.Clear();
			foreach (var piece in _state.CapturedByWhite)
			{
				var src = GetWpfPieceImage(piece);
				if (src is null) continue;

				WhiteCapturedPanel.Children.Add(new Image
				{
					Source = src,
					Width = 24,
					Height = 24,
					Margin = new Thickness(2, 0, 2, 0)
				});
			}

			BlackCapturedPanel.Children.Clear();
			foreach (var piece in _state.CapturedByBlack)
			{
				var src = GetWpfPieceImage(piece);
				if (src is null) continue;

				BlackCapturedPanel.Children.Add(new Image
				{
					Source = src,
					Width = 24,
					Height = 24,
					Margin = new Thickness(2, 0, 2, 0)
				});
			}
		}

		private void RefreshMoveHistory()
		{
			if (MoveHistoryPanel is null)
				return;

			MoveHistoryPanel.Children.Clear();

			var history = _state.MoveHistory;
			if (history.Count == 0)
			{
				MoveHistoryPanel.Children.Add(new TextBlock
				{
					Text = "No moves yet.",
					Foreground = Brushes.Gray,
					FontStyle = FontStyles.Italic
				});
				return;
			}

			for (int i = 0; i < history.Count;)
			{
				var whiteMove = history[i];
				ChessMoveRecord? blackMove = null;

				if (i + 1 < history.Count && history[i + 1].Color == ChessColor.Black)
				{
					blackMove = history[i + 1];
				}

				string line = blackMove is not null
					? $"{whiteMove.MoveNumber}. {whiteMove.San}  {blackMove.San}"
					: $"{whiteMove.MoveNumber}. {whiteMove.San}";

				MoveHistoryPanel.Children.Add(new TextBlock
				{
					Text = line,
					Foreground = Brushes.White,
					Margin = new Thickness(0, 0, 0, 2)
				});

				i += blackMove is not null ? 2 : 1;
			}
		}

		private void ShowGameOverPopupIfNeeded()
		{
			if (!_state.IsGameOver || !_state.Winner.HasValue)
				return;

			string text = _state.Winner.Value == ChessColor.White
				? "White wins!"
				: "Black wins!";

			GameOverText.Text = text;
			GameOverOverlay.Visibility = Visibility.Visible;
		}

		private void AfterSuccessfulMove()
		{
			RefreshCapturedPanels();
			RefreshMoveHistory();
			SkiaView.InvalidateVisual();
			ShowGameOverPopupIfNeeded();

			// UX: allow offering again; server will decide if it's valid right now.
			OfferDrawButton.IsEnabled = _myColor.HasValue && !_state.IsGameOver;
			DrawResponsePanel.Visibility = Visibility.Collapsed;
		}
		private void ApplyResign(ChessResignedPayload payload)
		{
			// Freeze local interaction
			_selectedCell = null;
			_legalMoves.Clear();
			_isDragging = false;
			_dragFromCell = null;
			OfferDrawButton.IsEnabled = false;
			DrawResponsePanel.Visibility = Visibility.Collapsed;

			var iResigned = _playerId != null && payload.ResigningPlayerId == _playerId;

			GameOverText.Text = iResigned ? "You resigned." : "Opponent resigned.";
			GameOverOverlay.Visibility = Visibility.Visible;

			StatusText.Text = iResigned ? "You lost by resignation." : "You won by resignation.";

			// Keep board visible; just block via overlay + your existing checks
			SkiaView.InvalidateVisual();

			ResignButton.IsEnabled = false;
		}


		private async Task SendResignAsync()
		{
			if (_sendAsync == null || _isSocketOpen == null || !_isSocketOpen() || _roomCode == null || _playerId == null)
				return;

			var payload = new ChessResignPayload
			{
				RoomCode = _roomCode,
				PlayerId = _playerId
			};

			var msg = new HubMessage
			{
				MessageType = "ChessResign",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}
		private async Task SendOfferDrawAsync()
		{
			if (_sendAsync == null || _isSocketOpen == null || !_isSocketOpen() || _roomCode == null || _playerId == null)
				return;

			var payload = new ChessDrawOfferPayload
			{
				RoomCode = _roomCode,
				PlayerId = _playerId
			};

			var msg = new HubMessage
			{
				MessageType = "ChessDrawOffer",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}

		private async Task SendDrawResponseAsync(bool accept)
		{
			if (_sendAsync == null || _isSocketOpen == null || !_isSocketOpen() || _roomCode == null || _playerId == null)
				return;

			var payload = new ChessDrawResponsePayload
			{
				RoomCode = _roomCode,
				PlayerId = _playerId,
				Accept = accept
			};

			var msg = new HubMessage
			{
				MessageType = "ChessDrawResponse",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}

		private async void OnResignClicked(object sender, RoutedEventArgs e)
		{
			// Only players can resign (not spectators)
			if (!_myColor.HasValue) return;

			// Optional: disable button to prevent spam
			ResignButton.IsEnabled = false;

			await SendResignAsync();
		}
		private async void OnOfferDrawClicked(object sender, RoutedEventArgs e)
		{
			if (!_myColor.HasValue) return;
			if (_state.IsGameOver) return;

			// Client-side UX: disable immediately; server is still authority
			OfferDrawButton.IsEnabled = false;

			await SendOfferDrawAsync();
		}

		private async void OnAcceptDrawClicked(object sender, RoutedEventArgs e)
		{
			await SendDrawResponseAsync(accept: true);
		}

		private async void OnDeclineDrawClicked(object sender, RoutedEventArgs e)
		{
			await SendDrawResponseAsync(accept: false);
		}




		// ==== Game over buttons ============================================

		private void OnGameOverCloseClicked(object sender, RoutedEventArgs e)
		{
			GameOverOverlay.Visibility = Visibility.Collapsed;
		}

		private async void OnGameOverRestartClicked(object sender, RoutedEventArgs e)
		{
			await SendRestartAsync();
		}

		private void ResetGame()
		{
			_state = new ChessState();
			_selectedCell = null;
			_legalMoves.Clear();
			_isDragging = false;
			_dragFromCell = null;

			GameOverOverlay.Visibility = Visibility.Collapsed;

			_myColor = null;
			_colorsAssigned = false;
			_isFlipped = false;

			TopPlayerLabel.Text = "Opponent";
			BottomPlayerLabel.Text = "You";

			RefreshCapturedPanels();
			RefreshMoveHistory();
			OfferDrawButton.IsEnabled = true;
			DrawResponsePanel.Visibility = Visibility.Collapsed;
			SkiaView.InvalidateVisual();
		}

		// ==== Color-choice button handlers =================================

		private async void OnChooseWhiteClicked(object sender, RoutedEventArgs e)
		{
			if (_playerId != "P1")
				return;

			await SendColorChoiceAsync(ChessColorDto.White);
		}

		private async void OnChooseBlackClicked(object sender, RoutedEventArgs e)
		{
			if (_playerId != "P1")
				return;

			await SendColorChoiceAsync(ChessColorDto.Black);
		}
	}
}

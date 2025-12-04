using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GameContracts;
using System.Windows.Controls;

namespace GameClient.Wpf
{
	public partial class PongGameClient : UserControl, IGameClient
	{
		private Func<HubMessage, Task>? _sendAsync;
		private Func<bool>? _isSocketOpen;

		private string? _roomCode;
		private string? _playerId;

		private int _currentDirection = 0; // -1,0,1

		public PongGameClient()
		{
			InitializeComponent();
		}

		public GameType GameType => GameType.Pong;
		public FrameworkElement View => this;

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
			_currentDirection = 0;
			PongCanvas.Children.Clear();
		}

		public bool TryHandleMessage(HubMessage msg)
		{
			if (msg.MessageType != "PongState")
				return false;

			var payload = JsonSerializer.Deserialize<PongStatePayload>(msg.PayloadJson);
			if (payload == null) return true;

			Dispatcher.Invoke(() => DrawPong(payload));
			return true;
		}

		public void OnKeyDown(KeyEventArgs e)
		{
			if (_roomCode == null || _playerId == null)
				return;

			var newDir = _currentDirection;

			if (e.Key == Key.W || e.Key == Key.Up)
			{
				newDir = -1;
			}
			else if (e.Key == Key.S || e.Key == Key.Down)
			{
				newDir = 1;
			}
			else
			{
				return;
			}

			if (newDir != _currentDirection)
			{
				_currentDirection = newDir;
				_ = SendPongInputAsync(newDir);
			}
		}

		public void OnKeyUp(KeyEventArgs e)
		{
			if (_roomCode == null || _playerId == null)
				return;

			if ((e.Key == Key.W || e.Key == Key.Up) && _currentDirection == -1)
			{
				_currentDirection = 0;
				_ = SendPongInputAsync(0);
			}
			else if ((e.Key == Key.S || e.Key == Key.Down) && _currentDirection == 1)
			{
				_currentDirection = 0;
				_ = SendPongInputAsync(0);
			}
		}

		// ── Drawing ───────────────────────────────────────────────────────────

		private void DrawPong(PongStatePayload state)
		{
			var width = PongCanvas.ActualWidth;
			var height = PongCanvas.ActualHeight;

			if (width <= 0) width = PongCanvas.Width;
			if (height <= 0) height = PongCanvas.Height;
			if (width <= 0 || height <= 0) return;

			PongCanvas.Children.Clear();

			double ballSize = 10;
			double paddleWidth = 10;
			double paddleHeight = 40;

			// Helper: clamp 0–100 to avoid drawing outside the window
			double Clamp01(double v) => v < 0 ? 0 : (v > 100 ? 100 : v);

			// Convert from 0-100 space to canvas pixels
			double ToPxX(float x) => Clamp01(x) / 100.0 * width;
			double ToPxY(float y) => Clamp01(y) / 100.0 * height;

			// Paddles: center Y is in 0-100 space
			double p1centerY = ToPxY(state.Paddle1Y);
			double p2centerY = ToPxY(state.Paddle2Y);

			// Left paddle
			var paddle1 = new Rectangle
			{
				Width = paddleWidth,
				Height = paddleHeight,
				Fill = Brushes.White
			};
			Canvas.SetLeft(paddle1, ToPxX(5) - paddleWidth / 2);
			Canvas.SetTop(paddle1, p1centerY - paddleHeight / 2);
			PongCanvas.Children.Add(paddle1);

			// Right paddle
			var paddle2 = new Rectangle
			{
				Width = paddleWidth,
				Height = paddleHeight,
				Fill = Brushes.White
			};
			Canvas.SetLeft(paddle2, ToPxX(95) - paddleWidth / 2);
			Canvas.SetTop(paddle2, p2centerY - paddleHeight / 2);
			PongCanvas.Children.Add(paddle2);

			// Ball
			var ball = new Ellipse
			{
				Width = ballSize,
				Height = ballSize,
				Fill = Brushes.White
			};
			var ballX = ToPxX(state.BallX);
			var ballY = ToPxY(state.BallY);
			Canvas.SetLeft(ball, ballX - ballSize / 2);
			Canvas.SetTop(ball, ballY - ballSize / 2);
			PongCanvas.Children.Add(ball);

			// Score at top middle
			var scoreText = new TextBlock
			{
				Text = $"{state.Score1} : {state.Score2}",
				Foreground = Brushes.White,
				FontSize = 24,
				FontWeight = FontWeights.Bold
			};

			// Measure so we can center properly
			scoreText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			var scoreWidth = scoreText.DesiredSize.Width;

			Canvas.SetLeft(scoreText, (width - scoreWidth) / 2);
			Canvas.SetTop(scoreText, 5); // a little padding from top
			PongCanvas.Children.Add(scoreText);
		}
		// ── Send input ────────────────────────────────────────────────────────

		private async Task SendPongInputAsync(int direction)
		{
			if (_sendAsync == null || _isSocketOpen == null)
				return;
			if (!_isSocketOpen() || _roomCode == null || _playerId == null)
				return;

			var payload = new PongInputPayload
			{
				Direction = direction
			};

			var msg = new HubMessage
			{
				MessageType = "PongInput",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(msg);
		}
	}
}

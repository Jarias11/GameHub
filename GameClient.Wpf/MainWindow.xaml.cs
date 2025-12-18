using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GameContracts;
using System.Windows.Media;
using System.Windows.Controls;
using System.ComponentModel;
using System.IO;
using System.Reflection;

namespace GameClient.Wpf
{
	public partial class MainWindow : Window, INotifyPropertyChanged
	{
		private ClientWebSocket? _socket;
		private string? _roomCode;
		private string? _playerId;
		private GameType? _currentGameType;
		private int _playerCount;
		private bool _isConnected;


		//XAML for binding
		public List<GameCardModel> ArcadeGames { get; } = new();
		public List<GameCardModel> BoardGames { get; } = new();
		public List<GameCardModel> WordGames { get; } = new();
		public List<GameCardModel> CardGames { get; } = new();



		public event PropertyChangedEventHandler? PropertyChanged;
		private readonly Dictionary<GameType, IGameClient> _gameClients = new();


		public MainWindow()
		{
			InitializeComponent();

			BuildGameCards();
			DataContext = this;

			// Create game clients & wire them up and now discover all game clients automatically
			BuildGameClients();



			this.Loaded += (_, __) =>
			{
				Focus();
				RoomStatusText.Text = "Not connected.";


				// Fire-and-forget update check on startup
				_ = UpdateService.CheckForUpdatesAsync(this);
				// 			MessageBox.Show(
				// $"Assembly version: {Assembly.GetExecutingAssembly().GetName().Version}",
				// "Version debug");
			};

		}


		private async void TestUpdateButton_Click(object sender, RoutedEventArgs e)
		{
			await UpdateService.CheckForUpdatesAsync(this);
		}


		private void BuildGameClients()
		{
			// Find all non-abstract types in this assembly that implement IGameClient
			var gameClientTypes = typeof(MainWindow).Assembly
				.GetTypes()
				.Where(t =>
					typeof(IGameClient).IsAssignableFrom(t) &&
					!t.IsAbstract &&
					t.GetConstructor(Type.EmptyTypes) != null); // needs parameterless ctor

			foreach (var type in gameClientTypes)
			{
				try
				{
					// Create instance
					if (Activator.CreateInstance(type) is IGameClient client)
					{
						// Use the GameType property as the key
						_gameClients[client.GameType] = client;

						// Wire up shared connection hooks (even if offline games ignore them)
						client.SetConnection(SendMessageAsync, IsSocketOpen);
					}
				}
				catch (Exception ex)
				{
					// This prevents a single bad client from killing the whole app
					Log($"Failed to create game client {type.FullName}: {ex}");
				}
			}
		}
		private void BuildGameCards()
		{
			foreach (var info in GameCatalog.All)
			{
				var card = new GameCardModel
				{
					GameType = info.Type,
					Category = info.Category,
					Emoji = info.Emoji,
					Name = info.Name,
					Tagline = info.Tagline,
					PlayersText = info.PlayersText,
					IsOnline = info.IsOnline
				};

				switch (info.Category)
				{
					case GameCategory.Arcade:
						ArcadeGames.Add(card);
						break;
					case GameCategory.Board:
						BoardGames.Add(card);
						break;
					case GameCategory.Word:
						WordGames.Add(card);
						break;
					case GameCategory.Card:
						CardGames.Add(card);
						break;
				}
			}
		}











		// ========= Connection =========

		private async void ConnectButton_Click(object sender, RoutedEventArgs e)
		{
			// If we're already connected, this click means "Disconnect"
			if (_socket != null && _socket.State == WebSocketState.Open)
			{
				await DisconnectAsync();
				return;
			}

			try
			{
				_socket = new ClientWebSocket();
				var uri = new Uri(ServerUrlBox.Text.Trim());
				await _socket.ConnectAsync(uri, CancellationToken.None);
				Log("Connected to server.");

				IsConnected = true;

				Dispatcher.Invoke(() =>
				{
					RoomStatusText.Text = "Connected. Create or join a room.";
					ConnectButton.Content = "Disconnect";
					ConnectButton.Background = (Brush)FindResource("Brush.DisconnectRed");
				});

				_ = Task.Run(ReceiveLoop);
			}
			catch (Exception ex)
			{
				Log("Connect failed: " + ex.Message);
				Dispatcher.Invoke(() =>
				{
					RoomStatusText.Text = "Connect failed.";
					ConnectButton.Content = "Connect";
					ConnectButton.Background = (Brush)FindResource("Brush.ConnectGreen");
				});
			}
		}
		private async Task DisconnectAsync()
		{
			try
			{
				if (_socket != null)
				{
					if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseSent)
					{
						await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure,
												 "Client disconnect",
												 CancellationToken.None);
					}

					_socket.Dispose();
					_socket = null;
					IsConnected = false;
				}

				// Clear active room state
				_roomCode = null;
				_playerId = null;
				_currentGameType = null;
				UpdateRoomUiState();

				Dispatcher.Invoke(() =>
				{
					RoomStatusText.Text = "Disconnected.";
					ConnectButton.Content = "Connect";
					ConnectButton.Background = (Brush)FindResource("Brush.ConnectGreen");
					GameHost.Content = null;
				});
			}
			catch (Exception ex)
			{
				Log("Disconnect failed: " + ex.Message);
			}
		}

		private async void GameCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (sender is Border border && border.DataContext is GameCardModel card)
			{
				var info = GameCatalog.Get(card.GameType);

				// If this game is offline, just activate it locally (no room, no server)
				if (info != null && !info.IsOnline)
				{
					_roomCode = null;
					_playerId = null;
					_playerCount = 0;
					_currentGameType = card.GameType;

					RoomStatusText.Text = $"Playing offline: {info.Name}";
					ActivateGame(card.GameType);
					UpdateRoomUiState();
					ScrollToCurrentGame();
					return;
				}

				// Online games -> normal flow
				await CreateRoomAsync(card.GameType);
			}
		}



		private async Task ReceiveLoop()
		{
			var buffer = new byte[4 * 1024];

			try
			{
				while (_socket != null && _socket.State == WebSocketState.Open)
				{
					using var ms = new MemoryStream();

					WebSocketReceiveResult result;
					do
					{
						result = await _socket.ReceiveAsync(
							new ArraySegment<byte>(buffer),
							CancellationToken.None);

						if (result.MessageType == WebSocketMessageType.Close)
						{
							Log("Server initiated close.");
							// Optionally acknowledge the close
							if (_socket.State == WebSocketState.CloseReceived)
							{
								await _socket.CloseAsync(
									WebSocketCloseStatus.NormalClosure,
									"Closing",
									CancellationToken.None);
							}
							return; // end ReceiveLoop
						}

						if (result.Count > 0)
						{
							ms.Write(buffer, 0, result.Count);
						}
					}
					while (!result.EndOfMessage);

					var jsonBytes = ms.ToArray();
					var json = Encoding.UTF8.GetString(jsonBytes);

					HandleIncoming(json);
				}
			}
			catch (Exception ex)
			{
				Log("Receive error: " + ex.Message);
			}
			finally
			{
				// Ensure UI reflects disconnected state when loop ends
				_roomCode = null;
				_playerId = null;
				_currentGameType = null;
				_playerCount = 0;
				IsConnected = false;

				UpdateRoomUiState();

				Dispatcher.Invoke(() =>
				{
					RoomStatusText.Text = "Disconnected.";
					ConnectButton.Content = "Connect";
					ConnectButton.Background = (Brush)FindResource("Brush.ConnectGreen");
					GameHost.Content = null;
				});
			}
		}


		private void HandleIncoming(string json)
		{
			try
			{
				var msg = JsonSerializer.Deserialize<HubMessage>(json);
				if (msg == null) return;

				switch (msg.MessageType)
				{
					case "RoomCreated":
						HandleRoomCreated(msg);
						break;

					case "RoomJoined":
						HandleRoomJoined(msg);
						break;
					case "RoomLeft":
						HandleRoomLeft(msg);
						break;

					default:
						// Let each game client decide if it wants this message
						bool handled = false;
						foreach (var gc in _gameClients.Values)
						{
							if (gc.TryHandleMessage(msg))
							{
								handled = true;
								break; // one client handled it, we're done
							}
						}

						if (!handled)
						{
							Log("<< " + json);
						}
						break;
				}
			}
			catch (Exception ex)
			{
				Log("HandleIncoming error: " + ex.Message);
			}
		}

		// ========= Room actions =========

		private async void CreatePongRoomButton_Click(object sender, RoutedEventArgs e)
		{
			await CreateRoomAsync(GameType.Pong);
		}

		private async void CreateWordGuessRoomButton_Click(object sender, RoutedEventArgs e)
		{
			await CreateRoomAsync(GameType.WordGuess);
		}
		private async void CreateTicTacToeRoomButton_Click(object sender, RoutedEventArgs e)
		{
			await CreateRoomAsync(GameType.TicTacToe);
		}
		private async void CreateAnagramRoomButton_Click(object sender, RoutedEventArgs e)
		{
			await CreateRoomAsync(GameType.Anagram);
		}

		private async Task CreateRoomAsync(GameType gameType)
		{
			if (!IsSocketOpen())
			{
				Log("Not connected.");
				return;
			}

			var payload = new CreateRoomPayload
			{
				GameType = gameType
			};

			var msg = new HubMessage
			{
				MessageType = "CreateRoom",
				RoomCode = "",
				PlayerId = "",
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await SendMessageAsync(msg);
		}

		private async void JoinRoomButton_Click(object sender, RoutedEventArgs e)
		{
			if (!IsSocketOpen())
			{
				Log("Not connected.");
				return;
			}
			// If we're already in a room, this acts as "Leave"
			if (!string.IsNullOrEmpty(_roomCode))
			{
				await LeaveRoomAsync();
				return;
			}

			var code = JoinCodeBox.Text.Trim().ToUpperInvariant();
			if (string.IsNullOrWhiteSpace(code))
			{
				Log("Enter a room code.");
				return;
			}

			var payload = new JoinRoomPayload
			{
				RoomCode = code
			};

			var msg = new HubMessage
			{
				MessageType = "JoinRoom",
				RoomCode = code,
				PlayerId = "",
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await SendMessageAsync(msg);
		}
		private void HandleRoomLeft(HubMessage msg)
		{
			var payload = JsonSerializer.Deserialize<RoomLeftPayload>(msg.PayloadJson);
			if (payload == null) return;

			if (!payload.Success)
			{
				Log("Leave failed: " + payload.Message);
				Dispatcher.Invoke(() =>
				{
					RoomStatusText.Text = "Leave failed: " + payload.Message;
				});
				return;
			}

			// If WE are the one who left
			if (!string.IsNullOrEmpty(payload.LeavingPlayerId) &&
				payload.LeavingPlayerId == _playerId)
			{
				_roomCode = null;
				_playerId = null;
				_currentGameType = null;
				_playerCount = 0;

				Dispatcher.Invoke(() =>
				{
					RoomStatusText.Text = "Left room.";
					GameHost.Content = null;
					JoinCodeBox.Text = string.Empty;
					UpdateRoomUiState();
				});

				Log("Left room.");
			}
			else
			{
				// Someone else left OUR room
				_playerCount = payload.PlayerCount;

				Dispatcher.Invoke(() =>
				{
					RoomStatusText.Text = "Other player left the room.";
					UpdateRoomUiState();
				});

				Log("Other player left room.");
			}
		}
		private async void RestartGameButton_Click(object sender, RoutedEventArgs e)
		{
			// If there's a current game and it's offline, restart locally
			if (_currentGameType.HasValue)
			{
				var info = GameCatalog.Get(_currentGameType.Value);
				if (info != null && !info.IsOnline)
				{
					if (_gameClients.TryGetValue(_currentGameType.Value, out var gc))
					{
						// Our Snake client treats OnRoomChanged as "reset"
						gc.OnRoomChanged(null, null);
					}
					FocusGameHost();
					return;
				}
			}



			if (!IsSocketOpen())
			{
				Log("Not connected.");
				return;
			}

			if (string.IsNullOrEmpty(_roomCode) || string.IsNullOrEmpty(_playerId) || !_currentGameType.HasValue)
			{
				Log("No active room to restart.");
				return;
			}

			var payload = new RestartGamePayload
			{
				RoomCode = _roomCode,
				GameType = _currentGameType.Value
			};

			var msg = new HubMessage
			{
				MessageType = "RestartGame",
				RoomCode = _roomCode,
				PlayerId = _playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await SendMessageAsync(msg);
		}

		private void HandleRoomCreated(HubMessage msg)
		{
			var payload = JsonSerializer.Deserialize<RoomCreatedPayload>(msg.PayloadJson);
			if (payload == null) return;

			_roomCode = payload.RoomCode;
			_playerId = payload.PlayerId;
			_currentGameType = payload.GameType;
			_playerCount = payload.PlayerCount;   // NEW

			Dispatcher.Invoke(() =>
			{
				JoinCodeBox.Text = payload.RoomCode;
				RoomStatusText.Text =
					$"Room {payload.RoomCode} created ({payload.GameType}). You are {payload.PlayerId}. Share the code with the other player.";
				ActivateGame(payload.GameType);
				UpdateRoomUiState();
				ScrollToCurrentGame();

			});

			Log($"RoomCreated: code={payload.RoomCode}, game={payload.GameType}, you={payload.PlayerId}");
		}

		private void HandleRoomJoined(HubMessage msg)
		{
			var payload = JsonSerializer.Deserialize<RoomJoinedPayload>(msg.PayloadJson);
			if (payload == null) return;

			if (!payload.Success)
			{
				Log("Join failed: " + payload.Message);
				Dispatcher.Invoke(() =>
				{
					RoomStatusText.Text = "Join failed: " + payload.Message;
				});
				return;
			}

			_roomCode = payload.RoomCode;
			_playerId = payload.PlayerId;
			_currentGameType = payload.GameType;
			_playerCount = payload.PlayerCount;   // NEW

			Dispatcher.Invoke(() =>
			{
				RoomStatusText.Text = $"Joined room {payload.RoomCode} ({payload.GameType}) as {payload.PlayerId}.";
				ActivateGame(payload.GameType);
				UpdateRoomUiState();
			});

			Log($"RoomJoined: code={payload.RoomCode}, game={payload.GameType}, you={payload.PlayerId}, players={payload.PlayerCount}");
		}
		private void UpdateRoomUiState()
		{
			Dispatcher.Invoke(() =>
			{
				bool inRoom =
					!string.IsNullOrEmpty(_roomCode) &&
					!string.IsNullOrEmpty(_playerId) &&
					_currentGameType.HasValue;

				var currentInfo = _currentGameType.HasValue
					? GameCatalog.Get(_currentGameType.Value)
					: null;

				bool isOfflineCurrent = currentInfo != null && !currentInfo.IsOnline;

				// ----- Title: "Current Game" or "Current Game: X" -----
				string title = "Current Game";
				if (currentInfo != null)
				{
					title = $"Current Game: {currentInfo.Name}";
				}

				CurrentGameTitleText.Text = title;

				// ----- Player count text -----
				string playersText;
				if (!inRoom)
				{
					playersText = isOfflineCurrent
						? "Offline game"
						: "No game active";
				}
				else
				{
					int count = Math.Max(_playerCount, 1);
					playersText = count == 1
						? "1 player connected"
						: $"{count} players connected";
				}

				CurrentGamePlayersText.Text = playersText;

				// Join ↔ Leave toggle
				JoinRoomButton.Content = inRoom ? "Leave" : "Join";
				JoinRoomButton.Background = inRoom
					? (Brush)FindResource("Brush.DisconnectRed")
					: (Brush)FindResource("Brush.PanelDark");

				// Restart button:
				// - Online games: only when in a room and connected
				// - Offline games: always allowed if current game is offline
				if (RestartGameButton != null)
				{
					RestartGameButton.IsEnabled =
						(inRoom && IsSocketOpen()) || isOfflineCurrent;
				}
			});
		}

		private async Task LeaveRoomAsync()
		{
			if (!IsSocketOpen())
			{
				Log("Not connected.");
				return;
			}

			if (string.IsNullOrEmpty(_roomCode))
			{
				Log("No active room to leave.");
				return;
			}

			var payload = new LeaveRoomPayload
			{
				RoomCode = _roomCode!
			};

			var msg = new HubMessage
			{
				MessageType = "LeaveRoom",
				RoomCode = _roomCode!,
				PlayerId = _playerId ?? "",
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await SendMessageAsync(msg);
		}

		private void ActivateGame(GameType gameType)
		{
			if (_gameClients.TryGetValue(gameType, out var gc))
			{
				GameHost.Content = gc.View;
				gc.OnRoomChanged(_roomCode, _playerId);
			}
			else
			{
				GameHost.Content = null;
			}
		}
		private void FocusGameHost()
		{
			// Ensure the game host can receive focus
			GameHost.Focusable = true;

			if (GameHost.Content is IInputElement element)
			{
				Keyboard.Focus(element);
			}
			else
			{
				Keyboard.Focus(GameHost);
			}
		}

		// ========= Input handling =========

		private void Window_KeyDown(object sender, KeyEventArgs e)
		{
			if (_currentGameType.HasValue &&
				_gameClients.TryGetValue(_currentGameType.Value, out var gc))
			{
				gc.OnKeyDown(e);
				// If we're in a game, prevent WPF/ScrollViewer from also using these keys
				switch (e.Key)
				{
					case Key.Left:
					case Key.Right:
					case Key.Up:
					case Key.Down:
					
					case Key.Tab:
					case Key.LeftShift:
					case Key.RightShift:
					case Key.CapsLock:
						e.Handled = true;
						break;
				}
			}
		}

		private void Window_KeyUp(object sender, KeyEventArgs e)
		{
			if (_currentGameType.HasValue &&
				_gameClients.TryGetValue(_currentGameType.Value, out var gc))
			{
				gc.OnKeyUp(e);
				switch (e.Key)
				{
					case Key.Left:
					case Key.Right:
					case Key.Up:
					case Key.Down:
					
					case Key.Tab:
					case Key.LeftShift:
					case Key.RightShift:
					case Key.CapsLock:
						e.Handled = true;
						break;
				}
			}
		}

		// ========= Helpers =========

		private bool IsSocketOpen() =>
			_socket != null && _socket.State == WebSocketState.Open;

		private async Task SendMessageAsync(HubMessage msg)
		{
			if (!IsSocketOpen() || _socket == null) return;
			try
			{
				var json = JsonSerializer.Serialize(msg);
				var bytes = Encoding.UTF8.GetBytes(json);
				await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
				Log(">> " + msg.MessageType);
			}
			catch (Exception ex)
			{
				Log("Send failed: " + ex.Message);
			}
		}

		private void Log(string text)
		{
			Dispatcher.Invoke(() =>
			{
				LogBox.AppendText(text + Environment.NewLine);
				LogBox.ScrollToEnd();
			});
		}
		private void ScrollToCurrentGame()
		{
			// Jump to top so the header + current game are visible
			CurrentGameGroupBox?.BringIntoView();
		}
		private void OnPropertyChanged(string propertyName)
		=> PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));


		public bool IsConnected
		{
			get => _isConnected;
			private set
			{
				if (_isConnected != value)
				{
					_isConnected = value;
					OnPropertyChanged(nameof(IsConnected));
				}
			}
		}
	}
}

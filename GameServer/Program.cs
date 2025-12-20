// Program.cs
using GameServer;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GameContracts;
using GameLogic;
using System.Diagnostics;
using GameLogic.SpaceShooter;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

// ── JSON options (consistent + slightly faster, avoids extra config per call) ──
var JsonOpts = new JsonSerializerOptions
{
	PropertyNameCaseInsensitive = true
};


// ── Core hub state ───────────────────────────────────────────────────────────
var roomManager = new RoomManager();
var clients = new List<ClientConnection>();
var syncLock = new object();
var rng = new Random();
var debugMessages = false;

// ── Register game handlers ───────────────────────────────────────────────────
var handlers = new Dictionary<GameType, IGameHandler>
{
	[GameType.Pong] = new PongGameHandler(roomManager, clients, syncLock, rng, SendAsync),
	[GameType.WordGuess] = new WordGuessGameHandler(roomManager, clients, syncLock, SendAsync),
	[GameType.TicTacToe] = new TicTacToeGameHandler(roomManager, clients, syncLock, SendAsync),
	[GameType.Anagram] = new AnagramGameHandler(roomManager, clients, syncLock, SendAsync),
	[GameType.Checkers] = new CheckersGameHandler(roomManager, clients, syncLock, SendAsync),
	[GameType.Chess] = new ChessGameHandler(roomManager, clients, syncLock, SendAsync),
	[GameType.JumpsOnline] = new JumpsOnlineGameHandler(roomManager, clients, syncLock, rng, SendAsync),
	[GameType.WarOnline] = new WarGameHandler(roomManager, clients, syncLock, rng, SendAsync),
	[GameType.Blackjack] = new BlackjackGameHandler(roomManager, clients, syncLock, rng, SendAsync),
	[GameType.Uno] = new UnoGameHandler(roomManager, clients, syncLock, rng, SendAsync),
	[GameType.SpaceShooter] = new SpaceShooterGameHandler(roomManager, clients, syncLock, rng, SendAsync),
};

// ── Fast message routing table (keeps current behavior, but avoids scanning) ──
// NOTE: Hub/system messages are still handled by the switch in HandleMessageAsync.
// This dictionary is only for game message types.
var messageRoutes = new Dictionary<string, IGameHandler>(StringComparer.Ordinal)
{
	// Tick-based games
	["PongInput"] = handlers[GameType.Pong],

	// JumpsOnline
	["JumpsOnlineInput"] = handlers[GameType.JumpsOnline],
	["JumpsOnlineStartRequest"] = handlers[GameType.JumpsOnline],
	["JumpsOnlineRestartRequest"] = handlers[GameType.JumpsOnline],

	// SpaceShooter uses constants
	[SpaceShooterMsg.Input] = handlers[GameType.SpaceShooter],

	// Add other game message types here over time (Uno, Blackjack, War, etc.)
};

// ── Tick handlers ────────────────────────────────────────────────────────────
var tickHandlers = handlers.Values.OfType<ITickableGameHandler>().ToList();

// ── Background tick loop (cancellable + concurrent per handler) ───────────────
var stopToken = app.Lifetime.ApplicationStopping;

_ = Task.Run(async () =>
{
	using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16.666)); // ~60 Hz
	var sw = Stopwatch.StartNew();
	long last = sw.ElapsedTicks;

	while (!stopToken.IsCancellationRequested &&
		   await timer.WaitForNextTickAsync(stopToken))
	{
		try
		{
			long now = sw.ElapsedTicks;
			float dt = (now - last) / (float)Stopwatch.Frequency;
			last = now;

			if (dt > 0.25f) dt = 0.25f;

			// Run all tick handlers concurrently so one slow game doesn't stall others
			var tasks = new Task[tickHandlers.Count];
			for (int i = 0; i < tickHandlers.Count; i++)
				tasks[i] = tickHandlers[i].TickAsync(dt);

			await Task.WhenAll(tasks);
		}
		catch (OperationCanceledException)
		{
			break; // shutdown
		}
		catch (Exception ex)
		{
			Console.WriteLine("[TICK LOOP ERROR] " + ex);
			// keep loop alive
		}
	}
}, stopToken);

// ── WebSocket endpoint ───────────────────────────────────────────────────────
app.Map("/ws", async context =>
{
	if (!context.WebSockets.IsWebSocketRequest)
	{
		context.Response.StatusCode = 400;
		return;
	}

	using var socket = await context.WebSockets.AcceptWebSocketAsync();
	var client = new ClientConnection(socket);

	lock (syncLock)
	{
		clients.Add(client);
	}

	Console.WriteLine($"Client connected: {client.ClientId}");

	var buffer = new byte[4 * 1024];
	using var ms = new MemoryStream(); // reused per message

	try
	{
		while (socket.State == WebSocketState.Open)
		{
			ms.SetLength(0);

			WebSocketReceiveResult result;
			do
			{
				result = await socket.ReceiveAsync(
					new ArraySegment<byte>(buffer),
					CancellationToken.None);

				if (result.MessageType == WebSocketMessageType.Close)
				{
					await socket.CloseAsync(
						WebSocketCloseStatus.NormalClosure,
						"Closing",
						CancellationToken.None);
					return;
				}

				ms.Write(buffer, 0, result.Count);

				// Optional safety guard (prevents giant payload OOM / abuse)
				// if (ms.Length > 256_000) throw new Exception("Message too large");
			}
			while (!result.EndOfMessage);

			var jsonBytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);

			if (debugMessages)
				Console.WriteLine($"Received {jsonBytes.Length} bytes");

			try
			{
				// Deserialize directly from UTF-8 bytes (avoids string allocation)
				var hubMsg = JsonSerializer.Deserialize<HubMessage>(jsonBytes, JsonOpts);
				if (hubMsg != null)
				{
					await HandleMessageAsync(hubMsg, client);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("Error handling message: " + ex.Message);
			}
		}
	}
	finally
	{
		Console.WriteLine($"Client disconnected: {client.ClientId}");

		lock (syncLock)
		{
			if (client.RoomCode != null && client.PlayerId != null)
			{
				roomManager.LeaveRoom(client.RoomCode, client.PlayerId, out _);
			}

			clients.Remove(client);
		}

		// Let each handler clean up if it wants
		foreach (var handler in handlers.Values)
		{
			handler.OnClientDisconnected(client);
		}
	}
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");

// ── Helper methods ───────────────────────────────────────────────────────────

async Task HandleMessageAsync(HubMessage msg, ClientConnection client)
{
	switch (msg.MessageType)
	{
		case "CreateRoom":
			await HandleCreateRoom(msg, client);
			return;

		case "JoinRoom":
			await HandleJoinRoom(msg, client);
			return;

		case "RestartGame":
			await HandleRestartGame(msg, client);
			return;

		case "LeaveRoom":
			await HandleLeaveRoom(msg, client);
			return;
	}

	// Fast O(1) routing for known game message types
	if (messageRoutes.TryGetValue(msg.MessageType, out var routedHandler))
	{
		await routedHandler.HandleMessageAsync(msg, client);
		return;
	}

	// Backward-compatible fallback: scan handlers (keeps "anything that worked" working)
	var handler = handlers.Values.FirstOrDefault(h => h.HandlesMessageType(msg.MessageType));
	if (handler != null)
	{
		await handler.HandleMessageAsync(msg, client);
	}
	else
	{
		Console.WriteLine("Unknown MessageType: " + msg.MessageType);
	}
}

async Task HandleCreateRoom(HubMessage msg, ClientConnection client)
{
	CreateRoomPayload? payload;
	try
	{
		payload = JsonSerializer.Deserialize<CreateRoomPayload>(msg.PayloadJson, JsonOpts);
	}
	catch
	{
		return;
	}
	if (payload == null) return;

	// 1) Remember which room this client was in before creating a new one
	string? oldRoomCode;
	lock (syncLock)
	{
		oldRoomCode = client.RoomCode;
	}

	if (!handlers.TryGetValue(payload.GameType, out var handler))
	{
		Console.WriteLine($"No handler registered for game type {payload.GameType}");
		return;
	}

	// 2) Create the new room
	var room = roomManager.CreateRoom(payload.GameType);

	List<ClientConnection> movedClients;

	lock (syncLock)
	{
		// 3) Caller becomes P1 in the new room
		client.RoomCode = room.RoomCode;
		client.PlayerId = "P1";
		roomManager.TryJoinRoom(room.RoomCode, client.PlayerId, out _);

		movedClients = new();

		// 4) If caller was already in a room, move other clients from that room
		if (!string.IsNullOrEmpty(oldRoomCode))
		{
			var othersInOldRoom = clients
				.Where(c => c != client && c.RoomCode == oldRoomCode)
				.ToList();

			foreach (var other in othersInOldRoom)
			{
				var slot = FindOpenPlayerSlot(room);
				if (slot == null)
				{
					// new room is full; skip extra watchers if any
					continue;
				}

				roomManager.TryJoinRoom(room.RoomCode, slot, out _);

				other.RoomCode = room.RoomCode;
				other.PlayerId = slot;

				movedClients.Add(other);
			}
		}
	}

	// 5) Let the game handler know about the new room + joins
	await handler.OnRoomCreated(room, client);

	foreach (var other in movedClients)
	{
		await handler.OnPlayerJoined(room, other);
	}

	// 6) Host gets RoomCreated as before
	var responsePayload = new RoomCreatedPayload
	{
		RoomCode = room.RoomCode,
		GameType = room.GameType,
		PlayerId = client.PlayerId!,
		PlayerCount = room.Players.Count
	};

	var response = new HubMessage
	{
		MessageType = "RoomCreated",
		RoomCode = room.RoomCode,
		PlayerId = client.PlayerId!,
		PayloadJson = JsonSerializer.Serialize(responsePayload)
	};

	await SendAsync(client, response);

	// 7) Other players get RoomJoined so their MainWindow updates & switches UI
	foreach (var other in movedClients)
	{
		var joinPayload = new RoomJoinedPayload
		{
			Success = true,
			Message = "Switched to new room.",
			RoomCode = room.RoomCode,
			GameType = room.GameType,
			PlayerId = other.PlayerId!,
			PlayerCount = room.Players.Count
		};

		var joinMsg = new HubMessage
		{
			MessageType = "RoomJoined",
			RoomCode = room.RoomCode,
			PlayerId = other.PlayerId!,
			PayloadJson = JsonSerializer.Serialize(joinPayload)
		};

		await SendAsync(other, joinMsg);
	}
}

async Task HandleRestartGame(HubMessage msg, ClientConnection client)
{
	if (string.IsNullOrEmpty(client.RoomCode))
	{
		Console.WriteLine("RestartGame ignored: client not in a room.");
		return;
	}

	Room? room;
	lock (syncLock)
	{
		room = roomManager.GetRoom(client.RoomCode);
	}

	if (room == null)
	{
		Console.WriteLine("RestartGame ignored: room not found.");
		return;
	}

	if (!handlers.TryGetValue(room.GameType, out var handler))
	{
		Console.WriteLine($"RestartGame ignored: no handler for game type {room.GameType}.");
		return;
	}

	// Optional: access control – only room players can restart
	lock (syncLock)
	{
		if (!room.Players.Contains(client.PlayerId ?? ""))
		{
			Console.WriteLine("RestartGame ignored: client is not a player in this room.");
			return;
		}
	}

	await handler.RestartRoomAsync(room, client);
}

async Task HandleJoinRoom(HubMessage msg, ClientConnection client)
{
	JoinRoomPayload? payload;
	try
	{
		payload = JsonSerializer.Deserialize<JoinRoomPayload>(msg.PayloadJson, JsonOpts);
	}
	catch
	{
		return;
	}
	if (payload == null) return;

	RoomJoinedPayload responsePayload;

	Room? room;
	lock (syncLock)
	{
		room = roomManager.GetRoom(payload.RoomCode);
	}

	if (room == null)
	{
		responsePayload = new RoomJoinedPayload
		{
			Success = false,
			Message = "Room not found."
		};
	}
	else if (!handlers.TryGetValue(room.GameType, out var handler))
	{
		responsePayload = new RoomJoinedPayload
		{
			Success = false,
			Message = $"No handler for game type {room.GameType}."
		};
	}
	else
	{
		// Decide slot: P1, P2 (and P3 for JumpsOnline)
		int playerCount;
		string slot;

		lock (syncLock)
		{
			slot = FindOpenPlayerSlot(room) ?? "";

			if (string.IsNullOrEmpty(slot))
			{
				responsePayload = new RoomJoinedPayload
				{
					Success = false,
					Message = "Room is full."
				};
				goto send;
			}

			roomManager.TryJoinRoom(room.RoomCode, slot, out _);
			client.RoomCode = room.RoomCode;
			client.PlayerId = slot;
			playerCount = room.Players.Count;
		}

		await handler.OnPlayerJoined(room, client);

		// Payload for the client that just joined
		responsePayload = new RoomJoinedPayload
		{
			Success = true,
			Message = "Joined room.",
			RoomCode = room.RoomCode,
			GameType = room.GameType,
			PlayerId = client.PlayerId!,
			PlayerCount = playerCount
		};

		// Notify other players
		if (responsePayload.Success && room != null)
		{
			List<ClientConnection> others;
			lock (syncLock)
			{
				others = clients
					.Where(c => c != client && c.RoomCode == room.RoomCode)
					.ToList();
			}

			foreach (var other in others)
			{
				var otherPayload = new RoomJoinedPayload
				{
					Success = true,
					Message = "Another player joined.",
					RoomCode = room.RoomCode,
					GameType = room.GameType,
					PlayerId = other.PlayerId!,
					PlayerCount = playerCount
				};

				var otherMsg = new HubMessage
				{
					MessageType = "RoomJoined",
					RoomCode = room.RoomCode,
					PlayerId = other.PlayerId!,
					PayloadJson = JsonSerializer.Serialize(otherPayload)
				};

				await SendAsync(other, otherMsg);
			}
		}
	}

send:
	var response = new HubMessage
	{
		MessageType = "RoomJoined",
		RoomCode = payload.RoomCode,
		PlayerId = client.PlayerId ?? "",
		PayloadJson = JsonSerializer.Serialize(responsePayload)
	};

	await SendAsync(client, response);
}

async Task HandleLeaveRoom(HubMessage msg, ClientConnection client)
{
	LeaveRoomPayload? payload;
	try
	{
		payload = JsonSerializer.Deserialize<LeaveRoomPayload>(msg.PayloadJson, JsonOpts);
	}
	catch
	{
		return;
	}
	if (payload == null) return;

	RoomLeftPayload responsePayload;
	Room? room = null;
	string? roomCode;
	string? playerId;
	int playerCount = 0;

	lock (syncLock)
	{
		roomCode = client.RoomCode;
		playerId = client.PlayerId;

		if (string.IsNullOrEmpty(roomCode) || string.IsNullOrEmpty(playerId))
		{
			responsePayload = new RoomLeftPayload
			{
				Success = false,
				Message = "You are not in a room.",
				LeavingPlayerId = client.PlayerId ?? "",
				PlayerCount = 0
			};
			goto send;
		}

		if (!roomManager.LeaveRoom(roomCode, playerId, out room))
		{
			responsePayload = new RoomLeftPayload
			{
				Success = false,
				Message = "Room not found.",
				LeavingPlayerId = playerId,
				PlayerCount = 0
			};
			goto send;
		}

		playerCount = room?.Players.Count ?? 0;

		client.RoomCode = null;
		client.PlayerId = null;
	}

	if (room != null && handlers.TryGetValue(room.GameType, out var handler))
	{
		handler.OnClientDisconnected(client);
	}

	// Response for the leaving client
	responsePayload = new RoomLeftPayload
	{
		Success = true,
		Message = "Left room.",
		LeavingPlayerId = playerId ?? "",
		PlayerCount = playerCount
	};

	// Notify remaining players in the room
	if (playerCount > 0 && roomCode != null)
	{
		List<ClientConnection> others;
		lock (syncLock)
		{
			others = clients
				.Where(c => c.RoomCode == roomCode)
				.ToList();
		}

		foreach (var other in others)
		{
			var otherPayload = new RoomLeftPayload
			{
				Success = true,
				Message = "Other player left.",
				LeavingPlayerId = playerId ?? "",
				PlayerCount = playerCount
			};

			var otherMsg = new HubMessage
			{
				MessageType = "RoomLeft",
				RoomCode = roomCode,
				PlayerId = other.PlayerId ?? "",
				PayloadJson = JsonSerializer.Serialize(otherPayload)
			};

			await SendAsync(other, otherMsg);
		}
	}

send:
	var response = new HubMessage
	{
		MessageType = "RoomLeft",
		RoomCode = roomCode ?? payload.RoomCode,
		PlayerId = client.ClientId, // not important for clients
		PayloadJson = JsonSerializer.Serialize(responsePayload)
	};

	await SendAsync(client, response);
}

async Task SendAsync(ClientConnection client, HubMessage msg)
{
	try
	{
		if (client.Socket.State != WebSocketState.Open) return;

		// Keep your current behavior (string JSON), but use consistent options.
		var json = JsonSerializer.Serialize(msg);
		var bytes = Encoding.UTF8.GetBytes(json);

		await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
	}
	catch (WebSocketException)
	{
		// client likely disconnected mid-send
	}
	catch (ObjectDisposedException)
	{
		// socket already disposed
	}
}

static string? FindOpenPlayerSlot(Room room)
{
	for (int i = 1; i <= room.MaxPlayers; i++)
	{
		var slot = $"P{i}";
		if (!room.Players.Contains(slot))
			return slot;
	}
	return null;
}

// ── Shared hub-side client type ──────────────────────────────────────────────
public class ClientConnection
{
	public WebSocket Socket { get; }
	public string ClientId { get; } = Guid.NewGuid().ToString("N");
	public string? RoomCode { get; set; }
	public string? PlayerId { get; set; }

	public ClientConnection(WebSocket socket)
	{
		Socket = socket;
	}
}

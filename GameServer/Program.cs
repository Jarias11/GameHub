using GameServer;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GameContracts;
using GameLogic;
using System.Diagnostics;



var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

// ── Core hub state ───────────────────────────────────────────────────────────

var roomManager = new RoomManager();
var clients = new List<ClientConnection>();
var syncLock = new object();
var rng = new Random();


// Register game handlers
var handlers = new Dictionary<GameType, IGameHandler>
{
	[GameType.Pong] = new PongGameHandler(roomManager, clients, syncLock, rng, SendAsync),
	[GameType.WordGuess] = new WordGuessGameHandler(roomManager, clients, syncLock, SendAsync),
	[GameType.TicTacToe] = new TicTacToeGameHandler(roomManager, clients, syncLock, SendAsync),
	[GameType.Anagram] = new AnagramGameHandler(roomManager, clients, syncLock, SendAsync),
	[GameType.Checkers]  = new CheckersGameHandler(roomManager, clients, syncLock, SendAsync),
};

var tickHandlers = handlers.Values.OfType<ITickableGameHandler>().ToList();

// ── Background tick loop (for games like Pong) ──────────────────────────────

_ = Task.Run(async () =>
{
	var stopwatch = Stopwatch.StartNew();
	while (true)
	{
		var dtSeconds = (float)stopwatch.Elapsed.TotalSeconds;
		stopwatch.Restart();

		foreach (var handler in tickHandlers)
		{
			await handler.TickAsync(dtSeconds);   // <-- pass dt
		}

		await Task.Delay(16); // ~60 FPS
	}
});

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

	try
	{
		while (socket.State == WebSocketState.Open)
		{
			var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
			if (result.MessageType == WebSocketMessageType.Close)
				break;

			var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
			Console.WriteLine("Received: " + json);

			try
			{
				var hubMsg = JsonSerializer.Deserialize<HubMessage>(json);
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
			break;

		case "JoinRoom":
			await HandleJoinRoom(msg, client);
			break;
		case "RestartGame":
			await HandleRestartGame(msg, client);
			break;
		case "LeaveRoom":
			await HandleLeaveRoom(msg, client);
			break;

		default:
			// Route to whichever game handler claims this MessageType
			var handler = handlers.Values.FirstOrDefault(h => h.HandlesMessageType(msg.MessageType));
			if (handler != null)
			{
				await handler.HandleMessageAsync(msg, client);
			}
			else
			{
				Console.WriteLine("Unknown MessageType: " + msg.MessageType);
			}
			break;
	}
}

async Task HandleCreateRoom(HubMessage msg, ClientConnection client)
{
	CreateRoomPayload? payload;
	try
	{
		payload = JsonSerializer.Deserialize<CreateRoomPayload>(msg.PayloadJson);
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
				// Find an open slot in the new room
				string slot;
				if (!room.Players.Contains("P1"))
					slot = "P1";
				else if (!room.Players.Contains("P2"))
					slot = "P2";
				else
					continue; // new room is full; skip extra watchers if any

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
		payload = JsonSerializer.Deserialize<JoinRoomPayload>(msg.PayloadJson);
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
		// Decide slot: P1 or P2
		int playerCount;
		string slot;
		lock (syncLock)
		{
			if (!room.Players.Contains("P1"))
				slot = "P1";
			else if (!room.Players.Contains("P2"))
				slot = "P2";
			else
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

		// 🔹 HERE is the "notify other players" block 🔹
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
		payload = JsonSerializer.Deserialize<LeaveRoomPayload>(msg.PayloadJson);
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
	if (client.Socket.State != WebSocketState.Open) return;
	var json = JsonSerializer.Serialize(msg);
	var bytes = Encoding.UTF8.GetBytes(json);
	await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
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

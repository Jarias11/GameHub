namespace GameServer
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text.Json;
	using System.Threading.Tasks;
	using GameContracts;
	using GameLogic;
	using GameLogic.TicTacToe;

	public sealed class TicTacToeGameHandler : TurnBasedGameHandler<TicTacToeRoomState>
	{
		public TicTacToeGameHandler(
			RoomManager roomManager,
			List<ClientConnection> clients,
			object syncLock,
			Func<ClientConnection, HubMessage, Task> sendAsync)
			: base(roomManager, clients, syncLock, sendAsync)
		{
		}

		public override GameType GameType => GameType.TicTacToe;

		public override bool HandlesMessageType(string messageType) =>
			messageType == "TicTacToeMove";

		protected override TicTacToeRoomState CreateRoomState(string roomCode)
		{
			Console.WriteLine($"[TicTacToe] Room {roomCode} created.");
			return new TicTacToeRoomState(roomCode);
		}

		// ── Room lifecycle ---------------------------------------------------

		public override async Task OnRoomCreated(Room room, ClientConnection creator)
		{
			// Let the base handler create the TicTacToeRoomState entry, etc.
			await base.OnRoomCreated(room, creator);

			TicTacToeStatePayload statePayload;
			List<ClientConnection> roomClients;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(room.RoomCode, out var state))
					return;

				// First player is X by default (you can randomize later if you want)
				state.PlayerXId ??= creator.PlayerId;
				state.CurrentPlayerId = creator.PlayerId!; // P1 starts until P2 joins

				statePayload = TicTacToeLogic.ToPayload(state, "Waiting for opponent...");
				roomClients = _clients.Where(c => c.RoomCode == room.RoomCode).ToList();
			}

			// Send initial board to the creator
			var hubMsg = new HubMessage
			{
				MessageType = "TicTacToeState",
				RoomCode = room.RoomCode,
				PlayerId = creator.PlayerId!,
				PayloadJson = JsonSerializer.Serialize(statePayload)
			};

			foreach (var c in roomClients)
			{
				await _sendAsync(c, hubMsg);
			}
		}
		public override async Task RestartRoomAsync(Room room, ClientConnection? initiator)
		{
			TicTacToeRoomState state;
			List<ClientConnection> roomClients;
			TicTacToeStatePayload payload;

			lock (_syncLock)
			{
				// Get old state (to keep players) or create if missing
				if (!_rooms.TryGetValue(room.RoomCode, out state!))
				{
					state = CreateRoomState(room.RoomCode);
					_rooms[room.RoomCode] = state;
				}

				// Preserve current players
				var xId = state.PlayerXId;
				var oId = state.PlayerOId;

				// Create a fresh state instance (clears board / flags)
				state = new TicTacToeRoomState(room.RoomCode)
				{
					PlayerXId = xId,
					PlayerOId = oId
				};

				_rooms[room.RoomCode] = state;

				// If both players are present, pick who starts
				string message = "Game restarted. Waiting for opponent...";
				if (state.PlayerXId != null && state.PlayerOId != null)
				{
					TicTacToeLogic.InitializeStartingPlayer(state);

					message = $"Game restarted. {state.CurrentPlayerId} starts.";
				}

				payload = TicTacToeLogic.ToPayload(state, message);
				roomClients = GetRoomClients(room.RoomCode);
			}

			var hubMsg = new HubMessage
			{
				MessageType = "TicTacToeState",
				RoomCode = room.RoomCode,
				PlayerId = initiator?.PlayerId ?? "",
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			foreach (var c in roomClients)
			{
				await _sendAsync(c, hubMsg);
			}

			Console.WriteLine($"[TicTacToe] Room {room.RoomCode} restarted"
				+ (initiator != null ? $" by {initiator.PlayerId}" : ""));
		}

		public override async Task OnPlayerJoined(Room room, ClientConnection client)
		{
			// Base logic first
			await base.OnPlayerJoined(room, client);

			TicTacToeStatePayload statePayload;
			List<ClientConnection> roomClients;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(room.RoomCode, out var state))
					return;

				// Assign this player into X/O slot if needed
				if (state.PlayerXId == null)
					state.PlayerXId = client.PlayerId;
				else if (state.PlayerOId == null)
					state.PlayerOId = client.PlayerId;

				// When both players are known, randomize who starts
				string message = "Waiting for opponent...";
				if (state.PlayerXId != null && state.PlayerOId != null && !state.IsGameOver)
				{
					TicTacToeLogic.InitializeStartingPlayer(state);

					string starterLabel =
						state.CurrentPlayerId == state.PlayerXId ? "P1 (X)" : "P2 (O)";
					message = $"Game ready. {starterLabel} starts.";
				}

				statePayload = TicTacToeLogic.ToPayload(state, message);
				roomClients = _clients.Where(c => c.RoomCode == room.RoomCode).ToList();
			}

			// Broadcast state to *both* players in the room
			var hubMsg = new HubMessage
			{
				MessageType = "TicTacToeState",
				RoomCode = room.RoomCode,
				PlayerId = client.PlayerId ?? "",
				PayloadJson = JsonSerializer.Serialize(statePayload)
			};

			foreach (var c in roomClients)
			{
				await _sendAsync(c, hubMsg);
			}
		}

		// ── Message routing --------------------------------------------------

		public override async Task HandleMessageAsync(HubMessage msg, ClientConnection client)
		{
			if (msg.MessageType == "TicTacToeMove")
			{
				await HandleMoveAsync(msg, client);
			}
		}

		// ── TicTacToe-specific move handling --------------------------------

		private async Task HandleMoveAsync(HubMessage msg, ClientConnection client)
		{
			// 1) Deserialize payload
			TicTacToeMovePayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<TicTacToeMovePayload>(msg.PayloadJson);
			}
			catch
			{
				return;
			}

			if (payload == null)
				return;

			if (client.RoomCode == null || client.PlayerId == null)
				return;

			TicTacToeRoomState state;
			List<ClientConnection> roomClients;
			TicTacToeStatePayload statePayload;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(client.RoomCode, out state!))
					return;

				// 2) Validate move
				if (!TicTacToeLogic.IsValidMove(state, payload.Row, payload.Col, client.PlayerId))
				{
					// Invalid move: notify only this client with current state
					statePayload = TicTacToeLogic.ToPayload(state, "Invalid move.");
					roomClients = new List<ClientConnection> { client };
				}
				else
				{
					// 3) Apply move
					TicTacToeLogic.ApplyMove(state, payload.Row, payload.Col, client.PlayerId);

					// Decide message based on game state
					string message;
					if (state.IsGameOver)
					{
						if (state.IsDraw)
							message = "Game over: draw.";
						else
							message = $"Game over: {state.WinnerPlayerId} wins.";
					}
					else
					{
						message = "Move applied.";
					}

					// 4) Build state payload
					statePayload = TicTacToeLogic.ToPayload(state, message);

					// Notify all clients in this room
					roomClients = _clients
						.Where(c => c.RoomCode == client.RoomCode)
						.ToList();
				}
			}

			// 5) Broadcast state to all selected clients
			var hubMsg = new HubMessage
			{
				MessageType = "TicTacToeState",
				RoomCode = client.RoomCode,
				PlayerId = client.PlayerId, // sender; receivers can ignore/use if they want
				PayloadJson = JsonSerializer.Serialize(statePayload)
			};

			foreach (var c in roomClients)
			{
				await _sendAsync(c, hubMsg);
			}
		}
	}
}

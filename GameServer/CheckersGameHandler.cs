// GameServer/CheckersGameHandler.cs
namespace GameServer
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text.Json;
	using System.Threading.Tasks;
	using GameContracts;
	using GameLogic;
	using GameLogic.Checkers;

	/// <summary>
	/// Turn-based handler for the Checkers game.
	/// - Random Red/Black assignment when both players join.
	/// - Starts with a standard Checkers setup.
	/// - Uses CheckersEngine for all rule enforcement.
	/// - Supports resign messages.
	/// </summary>
	public sealed class CheckersGameHandler : TurnBasedGameHandler<CheckersRoomState>
	{
		private readonly Random _rng = new();

		public CheckersGameHandler(
			RoomManager roomManager,
			List<ClientConnection> clients,
			object syncLock,
			Func<ClientConnection, HubMessage, Task> sendAsync)
			: base(roomManager, clients, syncLock, sendAsync)
		{
		}

		public override GameType GameType => GameType.Checkers;

		public override bool HandlesMessageType(string messageType) =>
			messageType == "CheckersMove" ||
			messageType == "CheckersResign";

		/// <summary>
		/// Create initial room state. Board will be initialized once both players
		/// are present and colors assigned.
		/// </summary>
		protected override CheckersRoomState CreateRoomState(string roomCode)
		{
			var room = _roomManager.GetRoom(roomCode);
			if (room != null)
			{
				return CheckersEngine.CreateInitialState(roomCode, room, _rng);
			}

			// Fallback: empty state with just the roomCode.
			return new CheckersRoomState(roomCode);
		}

		// ─────────────────────────────────────────────────────────
		// Room lifecycle
		// ─────────────────────────────────────────────────────────

		public override async Task OnRoomCreated(Room room, ClientConnection owner)
		{
			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);
				CheckersEngine.SyncPlayersFromRoom(state, room, _rng);
			}

			// Send snapshot (usually just P1, waiting for P2).
			await BroadcastState(room.RoomCode);
		}

		public override async Task OnPlayerJoined(Room room, ClientConnection client)
		{
			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);
				CheckersEngine.SyncPlayersFromRoom(state, room, _rng);
			}

			// If this was P2, both players now see a fully-initialized board.
			await BroadcastState(room.RoomCode);
		}

		// ─────────────────────────────────────────────────────────
		// Game messages
		// ─────────────────────────────────────────────────────────

		public override async Task HandleMessageAsync(HubMessage msg, ClientConnection client)
		{
			if (string.IsNullOrEmpty(client.RoomCode))
			{
				Console.WriteLine("[Checkers] Ignoring message: client not in a room.");
				return;
			}

			if (!_rooms.TryGetValue(client.RoomCode, out var state))
			{
				Console.WriteLine($"[Checkers] No state found for room {client.RoomCode}.");
				return;
			}

			switch (msg.MessageType)
			{
				case "CheckersMove":
					await HandleMoveMessage(msg, client, state);
					break;

				case "CheckersResign":
					await HandleResignMessage(client, state);
					break;
			}
		}

		private async Task HandleMoveMessage(
			HubMessage msg,
			ClientConnection client,
			CheckersRoomState state)
		{
			CheckersMovePayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<CheckersMovePayload>(msg.PayloadJson);
			}
			catch (Exception ex)
			{
				Console.WriteLine("[Checkers] Failed to deserialize move payload: " + ex);
				return;
			}

			if (payload == null) return;

			string playerId = client.PlayerId ?? string.Empty;
			if (string.IsNullOrWhiteSpace(playerId))
			{
				Console.WriteLine("[Checkers] Move ignored: missing PlayerId on client.");
				return;
			}

			bool moveAccepted = false;
			string? error = null;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(client.RoomCode!, out state!))
				{
					Console.WriteLine($"[Checkers] No state found for room {client.RoomCode}.");
					return;
				}

				moveAccepted = CheckersEngine.TryApplyMove(state, playerId, payload, out error);
			}

			if (!moveAccepted && !string.IsNullOrWhiteSpace(error))
			{
				Console.WriteLine($"[Checkers] Move rejected for {playerId}: {error}");
				// For now, we just log. Later you could send an error message back.
			}

			// Broadcast latest state (either with updated board or unchanged).
			await BroadcastState(client.RoomCode!);
		}

		private async Task HandleResignMessage(
			ClientConnection client,
			CheckersRoomState state)
		{
			string playerId = client.PlayerId ?? string.Empty;
			if (string.IsNullOrWhiteSpace(playerId))
			{
				Console.WriteLine("[Checkers] Resign ignored: missing PlayerId on client.");
				return;
			}

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(client.RoomCode!, out state!))
				{
					Console.WriteLine($"[Checkers] No state found for room {client.RoomCode}.");
					return;
				}

				if (state.IsGameOver)
				{
					Console.WriteLine("[Checkers] Resign ignored: game already over.");
					return;
				}

				// Determine winner as "the other player".
				string? winner = null;
				if (!string.IsNullOrWhiteSpace(state.RedPlayerId) &&
					!string.IsNullOrWhiteSpace(state.BlackPlayerId))
				{
					if (playerId == state.RedPlayerId)
						winner = state.BlackPlayerId;
					else if (playerId == state.BlackPlayerId)
						winner = state.RedPlayerId;
				}

				state.IsGameOver = true;
				state.WinnerPlayerId = winner;
				state.CurrentTurnPlayerId = null;
				state.ForcedFromRow = null;
				state.ForcedFromCol = null;
				state.StatusMessage = $"{playerId} resigns.";
			}

			await BroadcastState(client.RoomCode!);
		}

		public override async Task RestartRoomAsync(Room room, ClientConnection? initiator)
		{
			lock (_syncLock)
			{
				var state = new CheckersRoomState(room.RoomCode);
				_rooms[room.RoomCode] = state;

				// Re-sync players & re-randomize colors + starting player.
				CheckersEngine.SyncPlayersFromRoom(state, room, _rng);
			}

			await BroadcastState(room.RoomCode);
		}

		// ─────────────────────────────────────────────────────────
		// Helpers
		// ─────────────────────────────────────────────────────────

		private async Task BroadcastState(string roomCode)
		{
			CheckersRoomState state;
			List<ClientConnection> roomClients;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(roomCode, out state!))
				{
					Console.WriteLine($"[Checkers] Broadcast skipped: no state for room {roomCode}.");
					return;
				}

				roomClients = GetRoomClients(roomCode);
			}

			if (roomClients.Count == 0)
				return;

			var payload = CheckersEngine.ToPayload(state);

			var msg = new HubMessage
			{
				MessageType = "CheckersState",
				RoomCode = roomCode,
				PlayerId = string.Empty, // not important; clients use payload
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			foreach (var c in roomClients)
			{
				await _sendAsync(c, msg);
			}
		}
	}
}

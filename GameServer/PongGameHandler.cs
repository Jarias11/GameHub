namespace GameServer
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text.Json;
	using System.Threading.Tasks;
	using GameContracts;
	using GameLogic;
	using GameLogic.Pong;

	/// <summary>
	/// Golden example of a tick-based game handler using the TickableGameHandler template.
	/// </summary>
	public sealed class PongGameHandler : TickableGameHandler<PongRoomState>
	{
		public PongGameHandler(
			RoomManager roomManager,
			List<ClientConnection> clients,
			object syncLock,
			Random rng,
			Func<ClientConnection, HubMessage, Task> sendAsync)
			: base(roomManager, clients, syncLock, rng, sendAsync)
		{
		}

		public override GameType GameType => GameType.Pong;

		public override bool HandlesMessageType(string messageType) =>
			messageType == "PongInput";

		/// <summary>Create initial Pong state for a new room.</summary>
		protected override PongRoomState CreateRoomState(string roomCode)
		{
			var state = new PongRoomState(roomCode);

			// Start with ball idle in the center; we will launch it when 2 players are present.
			state.BallX = 50;
			state.BallY = 50;
			state.VelX = 0;
			state.VelY = 0;

			// Make sure difficulty is at base
			state.HitCount = 0;
			state.BallSpeedMultiplier = 1f;
			state.PaddleSpeedMultiplier = 1f;

			Console.WriteLine($"[Pong] Room {roomCode} created.");
			return state;
		}

		/// <summary>Advance Pong simulation by one tick.</summary>
		protected override void UpdateState(PongRoomState state, float dtSeconds)
		{
			// Look up the room to see how many players are in it
			var room = _roomManager.GetRoom(state.RoomCode);
			bool hasTwoPlayers = room != null && room.Players.Count >= 2;

			if (!hasTwoPlayers)
			{
				// Freeze ball in the center while waiting for player 2
				state.BallX = 50;
				state.BallY = 50;
				state.VelX = 0;
				state.VelY = 0;

				// Reset difficulty so the next rally starts fresh
				state.HitCount = 0;
				state.BallSpeedMultiplier = 1f;
				state.PaddleSpeedMultiplier = 1f;

				// We still let paddles move (so P1 can wiggle), but no ball physics.
				return;
			}

			// If we *just* reached 2 players and the ball is idle, launch it
			if (state.VelX == 0 && state.VelY == 0)
			{
				state.HitCount = 0;
				state.BallSpeedMultiplier = 1f;
				state.PaddleSpeedMultiplier = 1f;
				state.ResetBall(_rng);
			}

			// Normal physics when there are 2 players and the ball is live
			PongEngine.Update(state, _rng, dtSeconds);
		}

		/// <summary>Build the PongState message to broadcast to clients.</summary>
		protected override HubMessage CreateStateMessage(PongRoomState state)
		{
			var statePayload = new PongStatePayload
			{
				BallX = state.BallX,
				BallY = state.BallY,
				Paddle1Y = state.Paddle1Y,
				Paddle2Y = state.Paddle2Y,
				Score1 = state.Score1,
				Score2 = state.Score2
			};

			return new HubMessage
			{
				MessageType = "PongState",
				RoomCode = state.RoomCode,
				PlayerId = "",
				PayloadJson = JsonSerializer.Serialize(statePayload)
			};
		}
		public override async Task RestartRoomAsync(Room room, ClientConnection? initiator)
		{
			PongRoomState state;
			List<ClientConnection> roomClients;

			lock (_syncLock)
			{
				// Ensure state exists (or recreate if needed)
				state = EnsureRoomState(room.RoomCode);

				// Reset scores & directions
				state.Score1 = 0;
				state.Score2 = 0;
				state.Direction1 = 0;
				state.Direction2 = 0;

				// Reset difficulty scaling
				state.HitCount = 0;
				state.BallSpeedMultiplier = 1f;
				state.PaddleSpeedMultiplier = 1f;

				// Reset ball to center / initial state
				state.ResetBall(_rng);

				// Grab current room clients
				roomClients = GetRoomClients(room.RoomCode);
			}

			// Build a state snapshot and broadcast it
			var hubMsg = CreateStateMessage(state);

			foreach (var c in roomClients)
			{
				await _sendAsync(c, hubMsg);
			}

			Console.WriteLine($"[Pong] Room {room.RoomCode} restarted"
				+ (initiator != null ? $" by {initiator.PlayerId}" : ""));
		}

		/// <summary>Handle paddle input from clients.</summary>
		public override Task HandleMessageAsync(HubMessage msg, ClientConnection client)
		{
			if (msg.MessageType != "PongInput")
				return Task.CompletedTask;

			PongInputPayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<PongInputPayload>(msg.PayloadJson);
			}
			catch
			{
				return Task.CompletedTask;
			}

			if (payload == null)
				return Task.CompletedTask;

			lock (_syncLock)
			{
				if (string.IsNullOrEmpty(client.RoomCode) || string.IsNullOrEmpty(client.PlayerId))
					return Task.CompletedTask;

				if (!_rooms.TryGetValue(client.RoomCode, out var state))
					return Task.CompletedTask;

				var dir = Math.Clamp(payload.Direction, -1, 1);

				if (client.PlayerId == "P1")
					state.Direction1 = dir;
				else if (client.PlayerId == "P2")
					state.Direction2 = dir;
			}

			return Task.CompletedTask;
		}
	}
}

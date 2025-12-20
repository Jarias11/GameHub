using System.Text.Json;
using System.Threading.Tasks;
using GameContracts;
using GameLogic;
using GameLogic.SpaceShooter;

namespace GameServer;

public sealed class SpaceShooterGameHandler : TickableGameHandler<SpaceShooterRoomState>
{
	public SpaceShooterGameHandler(
		RoomManager roomManager,
		System.Collections.Generic.List<ClientConnection> clients,
		object syncLock,
		System.Random rng,
		System.Func<ClientConnection, HubMessage, Task> sendAsync)
		: base(roomManager, clients, syncLock, rng, sendAsync)
	{
	}

	public override GameType GameType => GameType.SpaceShooter;

	// Space shooter likes smoothness; 30 is fine, 40 also feels nice.
	protected override float BroadcastRateHz => 60f;

	public override bool HandlesMessageType(string messageType)
		=> messageType == SpaceShooterMsg.Input;

	protected override SpaceShooterRoomState CreateRoomState(string roomCode)
	{
		// stable per-room seed
		int seed;
		lock (_syncLock) seed = _rng.Next();

		var state = new SpaceShooterRoomState(roomCode, seed)
		{
			WorldRadius = 2000f,
			CameraViewRadius = 350f,
			CameraDeadzone = 0f
		};

		// if room exists already, seed ships based on current players
		var room = _roomManager.GetRoom(roomCode);
		if (room != null)
		{
			SpaceShooterEngine.Reset(state, room.Players);
		}

		return state;
	}

	public override Task OnRoomCreated(Room room, ClientConnection owner)
	{
		lock (_syncLock)
		{
			var state = EnsureRoomState(room.RoomCode);
			SpaceShooterEngine.Reset(state, room.Players);
		}
		return Task.CompletedTask;
	}

	public override Task OnPlayerJoined(Room room, ClientConnection client)
	{
		lock (_syncLock)
		{
			var state = EnsureRoomState(room.RoomCode);

			// Rebuild from scratch so spawns become evenly spaced with N players
			// (Simple + deterministic; later we can do “join mid-match” rules.)
			SpaceShooterEngine.Reset(state, room.Players);
		}
		return Task.CompletedTask;
	}

	public override async Task RestartRoomAsync(Room room, ClientConnection? initiator)
	{
		SpaceShooterRoomState? state;
		lock (_syncLock)
		{
			state = EnsureRoomState(room.RoomCode);
			SpaceShooterEngine.Reset(state, room.Players);
		}

		// optional: immediately push a snapshot
		if (state != null)
		{
			var msg = CreateStateMessage(state);
			var roomClients = GetRoomClients(room.RoomCode);
			var tasks = new Task[roomClients.Count];
			for (int i = 0; i < roomClients.Count; i++)
				tasks[i] = _sendAsync(roomClients[i], msg);
			await Task.WhenAll(tasks);
		}
	}

	protected override void UpdateState(SpaceShooterRoomState state, float dtSeconds)
	{
		SpaceShooterEngine.Tick(state, dtSeconds);
	}

	protected override HubMessage CreateStateMessage(SpaceShooterRoomState state)
	{
		var payload = SpaceShooterEngine.BuildStatePayload(state);

		return new HubMessage
		{
			MessageType = SpaceShooterMsg.State,
			RoomCode = state.RoomCode,
			PlayerId = "",
			PayloadJson = JsonSerializer.Serialize(payload)
		};
	}

	public override Task HandleMessageAsync(HubMessage msg, ClientConnection client)
	{
		if (client.RoomCode == null || client.PlayerId == null)
			return Task.CompletedTask;

		if (msg.MessageType != SpaceShooterMsg.Input)
			return Task.CompletedTask;

		SpaceShooterInputPayload? input;
		try
		{
			input = JsonSerializer.Deserialize<SpaceShooterInputPayload>(msg.PayloadJson);
		}
		catch
		{
			return Task.CompletedTask;
		}
		if (input == null) return Task.CompletedTask;

		lock (_syncLock)
		{
			if (_rooms.TryGetValue(client.RoomCode, out var state))
			{
				SpaceShooterEngine.SetInput(state, client.PlayerId, input);
			}
		}

		return Task.CompletedTask;
	}
}

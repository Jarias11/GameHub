namespace GameServer
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Threading.Tasks;
	using GameContracts;
	using GameLogic;

	/// <summary>
	/// Base class for real-time / tick-based games (Pong, fighting games, etc.).
	/// Handles:
	/// - tracking room states
	/// - periodic TickAsync broadcast
	/// - basic room cleanup when empty
	/// Subclasses just create state, update it, and build the outgoing HubMessage.
	/// </summary>
	public abstract class TickableGameHandler<TState> : ITickableGameHandler
		where TState : IRoomState
	{
		protected readonly RoomManager _roomManager;
		protected readonly List<ClientConnection> _clients;
		protected readonly object _syncLock;
		protected readonly Random _rng;
		protected readonly Func<ClientConnection, HubMessage, Task> _sendAsync;
		private float _broadcastAccumulator = 0f;
		private float _simAccumulator = 0f;
		private const float SimStep = 1f / 120f;    // 120 Hz logic step
		private const int MaxStepsPerTick = 4;      // avoid spiral-of-death
		/// <summary>
		/// How often we broadcast snapshots to clients (Hz).
		/// Override in specific handlers if needed.
		/// </summary>
		protected virtual float BroadcastRateHz => 30f; // default 30 fps

		// Per-room state for this game type (key = RoomCode)
		protected readonly Dictionary<string, TState> _rooms = new();

		protected TickableGameHandler(
			RoomManager roomManager,
			List<ClientConnection> clients,
			object syncLock,
			Random rng,
			Func<ClientConnection, HubMessage, Task> sendAsync)
		{
			_roomManager = roomManager;
			_clients = clients;
			_syncLock = syncLock;
			_rng = rng;
			_sendAsync = sendAsync;
		}

		public abstract GameType GameType { get; }

		/// <summary>Which MessageTypes (e.g. "PongInput") this handler cares about.</summary>
		public abstract bool HandlesMessageType(string messageType);

		public virtual Task OnRoomCreated(Room room, ClientConnection owner)
		{
			lock (_syncLock)
			{
				EnsureRoomState(room.RoomCode);
			}
			return Task.CompletedTask;
		}

		public virtual Task OnPlayerJoined(Room room, ClientConnection client)
		{
			lock (_syncLock)
			{
				EnsureRoomState(room.RoomCode);
			}
			return Task.CompletedTask;
		}

		/// <summary>Create the initial state for a given room.</summary>
		protected abstract TState CreateRoomState(string roomCode);

		/// <summary>Run one simulation step on this room's state.</summary>
		protected abstract void UpdateState(TState state, float dtSeconds);

		/// <summary>Build the outbound HubMessage (e.g. PongState) for this room.</summary>
		protected abstract HubMessage CreateStateMessage(TState state);

		/// <summary>
		/// Main tick loop entry point. Called ~30 times per second by the hub.
		/// </summary>
		public async Task TickAsync(float dtSeconds)
		{
			if (dtSeconds > 0.1f)
				dtSeconds = 0.1f; // a bit looser cap now that we sub-step

			_simAccumulator += dtSeconds;

			var steps = 0;
			while (_simAccumulator >= SimStep && steps < MaxStepsPerTick)
			{
				_simAccumulator -= SimStep;
				steps++;

				StepSimulationForAllRooms(SimStep);
			}

			// use the *real* dt for broadcast timing, not SimStep
			await BroadcastSnapshotsIfNeeded(dtSeconds);
		}

		private void StepSimulationForAllRooms(float stepDt)
{
    lock (_syncLock)
    {
        foreach (var state in _rooms.Values)
            UpdateState(state, stepDt);
    }
}

		private async Task BroadcastSnapshotsIfNeeded(float dtSeconds)
		{
			_broadcastAccumulator += dtSeconds;
			var broadcastInterval = 1f / BroadcastRateHz;

			if (_broadcastAccumulator < broadcastInterval)
				return;

			_broadcastAccumulator -= broadcastInterval;

			List<(ClientConnection client, HubMessage message)> outgoing;

			lock (_syncLock)
			{
				outgoing = new();

				foreach (var kvp in _rooms)
				{
					var state = kvp.Value;
					var hubMsg = CreateStateMessage(state);

					for (int i = 0; i < _clients.Count; i++)
					{
						var c = _clients[i];
						if (c.RoomCode == state.RoomCode)
						{
							outgoing.Add((c, hubMsg));
						}
					}
				}
			}

			if (outgoing.Count == 0)
				return;

			var tasks = new Task[outgoing.Count];
			for (int i = 0; i < outgoing.Count; i++)
			{
				var (client, message) = outgoing[i];
				tasks[i] = _sendAsync(client, message);
			}

			await Task.WhenAll(tasks);
		}


		public virtual void OnClientDisconnected(ClientConnection client)
		{
			if (client.RoomCode == null)
				return;

			lock (_syncLock)
			{
				if (!_rooms.ContainsKey(client.RoomCode))
					return;

				var stillHasPlayers = _clients.Any(c => c.RoomCode == client.RoomCode);
				if (!stillHasPlayers)
				{
					_rooms.Remove(client.RoomCode);
					Console.WriteLine($"[{GameType}] Room {client.RoomCode} removed (empty).");
				}
			}
		}

		protected List<ClientConnection> GetRoomClients(string roomCode)
		{
			lock (_syncLock)
			{
				return _clients
					.Where(c => c.RoomCode == roomCode)
					.ToList();
			}
		}

		/// <summary>
		/// Game-specific message handling (inputs, etc.).
		/// e.g. PongInput, FightingInput, etc.
		/// </summary>
		public abstract Task HandleMessageAsync(HubMessage msg, ClientConnection client);

		public abstract Task RestartRoomAsync(Room room, ClientConnection? initiator);


		/// <summary>Ensure we have a state instance for the given room.</summary>
		protected TState EnsureRoomState(string roomCode)
		{
			if (!_rooms.TryGetValue(roomCode, out var state))
			{
				state = CreateRoomState(roomCode);
				_rooms[roomCode] = state;
			}

			return state;
		}
	}
}

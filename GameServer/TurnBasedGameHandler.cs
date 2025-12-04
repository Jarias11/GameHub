namespace GameServer
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Threading.Tasks;
	using GameContracts;
	using GameLogic;

	/// <summary>
	/// Base class for turn-based games (WordGuess, TicTacToe, Chess, etc.).
	/// Handles:
	/// - tracking room states
	/// - creating state when rooms appear
	/// - cleaning up empty rooms
	/// Subclasses implement game-specific message handling.
	/// </summary>
	public abstract class TurnBasedGameHandler<TState> : IGameHandler
		where TState : IRoomState
	{
		protected readonly RoomManager _roomManager;
		protected readonly List<ClientConnection> _clients;
		protected readonly object _syncLock;
		protected readonly Func<ClientConnection, HubMessage, Task> _sendAsync;

		// Per-room state for this game type (key = RoomCode)
		protected readonly Dictionary<string, TState> _rooms = new();

		protected TurnBasedGameHandler(
			RoomManager roomManager,
			List<ClientConnection> clients,
			object syncLock,
			Func<ClientConnection, HubMessage, Task> sendAsync)
		{
			_roomManager = roomManager;
			_clients = clients;
			_syncLock = syncLock;
			_sendAsync = sendAsync;
		}

		public abstract GameType GameType { get; }

		/// <summary>Which MessageTypes (e.g. "WordGuessGuess") this handler cares about.</summary>
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

		/// <summary>
		/// Game-specific message handling. Subclasses decide what to do.
		/// </summary>
		public abstract Task HandleMessageAsync(HubMessage msg, ClientConnection client);

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
		public abstract Task RestartRoomAsync(Room room, ClientConnection? initiator);

		/// <summary>Create initial state for a room.</summary>
		protected abstract TState CreateRoomState(string roomCode);

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

		/// <summary>Get all clients currently in a room.</summary>
		protected List<ClientConnection> GetRoomClients(string roomCode)
		{
			lock (_syncLock)
			{
				return _clients.Where(c => c.RoomCode == roomCode).ToList();
			}
		}
	}
}

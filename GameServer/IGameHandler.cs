namespace GameServer;

using System.Threading.Tasks;
using GameContracts;
using GameLogic;

public interface IGameHandler
{
	GameType GameType { get; }

	/// <summary>
	/// Returns true if this handler wants to handle the given MessageType.
	/// e.g. "PongInput", "WordGuessGuess".
	/// </summary>
	bool HandlesMessageType(string messageType);

	/// <summary>
	/// Called after a room is created for this game and the owner is assigned as P1.
	/// </summary>
	Task OnRoomCreated(Room room, ClientConnection owner);

	/// <summary>
	/// Called when a player successfully joins a room for this game.
	/// </summary>
	Task OnPlayerJoined(Room room, ClientConnection client);

	/// <summary>
	/// Called for any game-specific message (not CreateRoom/JoinRoom).
	/// </summary>
	Task HandleMessageAsync(HubMessage msg, ClientConnection client);
	
	/// <summary>
	/// Restart this room's game state and notify its players.
	/// </summary>
	Task RestartRoomAsync(Room room, ClientConnection? initiator);

	/// <summary>
	/// Called when a client disconnects from the server.
	/// </summary>
	void OnClientDisconnected(ClientConnection client);
}

public interface ITickableGameHandler : IGameHandler
{
	/// <summary>
	/// Called periodically by the hub (e.g. 30 times per second for Pong).
	/// </summary>
	Task TickAsync();
}

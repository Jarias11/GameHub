using GameContracts;

namespace GameLogic;

public class Room
{
	public string RoomCode { get; }
	public GameType GameType { get; }
	public GameCategory Category => GameCatalog.Get(GameType)!.Category;

	public int MaxPlayers { get; }
	// Just store logical player IDs ("P1", "P2")
	public List<string> Players { get; } = new();

	public Room(string roomCode, GameType gameType)
	{
		RoomCode = roomCode;
		GameType = gameType;
		MaxPlayers = GameCatalog.GetMaxPlayers(gameType);
	}
}

public class RoomManager
{
	private readonly Dictionary<string, Room> _rooms = new();
	private readonly Random _rng = new();

	public Room CreateRoom(GameType gameType)
	{
		var code = GenerateCode();
		var room = new Room(code, gameType);
		_rooms[code] = room;
		return room;
	}

	public Room? GetRoom(string code) =>
		_rooms.TryGetValue(code, out var room) ? room : null;

	public bool TryJoinRoom(string code, string playerId, out Room? room)
	{
		room = null;
		if (!_rooms.TryGetValue(code, out var r)) return false;

		if (!r.Players.Contains(playerId) && r.Players.Count < r.MaxPlayers)
		{
			r.Players.Add(playerId);
		}

		room = r;
		return true;
	}
	public bool LeaveRoom(string code, string playerId, out Room? room)
	{
		room = null;
		if (!_rooms.TryGetValue(code, out var r)) return false;

		if (r.Players.Contains(playerId))
		{
			r.Players.Remove(playerId);
		}

		// Optionally delete empty rooms
		if (r.Players.Count == 0)
		{
			_rooms.Remove(code);
		}

		room = r;
		return true;
	}

	private string GenerateCode()
	{
		const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
		while (true)
		{
			var s = new string(Enumerable.Range(0, 4)
				.Select(_ => chars[_rng.Next(chars.Length)]).ToArray());
			if (!_rooms.ContainsKey(s))
				return s;
		}
	}

}

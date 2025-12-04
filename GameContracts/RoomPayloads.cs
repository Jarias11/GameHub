namespace GameContracts;

// ---------- ROOM LIFECYCLE ----------

public class CreateRoomPayload
{
    public GameType GameType { get; set; }
}

public class JoinRoomPayload
{
    public string RoomCode { get; set; } = string.Empty;
}

public class RoomLeftPayload
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string LeavingPlayerId { get; set; } = string.Empty;
    public int PlayerCount { get; set; }
}

public class RoomCreatedPayload
{
    public string RoomCode { get; set; } = string.Empty;
    public GameType GameType { get; set; }
    public string PlayerId { get; set; } = string.Empty;    // "P1"
    public int PlayerCount { get; set; }
}

public class RoomJoinedPayload
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string RoomCode { get; set; } = string.Empty;
    public GameType GameType { get; set; }
    public string PlayerId { get; set; } = string.Empty;    // "P1" or "P2"
    public int PlayerCount { get; set; }
}

public class RestartGamePayload
{
    public string RoomCode { get; set; } = string.Empty;
    public GameType GameType { get; set; }
}

public class LeaveRoomPayload
{
    public string RoomCode { get; set; } = string.Empty;
}

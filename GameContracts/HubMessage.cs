namespace GameContracts;

/// <summary>
/// Envelope for messages over WebSocket.
/// </summary>
public class HubMessage
{
    public string MessageType { get; set; } = string.Empty; // "CreateRoom", "JoinRoom", "PongInput", ...
    public string RoomCode { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;    // "P1"/"P2" for two-player games
    public string PayloadJson { get; set; } = string.Empty;
}

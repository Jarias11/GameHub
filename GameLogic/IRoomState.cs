namespace GameLogic
{
    /// <summary>
    /// Minimal interface for server-side room state used by tick-based games.
    /// Lets generic handlers know which room a state belongs to.
    /// </summary>
    public interface IRoomState
    {
        string RoomCode { get; }
    }
}
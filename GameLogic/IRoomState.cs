namespace GameLogic
{
    /// <summary>
    /// Lets generic handlers know which room a state belongs to.
    /// </summary>
    public interface IRoomState
    {
        string RoomCode { get; }
    }
}
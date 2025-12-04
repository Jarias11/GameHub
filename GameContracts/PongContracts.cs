namespace GameContracts;



// Client → Server: paddle input
public class PongInputPayload
{
	/// <summary>
	/// -1 = move up, 0 = stop, 1 = move down
	/// </summary>
	public int Direction { get; set; }
}

// Server → Client: full game state snapshot
public class PongStatePayload
{
	public float BallX { get; set; }
	public float BallY { get; set; }

	public float Paddle1Y { get; set; }
	public float Paddle2Y { get; set; }

	public int Score1 { get; set; }
	public int Score2 { get; set; }
}
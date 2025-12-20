namespace GameContracts;

// ===========================
// MessageType strings
// ===========================
public static class SpaceShooterMsg
{
	// client -> server
	public const string Input = "SpaceShooterInput";

	// server -> clients
	public const string State = "SpaceShooterState";
	public const string GameOver = "SpaceShooterGameOver";
}

// ===========================
// Client -> Server input
// ===========================
public sealed class SpaceShooterInputPayload
{
	// Monotonic increasing on client; helps ignore out-of-order inputs if you want.
	public int Sequence { get; set; }

	// Client-side time (optional). Server should not trust this for simulation.
	public long ClientTicks { get; set; }

	// Thrust forward (ship accelerates in facing direction)
	public bool Thrust { get; set; }

	// Rotate ship
	public bool TurnLeft { get; set; }
	public bool TurnRight { get; set; }

	// Fire gun (server handles cooldown)
	public bool Fire { get; set; }
}

// ===========================
// Server -> Clients snapshot
// ===========================
public sealed class SpaceShooterStatePayload
{
	public string RoomCode { get; set; } = string.Empty;

	// Server tick index (monotonic). Helps clients smooth/interpolate.
	public long Tick { get; set; }

	// World definition
	public SpaceShooterWorldPayload World { get; set; } = new();

	// Entities
	public List<SpaceShooterShipPayload> Ships { get; set; } = new();
	public List<SpaceShooterBulletPayload> Bullets { get; set; } = new();
	public List<SpaceShooterAsteroidPayload> Asteroids { get; set; } = new();

	// Optional: quick UI info
	public List<SpaceShooterEventPayload> Events { get; set; } = new();

	public int PlayersAlive { get; set; }
public int PlayersTotal { get; set; }
}

public sealed class SpaceShooterWorldPayload
{
	// Big circular map (players wrap / clamp / bounce — your choice in engine)
	public float WorldRadius { get; set; }

	// Camera behavior (client uses this to render)
	public float CameraViewRadius { get; set; }   // “zoomed-in area radius”
	public float CameraDeadzone { get; set; }     // optional (can be 0)

	// Limits
	public int MaxPlayers { get; set; } = 4;

	// Random seed so obstacles can be deterministic if you ever want that
	public int Seed { get; set; }
}

public sealed class SpaceShooterShipPayload
{
	public string PlayerId { get; set; } = string.Empty; // "P1".."P4"

	// Position + facing
	public float X { get; set; }
	public float Y { get; set; }
	public float AngleRad { get; set; }

	// Velocity
	public float Vx { get; set; }
	public float Vy { get; set; }

	// State
	public bool Alive { get; set; } = true;
	public int Hp { get; set; } = 3;

	// Cooldowns
	public float FireCooldownSec { get; set; }

	// Score / stats
	public int Kills { get; set; }
	public int Deaths { get; set; }
}

public sealed class SpaceShooterBulletPayload
{
	public int Id { get; set; }

	public string OwnerPlayerId { get; set; } = string.Empty;

	public float X { get; set; }
	public float Y { get; set; }

	public float Vx { get; set; }
	public float Vy { get; set; }

	public float LifeSec { get; set; }
}

public sealed class SpaceShooterAsteroidPayload
{
	public int Id { get; set; }

	public float X { get; set; }
	public float Y { get; set; }

	public float Vx { get; set; }
	public float Vy { get; set; }

	public float Radius { get; set; }
	public bool Deadly { get; set; } = true; // if true: colliding kills ship / damages
}

// Tiny “events” list for UI feedback (optional)
public sealed class SpaceShooterEventPayload
{
	// e.g. "Explosion", "Hit", "PlayerKilled", "AsteroidHit"
	public string Type { get; set; } = string.Empty;

	// Often: who caused it / who was affected
	public string A { get; set; } = string.Empty;
	public string B { get; set; } = string.Empty;

	// Where it happened
	public float X { get; set; }
	public float Y { get; set; }
}

// ===========================
// Game Over
// ===========================
public sealed class SpaceShooterGameOverPayload
{
	public string WinnerPlayerId { get; set; } = string.Empty; // or empty for draw
	public string Reason { get; set; } = string.Empty;         // "LastAlive", "TimeLimit", etc.
	public List<SpaceShooterShipPayload> FinalShips { get; set; } = new();
}

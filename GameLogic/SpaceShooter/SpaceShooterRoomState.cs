using System;
using System.Collections.Generic;
using GameContracts;

namespace GameLogic.SpaceShooter;

public sealed class SpaceShooterRoomState : IRoomState
{
	public string RoomCode { get; }

	// room config
	public int Seed { get; set; }
	public float WorldRadius { get; set; } = 2000f;      // big map
	public float CameraViewRadius { get; set; } = 350f;  // small zoomed-in radius
	public float CameraDeadzone { get; set; } = 0f;

	// simulation
	public long Tick { get; set; }
	public Random Rng { get; }

	// entities
	public readonly Dictionary<string, Ship> Ships = new(); // key: "P1".."P4"
	public readonly List<Bullet> Bullets = new();
	public readonly List<Asteroid> Asteroids = new();

	// last input per player (sticky)
	public readonly Dictionary<string, InputState> Inputs = new();

	// id generators
	public int NextBulletId { get; set; } = 1;
	public int NextAsteroidId { get; set; } = 1;

	// game flow
	public bool GameOver { get; set; }
	public string WinnerPlayerId { get; set; } = "";
	public readonly List<SpaceShooterEventPayload> Events = new();

	public SpaceShooterRoomState(string roomCode, int seed)
	{
		RoomCode = roomCode;
		Seed = seed;
		Rng = new Random(seed);
	}

	// -------------------------
	// Internal sim structs
	// -------------------------
	public sealed class Ship
{
	public string PlayerId = "";
	public float X, Y;
	public float Vx, Vy;
	public float AngleRad;

	public bool Alive = true;

	// ✅ Lives
	public int Hp = 3;

	// ✅ Spawn (so respawn puts you back where you started)
	public float SpawnX, SpawnY, SpawnAngleRad;

	// ✅ Optional: tiny invulnerability after respawn (prevents instant chain deaths)
	public float InvulnSec = 0f;

	// shooting
	public float FireCooldownSec = 0f;
	public int Kills = 0;
	public int Deaths = 0;
}


	public sealed class Bullet
	{
		public int Id;
		public string OwnerPlayerId = "";
		public float X, Y;
		public float Vx, Vy;
		public float LifeSec;
	}

	public sealed class Asteroid
	{
		public int Id;
		public float X, Y;
		public float Vx, Vy;
		public float Radius;
		public bool Deadly = true;
	}

	public sealed class InputState
	{
		public int Sequence;
		public bool Thrust;
		public bool TurnLeft;
		public bool TurnRight;
		public bool Fire;
	}
}

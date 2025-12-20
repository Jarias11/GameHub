using System;
using System.Collections.Generic;
using GameContracts;

namespace GameLogic.SpaceShooter;

public static class SpaceShooterEngine
{
	// ========= Tunables (start here, tweak later) =========

	// ship feel (Asteroids-like)
	private const float TurnRateRad = 3.5f;        // radians/sec
	private const float ThrustAccel = 420f;        // units/sec^2
	private const float LinearDamping = 0.35f;     // low friction (0 = none, 1 = heavy)
	private const float MaxShipSpeed = 650f;

	// collision sizes (approx ship as circle for now)
	private const float ShipRadius = 16f;

	// bullets
	private const float BulletSpeed = 700f;        // “medium”
	private const float BulletLifeSec = 2.2f;
	private const float FireCooldownSec = 0.22f;   // hold-to-fire rate
	private const int MaxBulletsAlivePerPlayer = 8;

	// triple-shot spread
	// private const float SpreadRad = 0.10f;         // ~5.7 degrees
	// private static readonly float[] ShotAngles = new float[] { -SpreadRad, 0f, +SpreadRad };

	// asteroids
	private const int DefaultAsteroidCount = 20;
	private const float AsteroidMinRadius = 18f;
	private const float AsteroidMaxRadius = 100f;
	private const float AsteroidMaxSpeed = 60f;

	// ===============================================

	public static void Reset(SpaceShooterRoomState state, IReadOnlyList<string> players)
	{
		state.Tick = 0;
		state.GameOver = false;
		state.WinnerPlayerId = "";
		state.Events.Clear();

		state.Ships.Clear();
		state.Bullets.Clear();
		state.Asteroids.Clear();
		state.Inputs.Clear();

		state.NextBulletId = 1;
		state.NextAsteroidId = 1;

		SpawnShipsOnCircle(state, players);
		SpawnAsteroids(state, DefaultAsteroidCount);
	}

	public static void SetInput(SpaceShooterRoomState state, string playerId, SpaceShooterInputPayload input)
	{
		if (!state.Inputs.TryGetValue(playerId, out var s))
		{
			s = new SpaceShooterRoomState.InputState();
			state.Inputs[playerId] = s;
		}

		// ignore out-of-order if you want
		if (input.Sequence < s.Sequence) return;

		s.Sequence = input.Sequence;
		s.Thrust = input.Thrust;
		s.TurnLeft = input.TurnLeft;
		s.TurnRight = input.TurnRight;
		s.Fire = input.Fire;
	}

	public static void Tick(SpaceShooterRoomState state, float dt)
	{
		if (state.GameOver) return;

		state.Tick++;
		state.Events.Clear();

		// ---- ships ----
		foreach (var kv in state.Ships)
		{
			var ship = kv.Value;
			if (!ship.Alive) continue;

			// cooldowns
			if (ship.FireCooldownSec > 0f)
				ship.FireCooldownSec = MathF.Max(0f, ship.FireCooldownSec - dt);
			if (ship.InvulnSec > 0f)
				ship.InvulnSec = MathF.Max(0f, ship.InvulnSec - dt);


			// input
			state.Inputs.TryGetValue(ship.PlayerId, out var input);
			bool thrust = input?.Thrust ?? false;
			bool left = input?.TurnLeft ?? false;
			bool right = input?.TurnRight ?? false;
			bool fire = input?.Fire ?? false;

			// rotate
			float turnDir = 0f;
			if (left) turnDir -= 1f;
			if (right) turnDir += 1f;
			if (turnDir != 0f)
				ship.AngleRad += turnDir * TurnRateRad * dt;

			// thrust (forward)
			if (thrust)
			{
				float fx = MathF.Cos(ship.AngleRad);
				float fy = MathF.Sin(ship.AngleRad);
				ship.Vx += fx * ThrustAccel * dt;
				ship.Vy += fy * ThrustAccel * dt;
			}

			// damping (low friction)
			float damp = MathF.Max(0f, 1f - (LinearDamping * dt));
			ship.Vx *= damp;
			ship.Vy *= damp;

			// clamp speed
			ClampSpeed(ref ship.Vx, ref ship.Vy, MaxShipSpeed);

			// integrate
			ship.X += ship.Vx * dt;
			ship.Y += ship.Vy * dt;

			// boundary kill: leaving world circle kills you
			if (ship.X * ship.X + ship.Y * ship.Y > state.WorldRadius * state.WorldRadius)
			{
				HitShip(state, ship.PlayerId, "WORLD");
				continue;
			}

			// shooting: hold-to-fire, triple-shot, but max 4 bullets alive per player
			// shooting: hold-to-fire, SINGLE shot, max bullets alive per player
			if (fire && ship.FireCooldownSec <= 0f)
			{
				int aliveBullets = CountBulletsForOwner(state.Bullets, ship.PlayerId);
				if (aliveBullets < MaxBulletsAlivePerPlayer)
				{
					FireSingleShot(state, ship);
					ship.FireCooldownSec = FireCooldownSec;
				}
			}

		}

		// ---- asteroids ----
		for (int i = 0; i < state.Asteroids.Count; i++)
		{
			var a = state.Asteroids[i];
			a.X += a.Vx * dt;
			a.Y += a.Vy * dt;

			// keep asteroids inside the circle (simple bounce)
			float r2 = a.X * a.X + a.Y * a.Y;
			float maxR = state.WorldRadius - a.Radius;
			if (r2 > maxR * maxR)
			{
				// push back toward center and reverse velocity a bit
				float r = MathF.Sqrt(r2);
				if (r > 0.001f)
				{
					float nx = a.X / r;
					float ny = a.Y / r;
					a.X = nx * maxR;
					a.Y = ny * maxR;

					// reflect velocity along normal
					float vn = a.Vx * nx + a.Vy * ny;
					a.Vx = a.Vx - 2f * vn * nx;
					a.Vy = a.Vy - 2f * vn * ny;
				}
			}
		}

		// ---- bullets ----
		for (int i = state.Bullets.Count - 1; i >= 0; i--)
		{
			var b = state.Bullets[i];
			b.X += b.Vx * dt;
			b.Y += b.Vy * dt;
			b.LifeSec -= dt;

			// expire
			if (b.LifeSec <= 0f)
			{
				state.Bullets.RemoveAt(i);
				continue;
			}

			// leaving world circle just deletes bullet (no kill)
			if (b.X * b.X + b.Y * b.Y > state.WorldRadius * state.WorldRadius)
			{
				state.Bullets.RemoveAt(i);
				continue;
			}

			// bullet vs asteroid
			bool hitSomething = false;
			for (int a = 0; a < state.Asteroids.Count; a++)
			{
				var ast = state.Asteroids[a];
				if (CircleContainsPoint(ast.X, ast.Y, ast.Radius, b.X, b.Y))
				{
					state.Events.Add(new SpaceShooterEventPayload
					{
						Type = "AsteroidHit",
						A = b.OwnerPlayerId,
						X = b.X,
						Y = b.Y
					});
					state.Bullets.RemoveAt(i);
					hitSomething = true;
					break;
				}
			}
			if (hitSomething) continue;

			// bullet vs ship (1-hit kill)
			foreach (var kv in state.Ships)
			{
				var ship = kv.Value;
				if (!ship.Alive) continue;
				if (ship.PlayerId == b.OwnerPlayerId) continue;

				if (CircleContainsPoint(ship.X, ship.Y, ShipRadius, b.X, b.Y))
				{
					// kill
					HitShip(state, ship.PlayerId, b.OwnerPlayerId);
					state.Bullets.RemoveAt(i);

					// only award a kill if the victim actually died (Hp hit 0)
					if (state.Ships.TryGetValue(ship.PlayerId, out var victim) && !victim.Alive)
					{
						if (state.Ships.TryGetValue(b.OwnerPlayerId, out var killer))
							killer.Kills += 1;
					}

					break;
				}
			}
		}

		// ---- ship vs asteroid (deadly) ----
		foreach (var kv in state.Ships)
		{
			var ship = kv.Value;
			if (!ship.Alive) continue;

			for (int a = 0; a < state.Asteroids.Count; a++)
			{
				var ast = state.Asteroids[a];
				float rr = ShipRadius + ast.Radius;
				if (CircleOverlapsCircle(ship.X, ship.Y, rr, ast.X, ast.Y, 0f))
				{
					HitShip(state, ship.PlayerId, "ASTEROID");
					break;
				}
			}
		}

		// ---- win check (last ship alive) ----
		int aliveCount = 0;
		string lastAlive = "";
		foreach (var kv in state.Ships)
		{
			if (kv.Value.Alive)
			{
				aliveCount++;
				lastAlive = kv.Key;
			}
		}
		if (aliveCount <= 1 && state.Ships.Count >= 2)
		{
			state.GameOver = true;
			state.WinnerPlayerId = aliveCount == 1 ? lastAlive : "";
			state.Events.Add(new SpaceShooterEventPayload
			{
				Type = "GameOver",
				A = state.WinnerPlayerId,
				X = 0,
				Y = 0
			});
		}
	}
	private static void FireSingleShot(SpaceShooterRoomState state, SpaceShooterRoomState.Ship ship)
	{
		float fx = MathF.Cos(ship.AngleRad);
		float fy = MathF.Sin(ship.AngleRad);

		float startX = ship.X + fx * (ShipRadius + 6f);
		float startY = ship.Y + fy * (ShipRadius + 6f);

		state.Bullets.Add(new SpaceShooterRoomState.Bullet
		{
			Id = state.NextBulletId++,
			OwnerPlayerId = ship.PlayerId,
			X = startX,
			Y = startY,
			Vx = fx * BulletSpeed + ship.Vx,
			Vy = fy * BulletSpeed + ship.Vy,
			LifeSec = BulletLifeSec
		});

		state.Events.Add(new SpaceShooterEventPayload
		{
			Type = "Shoot",
			A = ship.PlayerId,
			X = startX,
			Y = startY
		});
	}


	public static SpaceShooterStatePayload BuildStatePayload(SpaceShooterRoomState state)
	{
		var payload = new SpaceShooterStatePayload
		{
			RoomCode = state.RoomCode,
			Tick = state.Tick,

			World = new SpaceShooterWorldPayload
			{
				WorldRadius = state.WorldRadius,
				CameraViewRadius = state.CameraViewRadius,
				CameraDeadzone = state.CameraDeadzone,
				MaxPlayers = 4,
				Seed = state.Seed
			}
		};

		payload.PlayersTotal = state.Ships.Count;
		int alive = 0;
		foreach (var kv in state.Ships)
			if (kv.Value.Alive) alive++;
		payload.PlayersAlive = alive;

		foreach (var kv in state.Ships)
		{
			var s = kv.Value;
			payload.Ships.Add(new SpaceShooterShipPayload
			{
				PlayerId = s.PlayerId,
				X = s.X,
				Y = s.Y,
				Vx = s.Vx,
				Vy = s.Vy,
				AngleRad = s.AngleRad,
				Alive = s.Alive,
				// Hp is ignored for now (1-hit kill), but keep it for future upgrades
				Hp = s.Hp,
				FireCooldownSec = s.FireCooldownSec,
				Kills = s.Kills,
				Deaths = s.Deaths
			});
		}

		for (int i = 0; i < state.Bullets.Count; i++)
		{
			var b = state.Bullets[i];
			payload.Bullets.Add(new SpaceShooterBulletPayload
			{
				Id = b.Id,
				OwnerPlayerId = b.OwnerPlayerId,
				X = b.X,
				Y = b.Y,
				Vx = b.Vx,
				Vy = b.Vy,
				LifeSec = b.LifeSec
			});
		}

		for (int i = 0; i < state.Asteroids.Count; i++)
		{
			var a = state.Asteroids[i];
			payload.Asteroids.Add(new SpaceShooterAsteroidPayload
			{
				Id = a.Id,
				X = a.X,
				Y = a.Y,
				Vx = a.Vx,
				Vy = a.Vy,
				Radius = a.Radius,
				Deadly = a.Deadly
			});
		}

		// events (copy)
		for (int i = 0; i < state.Events.Count; i++)
			payload.Events.Add(state.Events[i]);

		return payload;
	}

	public static SpaceShooterGameOverPayload BuildGameOverPayload(SpaceShooterRoomState state)
	{
		var p = new SpaceShooterGameOverPayload
		{
			WinnerPlayerId = state.WinnerPlayerId,
			Reason = "LastAlive"
		};

		foreach (var kv in state.Ships)
		{
			var s = kv.Value;
			p.FinalShips.Add(new SpaceShooterShipPayload
			{
				PlayerId = s.PlayerId,
				X = s.X,
				Y = s.Y,
				Vx = s.Vx,
				Vy = s.Vy,
				AngleRad = s.AngleRad,
				Alive = s.Alive,
				Hp = s.Alive ? 1 : 0,
				FireCooldownSec = s.FireCooldownSec,
				Kills = s.Kills,
				Deaths = s.Deaths
			});
		}

		return p;
	}

	// ==========================
	// Helpers
	// ==========================

	private static void SpawnShipsOnCircle(SpaceShooterRoomState state, IReadOnlyList<string> players)
	{
		// Put players evenly spaced around a spawn ring near the edge
		float spawnR = state.WorldRadius * 0.75f;
		int n = Math.Max(players.Count, 2);

		for (int i = 0; i < players.Count; i++)
		{
			string pid = players[i];
			float angle = (MathF.Tau * i) / n;

			float x = MathF.Cos(angle) * spawnR;
			float y = MathF.Sin(angle) * spawnR;

			state.Ships[pid] = new SpaceShooterRoomState.Ship
			{
				PlayerId = pid,

				SpawnX = x,
				SpawnY = y,
				SpawnAngleRad = angle + MathF.PI,

				X = x,
				Y = y,
				Vx = 0f,
				Vy = 0f,
				AngleRad = angle + MathF.PI,

				Alive = true,
				Hp = 3,
				InvulnSec = 1.0f // optional: 1 second spawn shield
			};


			state.Inputs[pid] = new SpaceShooterRoomState.InputState();
		}
	}

	private static void SpawnAsteroids(SpaceShooterRoomState state, int count)
	{
		// Spawn inside the world, away from center a bit, random drift
		for (int i = 0; i < count; i++)
		{
			float radius = Lerp(AsteroidMinRadius, AsteroidMaxRadius, (float)state.Rng.NextDouble());

			// random polar position within world
			float r = (float)state.Rng.NextDouble();
			r = MathF.Sqrt(r) * (state.WorldRadius - radius - 50f); // sqrt for uniform area
			float ang = (float)state.Rng.NextDouble() * MathF.Tau;

			float x = MathF.Cos(ang) * r;
			float y = MathF.Sin(ang) * r;

			// random velocity
			float vAng = (float)state.Rng.NextDouble() * MathF.Tau;
			float vMag = (float)state.Rng.NextDouble() * AsteroidMaxSpeed;
			float vx = MathF.Cos(vAng) * vMag;
			float vy = MathF.Sin(vAng) * vMag;

			state.Asteroids.Add(new SpaceShooterRoomState.Asteroid
			{
				Id = state.NextAsteroidId++,
				X = x,
				Y = y,
				Vx = vx,
				Vy = vy,
				Radius = radius,
				Deadly = true
			});
		}
	}

	// private static void FireTripleShot(SpaceShooterRoomState state, SpaceShooterRoomState.Ship ship)
	// {
	// 	// spawn bullets from ship nose
	// 	float fx = MathF.Cos(ship.AngleRad);
	// 	float fy = MathF.Sin(ship.AngleRad);

	// 	float startX = ship.X + fx * (ShipRadius + 6f);
	// 	float startY = ship.Y + fy * (ShipRadius + 6f);

	// 	for (int i = 0; i < ShotAngles.Length; i++)
	// 	{
	// 		// respect per-player max bullets alive:
	// 		if (CountBulletsForOwner(state.Bullets, ship.PlayerId) >= MaxBulletsAlivePerPlayer)
	// 			break;

	// 		float a = ship.AngleRad + ShotAngles[i];
	// 		float bx = MathF.Cos(a);
	// 		float by = MathF.Sin(a);

	// 		state.Bullets.Add(new SpaceShooterRoomState.Bullet
	// 		{
	// 			Id = state.NextBulletId++,
	// 			OwnerPlayerId = ship.PlayerId,
	// 			X = startX,
	// 			Y = startY,
	// 			Vx = bx * BulletSpeed + ship.Vx, // inherit a bit of ship velocity
	// 			Vy = by * BulletSpeed + ship.Vy,
	// 			LifeSec = BulletLifeSec
	// 		});
	// 	}

	// 	state.Events.Add(new SpaceShooterEventPayload
	// 	{
	// 		Type = "Shoot",
	// 		A = ship.PlayerId,
	// 		X = startX,
	// 		Y = startY
	// 	});
	// }

	private static int CountBulletsForOwner(List<SpaceShooterRoomState.Bullet> bullets, string owner)
	{
		int c = 0;
		for (int i = 0; i < bullets.Count; i++)
			if (bullets[i].OwnerPlayerId == owner) c++;
		return c;
	}

	private static void HitShip(SpaceShooterRoomState state, string victimPlayerId, string hitterId)
	{
		if (!state.Ships.TryGetValue(victimPlayerId, out var ship)) return;
		if (!ship.Alive) return;
		if (ship.InvulnSec > 0f) return; // ✅ ignore hits during invuln

		ship.Hp -= 1;
		ship.Deaths += 1;

		state.Events.Add(new SpaceShooterEventPayload
		{
			Type = "PlayerHit",
			A = hitterId,
			B = victimPlayerId,
			X = ship.X,
			Y = ship.Y
		});

		if (ship.Hp <= 0)
		{
			ship.Alive = false;

			state.Events.Add(new SpaceShooterEventPayload
			{
				Type = "PlayerKilled",
				A = hitterId,
				B = victimPlayerId,
				X = ship.X,
				Y = ship.Y
			});

			return;
		}

		// ✅ Respawn
		RespawnShip(ship);
	}

	private static void RespawnShip(SpaceShooterRoomState.Ship ship)
	{
		ship.X = ship.SpawnX;
		ship.Y = ship.SpawnY;
		ship.AngleRad = ship.SpawnAngleRad;

		ship.Vx = 0f;
		ship.Vy = 0f;

		ship.FireCooldownSec = 0.25f; // optional: tiny grace so you don't spawn and immediately shoot
		ship.InvulnSec = 1.0f;        // optional: spawn shield
	}


	private static bool CircleContainsPoint(float cx, float cy, float radius, float px, float py)
	{
		float dx = px - cx;
		float dy = py - cy;
		return (dx * dx + dy * dy) <= (radius * radius);
	}

	// Overlap helper (ship radius already includes asteroid radius in caller)
	private static bool CircleOverlapsCircle(float ax, float ay, float ar, float bx, float by, float br)
	{
		float dx = bx - ax;
		float dy = by - ay;
		float rr = ar + br;
		return (dx * dx + dy * dy) <= (rr * rr);
	}

	private static void ClampSpeed(ref float vx, ref float vy, float max)
	{
		float s2 = vx * vx + vy * vy;
		float m2 = max * max;
		if (s2 <= m2) return;

		float s = MathF.Sqrt(s2);
		if (s < 0.0001f) return;

		float k = max / s;
		vx *= k;
		vy *= k;
	}

	private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

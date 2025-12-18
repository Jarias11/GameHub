// GameServer/JumpsOnlineGameHandler.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GameContracts;
using GameLogic;
using GameLogic.Jumps;
using GameLogic.JumpsOnline;

namespace GameServer
{
	public sealed class JumpsOnlineGameHandler : TickableGameHandler<JumpsOnlineRoomState>
	{
		protected override float BroadcastRateHz => 60f;

		private const float CountdownDuration = 3f;
		private const float MagnetPullSpeed = 260f; // tweak to taste

		private readonly List<JumpsOnlinePlatformRuntime> _candidatePlatforms = new();


		// Latest input per player (key = "ROOM:PLAYER")
		private readonly Dictionary<string, JumpsOnlineInputPayload> _latestInputs = new();
		private readonly Dictionary<string, bool> _lastJumpHeld = new();
		public JumpsOnlineGameHandler(
			RoomManager roomManager,
			List<ClientConnection> clients,
			object syncLock,
			Random rng,
			Func<ClientConnection, HubMessage, Task> sendAsync)
			: base(roomManager, clients, syncLock, rng, sendAsync)
		{
		}

		public override GameType GameType => GameType.JumpsOnline;

		public override bool HandlesMessageType(string messageType) =>
			messageType == "JumpsOnlineInput" ||
			messageType == "JumpsOnlineStartRequest" ||
			messageType == "JumpsOnlineRestartRequest";

		// ─────────────────────────────────────────────
		// Room lifecycle
		// ─────────────────────────────────────────────

		protected override JumpsOnlineRoomState CreateRoomState(string roomCode)
		{
			var state = new JumpsOnlineRoomState(roomCode, _rng.Next());
			InitWorld(state);
			return state;
		}

		public override Task OnRoomCreated(Room room, ClientConnection owner)
		{
			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);

				if (!string.IsNullOrEmpty(owner.PlayerId) &&
					!state.PlayersById.ContainsKey(owner.PlayerId))
				{
					state.AddPlayer(owner.PlayerId);
					state.TotalColumns = Math.Max(3, state.Players.Count * 3);
					LayoutPlayersOnGround(state);
				}
			}
			return Task.CompletedTask;
		}

		public override Task OnPlayerJoined(Room room, ClientConnection client)
		{
			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);

				if (!string.IsNullOrEmpty(client.PlayerId) &&
					!state.PlayersById.ContainsKey(client.PlayerId))
				{
					state.AddPlayer(client.PlayerId);
					state.TotalColumns = Math.Max(3, state.Players.Count * 3);
					LayoutPlayersOnGround(state);
				}
			}
			return Task.CompletedTask;
		}

		public override void OnClientDisconnected(ClientConnection client)
		{
			if (client.RoomCode == null)
				return;

			lock (_syncLock)
			{
				if (_rooms.TryGetValue(client.RoomCode, out var state) &&
					client.PlayerId != null)
				{
					state.RemovePlayer(client.PlayerId);
				}

				// If there are no clients left in this room, remove state
				var stillHasPlayers = _clients.Any(c => c.RoomCode == client.RoomCode);
				if (!stillHasPlayers)
				{
					_rooms.Remove(client.RoomCode);
					string prefix = client.RoomCode + ":";

					foreach (var k in _latestInputs.Keys.Where(k => k.StartsWith(prefix)).ToList())
						_latestInputs.Remove(k);

					foreach (var k in _lastJumpHeld.Keys.Where(k => k.StartsWith(prefix)).ToList())
						_lastJumpHeld.Remove(k);
					Console.WriteLine($"[JumpsOnline] Room {client.RoomCode} removed (empty).");
				}
			}
		}

		public override async Task RestartRoomAsync(Room room, ClientConnection? initiator)
		{
			JumpsOnlineRoomState? newState = null;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(room.RoomCode, out var oldState))
					return;

				// Create a fresh state with a new seed
				newState = new JumpsOnlineRoomState(room.RoomCode, _rng.Next());

				// Copy players
				foreach (var player in oldState.Players)
				{
					newState.AddPlayer(player.PlayerId);
				}

				newState.TotalColumns = Math.Max(3, newState.Players.Count * 3);
				InitWorld(newState);
				LayoutPlayersOnGround(newState);

				_rooms[room.RoomCode] = newState;

				// Clear cached inputs for this room
				var keysToRemove = _latestInputs.Keys
					.Where(k => k.StartsWith(room.RoomCode + ":", StringComparison.Ordinal))
					.ToList();
				foreach (var key in keysToRemove)
					_latestInputs.Remove(key);
			}

			// Optional: immediately send a fresh snapshot; the normal tick
			// loop will also send one soon anyway.
			if (newState != null)
			{
				var msg = CreateStateMessage(newState);
				var roomClients = GetRoomClients(newState.RoomCode);
				foreach (var c in roomClients)
				{
					await _sendAsync(c, msg);
				}
			}
		}

		// ─────────────────────────────────────────────
		// Tick-based simulation
		// ─────────────────────────────────────────────

		protected override void UpdateState(JumpsOnlineRoomState state, float dtSeconds)
		{
			switch (state.Phase)
			{
				case JumpsOnlinePhase.Lobby:
					// Just idle in lobby, players stand on ground
					return;

				case JumpsOnlinePhase.Countdown:
					// NEW: let players move/jump during countdown, but no scroll yet
					SimulateCountdown(state, dtSeconds);

					state.CountdownSecondsRemaining -= dtSeconds;
					if (state.CountdownSecondsRemaining <= 0f)
					{
						state.CountdownSecondsRemaining = 0f;
						state.Phase = JumpsOnlinePhase.Running;
						state.ElapsedSinceRoundStart = 0f;
						state.ElapsedSinceScrollStart = 0f;
					}
					return;

				case JumpsOnlinePhase.Running:
					SimulateRunning(state, dtSeconds);
					return;

				case JumpsOnlinePhase.Finished:
					// Round over, wait for restart
					return;
			}
		}
		/// <summary>
		/// Countdown phase: players can move & jump to get into position,
		/// but the world does NOT scroll yet (ScrollSpeed = 0, level stays at 1).
		/// </summary>
		private void SimulateCountdown(JumpsOnlineRoomState state, float dt)
		{
			if (state.Players.Count == 0)
				return;

			// No scrolling during countdown
			state.ScrollSpeed = 0f;

			// Optional: tick powerups so any leftover timers still behave
			TickPowerupTimers(state, dt);

			// Use a baseline horizontal speed (feels like early-game)
			float baselineScrollForSpeed = JumpsEngine.BaseScrollSpeed;

			// 1) Horizontal movement + jump/double-jump per player
			foreach (var p in state.Players)
			{
				if (!p.IsAlive)
					continue;

				var input = GetLatestInput(state, p.PlayerId);

				// Direction: -1, 0, +1
				float dir = 0f;
				if (input.Left && !input.Right) dir = -1f;
				else if (input.Right && !input.Left) dir = 1f;

				float baseMoveSpeed = ComputeMoveSpeed(state, baselineScrollForSpeed);
				float moveSpeed = baseMoveSpeed;

				// SpeedBoost only for this player (if any are active)
				if (p.SpeedBoostActive && p.SpeedBoostTimeRemaining > 0f)
				{
					moveSpeed *= JumpsEngine.SpeedBoostMultiplier;
				}

				p.X += dir * moveSpeed * dt;
				if (p.X < 0f) p.X = 0f;
				if (p.X > JumpsEngine.WorldWidth - p.Width)
					p.X = JumpsEngine.WorldWidth - p.Width;

				// ─────────────────────────────────────
				// Jump input: edge + variable jump
				// ─────────────────────────────────────
				bool prevJumpHeld = GetLastJumpHeld(state, p.PlayerId);
				bool justPressedJump = input.JumpHeld && !prevJumpHeld;
				bool justReleasedJump = !input.JumpHeld && prevJumpHeld;
				SetLastJumpHeld(state, p.PlayerId, input.JumpHeld);

				// Jump / Double-jump (same rules as Running)
				if (justPressedJump && p.IsGrounded)
				{
					// Hold Down + Jump to drop through
					if (input.Down && p.CurrentPlatform != null)
					{
						StartDropThrough(p);
					}
					else
					{
						StartJump(state, p);
					}
				}
				else if (justPressedJump && !p.IsGrounded &&
						 p.DoubleJumpActive && p.DoubleJumpTimeRemaining > 0f &&
						 p.AirJumpsRemaining > 0)
				{
					UseAirJump(state, p);
				}

				// Short-hop (jump cut) if released while still rising
				if (justReleasedJump && p.VY < 0f && !p.JumpCutApplied)
				{
					const float jumpCutFactor = 0.35f;
					p.VY *= jumpCutFactor;
					p.JumpCutApplied = true;
				}
			}

			// 2) NO vertical scroll here – world stays still until Running
			//    (no state.GroundY += scroll, no plat.Y += scroll, no p.Y += scroll)

			// 3) Gravity + landing + death checks
			foreach (var p in state.Players)
			{
				if (!p.IsAlive)
					continue;

				float prevY = p.Y;
				float prevBottom = prevY + p.Height;

				p.VY += JumpsEngine.Gravity * dt;
				p.Y += p.VY * dt;

				if (p.VY >= 0f)
					p.IsJumping = false;

				float bottom = p.Y + p.Height;

				if (p.VY > 0f)
				{
					TryLandOnPlatform(state, p, prevBottom, bottom);
				}

				// You *can* die during countdown if you somehow fall off,
				// but that's probably rare. If you don't want that, you can
				// comment this block out.
				if (bottom > JumpsEngine.WorldHeight + JumpsEngine.DeathMargin)
				{
					if (p.IsAlive)
					{
						p.IsAlive = false;
						state.AlivePlayerCount--;
					}
				}
			}

			// 4) Magnet effects and pickup collisions still work
			UpdateMagnetPulls(state, dt);

			foreach (var p in state.Players)
			{
				if (!p.IsAlive)
					continue;

				CheckPickupCollisions(state, p);
			}

			// 5) Row recycling still allowed (in case you ever extend countdown),
			// but since nothing scrolls, it basically does nothing after initial fill.
			RecycleAndSpawnRows(state);

			// NOTE: we deliberately DO NOT call UpdateLevel(state) here,
			// so Level stays 1 for the entire countdown.
		}


		private void SimulateRunning(JumpsOnlineRoomState state, float dt)
		{
			if (state.Players.Count == 0)
				return;

			state.ElapsedSinceRoundStart += dt;
			state.ElapsedSinceScrollStart += dt;

			// 1) Tick per-player power-up timers
			TickPowerupTimers(state, dt);

			// 2) Base scroll speed (same formula as before)
			state.ScrollSpeed = JumpsEngine.BaseScrollSpeed +
								JumpsEngine.ScrollAccel * state.ElapsedSinceScrollStart;

			// 3) SlowScroll: if ANY player has it active, everyone is slowed
			bool anySlowScroll =
				state.Players.Exists(p => p.SlowScrollActive && p.SlowScrollTimeRemaining > 0f);

			float effectiveScroll = state.ScrollSpeed;
			if (anySlowScroll)
			{
				effectiveScroll *= JumpsEngine.SlowScrollMultiplier;
			}

			// 4) Horizontal movement + jump / double-jump per player
			//    JumpBoost + SpeedBoost only affect the player who owns them.
			foreach (var p in state.Players)
			{
				if (!p.IsAlive)
					continue;

				var input = GetLatestInput(state, p.PlayerId);

				// Direction: -1, 0, +1
				float dir = 0f;
				if (input.Left && !input.Right) dir = -1f;
				else if (input.Right && !input.Left) dir = 1f;

				float baseMoveSpeed = ComputeMoveSpeed(state, effectiveScroll);
				float moveSpeed = baseMoveSpeed;

				// SpeedBoost only for this player
				if (p.SpeedBoostActive && p.SpeedBoostTimeRemaining > 0f)
				{
					moveSpeed *= JumpsEngine.SpeedBoostMultiplier;
				}

				p.X += dir * moveSpeed * dt;
				if (p.X < 0f) p.X = 0f;
				if (p.X > JumpsEngine.WorldWidth - p.Width)
					p.X = JumpsEngine.WorldWidth - p.Width;

				// ─────────────────────────────────────
				// Jump input: edge + variable jump
				// ─────────────────────────────────────
				bool prevJumpHeld = GetLastJumpHeld(state, p.PlayerId);
				bool justPressedJump = input.JumpHeld && !prevJumpHeld;
				bool justReleasedJump = !input.JumpHeld && prevJumpHeld;
				SetLastJumpHeld(state, p.PlayerId, input.JumpHeld);

				// Jump / Double-jump using "just pressed"
				if (justPressedJump && p.IsGrounded)
				{
					// NEW: hold Down + Jump to drop through
					if (input.Down && p.CurrentPlatform != null)
					{
						StartDropThrough(p);
					}
					else
					{
						StartJump(state, p);
					}
				}
				else if (justPressedJump && !p.IsGrounded &&
						 p.DoubleJumpActive && p.DoubleJumpTimeRemaining > 0f &&
						 p.AirJumpsRemaining > 0)
				{
					UseAirJump(state, p);
				}


				// Variable jump height ("jump cut") – same feel as offline:
				// if you release while still going up and we haven't cut yet,
				// reduce upward velocity so you fall sooner (short hop).
				if (justReleasedJump && p.VY < 0f && !p.JumpCutApplied)
				{
					// If you already have JumpsEngine.JumpCutFactor, use that.
					// Otherwise pick a factor like 0.35f for a nice short hop.
					const float jumpCutFactor = 0.35f;
					p.VY *= jumpCutFactor;
					p.JumpCutApplied = true;
				}
			}

			// 5) Apply vertical scroll to world + players
			float scrollDelta = effectiveScroll * dt;
			state.GroundY += scrollDelta;

			foreach (var plat in state.Platforms)
				plat.Y += scrollDelta;

			foreach (var p in state.Players)
				p.Y += scrollDelta;

			// 6) Gravity, landing, death
			foreach (var p in state.Players)
			{
				if (!p.IsAlive)
					continue;

				float prevY = p.Y;
				float prevBottom = prevY + p.Height;

				p.VY += JumpsEngine.Gravity * dt;
				p.Y += p.VY * dt;

				if (p.VY >= 0f)
					p.IsJumping = false;

				float bottom = p.Y + p.Height;

				if (p.VY > 0f)
				{
					TryLandOnPlatform(state, p, prevBottom, bottom);
				}

				// Death if we fall too far below the screen
				if (bottom > JumpsEngine.WorldHeight + JumpsEngine.DeathMargin)
				{
					if (p.IsAlive)
					{
						p.IsAlive = false;
						state.AlivePlayerCount--;
					}
				}
			}

			UpdateMagnetPulls(state, dt);

			// 7) Pickup collisions
			foreach (var p in state.Players)
			{
				if (!p.IsAlive)
					continue;

				CheckPickupCollisions(state, p);
			}

			// 8) Recycle / spawn platform rows
			RecycleAndSpawnRows(state);

			// 9) Level progression based on scroll speed
			UpdateLevel(state);

			// 10) End of round
			if (state.AlivePlayerCount <= 0 && state.Phase == JumpsOnlinePhase.Running)
			{
				state.Phase = JumpsOnlinePhase.Finished;
				ComputeResults(state);
			}
		}
		private void StartDropThrough(JumpsOnlinePlayerRuntime p)
		{
			if (!p.IsGrounded || p.CurrentPlatform == null)
				return;

			p.HasStarted = true;

			p.IsGrounded = false;
			p.IsJumping = false;
			p.JumpCutApplied = false;

			// Ignore this platform for a short time
			p.DropThroughPlatform = p.CurrentPlatform;
			p.DropThroughTimer = JumpsEngine.DropThroughIgnoreTime;
			p.CurrentPlatform = null;

			// Cancel any upward motion and kick downward a bit (same vibe as offline)
			if (p.VY < 0f)
				p.VY = 0f;

			p.VY += JumpsEngine.DropThroughKickSpeed;
		}



		// ─────────────────────────────────────────────
		// Input helpers
		// ─────────────────────────────────────────────

		private JumpsOnlineInputPayload GetLatestInput(JumpsOnlineRoomState state, string playerId)
		{
			string key = $"{state.RoomCode}:{playerId}";
			if (_latestInputs.TryGetValue(key, out var input))
				return input;

			return new JumpsOnlineInputPayload();
		}

		private float ComputeMoveSpeed(JumpsOnlineRoomState state, float effectiveScrollSpeed)
		{
			// Roughly mirrors JumpsEngine.ComputeMoveSpeed
			float rawFactor = effectiveScrollSpeed / JumpsEngine.BaseScrollSpeed;

			float t = state.ElapsedSinceRoundStart / JumpsEngine.WarmupDuration;
			if (t < 0f) t = 0f;
			if (t > 1f) t = 1f;

			float baseFactor = JumpsEngine.StartSpeedFactor +
				(1f - JumpsEngine.StartSpeedFactor) * t;

			float difficultyFactor = 1f +
				(MathF.Sqrt(rawFactor) - 1f) * JumpsEngine.HorizontalScale;

			float speedFactor = baseFactor * difficultyFactor;
			if (speedFactor < JumpsEngine.StartSpeedFactor)
				speedFactor = JumpsEngine.StartSpeedFactor;

			float currentMoveSpeed = JumpsEngine.MoveSpeedBase * speedFactor;
			return currentMoveSpeed;
		}

		private void StartJump(JumpsOnlineRoomState state, JumpsOnlinePlayerRuntime p)
		{
			if (!p.IsGrounded)
				return;

			p.HasStarted = true;
			p.IsGrounded = false;
			p.IsJumping = true;
			p.JumpCutApplied = false;

			float jumpVelocity = GetJumpVelocityForCurrentLevel(state.Level);

			// JumpBoost only for this player
			if (p.JumpBoostActive && p.JumpBoostTimeRemaining > 0f)
			{
				jumpVelocity *= JumpsEngine.JumpBoostMultiplier;
			}

			p.VY = jumpVelocity;
		}
		private void UseAirJump(JumpsOnlineRoomState state, JumpsOnlinePlayerRuntime p)
		{
			if (!p.DoubleJumpActive || p.DoubleJumpTimeRemaining <= 0f || p.AirJumpsRemaining <= 0)
				return;

			p.HasStarted = true;
			p.IsGrounded = false;
			p.IsJumping = true;
			p.JumpCutApplied = false;

			float jumpVelocity = GetJumpVelocityForCurrentLevel(state.Level);

			if (p.JumpBoostActive && p.JumpBoostTimeRemaining > 0f)
			{
				jumpVelocity *= JumpsEngine.JumpBoostMultiplier;
			}

			p.VY = jumpVelocity;
			p.AirJumpsRemaining--;
		}

		private void UpdateMagnetPulls(JumpsOnlineRoomState state, float dt)
		{
			float magnetRadius = JumpsEngine.MagnetRadiusWorld;
			float magnetRadiusSq = magnetRadius * magnetRadius;

			foreach (var plat in state.Platforms)
			{
				var pickup = plat.Pickup;
				if (pickup == null || pickup.Collected)
					continue;

				// 🔴 IMPORTANT: only coins are affected by magnet
				if (pickup.Type != JumpsOnlinePickupType.Coin)
					continue;

				// Find nearest magnet-active player in range
				JumpsOnlinePlayerRuntime? targetPlayer = null;
				float bestDistSq = float.MaxValue;

				// Current coin center (either world position, or default above platform)
				float startX = (pickup.IsMagnetPulling && (pickup.WorldX != 0f || pickup.WorldY != 0f))
					? pickup.WorldX
					: (plat.X + plat.Width / 2f);

				float startY = (pickup.IsMagnetPulling && (pickup.WorldX != 0f || pickup.WorldY != 0f))
					? pickup.WorldY
					: (plat.Y - JumpsEngine.PickupRadius - 2f);

				foreach (var player in state.Players)
				{
					if (!player.IsAlive)
						continue;

					if (!player.MagnetActive || player.MagnetTimeRemaining <= 0f)
						continue;

					float px = player.X + player.Width / 2f;
					float py = player.Y + player.Height / 2f;

					float dx = px - startX;
					float dy = py - startY;
					float distSq = dx * dx + dy * dy;

					if (distSq <= magnetRadiusSq && distSq < bestDistSq)
					{
						bestDistSq = distSq;
						targetPlayer = player;
					}
				}

				if (targetPlayer == null)
				{
					// No magnet player near this coin – stop pulling and let it sit on the platform.
					pickup.IsMagnetPulling = false;
					pickup.WorldX = 0f;
					pickup.WorldY = 0f;
					continue;
				}

				// We have a magnet target – move coin toward that player
				float targetX = targetPlayer.X + targetPlayer.Width / 2f;
				float targetY = targetPlayer.Y + targetPlayer.Height / 2f;

				float vx = targetX - startX;
				float vy = targetY - startY;
				float dist = MathF.Sqrt(vx * vx + vy * vy);

				pickup.IsMagnetPulling = true;

				if (dist < 0.001f)
				{
					pickup.WorldX = targetX;
					pickup.WorldY = targetY;
				}
				else
				{
					float maxStep = MagnetPullSpeed * dt;
					if (maxStep >= dist)
					{
						// Snap directly to player
						pickup.WorldX = targetX;
						pickup.WorldY = targetY;
					}
					else
					{
						float t = maxStep / dist;
						pickup.WorldX = startX + vx * t;
						pickup.WorldY = startY + vy * t;
					}
				}
			}
		}



		private float GetJumpVelocityForCurrentLevel(int level)
		{
			float vMag = -JumpsEngine.BaseJumpVelocity;
			float baseHeight = (vMag * vMag) / (2f * JumpsEngine.Gravity);

			int extraPlatforms = Math.Max(0, (level - 1) / 5);
			const int MaxExtraPlatforms = 4;
			if (extraPlatforms > MaxExtraPlatforms)
				extraPlatforms = MaxExtraPlatforms;

			float targetHeight = baseHeight + extraPlatforms * JumpsEngine.RowSpacing;
			float scale = MathF.Sqrt(targetHeight / baseHeight);
			float scaledMag = vMag * scale;

			return -scaledMag;
		}

		// ─────────────────────────────────────────────
		// World generation & platforms
		// ─────────────────────────────────────────────

		private void InitWorld(JumpsOnlineRoomState state)
		{
			state.Phase = JumpsOnlinePhase.Lobby;
			state.CountdownSecondsRemaining = 0f;
			state.ElapsedSinceRoundStart = 0f;
			state.ElapsedSinceScrollStart = 0f;
			state.ScrollSpeed = 0f;
			state.Level = 1;
			state.GroundY = JumpsEngine.WorldHeight - 40f;
			state.BufferRows = 1;
			if (state.TotalColumns <= 0)
				state.TotalColumns = 3;

			state.Platforms.Clear();
			state.NextRowIndex = 0;

			float y = state.GroundY - JumpsEngine.RowSpacing;
			for (int i = 0; i < 10; i++)
			{
				GeneratePlatformsRow(state, y);
				y -= JumpsEngine.RowSpacing;
				state.NextRowIndex++;
			}

			state.AlivePlayerCount = state.Players.Count;
			state.ResultsCalculated = false;
			state.WinnerPlayerId = null;
			state.IsTie = false;
		}

		private void LayoutPlayersOnGround(JumpsOnlineRoomState state)
		{
			int count = state.Players.Count;
			if (count == 0)
				return;

			float segment = JumpsEngine.WorldWidth / (count + 1);

			// Pick “bottom-ish” platforms to use as spawn anchors
			// (rows close to the ground so it feels like level 1)
			float maxY = state.GroundY;
			float minY = state.GroundY - JumpsEngine.RowSpacing * 2f; // bottom 2 rows above ground

			_candidatePlatforms.Clear();
			for (int iPlat = 0; iPlat < state.Platforms.Count; iPlat++)
			{
				var plat = state.Platforms[iPlat];
				if (plat.Y <= maxY && plat.Y >= minY)
					_candidatePlatforms.Add(plat);
			}

			for (int i = 0; i < count; i++)
			{
				var p = state.Players[i];

				// Desired horizontal lane for this player
				float desiredCenterX = segment * (i + 1);

				JumpsOnlinePlatformRuntime? bestPlat = null;
				float bestDist = float.MaxValue;

				// Find platform whose center is closest to this lane
				for (int j = 0; j < _candidatePlatforms.Count; j++)
				{
					var plat = _candidatePlatforms[j];
					float platCenterX = plat.X + plat.Width / 2f;
					float dist = MathF.Abs(platCenterX - desiredCenterX);
					if (dist < bestDist)
					{
						bestDist = dist;
						bestPlat = plat;
					}
				}


				if (bestPlat != null)
				{
					// Snap player ONTO the platform
					float platCenterX = bestPlat.X + bestPlat.Width / 2f;

					p.X = platCenterX - JumpsEngine.PlayerSize / 2f;
					p.Y = bestPlat.Y - JumpsEngine.PlayerSize;

					p.CurrentPlatform = bestPlat;
					p.IsGrounded = true;
				}
				else
				{
					// Fallback: old behavior (stand on "ground")
					p.X = segment * (i + 1) - JumpsEngine.PlayerSize / 2f;
					p.Y = state.GroundY - JumpsEngine.PlayerSize;

					p.CurrentPlatform = null;
					p.IsGrounded = true;
				}

				p.VX = 0f;
				p.VY = 0f;
				p.HasStarted = false;
				p.IsAlive = true;
				p.Coins = 0;

				p.DropThroughPlatform = null;
				p.DropThroughTimer = 0f;
				p.AirJumpsRemaining = 0;

				p.JumpBoostActive = false;
				p.JumpBoostTimeRemaining = 0f;
				p.SpeedBoostActive = false;
				p.SpeedBoostTimeRemaining = 0f;
				p.MagnetActive = false;
				p.MagnetTimeRemaining = 0f;
				p.DoubleJumpActive = false;
				p.DoubleJumpTimeRemaining = 0f;
				p.SlowScrollActive = false;
				p.SlowScrollTimeRemaining = 0f;
			}

			state.AlivePlayerCount = count;
		}

		private void GeneratePlatformsRow(JumpsOnlineRoomState state, float y)
		{
			int columns = state.TotalColumns;
			if (columns <= 0) columns = 3;

			// 1 or 2 platforms
			int platformCount = state.Rng.Next(1, 3);

			// Pick 1–2 unique lanes without LINQ/shuffle allocations
			int laneA = state.Rng.Next(columns);
			int laneB = laneA;

			if (platformCount == 2)
			{
				// ensure unique
				while (laneB == laneA)
					laneB = state.Rng.Next(columns);
			}

			float laneWidth = JumpsEngine.WorldWidth / columns;

			SpawnLane(laneA);
			if (platformCount == 2) SpawnLane(laneB);

			void SpawnLane(int lane)
			{
				float centerX = laneWidth * (lane + 0.5f);
				float x = centerX - JumpsEngine.PlatformWidth / 2f;

				JumpsOnlinePickupRuntime? pickup = null;
				double roll = state.Rng.NextDouble();

				double c = JumpsEngine.CoinChance;
				double jb = JumpsEngine.JumpBoostChance;
				double sb = JumpsEngine.SpeedBoostChance;
				double mg = JumpsEngine.MagnetChance;
				double dj = JumpsEngine.DoubleJumpChance;
				double ss = JumpsEngine.SlowScrollChance;

				JumpsOnlinePickupRuntime NewPickup(JumpsOnlinePickupType type)
				{
					return new JumpsOnlinePickupRuntime
					{
						Type = type,
						Collected = false,
						IsMagnetPulling = false,
						WorldX = centerX,
						WorldY = y - JumpsEngine.PickupRadius - 2f
					};
				}

				if (roll < c)
					pickup = NewPickup(JumpsOnlinePickupType.Coin);
				else if (roll < c + jb)
					pickup = NewPickup(JumpsOnlinePickupType.JumpBoost);
				else if (roll < c + jb + sb)
					pickup = NewPickup(JumpsOnlinePickupType.SpeedBoost);
				else if (roll < c + jb + sb + mg)
					pickup = NewPickup(JumpsOnlinePickupType.Magnet);
				else if (roll < c + jb + sb + mg + dj)
					pickup = NewPickup(JumpsOnlinePickupType.DoubleJump);
				else if (roll < c + jb + sb + mg + dj + ss)
					pickup = NewPickup(JumpsOnlinePickupType.SlowScroll);

				state.Platforms.Add(new JumpsOnlinePlatformRuntime
				{
					RowIndex = state.NextRowIndex,
					X = x,
					Y = y,
					Width = JumpsEngine.PlatformWidth,
					Height = JumpsEngine.PlatformHeight,
					Pickup = pickup
				});
			}
		}



		// ─────────────────────────────────────────────
		// Power-up ticking helpers (per player)
		// ─────────────────────────────────────────────

		private static void TickPowerup(ref bool active, ref float timeRemaining, float dt)
		{
			if (!active)
				return;

			timeRemaining -= dt;
			if (timeRemaining <= 0f)
			{
				timeRemaining = 0f;
				active = false;
			}
		}

		private void TickPowerupTimers(JumpsOnlineRoomState state, float dt)
		{
			foreach (var p in state.Players)
			{
				// Jump boost
				if (p.JumpBoostActive)
				{
					p.JumpBoostTimeRemaining -= dt;
					if (p.JumpBoostTimeRemaining <= 0f)
					{
						p.JumpBoostTimeRemaining = 0f;
						p.JumpBoostActive = false;
					}
				}

				// Speed boost
				if (p.SpeedBoostActive)
				{
					p.SpeedBoostTimeRemaining -= dt;
					if (p.SpeedBoostTimeRemaining <= 0f)
					{
						p.SpeedBoostTimeRemaining = 0f;
						p.SpeedBoostActive = false;
					}
				}

				// Magnet
				if (p.MagnetActive)
				{
					p.MagnetTimeRemaining -= dt;
					if (p.MagnetTimeRemaining <= 0f)
					{
						p.MagnetTimeRemaining = 0f;
						p.MagnetActive = false;
					}
				}

				// Slow scroll
				if (p.SlowScrollActive)
				{
					p.SlowScrollTimeRemaining -= dt;
					if (p.SlowScrollTimeRemaining <= 0f)
					{
						p.SlowScrollTimeRemaining = 0f;
						p.SlowScrollActive = false;
					}
				}

				// Double jump
				if (p.DoubleJumpActive)
				{
					p.DoubleJumpTimeRemaining -= dt;
					if (p.DoubleJumpTimeRemaining <= 0f)
					{
						p.DoubleJumpTimeRemaining = 0f;
						p.DoubleJumpActive = false;
						p.AirJumpsRemaining = 0;
					}
				}
				else
				{
					p.AirJumpsRemaining = 0;
				}

				// NEW: drop-through timer
				if (p.DropThroughTimer > 0f)
				{
					p.DropThroughTimer -= dt;
					if (p.DropThroughTimer <= 0f)
					{
						p.DropThroughTimer = 0f;
						p.DropThroughPlatform = null;
					}
				}
			}
		}



		private void RecycleAndSpawnRows(JumpsOnlineRoomState state)
		{
			float bottomLimit = JumpsEngine.WorldHeight + JumpsEngine.RowSpacing * state.BufferRows;
			state.Platforms.RemoveAll(p => p.Y > bottomLimit);

			if (state.Platforms.Count == 0)
			{
				float y = state.GroundY - JumpsEngine.RowSpacing;
				for (int i = 0; i < 10; i++)
				{
					GeneratePlatformsRow(state, y);
					y -= JumpsEngine.RowSpacing;
					state.NextRowIndex++;
				}
				return;
			}

			float minY = float.MaxValue;
			for (int i = 0; i < state.Platforms.Count; i++)
			{
				float y = state.Platforms[i].Y;
				if (y < minY) minY = y;
			}


			while (minY > -JumpsEngine.RowSpacing * state.BufferRows)
			{
				float newRowY = minY - JumpsEngine.RowSpacing;
				GeneratePlatformsRow(state, newRowY);
				state.NextRowIndex++;
				minY = newRowY;
			}
		}

		private void TryLandOnPlatform(
			JumpsOnlineRoomState state,
			JumpsOnlinePlayerRuntime p,
			float prevBottom,
			float bottom)
		{
			foreach (var plat in state.Platforms)
			{
				// NEW: if we're currently dropping through this platform, ignore it
				if (p.DropThroughPlatform == plat && p.DropThroughTimer > 0f)
					continue;

				float platformTop = plat.Y;
				float playerCenterX = p.X + p.Width / 2f;

				bool passesVertically =
					prevBottom <= platformTop + JumpsEngine.LandingVerticalForgiveness &&
					bottom >= platformTop - JumpsEngine.LandingVerticalForgiveness;

				bool withinHorizontal =
					playerCenterX >= plat.X - JumpsEngine.LandingHorizontalForgiveness &&
					playerCenterX <= plat.X + plat.Width + JumpsEngine.LandingHorizontalForgiveness;

				if (!passesVertically || !withinHorizontal)
					continue;

				// Land
				p.Y = plat.Y - p.Height;
				p.VY = 0f;
				p.IsGrounded = true;
				p.IsJumping = false;

				p.JumpCutApplied = false;

				p.CurrentPlatform = plat;

				if (p.DoubleJumpActive && p.DoubleJumpTimeRemaining > 0f)
					p.AirJumpsRemaining = JumpsEngine.ExtraAirJumpsPerUse;
				else
					p.AirJumpsRemaining = 0;

				// v1: simple pickup check on landing
				CollectPickupIfOverlapping(p, plat);

				break;
			}
		}

		private void CheckPickupCollisions(JumpsOnlineRoomState state, JumpsOnlinePlayerRuntime player)
		{
			foreach (var plat in state.Platforms)
			{
				// Always use normal pickup radius; visual pull handles the rest
				CollectPickupIfOverlapping(player, plat, JumpsEngine.PickupRadius);
			}
		}
		private void CollectPickupIfOverlapping(
	JumpsOnlinePlayerRuntime player,
	JumpsOnlinePlatformRuntime plat)
		{
			// Just use the normal radius
			CollectPickupIfOverlapping(player, plat, JumpsEngine.PickupRadius);
		}

		private void CollectPickupIfOverlapping(
			JumpsOnlinePlayerRuntime player,
			JumpsOnlinePlatformRuntime plat,
			float radius)
		{
			var pickup = plat.Pickup;
			if (pickup == null || pickup.Collected)
				return;

			// Coin/powerup center (same position you use in drawing)
			float cx = plat.X + plat.Width / 2f;
			float cy = plat.Y - JumpsEngine.PickupRadius - 2f;

			if (pickup.IsMagnetPulling && (pickup.WorldX != 0f || pickup.WorldY != 0f))
			{
				cx = pickup.WorldX;
				cy = pickup.WorldY;
			}
			else
			{
				cx = plat.X + plat.Width / 2f;
				cy = plat.Y - JumpsEngine.PickupRadius - 2f;
			}

			// Player AABB
			float left = player.X;
			float right = player.X + player.Width;
			float top = player.Y;
			float bottom = player.Y + player.Height;

			// Nearest point on the player rect to the coin center
			float nearestX = MathF.Max(left, MathF.Min(cx, right));
			float nearestY = MathF.Max(top, MathF.Min(cy, bottom));

			float dx = cx - nearestX;
			float dy = cy - nearestY;

			if (dx * dx + dy * dy <= radius * radius)
			{
				pickup.Collected = true;
				ApplyPickupEffect(player, pickup);
			}
		}
		private bool GetLastJumpHeld(JumpsOnlineRoomState state, string playerId)
		{
			string key = $"{state.RoomCode}:{playerId}";
			return _lastJumpHeld.TryGetValue(key, out var last) && last;
		}

		private void SetLastJumpHeld(JumpsOnlineRoomState state, string playerId, bool current)
		{
			string key = $"{state.RoomCode}:{playerId}";
			_lastJumpHeld[key] = current;
		}

		private void ApplyPickupEffect(JumpsOnlinePlayerRuntime player, JumpsOnlinePickupRuntime pickup)
		{
			switch (pickup.Type)
			{
				case JumpsOnlinePickupType.Coin:
					// Only coins give points (matches offline JumpsEngine)
					player.Coins++;
					break;

				case JumpsOnlinePickupType.JumpBoost:
					player.JumpBoostActive = true;
					player.JumpBoostTimeRemaining += JumpsEngine.PowerupDuration;
					break;

				case JumpsOnlinePickupType.SpeedBoost:
					player.SpeedBoostActive = true;
					player.SpeedBoostTimeRemaining += JumpsEngine.PowerupDuration;
					break;

				case JumpsOnlinePickupType.Magnet:
					player.MagnetActive = true;
					player.MagnetTimeRemaining += JumpsEngine.PowerupDuration;
					break;

				case JumpsOnlinePickupType.DoubleJump:
					if (player.DoubleJumpActive)
					{
						player.DoubleJumpTimeRemaining += JumpsEngine.PowerupDuration;
					}
					else
					{
						player.DoubleJumpActive = true;
						player.DoubleJumpTimeRemaining = JumpsEngine.PowerupDuration;
					}

					player.AirJumpsRemaining = JumpsEngine.ExtraAirJumpsPerUse;
					break;

				case JumpsOnlinePickupType.SlowScroll:
					// This player shows the ring…
					player.SlowScrollActive = true;
					player.SlowScrollTimeRemaining += JumpsEngine.PowerupDuration;
					// …but in SimulateRunning we slow scroll for EVERYONE if ANY player has it.
					break;
			}
		}



		private void UpdateLevel(JumpsOnlineRoomState state)
		{
			int targetLevel = 1 + (int)(state.ScrollSpeed / 50f);
			if (targetLevel < 1)
				targetLevel = 1;

			if (targetLevel <= state.Level)
				return;

			state.Level = targetLevel;

			// For now, just keep total columns tied to player count
			state.TotalColumns = Math.Max(3, state.Players.Count * 3);
		}

		private void ComputeResults(JumpsOnlineRoomState state)
		{
			if (state.ResultsCalculated)
				return;

			state.ResultsCalculated = true;

			if (state.Players.Count == 0)
			{
				state.WinnerPlayerId = null;
				state.IsTie = false;
				return;
			}

			int maxCoins = state.Players.Max(p => p.Coins);
			var top = state.Players.Where(p => p.Coins == maxCoins).ToList();

			if (top.Count == 1)
			{
				state.WinnerPlayerId = top[0].PlayerId;
				state.IsTie = false;
			}
			else
			{
				state.WinnerPlayerId = null;
				state.IsTie = true;
			}
		}

		// ─────────────────────────────────────────────
		// Message handling
		// ─────────────────────────────────────────────

		public override async Task HandleMessageAsync(HubMessage msg, ClientConnection client)
		{
			switch (msg.MessageType)
			{
				case "JumpsOnlineInput":
					HandleInputMessage(msg, client);
					break;

				case "JumpsOnlineStartRequest":
					HandleStartRequest(msg, client);
					break;

				case "JumpsOnlineRestartRequest":
					await HandleRestartRequest(msg, client);
					break;
			}
		}

		private void HandleInputMessage(HubMessage msg, ClientConnection client)
		{
			if (string.IsNullOrEmpty(client.RoomCode) || string.IsNullOrEmpty(client.PlayerId))
				return;

			JumpsOnlineInputPayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<JumpsOnlineInputPayload>(msg.PayloadJson);
			}
			catch
			{
				return;
			}
			if (payload == null) return;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(client.RoomCode, out var state))
					return;

				if (!state.PlayersById.ContainsKey(client.PlayerId))
					return;

				string key = $"{state.RoomCode}:{client.PlayerId}";
				_latestInputs[key] = payload;
			}
		}

		private void HandleStartRequest(HubMessage msg, ClientConnection client)
		{
			if (string.IsNullOrEmpty(client.RoomCode) || string.IsNullOrEmpty(client.PlayerId))
				return;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(client.RoomCode, out var state))
					return;

				// Only P1 can start
				if (client.PlayerId != "P1")
					return;

				if (state.Phase == JumpsOnlinePhase.Lobby ||
					state.Phase == JumpsOnlinePhase.Finished)
				{
					state.Phase = JumpsOnlinePhase.Countdown;
					state.CountdownSecondsRemaining = CountdownDuration;
					state.ElapsedSinceRoundStart = 0f;
					state.ElapsedSinceScrollStart = 0f;

					// Reset players on the ground before countdown starts
					LayoutPlayersOnGround(state);
				}
			}
		}

		private async Task HandleRestartRequest(HubMessage msg, ClientConnection client)
		{
			if (string.IsNullOrEmpty(client.RoomCode))
				return;

			Room? room;
			lock (_syncLock)
			{
				room = _roomManager.GetRoom(client.RoomCode);
			}

			if (room != null)
			{
				await RestartRoomAsync(room, client);
			}
		}

		// ─────────────────────────────────────────────
		// Snapshot → HubMessage
		// ─────────────────────────────────────────────

		protected override HubMessage CreateStateMessage(JumpsOnlineRoomState state)
		{
			var snapshot = new JumpsOnlineSnapshotPayload
			{
				RoomCode = state.RoomCode,
				Phase = state.Phase,
				CountdownSecondsRemaining = state.CountdownSecondsRemaining,
				Level = state.Level,
				ScrollSpeed = state.ScrollSpeed,
				WinnerPlayerId = state.WinnerPlayerId,
				IsTie = state.IsTie
			};

			// Players
			foreach (var p in state.Players)
			{
				snapshot.Players.Add(new JumpsOnlinePlayerStateDto
				{
					PlayerId = p.PlayerId,
					PlayerIndex = p.PlayerIndex,
					X = p.X,
					Y = p.Y,
					IsAlive = p.IsAlive,
					Coins = p.Coins,

					// Powerups not used yet, but wired for future
					JumpBoostActive = p.JumpBoostActive,
					JumpBoostTimeRemaining = p.JumpBoostTimeRemaining,

					SpeedBoostActive = p.SpeedBoostActive,
					SpeedBoostTimeRemaining = p.SpeedBoostTimeRemaining,

					MagnetActive = p.MagnetActive,
					MagnetTimeRemaining = p.MagnetTimeRemaining,

					DoubleJumpActive = p.DoubleJumpActive,
					DoubleJumpTimeRemaining = p.DoubleJumpTimeRemaining,

					SlowScrollActive = p.SlowScrollActive,
					SlowScrollTimeRemaining = p.SlowScrollTimeRemaining
				});
			}

			// Platforms
			foreach (var plat in state.Platforms)
			{
				var platDto = new JumpsOnlinePlatformDto
				{
					X = plat.X,
					Y = plat.Y,
					Width = plat.Width,
					Height = plat.Height
				};

				if (plat.Pickup is { } pickup)
				{
					platDto.Pickup = new JumpsOnlinePickupDto
					{
						Type = pickup.Type,
						Collected = pickup.Collected,
						X = pickup.WorldX,   // v1: these will be 0 unless you later wire magnet positions
						Y = pickup.WorldY,
						IsMagnetPulling = pickup.IsMagnetPulling
					};
				}

				snapshot.Platforms.Add(platDto);
			}

			return new HubMessage
			{
				MessageType = "JumpsOnlineSnapshot",
				RoomCode = state.RoomCode,
				PayloadJson = JsonSerializer.Serialize(snapshot)
			};
		}
	}
}

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
		private const float CountdownDuration = 3f;

		// Latest input per player (key = "ROOM:PLAYER")
		private readonly Dictionary<string, JumpsOnlineInputPayload> _latestInputs = new();

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

		private void SimulateRunning(JumpsOnlineRoomState state, float dt)
		{
			if (state.Players.Count == 0)
				return;

			state.ElapsedSinceRoundStart += dt;
			state.ElapsedSinceScrollStart += dt;

			// Scroll speed (no slow-scroll power-up yet)
			state.ScrollSpeed = JumpsEngine.BaseScrollSpeed + JumpsEngine.ScrollAccel * state.ElapsedSinceScrollStart;
			float effectiveScroll = state.ScrollSpeed;

			// Horizontal movement + jump input
			foreach (var p in state.Players)
			{
				if (!p.IsAlive)
					continue;

				var input = GetLatestInput(state, p.PlayerId);

				// Direction: -1, 0, +1
				float dir = 0f;
				if (input.Left && !input.Right) dir = -1f;
				else if (input.Right && !input.Left) dir = 1f;

				float moveSpeed = ComputeMoveSpeed(state, effectiveScroll);

				p.X += dir * moveSpeed * dt;
				if (p.X < 0f) p.X = 0f;
				if (p.X > JumpsEngine.WorldWidth - p.Width)
					p.X = JumpsEngine.WorldWidth - p.Width;

				// Simple jump: if grounded and jump is held, start a jump.
				// (No buffering / jump-cut / double-jump yet to keep v1 simple.)
				if (input.JumpHeld && p.IsGrounded)
				{
					StartJump(state, p);
				}
			}

			// Apply vertical scroll to world + players
			float scrollDelta = effectiveScroll * dt;
			state.GroundY += scrollDelta;

			foreach (var plat in state.Platforms)
				plat.Y += scrollDelta;

			foreach (var p in state.Players)
				p.Y += scrollDelta;

			// Gravity, landing, death
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

			// Recycle / spawn platform rows
			RecycleAndSpawnRows(state);

			// Level progression based on scroll speed
			UpdateLevel(state);

			// Check end of round
			if (state.AlivePlayerCount <= 0 && state.Phase == JumpsOnlinePhase.Running)
			{
				state.Phase = JumpsOnlinePhase.Finished;
				ComputeResults(state);
			}
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
			p.VY = jumpVelocity;
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

			for (int i = 0; i < count; i++)
			{
				var p = state.Players[i];

				p.X = segment * (i + 1) - JumpsEngine.PlayerSize / 2f;
				p.Y = state.GroundY - JumpsEngine.PlayerSize;
				p.VX = 0f;
				p.VY = 0f;
				p.IsGrounded = true;
				p.HasStarted = false;
				p.IsAlive = true;
				p.Coins = 0;
			}

			state.AlivePlayerCount = count;
		}

		private void GeneratePlatformsRow(JumpsOnlineRoomState state, float y)
		{
			int columns = state.TotalColumns;
			if (columns <= 0) columns = 3;

			int platformCount = state.Rng.Next(1, 3); // 1 or 2 platforms per row

			var lanes = Enumerable.Range(0, columns)
				.OrderBy(_ => state.Rng.Next())
				.Take(platformCount);

			float laneWidth = JumpsEngine.WorldWidth / columns;

			foreach (int lane in lanes)
			{
				float centerX = laneWidth * (lane + 0.5f);
				float x = centerX - JumpsEngine.PlatformWidth / 2f;

				JumpsOnlinePickupRuntime? pickup = null;
				double roll = state.Rng.NextDouble();

				// v1: only coins for now
				if (roll < JumpsEngine.CoinChance)
				{
					pickup = new JumpsOnlinePickupRuntime
					{
						Type = JumpsOnlinePickupType.Coin,
						Collected = false
					};
				}

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

			float minY = state.Platforms.Min(p => p.Y);

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

				// v1: simple pickup check on landing
				HandlePickupCollision(p, plat);

				break;
			}
		}

		private void HandlePickupCollision(JumpsOnlinePlayerRuntime player, JumpsOnlinePlatformRuntime plat)
		{
			var pickup = plat.Pickup;
			if (pickup == null || pickup.Collected)
				return;

			// v1: only coins
			if (pickup.Type == JumpsOnlinePickupType.Coin)
			{
				pickup.Collected = true;
				player.Coins++;
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

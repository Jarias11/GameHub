// GameServer/WarGameHandler.cs
namespace GameServer
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text.Json;
	using System.Threading.Tasks;
	using GameContracts;
	using GameLogic;
	using GameLogic.War;
	using GameLogic.CardGames;

	/// <summary>
	/// Tick-based handler for the online War card game.
	/// - Waits for 2 players in the lobby
	/// - Either player can claim Left/Right
	/// - Once both slots are filled, starts the WarEngine
	/// - Ticks the engine and broadcasts WarState snapshots
	/// </summary>
	public sealed class WarGameHandler : TickableGameHandler<WarRoomState>
	{
		public WarGameHandler(
			RoomManager roomManager,
			List<ClientConnection> clients,
			object syncLock,
			Random rng,
			Func<ClientConnection, HubMessage, Task> sendAsync)
			: base(roomManager, clients, syncLock, rng, sendAsync)
		{
		}

		public override GameType GameType => GameType.War;

		public override bool HandlesMessageType(string messageType) =>
			messageType == "WarSelectSide" ||
			messageType == "WarReady" ||
			messageType == "WarShuffleRequest";

		// ─────────────────────────────────────────────────────────
		// Room lifecycle
		// ─────────────────────────────────────────────────────────

		public override async Task OnRoomCreated(Room room, ClientConnection owner)
		{
			await base.OnRoomCreated(room, owner);
			await BroadcastLobbyAsync(room.RoomCode);
		}

		public override async Task OnPlayerJoined(Room room, ClientConnection client)
		{
			await base.OnPlayerJoined(room, client);
			await BroadcastLobbyAsync(room.RoomCode);
		}

		protected override WarRoomState CreateRoomState(string roomCode) =>
			new WarRoomState(roomCode);

		// ─────────────────────────────────────────────────────────
		// Tick loop: advance engine & build WarState snapshot
		// ─────────────────────────────────────────────────────────

		protected override void UpdateState(WarRoomState state, float dtSeconds)
		{
			// Don't tick until we actually have a running game
			if (!state.GameStarted)
				return;

			state.Engine.Tick(dtSeconds);
		}

		protected override HubMessage CreateStateMessage(WarRoomState state)
		{
			var engine = state.Engine;

			var payload = new WarStatePayload
			{
				RoomCode = state.RoomCode,
				State = MapNetworkState(state),
				// This is a canonical "engine perspective".
				// Clients should use WarLobbyState + their PlayerId
				// to know whether they are Left or Right.
				LocalSide = WarSide.Left,

				LeftDeckCount = engine.LeftDeckCount,
				RightDeckCount = engine.RightDeckCount,
				CenterDeckCount = engine.CenterDeckCount,

				CountdownValue = engine.CountdownValue,
				WarFaceDownPlaced = engine.WarFaceDownPlaced,
				LastRoundWinner = MapNetworkWinner(state),

				HasDealCardInFlight = engine.HasDealCardInFlight,
				DealToLeftNext = engine.DealToLeftNext,
				DealProgress = engine.DealProgress,

				BattlePhase = (int)engine.CurrentBattlePhase,
				BattleAnimProgress = engine.BattleAnimProgress,

				LeftFaceUp = ToCardDto(engine.LeftFaceUp),
				RightFaceUp = ToCardDto(engine.RightFaceUp),

				ShuffleUnlocked = engine.ShuffleUnlocked
			};

			return new HubMessage
			{
				MessageType = "WarState",
				RoomCode = state.RoomCode,
				PlayerId = "", // filled per-client by the hub if needed
				PayloadJson = JsonSerializer.Serialize(payload)
			};
		}

		// ─────────────────────────────────────────────────────────
		// Game-specific messages
		// ─────────────────────────────────────────────────────────

		public override async Task HandleMessageAsync(HubMessage msg, ClientConnection client)
		{
			if (client.RoomCode == null)
				return;

			var roomCode = client.RoomCode;

			switch (msg.MessageType)
			{
				case "WarSelectSide":
					await HandleSelectSideAsync(roomCode, msg, client);
					break;

				case "WarReady":
					await HandleReadyAsync(roomCode, msg, client);
					break;

				case "WarShuffleRequest":
					await HandleShuffleAsync(roomCode, msg, client);
					break;
			}
		}

		private async Task HandleSelectSideAsync(string roomCode, HubMessage msg, ClientConnection client)
		{
			WarSelectSidePayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<WarSelectSidePayload>(msg.PayloadJson);
			}
			catch
			{
				return;
			}
			if (payload == null) return;

			// Fallback if client didn't fill PlayerId in payload
			if (string.IsNullOrEmpty(payload.PlayerId))
				payload.PlayerId = client.PlayerId ?? "";

			WarLobbyStatePayload lobbySnapshot;
			List<ClientConnection> recipients;

			lock (_syncLock)
			{
				var state = EnsureRoomState(roomCode);

				if (!string.Equals(client.RoomCode, roomCode, StringComparison.Ordinal))
				{
					// Safety: ignore messages for rooms client isn't in
					return;
				}

				if (!state.GameStarted)
				{
					// 🔹 If this player already has ANY side, ignore further select requests.
					bool playerAlreadyHasSide =
						state.LeftPlayerId == payload.PlayerId ||
						state.RightPlayerId == payload.PlayerId;

					if (playerAlreadyHasSide)
					{
						// Just rebuild and broadcast lobby; don't change assignments.
						goto buildLobby;
					}

					if (payload.Side == WarSide.Left)
					{
						// Claim Left only if empty
						if (state.LeftPlayerId == null)
							state.LeftPlayerId = payload.PlayerId;
					}
					else // Right
					{
						if (state.RightPlayerId == null)
							state.RightPlayerId = payload.PlayerId;
					}

					// Once both slots are filled, start the game
					if (state.HasTwoPlayers && !state.GameStarted)
					{
						state.GameStarted = true;

						// Reset and start dealing.
						// We treat "engine player" as LEFT for canonical view.
						state.Engine.ResetGame();
						state.Engine.SelectSide(playerOnLeft: true);
					}
				}

			buildLobby:
				lobbySnapshot = null;
				recipients = GetRoomClients(roomCode);
				int connected = recipients.Count;

				lobbySnapshot = new WarLobbyStatePayload
				{
					RoomCode = roomCode,
					LeftPlayerId = state.LeftPlayerId,
					RightPlayerId = state.RightPlayerId,
					GameStarted = state.GameStarted,
					ConnectedPlayers = connected
				};
			}

			await BroadcastLobbyAsync(roomCode, lobbySnapshot, recipients);
		}

		private Task HandleReadyAsync(string roomCode, HubMessage msg, ClientConnection client)
		{
			WarReadyPayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<WarReadyPayload>(msg.PayloadJson);
			}
			catch
			{
				return Task.CompletedTask;
			}
			if (payload == null) return Task.CompletedTask;

			if (string.IsNullOrEmpty(payload.PlayerId))
				payload.PlayerId = client.PlayerId ?? "";

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(roomCode, out var state))
					return Task.CompletedTask;

				if (!state.GameStarted)
					return Task.CompletedTask;

				// Map which side this player is on.
				bool isLeft = state.IsLeftPlayer(payload.PlayerId);
				bool isRight = state.IsRightPlayer(payload.PlayerId);

				if (!isLeft && !isRight)
					return Task.CompletedTask;

				// Important:
				// - Engine.OnPlayerDeckClicked controls "player" ready
				// - Engine.SetOpponentReady controls "opponent" ready
				// We treat LEFT as the engine "player" side.
				if (isLeft)
				{
					state.Engine.OnPlayerDeckClicked();
				}
				else if (isRight)
				{
					state.Engine.SetOpponentReady(true);
				}
			}

			return Task.CompletedTask;
		}

		private Task HandleShuffleAsync(string roomCode, HubMessage msg, ClientConnection client)
		{
			WarShuffleRequestPayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<WarShuffleRequestPayload>(msg.PayloadJson);
			}
			catch
			{
				return Task.CompletedTask;
			}
			if (payload == null) return Task.CompletedTask;

			if (string.IsNullOrEmpty(payload.PlayerId))
				payload.PlayerId = client.PlayerId ?? "";

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(roomCode, out var state))
					return Task.CompletedTask;

				if (!state.GameStarted)
					return Task.CompletedTask;

				// NOTE:
				// WarEngine currently exposes TryShufflePlayerDeck() which shuffles
				// the "player" side based on its internal _playerOnLeft flag.
				// Here we treat LEFT as the engine's "player" side, so for now
				// only the Left player can actually shuffle.
				//
				// If you want both sides to shuffle independently, we can extend
				// WarEngine with explicit TryShuffleLeft/Right methods.
				if (state.IsLeftPlayer(payload.PlayerId))
				{
					state.Engine.TryShufflePlayerDeck();
				}
			}

			return Task.CompletedTask;
		}

		// ─────────────────────────────────────────────────────────
		// Restart & disconnect handling
		// ─────────────────────────────────────────────────────────

		public override async Task RestartRoomAsync(Room room, ClientConnection? initiator)
		{
			WarLobbyStatePayload lobbySnapshot;
			List<ClientConnection> recipients;

			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);

				// Reset engine
				state.Engine.ResetGame();

				if (state.HasTwoPlayers)
				{
					state.GameStarted = true;
					// Again, treat LEFT as the engine "player" perspective.
					state.Engine.SelectSide(playerOnLeft: true);
				}
				else
				{
					state.GameStarted = false;
				}

				recipients = GetRoomClients(room.RoomCode);
				int connected = recipients.Count;

				lobbySnapshot = new WarLobbyStatePayload
				{
					RoomCode = room.RoomCode,
					LeftPlayerId = state.LeftPlayerId,
					RightPlayerId = state.RightPlayerId,
					GameStarted = state.GameStarted,
					ConnectedPlayers = connected
				};
			}

			await BroadcastLobbyAsync(room.RoomCode, lobbySnapshot, recipients);
		}

		public override void OnClientDisconnected(ClientConnection client)
		{
			string? roomCode = client.RoomCode;
			string? playerId = client.PlayerId;

			WarLobbyStatePayload? lobbySnapshot = null;
			List<ClientConnection>? recipients = null;

			lock (_syncLock)
			{
				base.OnClientDisconnected(client);

				if (roomCode == null || playerId == null)
					return;

				if (!_rooms.TryGetValue(roomCode, out var state))
					return;

				bool changed = false;

				if (state.LeftPlayerId == playerId)
				{
					state.LeftPlayerId = null;
					changed = true;
				}

				if (state.RightPlayerId == playerId)
				{
					state.RightPlayerId = null;
					changed = true;
				}

				if (changed)
				{
					// Drop back to lobby and reset engine.
					state.Engine.ResetGame();
					state.GameStarted = false;

					recipients = GetRoomClients(roomCode);
					int connected = recipients.Count;

					lobbySnapshot = new WarLobbyStatePayload
					{
						RoomCode = roomCode,
						LeftPlayerId = state.LeftPlayerId,
						RightPlayerId = state.RightPlayerId,
						GameStarted = state.GameStarted,
						ConnectedPlayers = connected
					};
				}

			}

			// Fire-and-forget lobby update; we can't await here.
			if (lobbySnapshot != null && recipients != null)
			{
				var json = JsonSerializer.Serialize(lobbySnapshot);
				foreach (var c in recipients)
				{
					var msg = new HubMessage
					{
						MessageType = "WarLobbyState",
						RoomCode = lobbySnapshot.RoomCode,
						PlayerId = c.PlayerId ?? "",
						PayloadJson = json
					};

					_ = _sendAsync(c, msg);
				}
			}
		}

		// ─────────────────────────────────────────────────────────
		// Mapping helpers
		// ─────────────────────────────────────────────────────────

		private static WarNetworkState MapNetworkState(WarRoomState state)
		{
			if (!state.GameStarted)
				return WarNetworkState.Lobby;

			return state.Engine.State switch
			{
				WarEngine.WarState.Dealing => WarNetworkState.Dealing,
				WarEngine.WarState.WaitingForPlayerClick => WarNetworkState.WaitingForClick,
				WarEngine.WarState.Countdown => WarNetworkState.Countdown,
				WarEngine.WarState.ShowingBattle => WarNetworkState.ShowingBattle,
				WarEngine.WarState.WarFaceDown => WarNetworkState.WarFaceDown,
				WarEngine.WarState.RoundResult => WarNetworkState.RoundResult,
				WarEngine.WarState.GameOver => WarNetworkState.GameOver,
				_ => WarNetworkState.Lobby
			};
		}

		private static WarNetworkRoundWinner MapNetworkWinner(WarRoomState state)
		{
			var engine = state.Engine;

			if (!state.GameStarted)
				return WarNetworkRoundWinner.None;

			// Game over: decide by deck counts
			if (engine.State == WarEngine.WarState.GameOver)
			{
				if (engine.LeftDeckCount > engine.RightDeckCount)
					return WarNetworkRoundWinner.Left;
				if (engine.RightDeckCount > engine.LeftDeckCount)
					return WarNetworkRoundWinner.Right;
				return WarNetworkRoundWinner.Tie;
			}

			// Round result (but not game over)
			if (engine.State == WarEngine.WarState.RoundResult)
			{
				if (engine.LastRoundWinner == WarEngine.RoundWinner.Tie)
					return WarNetworkRoundWinner.Tie;

				if (engine.LastRoundWinner == WarEngine.RoundWinner.None)
					return WarNetworkRoundWinner.None;

				// Non-tie winner: use PendingWinnerIsLeft
				return engine.PendingWinnerIsLeft
					? WarNetworkRoundWinner.Left
					: WarNetworkRoundWinner.Right;
			}

			return WarNetworkRoundWinner.None;
		}

		private static WarCardDto? ToCardDto(Card? card)
		{
			if (!card.HasValue)
				return null;

			return new WarCardDto
			{
				Rank = (int)card.Value.Rank,
				Suit = (int)card.Value.Suit
			};
		}

		// ─────────────────────────────────────────────────────────
		// Lobby broadcast helper
		// ─────────────────────────────────────────────────────────

		private async Task BroadcastLobbyAsync(string roomCode)
		{
			WarLobbyStatePayload payload;
			List<ClientConnection> recipients;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(roomCode, out var state))
					return;

				recipients = GetRoomClients(roomCode);
				int connected = recipients.Count;

				payload = new WarLobbyStatePayload
				{
					RoomCode = roomCode,
					LeftPlayerId = state.LeftPlayerId,
					RightPlayerId = state.RightPlayerId,
					GameStarted = state.GameStarted,
					ConnectedPlayers = connected
				};
			}

			await BroadcastLobbyAsync(roomCode, payload, recipients);
		}

		private async Task BroadcastLobbyAsync(
			string roomCode,
			WarLobbyStatePayload payload,
			List<ClientConnection> recipients)
		{
			var json = JsonSerializer.Serialize(payload);

			foreach (var c in recipients)
			{
				var msg = new HubMessage
				{
					MessageType = "WarLobbyState",
					RoomCode = roomCode,
					PlayerId = c.PlayerId ?? "",
					PayloadJson = json
				};

				await _sendAsync(c, msg);
			}
		}
	}
}

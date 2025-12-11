using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GameContracts;
using GameLogic;
using GameLogic.Blackjack;

namespace GameServer
{
	/// <summary>
	/// Tick-based handler for online Blackjack.
	/// - Up to 4 players per room.
	/// - Host (P1) can start a round with 1–4 seated players.
	/// - Server holds the BlackjackEngine; clients receive snapshots.
	/// </summary>
	public sealed class BlackjackGameHandler : TickableGameHandler<BlackjackRoomState>
	{
		public BlackjackGameHandler(
			RoomManager roomManager,
			List<ClientConnection> clients,
			object syncLock,
			Random rng,
			Func<ClientConnection, HubMessage, Task> sendAsync)
			: base(roomManager, clients, syncLock, rng, sendAsync)
		{
		}

		public override GameType GameType => GameType.Blackjack;

		public override bool HandlesMessageType(string messageType) =>
			messageType == "BlackjackStartRequest" ||
			messageType == "BlackjackAction";

		// ─────────────────────────────────────────────────────────
		// Room lifecycle
		// ─────────────────────────────────────────────────────────

		protected override BlackjackRoomState CreateRoomState(string roomCode) =>
			new BlackjackRoomState(roomCode, _rng);

		public override async Task OnRoomCreated(Room room, ClientConnection owner)
		{
			await base.OnRoomCreated(room, owner);

			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);
				// Seat P1 as first seat
				if (!string.IsNullOrEmpty(owner.PlayerId))
				{
					state.GetOrAssignSeatForPlayer(owner.PlayerId);
					state.Engine.EnsurePlayer(owner.PlayerId);
				}
			}

			await BroadcastSnapshotAsync(room.RoomCode);
		}

		public override async Task OnPlayerJoined(Room room, ClientConnection client)
		{
			await base.OnPlayerJoined(room, client);

			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);

				if (!string.IsNullOrEmpty(client.PlayerId))
				{
					// Seat new player if there is still space
					if (state.SeatedCount < 4)
					{
						state.GetOrAssignSeatForPlayer(client.PlayerId);
						state.Engine.EnsurePlayer(client.PlayerId);
					}
					// else: player joins as spectator; still receives snapshots
				}
			}

			await BroadcastSnapshotAsync(room.RoomCode);
		}

		public override async Task RestartRoomAsync(Room room, ClientConnection? initiator)
		{
			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);
				// Put engine back to lobby-phase (no active round).
				state.Engine.DealerHand.ToList().Clear(); // optional; StartRound will reset
				// Simplest: just leave engine as-is; next StartRound call will reset.
			}

			await BroadcastSnapshotAsync(room.RoomCode);
		}

		public override void OnClientDisconnected(ClientConnection client)
		{
			string? roomCode = client.RoomCode;
			string? playerId = client.PlayerId;

			base.OnClientDisconnected(client);

			if (roomCode == null || playerId == null)
				return;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(roomCode, out var state))
					return;

				state.UnseatPlayer(playerId);
				state.Engine.RemovePlayer(playerId);
			}

			// Fire-and-forget snapshot update
			_ = BroadcastSnapshotAsync(roomCode);
		}

		// ─────────────────────────────────────────────────────────
		// Tick loop
		// ─────────────────────────────────────────────────────────

		protected override void UpdateState(BlackjackRoomState state, float dtSeconds)
		{
			// v1: no continuous ticking needed.
			// Later we could add delays/animations between steps.
		}

		protected override HubMessage CreateStateMessage(BlackjackRoomState state)
		{
			// Not used: we'll broadcast per-room manually via BroadcastSnapshotAsync,
			// similar to War / JumpsOnline. But having this override lets you plug
			// into generic tick sending if you want.
			throw new NotImplementedException();
		}

		// ─────────────────────────────────────────────────────────
		// Game messages
		// ─────────────────────────────────────────────────────────

		public override async Task HandleMessageAsync(HubMessage msg, ClientConnection client)
		{
			if (client.RoomCode == null)
				return;

			var roomCode = client.RoomCode;

			switch (msg.MessageType)
			{
				case "BlackjackStartRequest":
					await HandleStartRequestAsync(roomCode, msg, client);
					break;

				case "BlackjackAction":
					await HandleActionAsync(roomCode, msg, client);
					break;
			}
		}

		private async Task HandleStartRequestAsync(string roomCode, HubMessage msg, ClientConnection client)
		{
			BlackjackStartRequestPayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<BlackjackStartRequestPayload>(msg.PayloadJson);
			}
			catch
			{
				return;
			}
			if (payload == null) return;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(roomCode, out var state))
					return;

				// Host-check: only P1 can start, similar to JumpsOnline.
				// Generic hub treats the room creator as "P1".
				if (!string.Equals(client.PlayerId, "P1", StringComparison.OrdinalIgnoreCase))
				{
					return;
				}

				if (state.SeatedCount <= 0)
					return;

				state.Engine.StartRound();
			}

			await BroadcastSnapshotAsync(roomCode);
		}

		private async Task HandleActionAsync(string roomCode, HubMessage msg, ClientConnection client)
		{
			BlackjackActionPayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<BlackjackActionPayload>(msg.PayloadJson);
			}
			catch
			{
				return;
			}
			if (payload == null) return;

			if (string.IsNullOrEmpty(payload.PlayerId))
				payload.PlayerId = client.PlayerId ?? "";

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(roomCode, out var state))
					return;

				state.Engine.ApplyAction(payload.PlayerId, payload.Action);
			}

			await BroadcastSnapshotAsync(roomCode);
		}

		// ─────────────────────────────────────────────────────────
		// Snapshot serialization
		// ─────────────────────────────────────────────────────────

		private async Task BroadcastSnapshotAsync(string roomCode)
		{
			BlackjackSnapshotPayload payload;
			List<ClientConnection> recipients;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(roomCode, out var state))
					return;

				var engine = state.Engine;

				payload = new BlackjackSnapshotPayload
				{
					RoomCode = roomCode,
					Phase = engine.Phase,
					CurrentPlayerId = engine.CurrentPlayerId,
					DealerCards = BuildDealerCardsDto(engine),
					DealerVisibleValue = ComputeDealerVisibleValue(engine),
					DealerRevealed = engine.DealerRevealed,
					Players = BuildPlayersDto(state),
					RoundComplete = engine.Phase == BlackjackPhase.RoundResults
				};

				recipients = GetRoomClients(roomCode);
			}

			var json = JsonSerializer.Serialize(payload);

			foreach (var c in recipients)
			{
				var msg = new HubMessage
				{
					MessageType = "BlackjackSnapshot",
					RoomCode = roomCode,
					PlayerId = c.PlayerId ?? "",
					PayloadJson = json
				};

				await _sendAsync(c, msg);
			}
		}

		private static List<BlackjackCardDto> BuildDealerCardsDto(BlackjackEngine engine)
		{
			var result = new List<BlackjackCardDto>();

			var dealerHand = engine.DealerHand;
			bool revealed = engine.DealerRevealed;

			for (int i = 0; i < dealerHand.Count; i++)
			{
				var card = dealerHand[i];
				bool isHole = (i == 1 && !revealed); // second card face-down until reveal

				result.Add(new BlackjackCardDto
				{
					Rank = (int)card.Rank,
					Suit = (int)card.Suit,
					IsFaceDown = isHole
				});
			}

			return result;
		}

		private static int ComputeDealerVisibleValue(BlackjackEngine engine)
		{
			// When hole card is hidden, we can show just first card value or 0.
			if (!engine.DealerRevealed && engine.DealerHand.Count > 0)
			{
				var first = engine.DealerHand[0];
				return BlackjackEngine.ComputeHandValue(new[] { first });
			}

			return BlackjackEngine.ComputeHandValue(engine.DealerHand);
		}

		private static List<BlackjackPlayerStateDto> BuildPlayersDto(BlackjackRoomState state)
		{
			var enginePlayers = state.Engine.Players;
			var list = new List<BlackjackPlayerStateDto>();

			for (int seat = 0; seat < state.SeatPlayerIds.Length; seat++)
			{
				var playerId = state.SeatPlayerIds[seat];
				if (string.IsNullOrEmpty(playerId))
					continue;

				var ep = enginePlayers.FirstOrDefault(p => p.PlayerId == playerId);
				if (ep == null)
					continue;

				var dto = new BlackjackPlayerStateDto
				{
					PlayerId = playerId,
					SeatIndex = seat,
					IsConnected = true, // if seat filled, they're connected
					IsInRound = ep.IsInRound,
					HasStood = ep.HasStood,
					IsBust = ep.IsBust,
					HandValue = BlackjackEngine.ComputeHandValue(ep.Hand),
					Chips = ep.Chips,
					Bet = ep.Bet,
					Result = ep.Result,
					IsCurrentTurn = (state.Engine.CurrentPlayerId == playerId)
				};

				foreach (var card in ep.Hand)
				{
					dto.Cards.Add(new BlackjackCardDto
					{
						Rank = (int)card.Rank,
						Suit = (int)card.Suit,
						IsFaceDown = false
					});
				}

				list.Add(dto);
			}

			return list;
		}
	}
}

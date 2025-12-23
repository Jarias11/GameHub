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
	public sealed class BlackjackGameHandler : TurnBasedGameHandler<BlackjackRoomState>
	{
		private readonly Random _rng;

		public BlackjackGameHandler(
			RoomManager roomManager,
			List<ClientConnection> clients,
			object syncLock,
			Random rng,
			Func<ClientConnection, HubMessage, Task> sendAsync)
			: base(roomManager, clients, syncLock, sendAsync)
		{
			_rng = rng;
		}

		public override GameType GameType => GameType.Blackjack;

		public override bool HandlesMessageType(string messageType)
			=> messageType == "BlackjackStartRequest"
			|| messageType == "BlackjackNextRoundRequest"
			|| messageType == "BlackjackAction"
			|| messageType == "BlackjackSeatSelect"
			|| messageType == "BlackjackBetSubmit"
			|| messageType == "BlackjackBailout";

		protected override BlackjackRoomState CreateRoomState(string roomCode)
			=> new BlackjackRoomState(roomCode, _rng);

		public override async Task OnPlayerJoined(Room room, ClientConnection client)
		{
			BlackjackRoomState state;
			lock (_syncLock)
			{
				state = EnsureRoomState(room.RoomCode);

				// If round is active, mark as spectator until end
				if (state.IsRoundActive)
				{
					state.MarkSpectatorThisRound(client.PlayerId!);
				}
				else
				{
					// In lobby: they can choose a seat; no auto-seat
					// If you want auto-seat for now, you can do it here.
					// state.EnsureStartingChips(client.PlayerId!); // only when they become eligible
				}
			}

			await BroadcastSnapshotAsync(room.RoomCode);
		}
		public override async Task OnRoomCreated(Room room, ClientConnection client)
		{
			lock (_syncLock)
			{
				EnsureRoomState(room.RoomCode);
				// optional: you could EnsureStartingChips(client.PlayerId!) here
				// BUT you currently only give chips when seated, which is fine.
			}

			await BroadcastSnapshotAsync(room.RoomCode);
		}
		public override void OnClientDisconnected(ClientConnection client)
		{
			base.OnClientDisconnected(client);

			// Optional: unseat disconnected players, but only if you want seats to free up.
			if (client.RoomCode == null || client.PlayerId == null) return;

			lock (_syncLock)
			{
				if (_rooms.TryGetValue(client.RoomCode, out var state))
				{
					// If you want seats to clear when someone leaves:
					state.UnseatPlayer(client.PlayerId);
					state.SpectatingThisRound.Remove(client.PlayerId);
					state.Engine.RemovePlayer(client.PlayerId);
				}
			}
		}

		public override async Task RestartRoomAsync(Room room, ClientConnection? initiator)
		{
			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);

				// Reset the engine to Lobby without wiping chips/seats
				state.Engine.ResetToLobby(); // you'll add this helper (see note below)
				state.UnlockSpectatorsForNextRound();
				state.RefreshBailoutStatus();
			}

			await BroadcastSnapshotAsync(room.RoomCode);
		}

		public override async Task HandleMessageAsync(HubMessage msg, ClientConnection client)
		{
			if (string.IsNullOrEmpty(msg.RoomCode) || string.IsNullOrEmpty(client.PlayerId))
				return;

			switch (msg.MessageType)
			{
				case "BlackjackSeatSelect":
					await HandleSeatSelect(msg, client);
					break;

				case "BlackjackStartRequest":
					await HandleStart(msg, client);
					break;
				case "BlackjackNextRoundRequest":
					await HandleNextRound(msg, client); // no-op
					break;

				case "BlackjackAction":
					await HandleAction(msg, client);
					break;
				case "BlackjackBetSubmit":
					await HandleBetSubmit(msg, client);
					break;
				case "BlackjackBailout":
					await HandleBailout(msg, client);
					break;
			}
		}

		private async Task HandleNextRound(HubMessage msg, ClientConnection client)
		{
			// Only host can advance rounds
			if (!string.Equals(client.PlayerId, "P1", StringComparison.OrdinalIgnoreCase))
				return;

			BlackjackNextRoundRequestPayload? payload;
			try { payload = JsonSerializer.Deserialize<BlackjackNextRoundRequestPayload>(msg.PayloadJson); }
			catch { return; }
			if (payload == null) return;

			lock (_syncLock)
			{
				var state = EnsureRoomState(payload.RoomCode);

				// only allow when round is actually complete
				if (state.Engine.Phase != BlackjackPhase.RoundResults)
					return;

				// keep the seats; skip lobby; go straight to betting
				state.UnlockSpectatorsForNextRound();
				state.RefreshBailoutStatus();

				var seated = state.SeatPlayerIds
					.Where(pid => !string.IsNullOrEmpty(pid))
					.Cast<string>()
					.ToList();

				if (seated.Count == 0)
					return;

				// Start betting immediately (no seat changes)
				state.StartBetting(seated);
			}

			await BroadcastSnapshotAsync(payload.RoomCode);
		}

		private async Task HandleBailout(HubMessage msg, ClientConnection client)
		{
			BlackjackBailoutPayload? payload;
			try { payload = JsonSerializer.Deserialize<BlackjackBailoutPayload>(msg.PayloadJson); }
			catch { return; }
			if (payload == null) return;

			lock (_syncLock)
			{
				var state = EnsureRoomState(payload.RoomCode);

				// only allow for the requesting player
				if (string.IsNullOrEmpty(client.PlayerId)) return;
				if (!string.Equals(payload.PlayerId, client.PlayerId, StringComparison.OrdinalIgnoreCase)) return;

				// (optional) require seated
				if (!state.IsSeated(client.PlayerId)) return;

				state.RefreshBailoutStatus();
				if (!state.TryApplyBailout(client.PlayerId)) return;
			}

			await BroadcastSnapshotAsync(payload.RoomCode);
		}

		private async Task HandleSeatSelect(HubMessage msg, ClientConnection client)
		{
			BlackjackSeatSelectPayload? payload;
			try { payload = JsonSerializer.Deserialize<BlackjackSeatSelectPayload>(msg.PayloadJson); }
			catch { return; }
			if (payload == null) return;

			lock (_syncLock)
			{
				var state = EnsureRoomState(payload.RoomCode);

				// disallow seat changes mid-round
				if (state.IsRoundActive) return;

				state.TrySeatPlayer(client.PlayerId!, payload.SeatIndex);
			}

			await BroadcastSnapshotAsync(payload.RoomCode);
		}

		private async Task HandleStart(HubMessage msg, ClientConnection client)
		{
			// Only host can start
			if (!string.Equals(client.PlayerId, "P1", StringComparison.OrdinalIgnoreCase))
				return;

			BlackjackStartRequestPayload? payload;
			try { payload = JsonSerializer.Deserialize<BlackjackStartRequestPayload>(msg.PayloadJson); }
			catch { return; }
			if (payload == null) return;

			lock (_syncLock)
			{
				var state = EnsureRoomState(payload.RoomCode);

				if (state.IsRoundActive) return; // already running

				// seated players in seat order
				var seated = state.SeatPlayerIds.Where(pid => !string.IsNullOrEmpty(pid)).Cast<string>().ToList();
				if (seated.Count == 0) return;

				// ensure chips for seated
				foreach (var pid in seated)
					state.EnsureStartingChips(pid);

				// move into Betting phase (no cards yet)
				state.StartBetting(seated);
			}

			await BroadcastSnapshotAsync(payload.RoomCode);
		}
		private async Task HandleBetSubmit(HubMessage msg, ClientConnection client)
		{
			BlackjackBetSubmitPayload? payload;
			try { payload = JsonSerializer.Deserialize<BlackjackBetSubmitPayload>(msg.PayloadJson); }
			catch { return; }
			if (payload == null) return;

			lock (_syncLock)
			{
				var state = EnsureRoomState(payload.RoomCode);

				// must be seated and not spectating
				if (!state.IsSeated(client.PlayerId!)) return;
				if (state.SpectatingThisRound.Contains(client.PlayerId!)) return;

				// must be in betting phase
				if (state.Engine.Phase != BlackjackPhase.Betting) return;

				// must be one of the betting players
				if (!state.BettingPlayers.Contains(client.PlayerId!)) return;

				// already submitted -> ignore
				if (state.BetSubmitted.Contains(client.PlayerId!)) return;

				// apply bet
				if (!state.Engine.TrySetBet(client.PlayerId!, payload.Bet))
					return;

				state.BetSubmitted.Add(client.PlayerId!);

				// if all submitted -> deal
				if (state.HasAllBetsSubmitted())
				{
					state.Engine.DealAfterBets();
					// Kick spectators out of the active round so they never receive a turn
					foreach (var pid in state.SpectatingThisRound)
						state.Engine.ExcludeFromRound(pid);

					// If turn landed on someone invalid, advance immediately
					state.Engine.EnsureTurnIsValid();
				}
			}

			await BroadcastSnapshotAsync(payload.RoomCode);
		}


		private async Task HandleAction(HubMessage msg, ClientConnection client)
		{
			BlackjackActionPayload? payload;
			try { payload = JsonSerializer.Deserialize<BlackjackActionPayload>(msg.PayloadJson); }
			catch { return; }
			if (payload == null) return;

			lock (_syncLock)
			{
				var state = EnsureRoomState(payload.RoomCode);

				// Spectators cannot act
				if (!state.IsSeated(client.PlayerId!)) return;
				if (state.SpectatingThisRound.Contains(client.PlayerId!)) return;

				state.Engine.ApplyAction(client.PlayerId!, payload.Action);
				state.Engine.EnsureTurnIsValid();

				// If round ended, unlock spectators so they get coins + can seat next
				if (state.Engine.Phase == BlackjackPhase.RoundResults)
				{
					state.UnlockSpectatorsForNextRound();
					state.RefreshBailoutStatus();
				}
			}

			await BroadcastSnapshotAsync(payload.RoomCode);
		}

		private async Task BroadcastSnapshotAsync(string roomCode)
		{
			BlackjackSnapshotPayload snapshot;
			List<ClientConnection> targets;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(roomCode, out var state))
					return;

				targets = GetRoomClients(roomCode);

				snapshot = BuildSnapshot(state, targets);
			}

			var msg = new HubMessage
			{
				MessageType = "BlackjackSnapshot",
				RoomCode = roomCode,
				PlayerId = "", // not important
				PayloadJson = JsonSerializer.Serialize(snapshot)
			};

			foreach (var c in targets)
				await _sendAsync(c, msg);
		}

		private static BlackjackSnapshotPayload BuildSnapshot(BlackjackRoomState state, List<ClientConnection> clients)
		{
			var connectedIds = new HashSet<string>(clients.Where(c => c.PlayerId != null).Select(c => c.PlayerId!));

			var payload = new BlackjackSnapshotPayload
			{
				RoomCode = state.RoomCode,
				Phase = state.Engine.Phase,
				CurrentPlayerId = state.Engine.CurrentPlayerId,
				DealerRevealed = state.Engine.DealerRevealed,
				DealerVisibleValue = state.Engine.DealerRevealed
					? BlackjackEngine.ComputeHandValue(state.Engine.DealerHand.ToList())
					: 0,
				RoundComplete = state.Engine.Phase == BlackjackPhase.RoundResults,
				SeatPlayerIds = state.SeatPlayerIds.ToArray()
			};

			// Dealer cards (hole card facedown until revealed)
			for (int i = 0; i < state.Engine.DealerHand.Count; i++)
			{
				var c = state.Engine.DealerHand[i];
				payload.DealerCards.Add(new BlackjackCardDto
				{
					Rank = (int)c.Rank,
					Suit = (int)c.Suit,
					IsFaceDown = !state.Engine.DealerRevealed && i == 1 // second card hidden
				});
			}

			// Players: include seated first in seat order, then unseated connected (spectators)
			var allIds = new List<string>();

			foreach (var pid in state.SeatPlayerIds)
				if (!string.IsNullOrEmpty(pid)) allIds.Add(pid!);

			foreach (var pid in connectedIds)
				if (!allIds.Contains(pid)) allIds.Add(pid);

			foreach (var pid in allIds)
			{
				var p = state.Engine.GetPlayer(pid);
				if (p == null) continue;

				var isSeated = state.IsSeated(pid);
				var submitted = state.BetSubmitted.Contains(pid);
				var canSplit = state.Engine.CanSplit(pid);
				var mainHand = p.Hand;
				var splitHand = p.SplitHand;

				payload.Players.Add(new BlackjackPlayerStateDto
				{
					PlayerId = pid,
					SeatIndex = GetSeatIndex(state, pid),
					IsConnected = connectedIds.Contains(pid),
					IsSeated = isSeated,
					IsSpectatingThisRound = state.SpectatingThisRound.Contains(pid),
					IsInRound = p.IsInRound,
					IsLoser = p.Chips <= 0,
					CanBailout = state.CanBailout(pid),
					IsCurrentTurn = state.Engine.CurrentPlayerId == pid,
					HasSubmittedBet = submitted,

					// ✅ MAIN HAND ALWAYS
					Cards = mainHand.Select(card => new BlackjackCardDto
					{
						Rank = (int)card.Rank,
						Suit = (int)card.Suit,
						IsFaceDown = false
					}).ToList(),

					HandValue = BlackjackEngine.ComputeHandValue(mainHand),
					HasStood = p.HasStood,
					IsBust = p.IsBust,

					// split metadata
					HasSplit = splitHand.Count > 0,
					ActiveHandIndex = p.ActiveHandIndex,
					CanSplit = state.Engine.CanSplit(pid),

					// ✅ SPLIT HAND ALWAYS
					SplitHandCards = splitHand.Select(card => new BlackjackCardDto
					{
						Rank = (int)card.Rank,
						Suit = (int)card.Suit,
						IsFaceDown = false
					}).ToList(),

					SplitHandValue = BlackjackEngine.ComputeHandValue(splitHand),
					SplitHandIsBust = p.SplitIsBust,
					SplitHandHasStood = p.SplitHasStood,

					Chips = p.Chips,
					Bet = submitted ? p.Bet : 0,
					Result = p.Result,
				});
			}

			return payload;
		}

		private static int GetSeatIndex(BlackjackRoomState state, string playerId)
		{
			for (int i = 0; i < state.SeatPlayerIds.Length; i++)
				if (state.SeatPlayerIds[i] == playerId) return i;
			return -1; // not seated
		}
	}
}

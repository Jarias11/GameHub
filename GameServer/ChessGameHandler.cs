using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GameContracts;
using GameLogic;
using GameLogic.Chess;

namespace GameServer
{
	/// <summary>
	/// Online Chess handler (turn-based, non-tick).
	/// Uses ChessRoomState + ChessState as the server-side authority.
	/// </summary>
	public sealed class ChessGameHandler : TurnBasedGameHandler<ChessRoomState>
	{
		public ChessGameHandler(
			RoomManager roomManager,
			System.Collections.Generic.List<ClientConnection> clients,
			object syncLock,
			Func<ClientConnection, HubMessage, Task> sendAsync)
			: base(roomManager, clients, syncLock, sendAsync)
		{
		}

		// This is just for the handler registry, not for HubMessage.
		public override GameType GameType => GameType.Chess;

		public override bool HandlesMessageType(string messageType)
		{
			return messageType == "ChessColorChoice"
				|| messageType == "ChessMove"
				|| messageType == "ChessResign"
				|| messageType == "ChessDrawOffer"
				|| messageType == "ChessDrawResponse";
		}

		protected override ChessRoomState CreateRoomState(string roomCode)
		{
			return new ChessRoomState(roomCode);
		}

		public override Task OnRoomCreated(Room room, ClientConnection owner)
		{
			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);
				state.OwnerPlayerId = owner.PlayerId;
				state.WhitePlayerId = null;
				state.BlackPlayerId = null;
			}

			return Task.CompletedTask;
		}

		public override async Task OnPlayerJoined(Room room, ClientConnection client)
		{
			bool shouldBroadcast = false;

			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);

				// If owner hasn't chosen colors yet, nothing to do.
				if (state.WhitePlayerId == null && state.BlackPlayerId == null)
					return;

				// If this player already has a color, nothing to do.
				if (state.WhitePlayerId == client.PlayerId || state.BlackPlayerId == client.PlayerId)
					return;

				// Exactly one color is still free: assign this joining player to it.
				if (state.WhitePlayerId == null)
				{
					state.WhitePlayerId = client.PlayerId;
					shouldBroadcast = true;
				}
				else if (state.BlackPlayerId == null)
				{
					state.BlackPlayerId = client.PlayerId;
					shouldBroadcast = true;
				}
			}

			if (shouldBroadcast)
			{
				await BroadcastColorAssignmentAsync(room.RoomCode);
			}
		}

		public override async Task HandleMessageAsync(HubMessage msg, ClientConnection client)
		{
			if (string.IsNullOrEmpty(msg.RoomCode))
				return;

			switch (msg.MessageType)
			{
				case "ChessColorChoice":
					{
						var payload = JsonSerializer.Deserialize<ChessColorChoicePayload>(msg.PayloadJson);
						if (payload == null) return;

						await HandleColorChoiceAsync(payload, client);
						break;
					}
				case "ChessMove":
					{
						var payload = JsonSerializer.Deserialize<ChessMovePayload>(msg.PayloadJson);
						if (payload == null) return;

						await HandleMoveAsync(payload, client);
						break;
					}
				case "ChessResign":
					{
						var payload = JsonSerializer.Deserialize<ChessResignPayload>(msg.PayloadJson);
						if (payload == null) return;

						await HandleResignAsync(payload, client);
						break;
					}
				case "ChessDrawOffer":
					{
						var payload = JsonSerializer.Deserialize<ChessDrawOfferPayload>(msg.PayloadJson);
						if (payload == null) return;

						await HandleDrawOfferAsync(payload, client);
						break;
					}
				case "ChessDrawResponse":
					{
						var payload = JsonSerializer.Deserialize<ChessDrawResponsePayload>(msg.PayloadJson);
						if (payload == null) return;

						await HandleDrawResponseAsync(payload, client);
						break;
					}
			}
		}

		private async Task HandleColorChoiceAsync(ChessColorChoicePayload payload, ClientConnection client)
		{
			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(payload.RoomCode, out var roomState))
					return;

				// Only owner (P1) can choose colors
				if (!string.Equals(roomState.OwnerPlayerId, client.PlayerId, StringComparison.Ordinal))
					return;

				// Already assigned? Ignore repeat
				if (roomState.WhitePlayerId != null || roomState.BlackPlayerId != null)
					return;

				// Assign P1
				if (payload.ChosenColor == ChessColorDto.White)
				{
					roomState.WhitePlayerId = client.PlayerId;
				}
				else
				{
					roomState.BlackPlayerId = client.PlayerId;
				}

				// Assign the other player (if already in room)
				var roomClients = GetRoomClients(payload.RoomCode);
				var other = roomClients.FirstOrDefault(c => c.PlayerId != client.PlayerId);

				if (other != null)
				{
					if (roomState.WhitePlayerId == null)
						roomState.WhitePlayerId = other.PlayerId;
					if (roomState.BlackPlayerId == null)
						roomState.BlackPlayerId = other.PlayerId;
				}

				// If we still don't have both, that's okay; we'll reuse this mapping when P2 joins.
			}

			// Broadcast the current mapping to everyone in the room
			await BroadcastColorAssignmentAsync(payload.RoomCode);
		}

		private async Task BroadcastColorAssignmentAsync(string roomCode)
		{
			ChessColorAssignedPayload? payload;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(roomCode, out var state))
					return;

				// If literally nobody has a color yet, nothing to broadcast
				if (state.WhitePlayerId == null && state.BlackPlayerId == null)
					return;

				payload = new ChessColorAssignedPayload
				{
					RoomCode = roomCode,
					WhitePlayerId = state.WhitePlayerId,
					BlackPlayerId = state.BlackPlayerId
				};
			}

			var msgOut = new HubMessage
			{
				MessageType = "ChessColorAssigned",
				RoomCode = roomCode,
				PlayerId = string.Empty, // system message
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			var clients = GetRoomClients(roomCode);
			foreach (var c in clients)
			{
				await _sendAsync(c, msgOut);
			}
		}


		private async Task HandleMoveAsync(ChessMovePayload payload, ClientConnection client)
		{
			ChessMovePayload? broadcastMove = null;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(payload.RoomCode, out var roomState))
					return;

				var state = roomState.State;

				// Determine which color this client controls
				var isWhite = string.Equals(roomState.WhitePlayerId, client.PlayerId, StringComparison.Ordinal);
				var isBlack = string.Equals(roomState.BlackPlayerId, client.PlayerId, StringComparison.Ordinal);

				if (!isWhite && !isBlack)
				{
					// Spectator or not assigned a color.
					return;
				}

				var myColor = isWhite ? ChessColor.White : ChessColor.Black;

				// Must be their turn
				if (state.CurrentTurn != myColor)
					return;

				// Try to apply the move on server-side ChessState
				if (!state.TryMove(payload.FromRow, payload.FromCol, payload.ToRow, payload.ToCol, out var error))
				{
					Console.WriteLine($"[Chess] Illegal move from client {client.PlayerId}: {error}");
					return;
				}
				roomState.PendingDrawOfferFromPlayerId = null;
				roomState.PendingDrawOfferPlyIndex = -1;

				// If we reach here, move is valid: broadcast to both clients
				broadcastMove = new ChessMovePayload
				{
					RoomCode = payload.RoomCode,
					FromRow = payload.FromRow,
					FromCol = payload.FromCol,
					ToRow = payload.ToRow,
					ToCol = payload.ToCol,
					PlayerId = client.PlayerId
				};
			}

			if (broadcastMove != null)
			{
				var msgOut = new HubMessage
				{
					MessageType = "ChessMoveApplied",
					RoomCode = broadcastMove.RoomCode,
					PlayerId = broadcastMove.PlayerId, // who made the move
					PayloadJson = JsonSerializer.Serialize(broadcastMove)
				};

				var clients = GetRoomClients(broadcastMove.RoomCode);
				foreach (var c in clients)
				{
					await _sendAsync(c, msgOut);
				}
			}
		}
		private async Task HandleResignAsync(ChessResignPayload payload, ClientConnection client)
		{
			ChessResignedPayload? outPayload = null;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(payload.RoomCode, out var roomState))
					return;

				// Must be a real player (not spectator)
				var isWhite = string.Equals(roomState.WhitePlayerId, client.PlayerId, StringComparison.Ordinal);
				var isBlack = string.Equals(roomState.BlackPlayerId, client.PlayerId, StringComparison.Ordinal);

				if (!isWhite && !isBlack)
					return;

				// Figure out winner (the other assigned player)
				var winnerId = isWhite ? roomState.BlackPlayerId : roomState.WhitePlayerId;
				if (string.IsNullOrEmpty(winnerId))
					return; // can't resign if opponent not assigned yet

				outPayload = new ChessResignedPayload
				{
					RoomCode = payload.RoomCode,
					ResigningPlayerId = client.PlayerId,
					WinnerPlayerId = winnerId
				};
			}

			var msgOut = new HubMessage
			{
				MessageType = "ChessResigned",
				RoomCode = payload.RoomCode,
				PlayerId = string.Empty,
				PayloadJson = JsonSerializer.Serialize(outPayload)
			};

			var clients = GetRoomClients(payload.RoomCode);
			foreach (var c in clients)
			{
				await _sendAsync(c, msgOut);
			}
		}


		public override async Task RestartRoomAsync(Room room, ClientConnection? initiator)
		{
			ChessRestartedPayload payload;

			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);

				// Reset game logic
				state.Reset();

				// IMPORTANT: force a fresh color choice
				state.WhitePlayerId = null;
				state.BlackPlayerId = null;

				// Keep owner the same (P1 stays the chooser)
				// state.OwnerPlayerId stays as-is

				payload = new ChessRestartedPayload
				{
					RoomCode = room.RoomCode
				};
			}

			var msgOut = new HubMessage
			{
				MessageType = "ChessRestarted",
				RoomCode = room.RoomCode,
				PlayerId = initiator?.PlayerId ?? string.Empty,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			var clients = GetRoomClients(room.RoomCode);
			foreach (var c in clients)
			{
				await _sendAsync(c, msgOut);
			}
		}

		private async Task HandleDrawOfferAsync(ChessDrawOfferPayload payload, ClientConnection client)
		{
			HubMessage? msgOut = null;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(payload.RoomCode, out var roomState))
					return;

				var state = roomState.State;

				var isWhite = string.Equals(roomState.WhitePlayerId, client.PlayerId, StringComparison.Ordinal);
				var isBlack = string.Equals(roomState.BlackPlayerId, client.PlayerId, StringComparison.Ordinal);
				if (!isWhite && !isBlack) return; // spectators can't offer

				var myColor = isWhite ? ChessColor.White : ChessColor.Black;

				// Standard chess: offer only right after you moved (i.e., it is now opponent's turn)
				if (state.CurrentTurn == myColor) return;

				// Can't offer if game already ended
				if (state.IsGameOver) return;

				// Can't stack offers
				if (roomState.PendingDrawOfferFromPlayerId != null) return;

				int ply = state.MoveHistory.Count;

				// Once per ply per player
				if (isWhite && roomState.WhiteLastOfferPlyIndex == ply) return;
				if (isBlack && roomState.BlackLastOfferPlyIndex == ply) return;

				// Record offer
				roomState.PendingDrawOfferFromPlayerId = client.PlayerId;
				roomState.PendingDrawOfferPlyIndex = ply;

				if (isWhite) roomState.WhiteLastOfferPlyIndex = ply;
				else roomState.BlackLastOfferPlyIndex = ply;

				var outPayload = new ChessDrawOfferedPayload
				{
					RoomCode = payload.RoomCode,
					OfferingPlayerId = client.PlayerId,
					PlyIndex = ply
				};

				msgOut = new HubMessage
				{
					MessageType = "ChessDrawOffered",
					RoomCode = payload.RoomCode,
					PlayerId = string.Empty,
					PayloadJson = JsonSerializer.Serialize(outPayload)
				};
			}

			if (msgOut != null)
			{
				var clients = GetRoomClients(payload.RoomCode);
				foreach (var c in clients)
					await _sendAsync(c, msgOut);
			}
		}

		private async Task HandleDrawResponseAsync(ChessDrawResponsePayload payload, ClientConnection client)
		{
			HubMessage? msgOut = null;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(payload.RoomCode, out var roomState))
					return;

				var state = roomState.State;

				var isWhite = string.Equals(roomState.WhitePlayerId, client.PlayerId, StringComparison.Ordinal);
				var isBlack = string.Equals(roomState.BlackPlayerId, client.PlayerId, StringComparison.Ordinal);
				if (!isWhite && !isBlack) return;

				if (state.IsGameOver) return;

				// Must have a pending offer
				if (roomState.PendingDrawOfferFromPlayerId is not string offeringId)
					return;

				// Only the OTHER player can respond
				if (string.Equals(offeringId, client.PlayerId, StringComparison.Ordinal))
					return;

				if (payload.Accept)
				{
					// Clear pending offer
					roomState.PendingDrawOfferFromPlayerId = null;
					roomState.PendingDrawOfferPlyIndex = -1;

					var outPayload = new ChessDrawAgreedPayload
					{
						RoomCode = payload.RoomCode,
						OfferingPlayerId = offeringId,
						AcceptingPlayerId = client.PlayerId
					};

					msgOut = new HubMessage
					{
						MessageType = "ChessDrawAgreed",
						RoomCode = payload.RoomCode,
						PlayerId = string.Empty,
						PayloadJson = JsonSerializer.Serialize(outPayload)
					};
				}
				else
				{
					// Declined: clear pending offer
					roomState.PendingDrawOfferFromPlayerId = null;
					roomState.PendingDrawOfferPlyIndex = -1;

					var outPayload = new ChessDrawDeclinedPayload
					{
						RoomCode = payload.RoomCode,
						DecliningPlayerId = client.PlayerId
					};

					msgOut = new HubMessage
					{
						MessageType = "ChessDrawDeclined",
						RoomCode = payload.RoomCode,
						PlayerId = string.Empty,
						PayloadJson = JsonSerializer.Serialize(outPayload)
					};
				}
			}

			if (msgOut != null)
			{
				var clients = GetRoomClients(payload.RoomCode);
				foreach (var c in clients)
					await _sendAsync(c, msgOut);
			}
		}


	}
}

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using GameContracts;
using GameLogic;
using GameLogic.Uno;

namespace GameServer
{
	public sealed class UnoGameHandler : TurnBasedGameHandler<UnoRoomState>
	{
		private readonly Random _rng;

		public UnoGameHandler(
			RoomManager roomManager,
			List<ClientConnection> clients,
			object syncLock,
			Random rng,
			Func<ClientConnection, HubMessage, Task> sendAsync)
			: base(roomManager, clients, syncLock, sendAsync)
		{
			_rng = rng;
		}

		public override GameType GameType => GameType.Uno;

		public override bool HandlesMessageType(string messageType) =>
			messageType == "UnoStart" ||
			messageType == "UnoPlayCard" ||
			messageType == "UnoDraw" ||
			messageType == "UnoChooseColor" ||
			messageType == "UnoCallUno" ||
			messageType == "UnoPlayCards";

		protected override UnoRoomState CreateRoomState(string roomCode)
		{
			return new UnoRoomState(roomCode, _rng);
		}

		public override async Task OnRoomCreated(Room room, ClientConnection owner)
		{
			await base.OnRoomCreated(room, owner);
			await BroadcastRoomStateAsync(room.RoomCode);
		}

		public override async Task OnPlayerJoined(Room room, ClientConnection client)
		{
			await base.OnPlayerJoined(room, client);
			await BroadcastRoomStateAsync(room.RoomCode);
		}

		public override async Task RestartRoomAsync(Room room, ClientConnection? initiator)
		{
			// Restart = re-deal + new random starting player
			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);
				UnoEngine.StartGame(state, room.Players);
			}

			await BroadcastRoomStateAsync(room.RoomCode);
		}

		public override async Task HandleMessageAsync(HubMessage msg, ClientConnection client)
		{
			if (string.IsNullOrEmpty(client.RoomCode) || string.IsNullOrEmpty(client.PlayerId))
				return;

			Room? room;
			lock (_syncLock)
			{
				room = _roomManager.GetRoom(client.RoomCode);
			}
			if (room == null) return;

			string? error = null;
			bool changed = false;

			switch (msg.MessageType)
			{
				case "UnoStart":
					{
						lock (_syncLock)
						{
							var state = EnsureRoomState(room.RoomCode);

							if (room.Players.Count < 2)
							{
								error = "Need at least 2 players to start UNO.";
								break;
							}

							UnoEngine.StartGame(state, room.Players);
							changed = true;
						}
						break;
					}

				case "UnoPlayCard":
					{
						UnoPlayCardPayload? payload = SafeDeserialize<UnoPlayCardPayload>(msg.PayloadJson);
						if (payload == null) return;

						lock (_syncLock)
						{
							var state = EnsureRoomState(room.RoomCode);

							CardColor? chosen = payload.ChosenColor.HasValue
								? FromDtoColor(payload.ChosenColor.Value)
								: (CardColor?)null;

							changed = UnoEngine.TryPlayCard(state, client.PlayerId!, payload.HandIndex, chosen, out error);
						}
						break;
					}

				case "UnoDraw":
					{
						_ = SafeDeserialize<UnoDrawPayload>(msg.PayloadJson); // optional currently
						lock (_syncLock)
						{
							var state = EnsureRoomState(room.RoomCode);
							changed = UnoEngine.TryDraw(state, client.PlayerId!, out error);
						}
						break;
					}

				case "UnoChooseColor":
					{
						var payload = SafeDeserialize<UnoChooseColorPayload>(msg.PayloadJson);
						if (payload == null) return;

						lock (_syncLock)
						{
							var state = EnsureRoomState(room.RoomCode);
							var color = FromDtoColor(payload.ChosenColor);
							changed = UnoEngine.TryChooseColor(state, client.PlayerId!, color, out error);
						}
						break;
					}

				case "UnoCallUno":
					{
						var payload = SafeDeserialize<UnoCallUnoPayload>(msg.PayloadJson);
						if (payload == null) return;

						lock (_syncLock)
						{
							var state = EnsureRoomState(room.RoomCode);
							UnoEngine.SetUnoArmed(state, client.PlayerId!, payload.IsSayingUno);
							changed = true;
						}
						break;
					}
				case "UnoPlayCards":
					{
						var payload = SafeDeserialize<UnoPlayCardsPayload>(msg.PayloadJson);
						if (payload == null) return;

						lock (_syncLock)
						{
							var state = EnsureRoomState(room.RoomCode);

							CardColor? chosen = payload.ChosenColor.HasValue
								? FromDtoColor(payload.ChosenColor.Value)
								: (CardColor?)null;

							changed = UnoEngine.TryPlayCards(state, client.PlayerId!, payload.HandIndices, chosen, out error);
						}
						break;
					}

			}

			if (!string.IsNullOrEmpty(error))
			{
				await SendErrorAsync(client, room.RoomCode, client.PlayerId!, error);
				// still send state so UI stays in sync
				await SendStateToClientAsync(client, room.RoomCode, client.PlayerId!);
				return;
			}

			if (changed)
			{
				await BroadcastRoomStateAsync(room.RoomCode);
			}
			else
			{
				// no-op actions still get your current state (useful for UI refresh)
				await SendStateToClientAsync(client, room.RoomCode, client.PlayerId!);
			}
		}

		private async Task BroadcastRoomStateAsync(string roomCode)
		{
			List<ClientConnection> roomClients = GetRoomClients(roomCode);

			// Build and send a per-player state payload (hand is private)
			foreach (var c in roomClients)
			{
				if (string.IsNullOrEmpty(c.PlayerId)) continue;
				await SendStateToClientAsync(c, roomCode, c.PlayerId!);
			}
		}

		private async Task SendStateToClientAsync(ClientConnection client, string roomCode, string playerId)
		{
			UnoStatePayload payload;
			lock (_syncLock)
			{
				var state = EnsureRoomState(roomCode);
				payload = UnoEngine.BuildStatePayloadForPlayer(state, playerId);
			}

			var msg = new HubMessage
			{
				MessageType = "UnoState",
				RoomCode = roomCode,
				PlayerId = playerId,
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			await _sendAsync(client, msg);
		}

		private async Task SendErrorAsync(ClientConnection client, string roomCode, string playerId, string message)
		{
			var err = new UnoErrorPayload { Message = message };

			var msg = new HubMessage
			{
				MessageType = "UnoError",
				RoomCode = roomCode,
				PlayerId = playerId,
				PayloadJson = JsonSerializer.Serialize(err)
			};

			await _sendAsync(client, msg);
		}

		private static T? SafeDeserialize<T>(string json)
		{
			try { return JsonSerializer.Deserialize<T>(json); }
			catch { return default; }
		}

		private static CardColor FromDtoColor(UnoCardColor c) =>
			c switch
			{
				UnoCardColor.Red => CardColor.Red,
				UnoCardColor.Yellow => CardColor.Yellow,
				UnoCardColor.Green => CardColor.Green,
				UnoCardColor.Blue => CardColor.Blue,
				_ => CardColor.Wild
			};
	}
}

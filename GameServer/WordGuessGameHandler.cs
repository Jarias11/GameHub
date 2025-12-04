namespace GameServer
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text.Json;
	using System.Threading.Tasks;
	using GameContracts;
	using GameLogic;
	using GameLogic.WordGuess;

	/// <summary>
	/// Golden example of a turn-based game handler using TurnBasedGameHandler.
	/// </summary>
	public sealed class WordGuessGameHandler : TurnBasedGameHandler<WordGuessRoomState>
	{
		public WordGuessGameHandler(
			RoomManager roomManager,
			List<ClientConnection> clients,
			object syncLock,
			Func<ClientConnection, HubMessage, Task> sendAsync)
			: base(roomManager, clients, syncLock, sendAsync)
		{
		}

		public override GameType GameType => GameType.WordGuess;

		public override bool HandlesMessageType(string messageType) =>
			messageType == "WordGuessSetSecret" ||
			messageType == "WordGuessGuess";

		/// <summary>Create initial WordGuess state for a new room.</summary>
		protected override WordGuessRoomState CreateRoomState(string roomCode)
		{
			Console.WriteLine($"[WordGuess] Room {roomCode} created.");
			return new WordGuessRoomState(roomCode);
		}
		public override async Task RestartRoomAsync(Room room, ClientConnection? initiator)
		{
			WordGuessResetPayload payload;
			List<ClientConnection> roomClients;

			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);

				state.SecretWord = null;
				state.AttemptsMade = 0;
				state.IsGameOver = false;
				state.History.Clear();

				Console.WriteLine($"[WordGuess] Room {room.RoomCode} restarted"
					+ (initiator != null ? $" by {initiator.PlayerId}" : ""));

				payload = new WordGuessResetPayload
				{
					Message = "Game restarted. Waiting for a new secret word."
				};

				roomClients = GetRoomClients(room.RoomCode);
			}

			var hubMsg = new HubMessage
			{
				MessageType = "WordGuessReset",
				RoomCode = room.RoomCode,
				PlayerId = initiator?.PlayerId ?? "",
				PayloadJson = JsonSerializer.Serialize(payload)
			};

			foreach (var c in roomClients)
			{
				await _sendAsync(c, hubMsg);
			}
		}


		public override async Task HandleMessageAsync(HubMessage msg, ClientConnection client)
		{
			switch (msg.MessageType)
			{
				case "WordGuessSetSecret":
					HandleSetSecret(msg, client);
					break;

				case "WordGuessGuess":
					await HandleGuessAsync(msg, client);
					break;
			}
		}

		// ── Implementation ───────────────────────────────────────────────────

		private void HandleSetSecret(HubMessage msg, ClientConnection client)
		{
			WordGuessSetSecretPayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<WordGuessSetSecretPayload>(msg.PayloadJson);
			}
			catch
			{
				return;
			}
			if (payload == null) return;

			if (client.RoomCode == null || client.PlayerId != "P1")
				return; // only host can set secret

			var word = payload.SecretWord.Trim().ToUpperInvariant();
			if (word.Length != 5)
			{
				Console.WriteLine("Secret word must be 5 letters.");
				return;
			}

			List<ClientConnection> roomClients;

			lock (_syncLock)
			{
				var state = EnsureRoomState(client.RoomCode);
				state.SecretWord = word;
				state.AttemptsMade = 0;
				state.IsGameOver = false;
				state.History.Clear();

				roomClients = _clients
					.Where(c => c.RoomCode == client.RoomCode)
					.ToList();
			}

			Console.WriteLine($"[WordGuess] Secret set for room {client.RoomCode}");

			// 🔹 NEW: tell everyone the secret is set
			var hubMsg = new HubMessage
			{
				MessageType = "WordGuessSecretSet",
				RoomCode = client.RoomCode!,
				PlayerId = client.PlayerId!,   // initiator
				PayloadJson = ""               // no payload needed
			};

			// Fire-and-forget to all players in this room
			foreach (var c in roomClients)
			{
				_ = _sendAsync(c, hubMsg);
			}
		}


		private async Task HandleGuessAsync(HubMessage msg, ClientConnection client)
		{
			WordGuessGuessPayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<WordGuessGuessPayload>(msg.PayloadJson);
			}
			catch
			{
				return;
			}
			if (payload == null) return;

			if (client.RoomCode == null || client.PlayerId != "P2")
				return; // only guesser should send guesses

			WordGuessResultPayload resultPayload;
			List<ClientConnection> roomClients;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(client.RoomCode, out var state))
					return;

				if (state.SecretWord == null || state.IsGameOver)
					return;

				var guess = payload.Guess.Trim().ToUpperInvariant();
				if (guess.Length != 5)
				{
					Console.WriteLine("Guess must be 5 letters.");
					return;
				}

				var results = WordGuessLogic.EvaluateGuess(state.SecretWord, guess);

				state.AttemptsMade++;

				bool correct = guess == state.SecretWord;
				bool outOfAttempts = state.AttemptsMade >= state.MaxAttempts;

				state.IsGameOver = correct || outOfAttempts;

				resultPayload = new WordGuessResultPayload
				{
					Guess = guess,
					LetterResults = results,
					AttemptNumber = state.AttemptsMade,
					MaxAttempts = state.MaxAttempts,
					IsCorrect = correct,
					IsGameOver = state.IsGameOver,
					Message = correct
						? "Correct! You win."
						: (outOfAttempts ? $"No attempts left. Word was {state.SecretWord}." : "Try again.")
				};

				state.History.Add(resultPayload);

				roomClients = _clients.Where(c => c.RoomCode == client.RoomCode).ToList();
			}

			var hubMsg = new HubMessage
			{
				MessageType = "WordGuessResult",
				RoomCode = client.RoomCode!,
				PlayerId = client.PlayerId!,
				PayloadJson = JsonSerializer.Serialize(resultPayload)
			};

			foreach (var c in roomClients)
			{
				await _sendAsync(c, hubMsg);
			}
		}
	}
}

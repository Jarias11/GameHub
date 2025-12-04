namespace GameServer
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text.Json;
	using System.Threading.Tasks;
	using GameContracts;
	using GameLogic;
	using GameLogic.Anagram;

	/// <summary>
	/// Game handler for the Anagram game.
	/// Uses TurnBasedGameHandler for room state management.
	/// </summary>
	public sealed class AnagramGameHandler : TurnBasedGameHandler<AnagramRoomState>
	{
		private readonly Random _rng = new();

		// You can tweak this if you want.
		private const int MinWordLength = 3;

		public AnagramGameHandler(
			RoomManager roomManager,
			List<ClientConnection> clients,
			object syncLock,
			Func<ClientConnection, HubMessage, Task> sendAsync)
			: base(roomManager, clients, syncLock, sendAsync)
		{
		}

		public override GameType GameType => GameType.Anagram;

		public override bool HandlesMessageType(string messageType) =>
			messageType == "AnagramConfigureRound" ||
			messageType == "AnagramSubmitWord" ||
			messageType == "AnagramTimeUp";

		/// <summary>Create initial Anagram state for a new room.</summary>
		protected override AnagramRoomState CreateRoomState(string roomCode)
		{
			Console.WriteLine($"[Anagram] Room {roomCode} created.");
			return new AnagramRoomState(roomCode);
		}

		public override async Task RestartRoomAsync(Room room, ClientConnection? initiator)
		{
			AnagramResetPayload payload;
			List<ClientConnection> roomClients;

			lock (_syncLock)
			{
				var state = EnsureRoomState(room.RoomCode);

				state.Status = AnagramRoundStatus.WaitingForConfig;
				state.Letters = string.Empty;
				state.DurationSeconds = 0;
				state.RoundEndUtc = null;
				state.IsRoundActive = false;
				state.RoundNumber = 0;

				foreach (var player in state.Players.Values)
				{
					player.Score = 0;
					player.AcceptedWords.Clear();
				}

				Console.WriteLine($"[Anagram] Room {room.RoomCode} restarted"
					+ (initiator != null ? $" by {initiator.PlayerId}" : ""));

				payload = new AnagramResetPayload
				{
					Message = "Anagram reset. Waiting for a new round configuration."
				};

				roomClients = _clients
					.Where(c => c.RoomCode == room.RoomCode)
					.ToList();
			}

			var hubMsg = new HubMessage
			{
				MessageType = "AnagramReset",
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
				case "AnagramConfigureRound":
					await HandleConfigureRoundAsync(msg, client);
					break;

				case "AnagramSubmitWord":
					await HandleSubmitWordAsync(msg, client);
					break;
				case "AnagramTimeUp":
					await HandleTimeUpAsync(msg, client);
					break;
			}
		}

		// ── Message handlers ────────────────────────────────────────────────


		private async Task HandleTimeUpAsync(HubMessage msg, ClientConnection client)
		{
			// Only process if the client is actually in a room
			if (client.RoomCode == null)
				return;

			var roomCode = client.RoomCode;
			AnagramRoundSummaryPayload? summary = null;
			List<ClientConnection> roomClients;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(roomCode, out var state))
					return;

				// If the round is not active, nothing to do.
				if (!state.IsRoundActive)
				{
					Console.WriteLine($"[Anagram] TimeUp ignored; round not active in room {roomCode}.");
					return;
				}

				// 👉 Don't re-check the clock here – trust the host timer.
				state.IsRoundActive = false;
				state.Status = AnagramRoundStatus.Completed;
				state.RoundEndUtc = DateTimeOffset.UtcNow; // optional: snap to now

				var messageText = BuildWinnerMessage(state);
				summary = AnagramLogic.BuildRoundSummary(state, messageText);

				roomClients = _clients
					.Where(c => c.RoomCode == roomCode)
					.ToList();

				Console.WriteLine($"[Anagram] TimeUp processed; round completed in room {roomCode}.");
			}

			if (summary != null)
			{
				var summaryMsg = new HubMessage
				{
					MessageType = "AnagramRoundSummary",
					RoomCode = roomCode,
					PlayerId = client.PlayerId ?? "", // the host who reported time-up
					PayloadJson = JsonSerializer.Serialize(summary)
				};

				foreach (var c in roomClients)
				{
					await _sendAsync(c, summaryMsg);
				}

				Console.WriteLine($"[Anagram] Round summary sent for room {roomCode} (TimeUp).");
			}
		}

		private async Task HandleConfigureRoundAsync(HubMessage msg, ClientConnection client)
		{
			AnagramConfigureRoundPayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<AnagramConfigureRoundPayload>(msg.PayloadJson);
			}
			catch
			{
				return;
			}
			if (payload == null) return;

			// Only host (P1) can configure a round
			if (client.RoomCode == null || client.PlayerId != "P1")
				return;

			var roomCode = client.RoomCode;

			// Basic validation / clamping
			int letterCount = payload.LetterCount;
			if (letterCount != 5 && letterCount != 8 && letterCount != 10)
			{
				// Clamp to a supported value
				letterCount = 5;
			}

			int durationSeconds = payload.DurationSeconds;
			if (durationSeconds != 60 && durationSeconds != 120 && durationSeconds != 180)
			{
				durationSeconds = 60;
			}

			AnagramRoundStartedPayload roundStartedPayload;
			List<ClientConnection> roomClients;

			lock (_syncLock)
			{
				var state = EnsureRoomState(roomCode);

				state.Status = AnagramRoundStatus.InProgress;
				state.Letters = GenerateLetters(letterCount);
				state.DurationSeconds = durationSeconds;
				state.RoundNumber += 1;

				var now = DateTimeOffset.UtcNow;
				state.RoundEndUtc = now.AddSeconds(durationSeconds);
				state.IsRoundActive = true;

				// Reset per-player scores / words for the new round
				foreach (var player in state.Players.Values)
				{
					player.Score = 0;
					player.AcceptedWords.Clear();
				}

				roundStartedPayload = new AnagramRoundStartedPayload
				{
					Letters = state.Letters,
					DurationSeconds = state.DurationSeconds,
					RoundEndUtc = state.RoundEndUtc,
					RoundNumber = state.RoundNumber,
					Message = $"Round {state.RoundNumber} starting!"
				};

				roomClients = _clients
					.Where(c => c.RoomCode == roomCode)
					.ToList();
			}

			var hubMsg = new HubMessage
			{
				MessageType = "AnagramRoundStarted",
				RoomCode = roomCode,
				PlayerId = client.PlayerId ?? "",
				PayloadJson = JsonSerializer.Serialize(roundStartedPayload)
			};

			foreach (var c in roomClients)
			{
				await _sendAsync(c, hubMsg);
			}

			Console.WriteLine($"[Anagram] Round started in room {roomCode} with {roundStartedPayload.Letters} for {roundStartedPayload.DurationSeconds}s.");

		}


		private async Task HandleSubmitWordAsync(HubMessage msg, ClientConnection client)
		{
			AnagramSubmitWordPayload? payload;
			try
			{
				payload = JsonSerializer.Deserialize<AnagramSubmitWordPayload>(msg.PayloadJson);
			}
			catch
			{
				return;
			}
			if (payload == null) return;

			if (client.RoomCode == null || string.IsNullOrEmpty(client.PlayerId))
				return;

			var roomCode = client.RoomCode;
			var playerId = client.PlayerId!;

			AnagramWordResultPayload wordResultPayload;
			AnagramRoundSummaryPayload? roundSummaryPayload = null;
			List<ClientConnection> roomClients;

			lock (_syncLock)
			{
				if (!_rooms.TryGetValue(roomCode, out var state))
					return;

				var now = DateTimeOffset.UtcNow;
				bool justCompleted = false;

				int secondsRemaining = 0;
				if (state.RoundEndUtc.HasValue)
				{
					secondsRemaining = (int)Math.Max(0, (state.RoundEndUtc.Value - now).TotalSeconds);
				}

				// If the round is already over, don't accept new words,
				// but optionally send back a simple "round is over" result.
				if (!state.IsRoundActive || (state.RoundEndUtc.HasValue && now >= state.RoundEndUtc.Value))
				{
					if (state.IsRoundActive)
					{
						state.IsRoundActive = false;
						state.Status = AnagramRoundStatus.Completed;
						justCompleted = true;
					}

					wordResultPayload = new AnagramWordResultPayload
					{
						Word = payload.Word ?? string.Empty,
						Accepted = false,
						Reason = "Round is over.",
						NewScore = state.GetOrCreatePlayer(playerId).Score,
						AcceptedWordCount = state.GetOrCreatePlayer(playerId).AcceptedWords.Count,
						SecondsRemaining = 0,
						IsRoundOver = true
					};

					// If we just transitioned to completed, build a summary.
					if (justCompleted)
					{
						var summaryMessage = BuildWinnerMessage(state);
						roundSummaryPayload = AnagramLogic.BuildRoundSummary(state, summaryMessage);
					}

					roomClients = _clients.Where(c => c.RoomCode == roomCode).ToList();
				}
				else
				{
					// Round is active: try to apply the word.
					string? reason;
					int newScore;
					int acceptedCount;

					bool accepted = AnagramLogic.TryApplyWord(
						state,
						playerId,
						payload.Word ?? string.Empty,
						MinWordLength,
						out reason,
						out newScore,
						out acceptedCount);

					// Recompute secondsRemaining after processing (optional; usually same).
					if (state.RoundEndUtc.HasValue)
					{
						secondsRemaining = (int)Math.Max(0, (state.RoundEndUtc.Value - now).TotalSeconds);
					}

					// Check if time expired right after this submission.
					bool isRoundOver = false;
					if (state.RoundEndUtc.HasValue && now >= state.RoundEndUtc.Value)
					{
						if (state.IsRoundActive)
						{
							state.IsRoundActive = false;
							state.Status = AnagramRoundStatus.Completed;
							justCompleted = true;
						}
						isRoundOver = true;
						secondsRemaining = 0;
					}

					wordResultPayload = new AnagramWordResultPayload
					{
						Word = payload.Word ?? string.Empty,
						Accepted = accepted,
						Reason = accepted ? null : reason,
						NewScore = newScore,
						AcceptedWordCount = acceptedCount,
						SecondsRemaining = secondsRemaining,
						IsRoundOver = isRoundOver
					};

					if (isRoundOver && justCompleted)
					{
						var summaryMessage = BuildWinnerMessage(state);
						roundSummaryPayload = AnagramLogic.BuildRoundSummary(state, summaryMessage);
					}

					roomClients = _clients.Where(c => c.RoomCode == roomCode).ToList();
				}
			}

			// Send the word result back only to the submitting player.
			var resultMsg = new HubMessage
			{
				MessageType = "AnagramWordResult",
				RoomCode = roomCode,
				PlayerId = playerId,
				PayloadJson = JsonSerializer.Serialize(wordResultPayload)
			};
			foreach (var c in roomClients)
			{
				await _sendAsync(c, resultMsg);
			}

			// If the round ended during this submission, broadcast summary to the room.
			if (roundSummaryPayload != null)
			{
				var summaryMsg = new HubMessage
				{
					MessageType = "AnagramRoundSummary",
					RoomCode = roomCode,
					PlayerId = playerId,
					PayloadJson = JsonSerializer.Serialize(roundSummaryPayload)
				};

				foreach (var c in roomClients)
				{
					await _sendAsync(c, summaryMsg);
				}

				Console.WriteLine($"[Anagram] Round completed in room {roomCode}.");
			}
		}

		// ── Helpers ─────────────────────────────────────────────────────────

		/// <summary>
		/// Generate a random letter set with a small vowel bias.
		/// </summary>
		private string GenerateLetters(int letterCount)
		{
			const string vowels = "AEIOU";
			const string consonants = "BCDFGHJKLMNPQRSTVWXYZ";

			var chars = new List<char>(letterCount);

			// Ensure at least 2 vowels (or less if letterCount small).
			int minVowels = Math.Min(3, Math.Max(1, letterCount / 3));
			minVowels = Math.Min(minVowels, letterCount);

			// Add required vowels
			for (int i = 0; i < minVowels; i++)
			{
				chars.Add(vowels[_rng.Next(vowels.Length)]);
			}

			// Fill the rest with random letters (mix of vowels + consonants)
			while (chars.Count < letterCount)
			{
				bool pickVowel = _rng.NextDouble() < 0.3; // 30% chance
				if (pickVowel)
				{
					chars.Add(vowels[_rng.Next(vowels.Length)]);
				}
				else
				{
					chars.Add(consonants[_rng.Next(consonants.Length)]);
				}
			}

			// Shuffle the letters
			for (int i = chars.Count - 1; i > 0; i--)
			{
				int j = _rng.Next(i + 1);
				(chars[i], chars[j]) = (chars[j], chars[i]);
			}

			return new string(chars.ToArray());
		}

		/// <summary>
		/// Build a simple "winner" message based on scores in the state.
		/// </summary>
		private static string BuildWinnerMessage(AnagramRoomState state)
		{
			if (state.Players.Count == 0)
				return "Round over.";

			var maxScore = state.Players.Values.Max(p => p.Score);
			var winners = state.Players.Values
				.Where(p => p.Score == maxScore)
				.Select(p => p.PlayerId)
				.ToList();

			if (winners.Count == 1)
			{
				return $"{winners[0]} wins with {maxScore} points!";
			}

			return $"It's a tie at {maxScore} points!";
		}
	}
}

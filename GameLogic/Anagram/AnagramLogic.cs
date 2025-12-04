using System;
using System.Collections.Generic;
using GameContracts;

namespace GameLogic.Anagram
{
	/// <summary>
	/// Pure Anagram rules – no networking, no ASP.NET.
	/// Works on AnagramRoomState and returns info the handler can use to build payloads.
	/// </summary>
	public static class AnagramLogic
	{
		/// <summary>
		/// Try to apply a word submission for a player.
		/// Updates scores / accepted-words in the given state if accepted.
		/// 
		/// Returns true if accepted; otherwise false with a reason.
		/// </summary>
		/// <param name="state">Room state.</param>
		/// <param name="playerId">Logical player ID ("P1", "P2").</param>
		/// <param name="rawWord">The word as sent by the client.</param>
		/// <param name="minWordLength">Minimum word length (default 3).</param>
		/// <param name="reason">Optional reason if rejected.</param>
		/// <param name="newScore">Player's total score after this word (or unchanged if rejected).</param>
		/// <param name="acceptedWordCount">Accepted word count after this word (or unchanged if rejected).</param>
		public static bool TryApplyWord(
			AnagramRoomState state,
			string playerId,
			string rawWord,
			int minWordLength,
			out string? reason,
			out int newScore,
			out int acceptedWordCount)
		{
			if (state == null) throw new ArgumentNullException(nameof(state));
			if (playerId == null) throw new ArgumentNullException(nameof(playerId));

			var player = state.GetOrCreatePlayer(playerId);

			// Default outputs in case of failure
			reason = null;
			newScore = player.Score;
			acceptedWordCount = player.AcceptedWords.Count;

			if (!state.IsRoundActive)
			{
				reason = "Round is not active.";
				return false;
			}

			if (string.IsNullOrWhiteSpace(state.Letters))
			{
				reason = "No letter set for this round.";
				return false;
			}

			var word = rawWord?.Trim().ToLowerInvariant() ?? string.Empty;
			if (word.Length < minWordLength)
			{
				reason = $"Word must be at least {minWordLength} letters.";
				return false;
			}

			// Ensure only alphabetic letters (no spaces, hyphens, etc.)
			foreach (var ch in word)
			{
				if (ch < 'a' || ch > 'z')
				{
					reason = "Word contains invalid characters.";
					return false;
				}
			}

			// Dictionary check
			if (!WordDictionary.IsValid(word))
			{
				reason = "Not in dictionary.";
				return false;
			}

			// Letter availability check
			if (!CanBuildFromLetters(word, state.Letters))
			{
				reason = "Uses letters not available in the pool.";
				return false;
			}

			// Duplicate check
			if (player.AcceptedWords.Contains(word))
			{
				reason = "Word already used.";
				return false;
			}

			// At this point, the word is accepted.
			int points = word.Length; // simple rule: length-based scoring
			player.Score += points;
			player.AcceptedWords.Add(word);

			newScore = player.Score;
			acceptedWordCount = player.AcceptedWords.Count;
			return true;
		}

		/// <summary>
		/// Builds a round summary payload from the room state.
		/// Useful at round end: the handler can broadcast this.
		/// </summary>
		public static AnagramRoundSummaryPayload BuildRoundSummary(AnagramRoomState state, string? message = null)
		{
			if (state == null) throw new ArgumentNullException(nameof(state));

			var players = new List<AnagramPlayerSummary>();

			foreach (var kvp in state.Players)
			{
				var p = kvp.Value;
				players.Add(new AnagramPlayerSummary
				{
					PlayerId = p.PlayerId,
					Score = p.Score,
					AcceptedWords = new List<string>(p.AcceptedWords).ToArray()
				});
			}

			return new AnagramRoundSummaryPayload
			{
				Letters = state.Letters,
				DurationSeconds = state.DurationSeconds,
				RoundNumber = state.RoundNumber,
				Players = players.ToArray(),
				Message = message
			};
		}

		/// <summary>
		/// Check if "word" can be made from the letters in "pool",
		/// respecting letter counts.
		/// </summary>
		private static bool CanBuildFromLetters(string word, string pool)
		{
			var counts = new Dictionary<char, int>();

			foreach (var ch in pool.ToLowerInvariant())
			{
				if (ch < 'a' || ch > 'z') continue;
				if (!counts.ContainsKey(ch))
					counts[ch] = 0;
				counts[ch]++;
			}

			foreach (var ch in word)
			{
				if (!counts.TryGetValue(ch, out var count) || count <= 0)
					return false;
				counts[ch] = count - 1;
			}

			return true;
		}
	}
}

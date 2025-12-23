using System;

namespace GameContracts
{
	/// <summary>
	/// Basic status of a round so clients can know where they are in the flow.
	/// </summary>
	public enum AnagramRoundStatus
	{
		WaitingForConfig,
		WaitingForLetters,
		Countdown,
		InProgress,
		Completed
	}

	/// <summary>
	/// P1 (host) requests a new round with a specific letter count and time control.
	/// LetterCount is typically 5, 8, or 10.
	/// DurationSeconds is typically 60, 120, or 180.
	/// </summary>
	public class AnagramConfigureRoundPayload
	{
		public int LetterCount { get; set; } = 5;     // 5, 8, 10
		public int DurationSeconds { get; set; } = 60; // 60, 120, 180
	}
	public class AnagramTimeUpPayload
	{
		// No fields needed for now, but you could add RoundNumber later if desired.
	}

	/// <summary>
	/// Server broadcasts when a round actually starts.
	/// It includes the letter set and the duration so both clients can sync timers.
	/// </summary>
	public class AnagramRoundStartedPayload
	{
		/// <summary>
		/// The letters available for this round (e.g. "RAETPOMI").
		/// All players must use only these letters.
		/// </summary>
		public string Letters { get; set; } = string.Empty;

		/// <summary>
		/// Total duration of the round in seconds (60 / 120 / 180).
		/// </summary>
		public int DurationSeconds { get; set; }

		/// <summary>
		/// Optional UTC end time if you want more precise syncing across clients.
		/// </summary>
		public DateTimeOffset? RoundEndUtc { get; set; }

		/// <summary>
		/// Round number within the match (1-based).
		/// </summary>
		public int RoundNumber { get; set; } = 1;

		/// <summary>
		/// Optional message like "Round 1 starting!".
		/// </summary>
		public string? Message { get; set; }
	}

	/// <summary>
	/// Client submits one candidate word during the round.
	/// Server will validate that the word:
	/// - uses only the given letters,
	/// - meets minimum length,
	/// - exists in the dictionary,
	/// - has not been used already by this player.
	/// </summary>
	public class AnagramSubmitWordPayload
	{
		public string Word { get; set; } = string.Empty;
	}

	/// <summary>
	/// Server responds to a submitted word.
	/// This is sent only to the submitting player.
	/// </summary>
	public class AnagramWordResultPayload
	{
		/// <summary>
		/// The word that was submitted.
		/// </summary>
		public string Word { get; set; } = string.Empty;

		/// <summary>
		/// True if the word was accepted and counted for score.
		/// </summary>
		public bool Accepted { get; set; }

		/// <summary>
		/// Optional reason if the word was rejected
		/// (e.g. "Not in dictionary", "Uses invalid letter", "Already used").
		/// </summary>
		public string? Reason { get; set; }

		/// <summary>
		/// Player's new total score after this word (0 if rejected).
		/// </summary>
		public int NewScore { get; set; }

		/// <summary>
		/// Number of accepted words the player has so far (including this one if accepted).
		/// </summary>
		public int AcceptedWordCount { get; set; }

		/// <summary>
		/// Seconds remaining in the round as seen by the server when this was processed.
		/// </summary>
		public int SecondsRemaining { get; set; }

		/// <summary>
		/// True if the round has ended after processing this word (time up or other condition).
		/// </summary>
		public bool IsRoundOver { get; set; }
	}

	/// <summary>
	/// Per-player summary data for the end-of-round broadcast.
	/// </summary>
	public class AnagramPlayerSummary
	{
		/// <summary>
		/// Logical player identifier ("P1", "P2", etc.).
		/// </summary>
		public string PlayerId { get; set; } = string.Empty;

		/// <summary>
		/// Final score for this round.
		/// </summary>
		public int Score { get; set; }

		/// <summary>
		/// All accepted words for this player in this round.
		/// </summary>
		public string[] AcceptedWords { get; set; } = Array.Empty<string>();
	}

	/// <summary>
	/// Server broadcasts this when the round ends to both players with final results.
	/// </summary>
	public class AnagramRoundSummaryPayload
	{
		/// <summary>
		/// The letters used in this round so clients can show them in a recap.
		/// </summary>
		public string Letters { get; set; } = string.Empty;

		/// <summary>
		/// Total duration (seconds) of the round.
		/// </summary>
		public int DurationSeconds { get; set; }

		/// <summary>
		/// Round number within the match (1-based).
		/// </summary>
		public int RoundNumber { get; set; } = 1;

		/// <summary>
		/// All players' scores and accepted words.
		/// </summary>
		public AnagramPlayerSummary[] Players { get; set; } = Array.Empty<AnagramPlayerSummary>();

		/// <summary>
		/// Optional message like "P1 wins!" or "It's a tie!".
		/// </summary>
		public string? Message { get; set; }

		public string[] PossibleWords { get; set; } = Array.Empty<string>();
	}

	/// <summary>
	/// Used to reset the game back to a waiting state (e.g. after a match or on user request).
	/// </summary>
	public class AnagramResetPayload
	{
		public string Message { get; set; } = "Anagram reset. Waiting for a new round configuration.";
	}
}

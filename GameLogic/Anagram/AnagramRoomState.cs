using System;
using System.Collections.Generic;
using GameContracts;

namespace GameLogic.Anagram
{
	/// <summary>
	/// Per-player state within an Anagram room.
	/// </summary>
	public class AnagramPlayerState
	{
		/// <summary>
		/// Logical player ID ("P1", "P2", etc.).
		/// </summary>
		public string PlayerId { get; }

		/// <summary>
		/// Total score accumulated this round.
		/// </summary>
		public int Score { get; set; }

		/// <summary>
		/// All accepted words (normalized, e.g. lowercase).
		/// </summary>
		public HashSet<string> AcceptedWords { get; } = new();

		public AnagramPlayerState(string playerId)
		{
			PlayerId = playerId ?? throw new ArgumentNullException(nameof(playerId));
		}
	}

	/// <summary>
	/// Server-side state for an Anagram room.
	/// No sockets, just game data.
	/// </summary>
	public class AnagramRoomState : IRoomState
	{
		public string RoomCode { get; }

		/// <summary>
		/// Current status of the round (waiting, in progress, completed, etc.).
		/// </summary>
		public AnagramRoundStatus Status { get; set; } = AnagramRoundStatus.WaitingForConfig;

		/// <summary>
		/// The letters available for the current round (e.g. "RAETPOMI").
		/// </summary>
		public string Letters { get; set; } = string.Empty;

		/// <summary>
		/// Total duration of the round in seconds (60 / 120 / 180).
		/// </summary>
		public int DurationSeconds { get; set; }

		/// <summary>
		/// When the round should end (UTC). Optional – handler may or may not enforce time.
		/// </summary>
		public DateTimeOffset? RoundEndUtc { get; set; }

		/// <summary>
		/// True if the round is currently accepting words.
		/// </summary>
		public bool IsRoundActive { get; set; }

		/// <summary>
		/// Round number within the match (1-based).
		/// </summary>
		public int RoundNumber { get; set; }

		/// <summary>
		/// Per-player state (scores, accepted words).
		/// Key is PlayerId ("P1", "P2", ...).
		/// </summary>
		public Dictionary<string, AnagramPlayerState> Players { get; } = new();

		public AnagramRoomState(string roomCode)
		{
			RoomCode = roomCode ?? throw new ArgumentNullException(nameof(roomCode));
		}

		/// <summary>
		/// Get or create a player state entry for the given player ID.
		/// </summary>
		public AnagramPlayerState GetOrCreatePlayer(string playerId)
		{
			if (!Players.TryGetValue(playerId, out var playerState))
			{
				playerState = new AnagramPlayerState(playerId);
				Players[playerId] = playerState;
			}

			return playerState;
		}
	}
}

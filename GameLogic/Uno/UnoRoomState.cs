using System;
using System.Collections.Generic;
using GameLogic.Uno;

namespace GameLogic.Uno
{
	public enum UnoPhase
	{
		WaitingForStart = 0,
		NormalTurn = 1,
		AwaitingWildColorChoice = 2,
		GameOver = 3
	}

	public sealed class UnoRoomState : GameLogic.IRoomState
	{
		public string RoomCode { get; }

		// Seating / turn order (matches Room.Players order at start, but we can keep it here)
		public List<string> Players { get; } = new();

		// Hands by playerId
		public Dictionary<string, UnoHand> Hands { get; } = new();

		// Deck + Discard
		public UnoDeck Deck { get; internal set; }
		public List<UnoCard> DiscardPile { get; } = new();

		// Game flow
		public UnoPhase Phase { get; set; } = UnoPhase.WaitingForStart;
		public int CurrentPlayerIndex { get; set; } = 0;

		// +1 clockwise, -1 counter-clockwise
		public int Direction { get; set; } = +1;

		// Active color matters especially after wilds
		public CardColor ActiveColor { get; set; } = CardColor.Red;

		// Pending wild-color choice (if the player played wild without choosing immediately)
		public string? AwaitingColorChoiceFromPlayerId { get; set; }

		// Draw stacking
		public int PendingDrawCount { get; set; } = 0;
		public CardValue? PendingDrawType { get; set; } = null; // DrawTwo or WildDrawFour

		// Turn-start snapshot to enforce: "can only stack if you had it at the beginning"
		public string? TurnSnapshotPlayerId { get; set; }
		public bool TurnStartHadDrawTwo { get; set; }
		public bool TurnStartHadWildDrawFour { get; set; }
		public bool HasDrawnThisTurn { get; set; }

		// UNO call arming
		public HashSet<string> SaidUnoArmed { get; } = new();

		// Win
		public string? WinnerPlayerId { get; set; }

		// RNG
		public Random Rng { get; }

		public UnoRoomState(string roomCode, Random rng)
		{
			RoomCode = roomCode;
			Rng = rng;
			Deck = new UnoDeck(rng);
		}

		public string CurrentPlayerId => Players.Count == 0 ? string.Empty : Players[CurrentPlayerIndex];

		public UnoCard TopDiscard => DiscardPile.Count == 0
			? throw new InvalidOperationException("Discard pile is empty.")
			: DiscardPile[DiscardPile.Count - 1];
	}
}

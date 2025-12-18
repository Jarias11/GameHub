using System;
using System.Collections.Generic;
using GameContracts;

namespace GameLogic.Blackjack
{
	public sealed class BlackjackRoomState : IRoomState
	{
		public string RoomCode { get; }
		public BlackjackEngine Engine { get; }

		// seat 0..3 -> "P1".."P4" (or null)
		public string?[] SeatPlayerIds { get; } = new string?[4];

		// Players who joined mid-round and must watch until round ends
		public HashSet<string> SpectatingThisRound { get; } = new();

		// Players who have ever been granted starting chips
		public HashSet<string> HasReceivedStartingChips { get; } = new();

		public List<string> BettingPlayers { get; } = new();
		public HashSet<string> BetSubmitted { get; } = new();

		public BlackjackRoomState(string roomCode, Random rng)
		{
			RoomCode = roomCode;
			Engine = new BlackjackEngine(rng);
		}

		public bool IsRoundActive =>
			Engine.Phase != BlackjackPhase.Lobby && Engine.Phase != BlackjackPhase.RoundResults;

		public bool RoundComplete => Engine.Phase == BlackjackPhase.RoundResults;

		public int SeatedCount
		{
			get
			{
				int count = 0;
				for (int i = 0; i < SeatPlayerIds.Length; i++)
					if (!string.IsNullOrEmpty(SeatPlayerIds[i])) count++;
				return count;
			}
		}
		public void StartBetting(IReadOnlyList<string> seatedPlayers)
		{
			BettingPlayers.Clear();
			BettingPlayers.AddRange(seatedPlayers);

			BetSubmitted.Clear();

			Engine.BeginBettingRound(seatedPlayers);
		}

		public bool HasAllBetsSubmitted()
		{
			if (BettingPlayers.Count == 0) return false;
			return BetSubmitted.Count == BettingPlayers.Count;
		}

		public bool TrySeatPlayer(string playerId, int seatIndex)
		{
			if (seatIndex < 0 || seatIndex > 3) return false;

			// Can't change seating mid-round
			if (IsRoundActive) return false;

			// If occupied by someone else
			var current = SeatPlayerIds[seatIndex];
			if (current != null && !string.Equals(current, playerId, StringComparison.OrdinalIgnoreCase))
				return false;

			// Unseat from old seat (if any)
			UnseatPlayer(playerId);

			SeatPlayerIds[seatIndex] = playerId;
			SpectatingThisRound.Remove(playerId);
			EnsureStartingChips(playerId);

			return true;
		}

		public void UnseatPlayer(string playerId)
		{
			for (int i = 0; i < SeatPlayerIds.Length; i++)
				if (SeatPlayerIds[i] == playerId) SeatPlayerIds[i] = null;
		}

		public bool IsSeated(string playerId)
		{
			for (int i = 0; i < SeatPlayerIds.Length; i++)
				if (SeatPlayerIds[i] == playerId) return true;
			return false;
		}

		public void EnsureStartingChips(string playerId)
		{
			if (HasReceivedStartingChips.Contains(playerId))
				return;

			Engine.EnsurePlayer(playerId);

			// give 1000 the first time they become eligible to play (seat or post-round unlock)
			var p = Engine.GetPlayer(playerId);
			if (p != null)
			{
				p.Chips = 1000;
				HasReceivedStartingChips.Add(playerId);
				RefreshBailoutStatus();
			}
		}

		// who is currently at 0 (or below) and therefore “in loser state”
		public HashSet<string> BailoutEligibleZero { get; } = new();

		// who already used the bailout during their current zero-event
		public HashSet<string> BailoutClaimedThisZero { get; } = new();

		public void RefreshBailoutStatus()
		{
			foreach (var p in Engine.Players)
			{
				var pid = p.PlayerId;
				if (p.Chips <= 0)
				{
					// newly hit zero => reset claim for this zero-event
					if (!BailoutEligibleZero.Contains(pid))
						BailoutClaimedThisZero.Remove(pid);

					BailoutEligibleZero.Add(pid);
				}
				else
				{
					// once they’re above 0 again, clear state so hitting 0 later re-enables
					BailoutEligibleZero.Remove(pid);
					BailoutClaimedThisZero.Remove(pid);
				}
			}
		}

		public bool CanBailout(string playerId)
		{
			var p = Engine.GetPlayer(playerId);
			if (p == null) return false;
			if (p.Chips > 0) return false;
			return BailoutEligibleZero.Contains(playerId) && !BailoutClaimedThisZero.Contains(playerId);
		}

		public bool TryApplyBailout(string playerId)
		{
			if (!CanBailout(playerId)) return false;

			var p = Engine.GetPlayer(playerId);
			if (p == null) return false;

			p.Chips = 100;
			BailoutClaimedThisZero.Add(playerId);

			// now they’re >0 so this removes eligibility until they hit 0 again
			RefreshBailoutStatus();
			return true;
		}

		// Called when someone joins mid-round
		public void MarkSpectatorThisRound(string playerId)
		{
			// don’t auto-seat; they just watch
			SpectatingThisRound.Add(playerId);
			Engine.EnsurePlayer(playerId); // optional: allow them to appear in snapshot as connected
		}

		// Called when a round ends -> lobby opens -> spectators now get chips
		public void UnlockSpectatorsForNextRound()
		{
			foreach (var pid in SpectatingThisRound)
				EnsureStartingChips(pid);

			SpectatingThisRound.Clear();
		}
	}
}

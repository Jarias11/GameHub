using System;
using System.Collections.Generic;
using GameContracts;       // for BlackjackPhase / Result enums
using GameLogic.CardGames; // for Card / Deck
using GameLogic.Blackjack;

namespace GameLogic.Blackjack
{
	public sealed class BlackjackPlayerState
	{
		public string PlayerId { get; set; } = string.Empty;

		public bool IsInRound { get; set; }
		public int Chips { get; set; } = 0;

		// Bet PER HAND (if split, you risk Bet twice)
		public int Bet { get; set; } = 1;

		public BlackjackResult Result { get; set; } = BlackjackResult.Pending; // main hand result (legacy)
		public BlackjackResult SplitResult { get; set; } = BlackjackResult.Pending;

		// Hand 0 is the original hand, hand 1 exists only if split
		public List<Card> Hand { get; } = new();
		public List<Card> SplitHand { get; } = new();

		public bool HasSplitThisRound => SplitHand.Count > 0;

		public int ActiveHandIndex { get; set; } = 0; // 0 or 1

		// Per-hand status
		public bool HasStood { get; set; }
		public bool IsBust { get; set; }

		public bool SplitHasStood { get; set; }
		public bool SplitIsBust { get; set; }

		public void ResetForNewRound()
		{
			IsInRound = true;
			Hand.Clear();
			SplitHand.Clear();

			ActiveHandIndex = 0;

			HasStood = false;
			IsBust = false;

			SplitHasStood = false;
			SplitIsBust = false;

			Result = BlackjackResult.Pending;
			SplitResult = BlackjackResult.Pending;
		}

		public List<Card> GetActiveHand()
			=> ActiveHandIndex == 0 ? Hand : SplitHand;

		public bool ActiveHandIsBust
			=> ActiveHandIndex == 0 ? IsBust : SplitIsBust;

		public bool ActiveHandHasStood
			=> ActiveHandIndex == 0 ? HasStood : SplitHasStood;

		public void SetActiveHandBust(bool bust)
		{
			if (ActiveHandIndex == 0) IsBust = bust;
			else SplitIsBust = bust;
		}

		public void SetActiveHandStood(bool stood)
		{
			if (ActiveHandIndex == 0) HasStood = stood;
			else SplitHasStood = stood;
		}

		public bool IsDoneWithAllHands()
		{
			// if no split => only main
			if (!HasSplitThisRound)
				return IsBust || HasStood;

			// split => both must be done
			bool mainDone = IsBust || HasStood;
			bool splitDone = SplitIsBust || SplitHasStood;
			return mainDone && splitDone;
		}
	}


	/// <summary>
	/// Core blackjack logic: 1–4 players vs dealer, no UI/no networking.
	/// </summary>
	public sealed class BlackjackEngine
	{
		private readonly Random _rng;
		private readonly Deck _deck;
		private readonly List<BlackjackPlayerState> _players = new();
		private readonly List<Card> _dealerHand = new();

		private int _currentPlayerIndex = -1;

		public BlackjackPhase Phase { get; private set; } = BlackjackPhase.Lobby;

		/// <summary>
		/// ID of current turn player (null outside PlayerTurns).
		/// </summary>
		public string? CurrentPlayerId
			=> Phase == BlackjackPhase.PlayerTurns && _currentPlayerIndex >= 0 && _currentPlayerIndex < _players.Count
				? _players[_currentPlayerIndex].PlayerId
				: null;

		public IReadOnlyList<BlackjackPlayerState> Players => _players;
		public IReadOnlyList<Card> DealerHand => _dealerHand;

		public bool DealerRevealed =>
			Phase == BlackjackPhase.DealerTurn || Phase == BlackjackPhase.RoundResults;

		public BlackjackEngine(Random rng)
		{
			_rng = rng;
			_deck = new Deck(rng);
		}

		/// <summary>
		/// Ensure a player exists in engine (called when room gets a player).
		/// </summary>
		public void EnsurePlayer(string playerId)
		{
			if (_players.Exists(p => p.PlayerId == playerId))
				return;

			_players.Add(new BlackjackPlayerState
			{
				PlayerId = playerId,
				IsInRound = false
			});
		}

		/// <summary>
		/// Remove player from engine (for disconnect).
		/// </summary>
		public void RemovePlayer(string playerId)
		{
			_players.RemoveAll(p => p.PlayerId == playerId);
			// Optional: end round if everyone left, etc.
		}
		public bool CanSplit(string playerId)
		{
			if (Phase != BlackjackPhase.PlayerTurns) return false;
			if (CurrentPlayerId != playerId) return false;

			var p = GetPlayer(playerId);
			if (p == null) return false;

			// only allow one split
			if (p.HasSplitThisRound) return false;

			// must be on main hand, with exactly 2 cards
			if (p.ActiveHandIndex != 0) return false;
			if (p.Hand.Count != 2) return false;

			// ranks must match
			if ((int)p.Hand[0].Rank != (int)p.Hand[1].Rank) return false;

			// must have enough chips to cover BOTH bets (since we don't deduct upfront)
			return p.Chips >= (p.Bet * 2);
		}

		/// <summary>
		/// Start a new round. Called by handler when host presses Start.
		/// </summary>
		public void StartRoundInternal()
		{
			if (_players.Count == 0)
				return;

			Phase = BlackjackPhase.Dealing;

			_deck.Reset();
			_deck.Shuffle();

			_dealerHand.Clear();
			foreach (var p in _players)
			{
				p.ResetForNewRound();
				// keep p.Bet as-is if using betting phase; otherwise set default:
				// p.Bet = 1;
			}

			// Deal initial 2 cards to each player (into main hand), then dealer
			for (int i = 0; i < 2; i++)
			{
				foreach (var p in _players)
				{
					if (_deck.TryDraw(out var card))
						p.Hand.Add(card);
				}

				if (_deck.TryDraw(out var dealerCard))
					_dealerHand.Add(dealerCard);
			}


			// After dealing, go straight into player turns
			Phase = BlackjackPhase.PlayerTurns;
			_currentPlayerIndex = FindNextActivePlayerIndex(-1);

			if (_currentPlayerIndex < 0)
			{
				// No active players => jump to dealer
				StartDealerTurn();
			}
		}

		/// <summary>
		/// Player action: Hit / Stand.
		/// </summary>
		public void ApplyAction(string playerId, BlackjackActionType action)
		{
			if (Phase != BlackjackPhase.PlayerTurns)
				return;

			int idx = _players.FindIndex(p => p.PlayerId == playerId);
			if (idx < 0 || idx != _currentPlayerIndex)
				return;

			var pState = _players[idx];
			if (!pState.IsInRound)
				return;

			// If active hand already done, ignore
			if (pState.ActiveHandIsBust || pState.ActiveHandHasStood)
				return;

			switch (action)
			{
				case BlackjackActionType.Hit:
					DoHit(pState);
					break;

				case BlackjackActionType.Stand:
					pState.SetActiveHandStood(true);
					break;

				case BlackjackActionType.Split:
					DoSplit(pState); // handles eligibility internally
					break;
			}

			// After action, advance within player or to next player if needed
			AdvanceTurnIfNeeded();
		}

		private void AdvanceTurnIfNeeded()
		{
			if (_currentPlayerIndex < 0 || _currentPlayerIndex >= _players.Count)
				return;

			var p = _players[_currentPlayerIndex];

			// If current active hand finished, move to next hand if split
			if (p.ActiveHandIsBust || p.ActiveHandHasStood)
			{
				if (p.HasSplitThisRound && p.ActiveHandIndex == 0)
				{
					// Move to split hand if it isn't done
					if (!(p.SplitIsBust || p.SplitHasStood))
					{
						p.ActiveHandIndex = 1;
						return;
					}
				}

				// If split hand is done too (or no split), go to next player
				if (p.IsDoneWithAllHands())
				{
					_currentPlayerIndex = FindNextActivePlayerIndex(_currentPlayerIndex);
					if (_currentPlayerIndex < 0)
						StartDealerTurn();
				}
			}
		}

		public void BeginBettingRound(IReadOnlyList<string> seatedPlayerIds)
		{
			if (seatedPlayerIds == null || seatedPlayerIds.Count == 0)
				return;

			// preserve chips for existing players
			var chipsMap = new Dictionary<string, int>();
			foreach (var p in _players)
				chipsMap[p.PlayerId] = p.Chips;

			_players.Clear();
			foreach (var pid in seatedPlayerIds)
			{
				EnsurePlayer(pid);
				var p = GetPlayer(pid);
				if (p != null && chipsMap.TryGetValue(pid, out var chips))
					p.Chips = chips;

				// reset per-round stuff (no cards yet)
				if (p != null)
				{
					p.Hand.Clear();
					p.IsInRound = false;
					p.HasStood = false;
					p.IsBust = false;
					p.Result = BlackjackResult.Pending;
					p.Bet = 0; // betting not submitted yet
				}
			}

			_dealerHand.Clear();
			_currentPlayerIndex = -1;
			Phase = BlackjackPhase.Betting;
		}

		public bool TrySetBet(string playerId, int bet)
		{
			if (Phase != BlackjackPhase.Betting)
				return false;

			var p = GetPlayer(playerId);
			if (p == null) return false;

			if (bet <= 0) return false;
			if (bet > p.Chips) bet = p.Chips; // clamp

			p.Bet = bet;
			return true;
		}

		public void DealAfterBets()
		{
			if (Phase != BlackjackPhase.Betting)
				return;

			Phase = BlackjackPhase.Dealing;

			_deck.Reset();
			_deck.Shuffle();

			_dealerHand.Clear();

			foreach (var p in _players)
			{
				p.Hand.Clear();
				p.IsInRound = true;
				p.HasStood = false;
				p.IsBust = false;
				p.Result = BlackjackResult.Pending;

				// IMPORTANT: do NOT overwrite p.Bet here
			}

			// Deal initial 2 cards to each player, then dealer
			for (int i = 0; i < 2; i++)
			{
				foreach (var p in _players)
				{
					if (_deck.TryDraw(out var card))
						p.Hand.Add(card);
				}

				if (_deck.TryDraw(out var dealerCard))
					_dealerHand.Add(dealerCard);
			}

			Phase = BlackjackPhase.PlayerTurns;
			_currentPlayerIndex = FindNextActivePlayerIndex(-1);

			if (_currentPlayerIndex < 0)
				StartDealerTurn();
		}


		private void DoHit(BlackjackPlayerState p)
		{
			var hand = p.GetActiveHand();

			if (_deck.TryDraw(out var card))
			{
				hand.Add(card);

				int value = ComputeHandValue(hand);
				if (value > 21)
					p.SetActiveHandBust(true);
			}
		}


		private int FindNextActivePlayerIndex(int startIndex)
		{
			for (int i = startIndex + 1; i < _players.Count; i++)
			{
				var p = _players[i];
				if (p.IsInRound && !p.IsDoneWithAllHands())
					return i;
			}
			return -1;
		}

		private void DoSplit(BlackjackPlayerState p)
		{
			// validate split
			if (p.HasSplitThisRound) return;
			if (p.ActiveHandIndex != 0) return;
			if (p.Hand.Count != 2) return;
			if ((int)p.Hand[0].Rank != (int)p.Hand[1].Rank) return;
			if (p.Chips < (p.Bet * 2)) return;

			// Create split hand by moving second card
			var moved = p.Hand[1];
			p.Hand.RemoveAt(1);
			p.SplitHand.Add(moved);

			// Immediately deal 1 card to each hand (simple standard flow)
			if (_deck.TryDraw(out var c1)) p.Hand.Add(c1);
			if (_deck.TryDraw(out var c2)) p.SplitHand.Add(c2);

			// Reset statuses for both hands
			p.HasStood = false;
			p.IsBust = ComputeHandValue(p.Hand) > 21;

			p.SplitHasStood = false;
			p.SplitIsBust = ComputeHandValue(p.SplitHand) > 21;

			p.ActiveHandIndex = 0; // play main hand first
		}


		private void StartDealerTurn()
		{
			Phase = BlackjackPhase.DealerTurn;

			// Reveal hole card by simply using the full dealer hand above.
			// Dealer hits until reaching 17+.
			while (ComputeHandValue(_dealerHand) < 17)
			{
				if (!_deck.TryDraw(out var card))
					break;
				_dealerHand.Add(card);
			}

			ComputeResults();
			Phase = BlackjackPhase.RoundResults;
		}

		private void ComputeResults()
		{
			int dealerValue = ComputeHandValue(_dealerHand);
			bool dealerBust = dealerValue > 21;

			foreach (var p in _players)
			{
				if (!p.IsInRound)
				{
					p.Result = BlackjackResult.Pending;
					p.SplitResult = BlackjackResult.Pending;
					continue;
				}

				// score main hand
				p.Result = ScoreHandAndApplyChips(p, p.Hand, dealerValue, dealerBust);

				// score split hand if it exists
				if (p.HasSplitThisRound)
					p.SplitResult = ScoreHandAndApplyChips(p, p.SplitHand, dealerValue, dealerBust);
				else
					p.SplitResult = BlackjackResult.Pending;
			}
		}

		private BlackjackResult ScoreHandAndApplyChips(BlackjackPlayerState p, List<Card> hand, int dealerValue, bool dealerBust)
		{
			int playerValue = ComputeHandValue(hand);
			bool playerBust = playerValue > 21;

			if (playerBust)
			{
				p.Chips -= p.Bet;
				return BlackjackResult.Lose;
			}

			// blackjack check (only if 2 cards in that hand)
			if (playerValue == 21 && hand.Count == 2)
			{
				if (dealerValue == 21 && _dealerHand.Count == 2)
					return BlackjackResult.Push;

				p.Chips += p.Bet;
				return BlackjackResult.Blackjack;
			}

			if (dealerBust)
			{
				p.Chips += p.Bet;
				return BlackjackResult.Win;
			}

			if (playerValue > dealerValue)
			{
				p.Chips += p.Bet;
				return BlackjackResult.Win;
			}
			if (playerValue < dealerValue)
			{
				p.Chips -= p.Bet;
				return BlackjackResult.Lose;
			}

			return BlackjackResult.Push;
		}

		public BlackjackPlayerState? GetPlayer(string playerId)
	=> _players.Find(p => p.PlayerId == playerId);

		public void StartRound(IReadOnlyList<string> seatedPlayerIds)
		{
			if (seatedPlayerIds == null || seatedPlayerIds.Count == 0)
				return;

			// keep existing chip values if players already existed
			var chipsMap = new Dictionary<string, int>();
			foreach (var p in _players)
				chipsMap[p.PlayerId] = p.Chips;

			_players.Clear();
			foreach (var pid in seatedPlayerIds)
			{
				EnsurePlayer(pid);
				var p = GetPlayer(pid);
				if (p != null && chipsMap.TryGetValue(pid, out var chips))
					p.Chips = chips;
			}

			StartRoundInternal();
		}
		public void ExcludeFromRound(string playerId)
		{
			var p = GetPlayer(playerId);
			if (p == null) return;

			p.IsInRound = false;
			p.Hand.Clear();
			p.SplitHand.Clear();
			p.ActiveHandIndex = 0;

			p.HasStood = false;
			p.IsBust = false;
			p.SplitHasStood = false;
			p.SplitIsBust = false;
		}

		public void EnsureTurnIsValid()
		{
			if (Phase != BlackjackPhase.PlayerTurns) return;

			// If current index is invalid or points at someone who can't act, advance.
			while (true)
			{
				if (_currentPlayerIndex < 0 || _currentPlayerIndex >= _players.Count)
				{
					_currentPlayerIndex = FindNextActivePlayerIndex(-1);
				}
				else
				{
					var p = _players[_currentPlayerIndex];
					if (p.IsInRound && !p.IsDoneWithAllHands())
						return;

					_currentPlayerIndex = FindNextActivePlayerIndex(_currentPlayerIndex);
				}

				if (_currentPlayerIndex < 0)
				{
					StartDealerTurn();
					return;
				}
			}
		}

		public static int ComputeHandValue(IReadOnlyList<Card> hand)
		{
			int total = 0;
			int aces = 0;

			foreach (var card in hand)
			{
				int rank = (int)card.Rank;
				if (rank >= 11 && rank <= 13)
				{
					total += 10; // JQK
				}
				else if (rank == 14) // Ace
				{
					aces++;
					total += 11;
				}
				else
				{
					total += rank;
				}
			}

			while (total > 21 && aces > 0)
			{
				total -= 10; // Ace: 11 -> 1
				aces--;
			}

			return total;
		}
		public void ResetToLobby()
		{
			Phase = BlackjackPhase.Lobby;
			_dealerHand.Clear();
			foreach (var p in _players)
			{
				p.Hand.Clear();
				p.IsInRound = false;
				p.HasStood = false;
				p.IsBust = false;
				p.Result = BlackjackResult.Pending;
				p.Bet = 1;
			}
			_currentPlayerIndex = -1;
		}

	}
}

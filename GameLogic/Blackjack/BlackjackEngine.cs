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
		public bool HasStood { get; set; }
		public bool IsBust { get; set; }
		public int Chips { get; set; } = 0;
		public int Bet { get; set; } = 1; // fixed for now
		public BlackjackResult Result { get; set; } = BlackjackResult.Pending;
		public List<Card> Hand { get; } = new();
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

		/// <summary>
		/// Start a new round. Called by handler when host presses Start.
		/// </summary>
		public void StartRound()
		{
			if (_players.Count == 0)
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
				p.Bet = 1;
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
			if (!pState.IsInRound || pState.IsBust || pState.HasStood)
				return;

			switch (action)
			{
				case BlackjackActionType.Hit:
					DoHit(pState);
					break;

				case BlackjackActionType.Stand:
					pState.HasStood = true;
					break;
			}

			// Determine if this player is done
			if (pState.IsBust || pState.HasStood)
			{
				_currentPlayerIndex = FindNextActivePlayerIndex(_currentPlayerIndex);
				if (_currentPlayerIndex < 0)
				{
					StartDealerTurn();
				}
			}
		}

		private void DoHit(BlackjackPlayerState p)
		{
			if (_deck.TryDraw(out var card))
			{
				p.Hand.Add(card);
				int value = ComputeHandValue(p.Hand);
				if (value > 21)
				{
					p.IsBust = true;
				}
			}
		}

		private int FindNextActivePlayerIndex(int startIndex)
		{
			for (int i = startIndex + 1; i < _players.Count; i++)
			{
				var p = _players[i];
				if (p.IsInRound && !p.IsBust && !p.HasStood)
					return i;
			}
			return -1;
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
					continue;
				}

				int playerValue = ComputeHandValue(p.Hand);

				if (p.IsBust)
				{
					p.Result = BlackjackResult.Lose;
					p.Chips -= p.Bet;
				}
				else if (playerValue == 21 && p.Hand.Count == 2)
				{
					// Simple blackjack rule: 1.5x payout could be added later
					if (dealerValue == 21 && _dealerHand.Count == 2)
					{
						p.Result = BlackjackResult.Push;
					}
					else
					{
						p.Result = BlackjackResult.Blackjack;
						p.Chips += p.Bet; // or 1.5x
					}
				}
				else if (dealerBust)
				{
					p.Result = BlackjackResult.Win;
					p.Chips += p.Bet;
				}
				else
				{
					if (playerValue > dealerValue)
					{
						p.Result = BlackjackResult.Win;
						p.Chips += p.Bet;
					}
					else if (playerValue < dealerValue)
					{
						p.Result = BlackjackResult.Lose;
						p.Chips -= p.Bet;
					}
					else
					{
						p.Result = BlackjackResult.Push;
					}
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
	}
}

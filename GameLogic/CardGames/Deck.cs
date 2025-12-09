// GameLogic/CardGames/Deck.cs
using System;
using System.Collections.Generic;

namespace GameLogic.CardGames
{
	/// <summary>
	/// Represents a standard 52-card deck with basic operations
	/// like shuffle and draw. Intended to be used per-room/per-game,
	/// not as a global shared deck.
	/// </summary>
	public sealed class Deck
	{
		private readonly List<Card> _cards = new();
		private readonly Random _rng;

		public Deck(Random? rng = null)
		{
			_rng = rng ?? new Random();
			Reset();
		}

		/// <summary>Number of cards remaining in the deck.</summary>
		public int Count => _cards.Count;

		/// <summary>
		/// Clears and rebuilds the deck as a standard 52-card deck,
		/// then shuffles.
		/// </summary>
		public void Reset()
		{
			_cards.Clear();

			foreach (CardSuit suit in Enum.GetValues(typeof(CardSuit)))
			{
				foreach (CardRank rank in Enum.GetValues(typeof(CardRank)))
				{
					_cards.Add(new Card(suit, rank));
				}
			}

			Shuffle();
		}

		/// <summary>Shuffles the deck in-place (Fisher–Yates).</summary>
		public void Shuffle()
		{
			for (int i = _cards.Count - 1; i > 0; i--)
			{
				int j = _rng.Next(i + 1);
				(_cards[i], _cards[j]) = (_cards[j], _cards[i]);
			}
		}

		/// <summary>Attempts to draw a single card from the top of the deck.</summary>
		public bool TryDraw(out Card card)
		{
			if (_cards.Count == 0)
			{
				card = default;
				return false;
			}

			int lastIndex = _cards.Count - 1;
			card = _cards[lastIndex];
			_cards.RemoveAt(lastIndex);
			return true;
		}

		/// <summary>Draws exactly count cards or fewer if the deck is exhausted.</summary>
		public IReadOnlyList<Card> DrawMany(int count)
		{
			var result = new List<Card>(count);

			for (int i = 0; i < count && _cards.Count > 0; i++)
			{
				int lastIndex = _cards.Count - 1;
				var card = _cards[lastIndex];
				_cards.RemoveAt(lastIndex);
				result.Add(card);
			}

			return result;
		}
	}
}

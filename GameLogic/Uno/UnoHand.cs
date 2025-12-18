using System;
using System.Collections.Generic;

namespace GameLogic.Uno
{
	public sealed class UnoHand
	{
		private readonly List<UnoCard> _cards = new List<UnoCard>();

		public int Count => _cards.Count;

		public UnoCard this[int index] => _cards[index];

		public IReadOnlyList<UnoCard> Cards => _cards.AsReadOnly();

		public void Clear() => _cards.Clear();

		public void Add(UnoCard card) => _cards.Add(card);

		public void AddRange(IEnumerable<UnoCard> cards)
		{
			foreach (var c in cards)
				_cards.Add(c);
		}

		public UnoCard RemoveAt(int index)
		{
			var c = _cards[index];
			_cards.RemoveAt(index);
			return c;
		}

		public bool Remove(UnoCard card) => _cards.Remove(card);

		public bool Contains(UnoCard card) => _cards.Contains(card);

		/// <summary>
		/// Returns true if the given card can be played on the current discard-top,
		/// using the currentActiveColor (important when last card was a Wild).
		/// Rule here: playable if Wild OR matches active color OR matches value.
		/// </summary>
		public static bool IsPlayable(UnoCard candidate, UnoCard topOfPile, CardColor currentActiveColor)
		{
			// Wilds can always be played
			if (candidate.IsWild)
				return true;

			// If top is wild, color is determined by currentActiveColor (set by player who played wild)
			CardColor effectiveTopColor = topOfPile.IsWild ? currentActiveColor : topOfPile.Color;

			// Match by color
			if (candidate.Color == effectiveTopColor)
				return true;

			// Match by value (number or action)
			if (candidate.Value == topOfPile.Value)
				return true;

			return false;
		}

		public bool HasPlayable(UnoCard topOfPile, CardColor currentActiveColor)
		{
			for (int i = 0; i < _cards.Count; i++)
			{
				if (IsPlayable(_cards[i], topOfPile, currentActiveColor))
					return true;
			}
			return false;
		}

		public List<int> GetPlayableIndices(UnoCard topOfPile, CardColor currentActiveColor)
		{
			var result = new List<int>();
			for (int i = 0; i < _cards.Count; i++)
			{
				if (IsPlayable(_cards[i], topOfPile, currentActiveColor))
					result.Add(i);
			}
			return result;
		}

		public override string ToString()
		{
			// Useful for debugging
			if (_cards.Count == 0) return "(empty hand)";

			// Example: "Red 5, Yellow Skip, Wild"
			var parts = new string[_cards.Count];
			for (int i = 0; i < _cards.Count; i++)
				parts[i] = _cards[i].ToString();

			return string.Join(", ", parts);
		}
	}
}

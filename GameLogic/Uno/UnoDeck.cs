using System;
using System.Collections.Generic;

namespace GameLogic.Uno
{
	public sealed class UnoDeck
	{
		private readonly List<UnoCard> _cards;
		private readonly Random _rng;

		public int Count => _cards.Count;

		public UnoDeck(Random? rng = null)
		{
			_rng = rng ?? new Random();
			_cards = BuildStandard108CardDeck();
			Shuffle();
		}

		public void Shuffle()
		{
			// Fisher–Yates shuffle
			for (int i = _cards.Count - 1; i > 0; i--)
			{
				int j = _rng.Next(i + 1);
				(_cards[i], _cards[j]) = (_cards[j], _cards[i]);
			}
		}

		public UnoCard Draw()
		{
			if (_cards.Count == 0)
				throw new InvalidOperationException("Deck is empty.");

			int last = _cards.Count - 1;
			UnoCard c = _cards[last];
			_cards.RemoveAt(last);
			return c;
		}

		public void AddToBottom(UnoCard card) => _cards.Insert(0, card);
		public void AddToTop(UnoCard card) => _cards.Add(card);

		public void AddRangeToBottom(IEnumerable<UnoCard> cards)
		{
			// keep order: first card in enumerable ends up closest to bottom
			_cards.InsertRange(0, new List<UnoCard>(cards));
		}

		public IReadOnlyList<UnoCard> Snapshot() => _cards.AsReadOnly();

		private static List<UnoCard> BuildStandard108CardDeck()
		{
			var list = new List<UnoCard>(108);

			// For each color: 1x zero, 2x (1..9), 2x each action (Skip/Reverse/DrawTwo)
			AddColor(list, CardColor.Red);
			AddColor(list, CardColor.Yellow);
			AddColor(list, CardColor.Green);
			AddColor(list, CardColor.Blue);

			// Wilds: 4 Wild, 4 WildDrawFour
			for (int i = 0; i < 4; i++)
				list.Add(new UnoCard(CardColor.Wild, CardValue.Wild));

			for (int i = 0; i < 4; i++)
				list.Add(new UnoCard(CardColor.Wild, CardValue.WildDrawFour));

			// Safety check: should be exactly 108
			if (list.Count != 108)
				throw new InvalidOperationException($"UNO deck build error: expected 108 cards, got {list.Count}.");

			return list;
		}

		private static void AddColor(List<UnoCard> list, CardColor color)
		{
			// 1x zero
			list.Add(new UnoCard(color, CardValue.Zero));

			// 2x each 1..9
			for (CardValue v = CardValue.One; v <= CardValue.Nine; v++)
			{
				list.Add(new UnoCard(color, v));
				list.Add(new UnoCard(color, v));
			}

			// 2x each action per color
			AddTwo(list, new UnoCard(color, CardValue.Skip));
			AddTwo(list, new UnoCard(color, CardValue.Reverse));
			AddTwo(list, new UnoCard(color, CardValue.DrawTwo));
		}

		private static void AddTwo(List<UnoCard> list, UnoCard card)
		{
			list.Add(card);
			list.Add(card);
		}
	}
}

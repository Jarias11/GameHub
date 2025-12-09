// GameLogic/CardGames/Card.cs
namespace GameLogic.CardGames
{
	public enum CardSuit
	{
		Clubs = 0,
		Diamonds = 1,
		Hearts = 2,
		Spades = 3
	}

	public enum CardRank
	{
		Two = 2,
		Three = 3,
		Four = 4,
		Five = 5,
		Six = 6,
		Seven = 7,
		Eight = 8,
		Nine = 9,
		Ten = 10,
		Jack = 11,
		Queen = 12,
		King = 13,
		Ace = 14
	}

	/// <summary>
	/// Immutable representation of a playing card.
	/// </summary>
	public readonly struct Card
	{
		public CardSuit Suit { get; }
		public CardRank Rank { get; }

		public Card(CardSuit suit, CardRank rank)
		{
			Suit = suit;
			Rank = rank;
		}

		public override string ToString()
		{
			// e.g. "A♠", "10♥", "J♦"
			string rankText = Rank switch
			{
				CardRank.Jack  => "J",
				CardRank.Queen => "Q",
				CardRank.King  => "K",
				CardRank.Ace   => "A",
				_              => ((int)Rank).ToString()
			};

			string suitText = Suit switch
			{
				CardSuit.Clubs    => "♣",
				CardSuit.Diamonds => "♦",
				CardSuit.Hearts   => "♥",
				CardSuit.Spades   => "♠",
				_                 => "?"
			};

			return $"{rankText}{suitText}";
		}
	}
}
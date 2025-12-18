using System;

namespace GameLogic.Uno
{
	public enum CardColor
	{
		Red,
		Yellow,
		Green,
		Blue,
		Wild
	}

	public enum CardValue
	{
		Zero,
		One,
		Two,
		Three,
		Four,
		Five,
		Six,
		Seven,
		Eight,
		Nine,
		Skip,
		Reverse,
		DrawTwo,
		Wild,
		WildDrawFour
	}

	public readonly struct UnoCard : IEquatable<UnoCard>
	{
		public CardColor Color { get; }
		public CardValue Value { get; }

		public UnoCard(CardColor color, CardValue value)
		{
			// Enforce valid combinations:
			// - Wild / WildDrawFour must be Color=Wild
			// - Non-wild values must NOT be Color=Wild
			bool isWildValue = value is CardValue.Wild or CardValue.WildDrawFour;

			if (isWildValue && color != CardColor.Wild)
				throw new ArgumentException("Wild cards must have Color=Wild.");

			if (!isWildValue && color == CardColor.Wild)
				throw new ArgumentException("Only wild cards may have Color=Wild.");

			Color = color;
			Value = value;
		}

		public bool IsWild => Value is CardValue.Wild or CardValue.WildDrawFour;
		public bool IsAction => Value is CardValue.Skip or CardValue.Reverse or CardValue.DrawTwo or CardValue.Wild or CardValue.WildDrawFour;
		public bool IsNumber => Value >= CardValue.Zero && Value <= CardValue.Nine;

		public int? Number =>
			Value switch
			{
				CardValue.Zero => 0,
				CardValue.One => 1,
				CardValue.Two => 2,
				CardValue.Three => 3,
				CardValue.Four => 4,
				CardValue.Five => 5,
				CardValue.Six => 6,
				CardValue.Seven => 7,
				CardValue.Eight => 8,
				CardValue.Nine => 9,
				_ => null
			};

		public override string ToString()
		{
			if (IsWild)
			{
				return Value == CardValue.Wild ? "Wild" : "Wild Draw Four";
			}

			string color = Color.ToString();
			string value = Value switch
			{
				CardValue.DrawTwo => "Draw Two",
				_ => Value.ToString()
			};

			return $"{color} {value}";
		}

		public bool Equals(UnoCard other) => Color == other.Color && Value == other.Value;
		public override bool Equals(object? obj) => obj is UnoCard other && Equals(other);
		public override int GetHashCode() => HashCode.Combine((int)Color, (int)Value);

		public static bool operator ==(UnoCard left, UnoCard right) => left.Equals(right);
		public static bool operator !=(UnoCard left, UnoCard right) => !left.Equals(right);
	}
}

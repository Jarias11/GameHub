using System;
using System.Collections.Generic;

namespace GameContracts
{
	// =========================
	// UNO DTOs / Enums
	// =========================

	public enum UnoCardColor
	{
		Red,
		Yellow,
		Green,
		Blue,
		Wild
	}

	public enum UnoCardValue
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

	public sealed class UnoCardDto
	{
		public UnoCardColor Color { get; set; }
		public UnoCardValue Value { get; set; }

		public override string ToString() => $"{Color} {Value}";
	}

	public enum UnoTurnPhase
	{
		WaitingForStart,
		NormalTurn,
		AwaitingWildColorChoice,
		GameOver
	}

	// =========================
	// Client -> Server Payloads
	// =========================

	public sealed class UnoStartGamePayload
	{
		public string RoomCode { get; set; } = string.Empty;
	}

	/// <summary>
	/// Play a card from your hand by index.
	/// If the card is Wild/WildDrawFour you may include ChosenColor.
	/// </summary>
	public sealed class UnoPlayCardPayload
	{
		public int HandIndex { get; set; }

		// Optional: if playing Wild / WildDrawFour
		public UnoCardColor? ChosenColor { get; set; }
	}

	/// <summary>
	/// Draw action.
	/// - If a draw penalty is pending on you, this draws the entire penalty and ends your turn.
	/// - Otherwise draws 1 and you may continue drawing or play.
	/// </summary>
	public sealed class UnoDrawPayload
	{
		// Reserved for future options; keep for extensibility.
	}

	/// <summary>
	/// Used if you played a wild WITHOUT including ChosenColor in UnoPlayCardPayload.
	/// </summary>
	public sealed class UnoChooseColorPayload
	{
		public UnoCardColor ChosenColor { get; set; }
	}

	/// <summary>
	/// Press the UNO button (may be early). It "arms" UNO for this player.
	/// If you end your turn with 1 card and didn't press it, you draw 1 penalty card.
	/// </summary>
	public sealed class UnoCallUnoPayload
	{
		public bool IsSayingUno { get; set; } = true;
	}

	// =========================
	// Server -> Client Payloads
	// =========================

	public sealed class UnoPlayerPublicDto
	{
		public string PlayerId { get; set; } = string.Empty;
		public int HandCount { get; set; }
		public bool SaidUnoArmed { get; set; }
	}

	public sealed class UnoStatePayload
	{
		public string RoomCode { get; set; } = string.Empty;

		public UnoTurnPhase Phase { get; set; }

		public string CurrentPlayerId { get; set; } = string.Empty;

		// Direction: +1 = clockwise, -1 = counter-clockwise
		public int Direction { get; set; }

		public UnoCardColor ActiveColor { get; set; }

		public UnoCardDto? TopDiscard { get; set; }

		public int DeckCount { get; set; }
		public int DiscardCount { get; set; }

		// Draw stacking info
		public int PendingDrawCount { get; set; }
		public UnoCardValue? PendingDrawType { get; set; } // DrawTwo or WildDrawFour

		// Players
		public List<string> PlayersInOrder { get; set; } = new();
		public List<UnoPlayerPublicDto> PlayersPublic { get; set; } = new();

		// PRIVATE for the receiving player
		public List<UnoCardDto> YourHand { get; set; } = new();

		// Helpful UI info
		public bool IsYourTurn { get; set; }
		public bool YouMayStackDraw { get; set; }
		public bool YouHavePlayableCard { get; set; }
		public string? WinnerPlayerId { get; set; }
	}

	public sealed class UnoErrorPayload
	{
		public string Message { get; set; } = string.Empty;
	}
	public sealed class UnoPlayCardsPayload
	{
		public List<int> HandIndices { get; set; } = new();

		// Optional for Wild / WildDrawFour (if you ever allow those in batches)
		public UnoCardColor? ChosenColor { get; set; }
	}

}

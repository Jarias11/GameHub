using System;
using System.Collections.Generic;
using System.Linq;
using GameContracts;

namespace GameLogic.Uno
{
	public static class UnoEngine
	{
		private const int StartingHandSize = 7;

		// =========================
		// Public API
		// =========================

		public static void StartGame(UnoRoomState state, IReadOnlyList<string> playersInRoom)
		{
			if (playersInRoom.Count < 2 || playersInRoom.Count > 4)
				throw new InvalidOperationException("UNO requires 2 to 4 players.");

			state.Players.Clear();
			state.Players.AddRange(playersInRoom);

			state.Hands.Clear();
			state.SaidUnoArmed.Clear();
			state.DiscardPile.Clear();

			state.Deck = new UnoDeck(state.Rng);

			state.Direction = +1;
			state.PendingDrawCount = 0;
			state.PendingDrawType = null;

			state.AwaitingColorChoiceFromPlayerId = null;
			state.WinnerPlayerId = null;
			state.Phase = UnoPhase.NormalTurn;

			// init hands
			foreach (var pid in state.Players)
				state.Hands[pid] = new UnoHand();

			// deal 7 each
			for (int i = 0; i < StartingHandSize; i++)
			{
				foreach (var pid in state.Players)
					state.Hands[pid].Add(DrawWithReshuffle(state));
			}

			// random starting player
			state.CurrentPlayerIndex = state.Rng.Next(state.Players.Count);

			// flip starting card from deck to discard
			var startCard = DrawWithReshuffle(state);
			state.DiscardPile.Add(startCard);

			// set active color (if wild, choose random to keep it simple/automatic)
			state.ActiveColor = startCard.IsWild
				? RandomNonWildColor(state.Rng)
				: startCard.Color;

			// Apply start-card effect as if it was played BEFORE the starting player takes their turn.
			// Effects target "next player after starting player" for draw/skip, etc.
			ApplyCardEffect(
				state,
				playedByPlayerId: null,              // starting flip isn't owned by a player
				card: startCard,
				chosenColor: startCard.IsWild ? state.ActiveColor : (CardColor?)null,
				applyToNextPlayerRelativeToCurrent: true);

			// Begin first player's snapshot
			BeginTurnSnapshot(state);
		}

		public static bool TryPlayCard(UnoRoomState state, string playerId, int handIndex, CardColor? chosenColor, out string? error)
		{
			error = null;

			if (state.Phase == UnoPhase.GameOver)
			{
				error = "Game is over.";
				return false;
			}

			if (state.Phase == UnoPhase.AwaitingWildColorChoice)
			{
				error = "A wild color must be chosen before playing another card.";
				return false;
			}

			if (!IsPlayersTurn(state, playerId))
			{
				error = "Not your turn.";
				return false;
			}

			if (!state.Hands.TryGetValue(playerId, out var hand))
			{
				error = "Invalid player.";
				return false;
			}

			// If a draw penalty is pending on this player, they may ONLY stack (if eligible),
			// otherwise they must draw the penalty (via TryDraw).
			if (IsPenaltyTurn(state))
			{
				var card = SafePeekHand(hand, handIndex, out error);
				if (error != null) return false;

				if (!IsValidStackPlay(state, playerId, card))
				{
					error = "You must draw the penalty, or stack with a valid Draw card you had at turn start.";
					return false;
				}

				// stacked draw cards are always playable in this mode
				hand.RemoveAt(handIndex);
				state.DiscardPile.Add(card);

				// Wild draw four stacking still needs a chosen color
				if (card.Value == CardValue.WildDrawFour)
				{
					if (chosenColor == null || chosenColor == CardColor.Wild)
					{
						// pause until color chosen
						state.Phase = UnoPhase.AwaitingWildColorChoice;
						state.AwaitingColorChoiceFromPlayerId = playerId;

						// Keep active color unchanged until chosen
						return true;
					}
					state.ActiveColor = chosenColor.Value;
				}

				// Increase penalty
				if (card.Value == CardValue.DrawTwo)
				{
					state.PendingDrawType = CardValue.DrawTwo;
					state.PendingDrawCount += 2;
				}
				else // WildDrawFour
				{
					state.PendingDrawType = CardValue.WildDrawFour;
					state.PendingDrawCount += 4;
				}

				// Stacking DOES NOT advance turn; it passes the penalty to the next player.
				AdvanceTurn(state);
				return true;
			}

			// Normal play: must be legal vs top + active color
			var top = state.TopDiscard;
			var candidate = SafePeekHand(hand, handIndex, out error);
			if (error != null) return false;

			if (!UnoHand.IsPlayable(candidate, top, state.ActiveColor))
			{
				error = "That card is not playable.";
				return false;
			}

			// Remove from hand and place on discard
			hand.RemoveAt(handIndex);
			state.DiscardPile.Add(candidate);

			// If it is wild and no color provided, pause for choose-color
			if (candidate.IsWild && (chosenColor == null || chosenColor == CardColor.Wild))
			{
				state.Phase = UnoPhase.AwaitingWildColorChoice;
				state.AwaitingColorChoiceFromPlayerId = playerId;
				// don't advance turn yet; effect finalization occurs after ChooseColor
				return true;
			}

			// Apply chosen color if needed
			if (candidate.IsWild)
				state.ActiveColor = chosenColor!.Value;
			else
				state.ActiveColor = candidate.Color;

			// Apply effect and advance turn as needed
			ApplyCardEffect(
				state,
				playedByPlayerId: playerId,
				card: candidate,
				chosenColor: candidate.IsWild ? state.ActiveColor : (CardColor?)null,
				applyToNextPlayerRelativeToCurrent: true);

			// UNO penalty check AFTER a play ends
			ApplyUnoPenaltyIfNeededAfterPlay(state, playerId);

			// Check win
			if (hand.Count == 0)
			{
				state.WinnerPlayerId = playerId;
				state.Phase = UnoPhase.GameOver;
				state.PendingDrawCount = 0;
				state.PendingDrawType = null;
				return true;
			}

			// Advance turn already handled by ApplyCardEffect for skip/draw, etc.
			// For normal number cards (no effect), we must advance once here.
			if (!candidate.IsAction || candidate.Value == CardValue.Wild)
			{
				// NOTE: Wild is "action-ish" but has no forced target; still advances like normal.
				AdvanceTurn(state);
			}

			return true;
		}

		public static bool TryDraw(UnoRoomState state, string playerId, out string? error)
		{
			error = null;

			if (state.Phase == UnoPhase.GameOver)
			{
				error = "Game is over.";
				return false;
			}

			if (state.Phase == UnoPhase.AwaitingWildColorChoice)
			{
				error = "Choose a color first.";
				return false;
			}

			if (!IsPlayersTurn(state, playerId))
			{
				error = "Not your turn.";
				return false;
			}

			if (!state.Hands.TryGetValue(playerId, out var hand))
			{
				error = "Invalid player.";
				return false;
			}

			// Penalty draw turn: draw the whole pending count and end turn
			if (IsPenaltyTurn(state))
			{
				int n = state.PendingDrawCount;
				for (int i = 0; i < n; i++)
					hand.Add(DrawWithReshuffle(state));

				// clear penalty and skip turn (penalty consumes your turn)
				state.PendingDrawCount = 0;
				state.PendingDrawType = null;

				AdvanceTurn(state);
				return true;
			}

			// Normal draw: draw 1, but you can keep drawing even if now playable (your "lie" rule)
			hand.Add(DrawWithReshuffle(state));
			state.HasDrawnThisTurn = true;

			// Your rule: If you had NO playable, you must keep drawing until you do.
			// Enforced by NOT providing any "Pass" action. You may draw forever and then play.
			return true;
		}

		public static bool TryChooseColor(UnoRoomState state, string playerId, CardColor chosenColor, out string? error)
		{
			error = null;

			if (state.Phase != UnoPhase.AwaitingWildColorChoice)
			{
				error = "No color choice is pending.";
				return false;
			}

			if (state.AwaitingColorChoiceFromPlayerId != playerId)
			{
				error = "Only the player who played the wild may choose the color.";
				return false;
			}

			if (chosenColor == CardColor.Wild)
			{
				error = "Invalid color choice.";
				return false;
			}

			state.ActiveColor = chosenColor;
			state.Phase = UnoPhase.NormalTurn;
			state.AwaitingColorChoiceFromPlayerId = null;

			// Finalize effect of the top discard (which must be wild or wild draw four)
			var top = state.TopDiscard;
			if (top.Value == CardValue.WildDrawFour)
			{
				// Set/continue penalty
				state.PendingDrawType = CardValue.WildDrawFour;

				// If this was a fresh play, PendingDrawCount might still be 0 (from play path that paused)
				if (state.PendingDrawCount == 0)
					state.PendingDrawCount = 4;

				// Pass penalty to next player
				AdvanceTurn(state);
				return true;
			}

			// Plain Wild: just advance turn
			AdvanceTurn(state);
			return true;
		}

		public static void SetUnoArmed(UnoRoomState state, string playerId, bool isArmed)
		{
			if (isArmed) state.SaidUnoArmed.Add(playerId);
			else state.SaidUnoArmed.Remove(playerId);
		}

		// =========================
		// Building a per-player view
		// =========================

		public static UnoStatePayload BuildStatePayloadForPlayer(UnoRoomState state, string forPlayerId)
		{
			var payload = new UnoStatePayload
			{
				RoomCode = state.RoomCode,
				Phase = state.Phase switch
				{
					UnoPhase.WaitingForStart => UnoTurnPhase.WaitingForStart,
					UnoPhase.NormalTurn => UnoTurnPhase.NormalTurn,
					UnoPhase.AwaitingWildColorChoice => UnoTurnPhase.AwaitingWildColorChoice,
					_ => UnoTurnPhase.GameOver
				},
				CurrentPlayerId = state.CurrentPlayerId,
				Direction = state.Direction,
				ActiveColor = ToDtoColor(state.ActiveColor),
				TopDiscard = state.DiscardPile.Count == 0 ? null : ToDto(state.TopDiscard),
				DeckCount = state.Deck.Count,
				DiscardCount = state.DiscardPile.Count,
				PendingDrawCount = state.PendingDrawCount,
				PendingDrawType = state.PendingDrawType == null ? null : ToDtoValue(state.PendingDrawType.Value),
				PlayersInOrder = new List<string>(state.Players),
				WinnerPlayerId = state.WinnerPlayerId,
				IsYourTurn = (state.CurrentPlayerId == forPlayerId)
			};

			foreach (var pid in state.Players)
			{
				payload.PlayersPublic.Add(new UnoPlayerPublicDto
				{
					PlayerId = pid,
					HandCount = state.Hands.TryGetValue(pid, out var h) ? h.Count : 0,
					SaidUnoArmed = state.SaidUnoArmed.Contains(pid)
				});
			}

			if (state.Hands.TryGetValue(forPlayerId, out var yourHand))
			{
				foreach (var c in yourHand.Cards)
					payload.YourHand.Add(ToDto(c));
			}

			// Helpful flags
			if (payload.IsYourTurn && state.Hands.TryGetValue(forPlayerId, out var h2) && state.DiscardPile.Count > 0)
			{
				payload.YouHavePlayableCard = h2.HasPlayable(state.TopDiscard, state.ActiveColor);

				// you may stack only on penalty turn, only if not drawn, and only if you had it at start
				payload.YouMayStackDraw = IsPenaltyTurn(state)
					&& state.TurnSnapshotPlayerId == forPlayerId
					&& !state.HasDrawnThisTurn
					&& (state.PendingDrawType == CardValue.DrawTwo ? state.TurnStartHadDrawTwo : state.TurnStartHadWildDrawFour);
			}

			return payload;
		}

		// =========================
		// Internal helpers
		// =========================

		private static bool IsPlayersTurn(UnoRoomState state, string playerId) =>
			state.Players.Count > 0 && state.Players[state.CurrentPlayerIndex] == playerId;

		private static bool IsPenaltyTurn(UnoRoomState state) =>
			state.PendingDrawCount > 0 && state.PendingDrawType != null;

		private static UnoCard SafePeekHand(UnoHand hand, int index, out string? error)
		{
			error = null;
			if (index < 0 || index >= hand.Count)
			{
				error = "Invalid hand index.";
				return default;
			}
			return hand[index];
		}

		private static bool IsValidStackPlay(UnoRoomState state, string playerId, UnoCard card)
		{
			// Must be penalty turn
			if (!IsPenaltyTurn(state)) return false;

			// Must be the correct responding type
			if (state.PendingDrawType == CardValue.DrawTwo && card.Value != CardValue.DrawTwo)
				return false;

			if (state.PendingDrawType == CardValue.WildDrawFour && card.Value != CardValue.WildDrawFour)
				return false;

			// Must not have drawn this turn
			if (state.HasDrawnThisTurn) return false;

			// Must have had the card at turn start (snapshot)
			if (state.TurnSnapshotPlayerId != playerId) return false;

			if (card.Value == CardValue.DrawTwo && !state.TurnStartHadDrawTwo) return false;
			if (card.Value == CardValue.WildDrawFour && !state.TurnStartHadWildDrawFour) return false;

			return true;
		}

		private static void BeginTurnSnapshot(UnoRoomState state)
		{
			var pid = state.CurrentPlayerId;
			state.TurnSnapshotPlayerId = pid;
			state.HasDrawnThisTurn = false;

			if (state.Hands.TryGetValue(pid, out var hand))
			{
				state.TurnStartHadDrawTwo = false;
				state.TurnStartHadWildDrawFour = false;

				for (int i = 0; i < hand.Count; i++)
				{
					if (hand[i].Value == CardValue.DrawTwo) state.TurnStartHadDrawTwo = true;
					if (hand[i].Value == CardValue.WildDrawFour) state.TurnStartHadWildDrawFour = true;
				}
			}
			else
			{
				state.TurnStartHadDrawTwo = false;
				state.TurnStartHadWildDrawFour = false;
			}
		}

		private static void AdvanceTurn(UnoRoomState state)
		{
			if (state.Phase == UnoPhase.GameOver) return;

			int n = state.Players.Count;
			if (n == 0) return;

			state.CurrentPlayerIndex = Mod(state.CurrentPlayerIndex + state.Direction, n);

			// new player turn snapshot
			BeginTurnSnapshot(state);
		}

		
		private static void SkipOnce(UnoRoomState state)
		{
			// Skip = move past the next player
			AdvanceTurn(state); // to skipped player
			AdvanceTurn(state); // to next after skipped
		}

		private static void ReverseOnce(UnoRoomState state)
		{
			ReverseDirection(state);

			// 2-player reverse acts like skip
			if (state.Players.Count == 2)
			{
				SkipOnce(state);
				return;
			}

			// Normal reverse still passes turn (in new direction)
			AdvanceTurn(state);
		}

		private static void ReverseDirection(UnoRoomState state)
		{
			state.Direction = -state.Direction;
		}

		private static void ApplyCardEffect(
	UnoRoomState state,
	string? playedByPlayerId,
	UnoCard card,
	CardColor? chosenColor,
	bool applyToNextPlayerRelativeToCurrent)
		{
			switch (card.Value)
			{
				case CardValue.Skip:
					SkipOnce(state);
					break;

				case CardValue.Reverse:
					ReverseOnce(state);
					break;

				case CardValue.DrawTwo:
					state.PendingDrawType = CardValue.DrawTwo;
					state.PendingDrawCount += 2;
					AdvanceTurn(state); // move to victim (penalty turn)
					break;

				case CardValue.Wild:
					if (chosenColor.HasValue) state.ActiveColor = chosenColor.Value;
					break;

				case CardValue.WildDrawFour:
					if (chosenColor.HasValue) state.ActiveColor = chosenColor.Value;
					state.PendingDrawType = CardValue.WildDrawFour;
					state.PendingDrawCount += 4;
					AdvanceTurn(state); // move to victim
					break;
			}
		}

		private static void ApplyUnoPenaltyIfNeededAfterPlay(UnoRoomState state, string playerId)
		{
			if (!state.Hands.TryGetValue(playerId, out var hand))
				return;

			// If they ended the play with exactly 1 card, they must have pressed UNO beforehand
			if (hand.Count == 1)
			{
				if (!state.SaidUnoArmed.Contains(playerId))
				{
					// penalty: draw 1
					hand.Add(DrawWithReshuffle(state));
				}
			}

			// Clear UNO "armed" unless they are actually sitting at 1 card
			if (hand.Count != 1)
				state.SaidUnoArmed.Remove(playerId);
		}
		public static bool TryPlayCards(
	UnoRoomState state,
	string playerId,
	IReadOnlyList<int> handIndices,
	CardColor? chosenColor,
	out string? error)
		{
			error = null;

			if (handIndices == null || handIndices.Count == 0) { error = "No cards selected."; return false; }
			if (state.Phase == UnoPhase.GameOver) { error = "Game is over."; return false; }
			if (state.Phase == UnoPhase.AwaitingWildColorChoice) { error = "Choose a color first."; return false; }
			if (!IsPlayersTurn(state, playerId)) { error = "Not your turn."; return false; }
			if (!state.Hands.TryGetValue(playerId, out var hand)) { error = "Invalid player."; return false; }

			// Validate indices: unique + in range
			var seen = new HashSet<int>();
			for (int i = 0; i < handIndices.Count; i++)
			{
				int idx = handIndices[i];
				if (idx < 0 || idx >= hand.Count) { error = "Invalid hand index."; return false; }
				if (!seen.Add(idx)) { error = "Duplicate card index selected."; return false; }
			}

			// IMPORTANT: play order is exactly what client sent
			var playOrder = handIndices.ToList();
			var first = hand[playOrder[0]];
			var batchValue = first.Value;
			int k = playOrder.Count;

			// =========================
			// RULES: penalty turn vs normal turn
			// =========================
			bool penaltyTurn = IsPenaltyTurn(state);

			if (penaltyTurn)
			{
				// must be stacking the correct draw type ONLY
				if (state.PendingDrawType == CardValue.DrawTwo && batchValue != CardValue.DrawTwo)
				{
					error = "You must stack +2 cards or draw the penalty.";
					return false;
				}
				if (state.PendingDrawType == CardValue.WildDrawFour && batchValue != CardValue.WildDrawFour)
				{
					error = "You must stack +4 cards or draw the penalty.";
					return false;
				}

				// must not have drawn this turn
				if (state.HasDrawnThisTurn) { error = "You already drew; you can't stack now."; return false; }

				// must have had the stack type at turn start
				if (state.TurnSnapshotPlayerId != playerId) { error = "Invalid stacking snapshot."; return false; }
				if (batchValue == CardValue.DrawTwo && !state.TurnStartHadDrawTwo) { error = "You didn't have a +2 at turn start."; return false; }
				if (batchValue == CardValue.WildDrawFour && !state.TurnStartHadWildDrawFour) { error = "You didn't have a +4 at turn start."; return false; }
			}
			else
			{
				// Normal turn: first must be playable (wild always playable)
				var top = state.TopDiscard;

				if (!UnoHand.IsPlayable(first, top, state.ActiveColor))
				{
					error = "First selected card is not playable.";
					return false;
				}
			}

			// House rule: all cards after first must share SAME value
			for (int i = 1; i < playOrder.Count; i++)
			{
				var c = hand[playOrder[i]];
				if (c.Value != batchValue) { error = "All selected cards must have the same value."; return false; }
			}

			// Capture cards to play IN ORDER
			var cardsToPlay = new List<UnoCard>(k);
			for (int i = 0; i < k; i++)
				cardsToPlay.Add(hand[playOrder[i]]);

			// Remove from hand safely (descending indices)
			var removeDesc = playOrder.OrderByDescending(x => x).ToList();
			for (int i = 0; i < removeDesc.Count; i++)
				hand.RemoveAt(removeDesc[i]);

			// Add to discard IN PLAY ORDER
			for (int i = 0; i < cardsToPlay.Count; i++)
				state.DiscardPile.Add(cardsToPlay[i]);

			// =========================
			// EFFECTS
			// =========================

			// If batch is Wild or +4 and no chosenColor, pause ONCE
			bool needsColorChoice =
				(batchValue == CardValue.Wild || batchValue == CardValue.WildDrawFour) &&
				(!chosenColor.HasValue || chosenColor.Value == CardColor.Wild);

			if (needsColorChoice)
			{
				// Wild/+4: only ONE effect no matter how many were played in this batch
				if (batchValue == CardValue.WildDrawFour)
				{
					state.PendingDrawType = CardValue.WildDrawFour;

					// stack +4 per card in the batch
					state.PendingDrawCount += 4 * k;
				}

				state.Phase = UnoPhase.AwaitingWildColorChoice;
				state.AwaitingColorChoiceFromPlayerId = playerId;

				ApplyUnoPenaltyIfNeededAfterPlay(state, playerId);

				if (hand.Count == 0)
				{
					state.WinnerPlayerId = playerId;
					state.Phase = UnoPhase.GameOver;
					state.PendingDrawCount = 0;
					state.PendingDrawType = null;
				}

				return true;
			}


			// Chosen color provided (or not needed): update active color
			if (batchValue == CardValue.Wild || batchValue == CardValue.WildDrawFour)
				state.ActiveColor = chosenColor!.Value;
			else
				state.ActiveColor = cardsToPlay[cardsToPlay.Count - 1].Color;

			switch (batchValue)
			{
				case CardValue.Wild:
					// only one effect: color already set above, just advance once
					AdvanceTurn(state);
					break;

				case CardValue.WildDrawFour:
					// stack +4 per card in the batch
					state.PendingDrawType = CardValue.WildDrawFour;
					state.PendingDrawCount += 4 * k;
					AdvanceTurn(state); // victim
					break;

				case CardValue.DrawTwo:
					// still stacks (unchanged)
					state.PendingDrawType = CardValue.DrawTwo;
					state.PendingDrawCount += 2 * k;
					AdvanceTurn(state); // victim
					break;

				case CardValue.Skip:
					// stacked skip: advance (1 + k) players total
					AdvanceSteps(state, 1 + k);
					break;

				case CardValue.Reverse:
					// flip direction as many times as reverses played
					for (int i = 0; i < k; i++)
						ReverseDirection(state);

					// 2-player reverse behaves like skip (keep your current behavior),
					// and it should happen per reverse if you're stacking reverses.
					if (state.Players.Count == 2)
					{
						for (int i = 0; i < k; i++)
							SkipOnce(state);
					}
					else
					{
						// after all reverses resolved, pass turn once in the final direction
						AdvanceTurn(state);
					}
					break;

				default:
					AdvanceTurn(state);
					break;
			}


			ApplyUnoPenaltyIfNeededAfterPlay(state, playerId);

			if (hand.Count == 0)
			{
				state.WinnerPlayerId = playerId;
				state.Phase = UnoPhase.GameOver;
				state.PendingDrawCount = 0;
				state.PendingDrawType = null;
				return true;
			}

			return true;
		}

		private static void AdvanceSteps(UnoRoomState state, int steps)
		{
			for (int i = 0; i < steps; i++)
				AdvanceTurn(state);
		}


		private static UnoCard DrawWithReshuffle(UnoRoomState state)
		{
			if (state.Deck.Count > 0)
				return state.Deck.Draw();

			// Rebuild deck from discards (keep top)
			if (state.DiscardPile.Count <= 1)
				throw new InvalidOperationException("No cards available to draw.");

			var top = state.TopDiscard;

			var toRecycle = new List<UnoCard>(state.DiscardPile.Count - 1);
			for (int i = 0; i < state.DiscardPile.Count - 1; i++)
				toRecycle.Add(state.DiscardPile[i]);

			state.DiscardPile.Clear();
			state.DiscardPile.Add(top);

			// Put recycled to deck bottom then shuffle
			state.Deck.AddRangeToBottom(toRecycle);
			state.Deck.Shuffle();

			return state.Deck.Draw();
		}

		private static int Mod(int x, int m)
		{
			int r = x % m;
			return r < 0 ? r + m : r;
		}

		private static CardColor RandomNonWildColor(Random rng)
		{
			int v = rng.Next(4);
			return v switch
			{
				0 => CardColor.Red,
				1 => CardColor.Yellow,
				2 => CardColor.Green,
				_ => CardColor.Blue
			};
		}

		// =========================
		// DTO Conversions
		// =========================

		private static UnoCardDto ToDto(UnoCard c) =>
			new UnoCardDto { Color = ToDtoColor(c.Color), Value = ToDtoValue(c.Value) };

		private static UnoCardColor ToDtoColor(CardColor c) =>
			c switch
			{
				CardColor.Red => UnoCardColor.Red,
				CardColor.Yellow => UnoCardColor.Yellow,
				CardColor.Green => UnoCardColor.Green,
				CardColor.Blue => UnoCardColor.Blue,
				_ => UnoCardColor.Wild
			};

		private static UnoCardValue ToDtoValue(CardValue v) =>
			v switch
			{
				CardValue.Zero => UnoCardValue.Zero,
				CardValue.One => UnoCardValue.One,
				CardValue.Two => UnoCardValue.Two,
				CardValue.Three => UnoCardValue.Three,
				CardValue.Four => UnoCardValue.Four,
				CardValue.Five => UnoCardValue.Five,
				CardValue.Six => UnoCardValue.Six,
				CardValue.Seven => UnoCardValue.Seven,
				CardValue.Eight => UnoCardValue.Eight,
				CardValue.Nine => UnoCardValue.Nine,
				CardValue.Skip => UnoCardValue.Skip,
				CardValue.Reverse => UnoCardValue.Reverse,
				CardValue.DrawTwo => UnoCardValue.DrawTwo,
				CardValue.Wild => UnoCardValue.Wild,
				_ => UnoCardValue.WildDrawFour
			};
	}
}

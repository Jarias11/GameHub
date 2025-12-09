// GameLogic/War/WarEngine.cs
using System;
using System.Collections.Generic;
using GameLogic.CardGames;

namespace GameLogic.War
{
	public sealed class WarEngine
	{
		public enum WarState
		{
			ChoosingSide,
			Dealing,
			WaitingForPlayerClick,
			Countdown,
			ShowingBattle,
			WarFaceDown,
			RoundResult,
			GameOver
		}

		public enum RoundWinner
		{
			None,
			Player,
			Opponent,
			Tie
		}

		public enum BattleAnimPhase
		{
			None,
			MoveToCenter,
			FaceDownIdle,
			Flip,
			AfterFlipPause,
			MoveToWinner
		}

		// ---- public constants for timings (engine + UI can share) ----
		public const float DealAnimDuration = 0.0625f;
		public const float MoveToCenterDuration = 0.175f;
		public const float FlipDuration = 0.2f;
		public const float AfterFlipPauseDuration = 0.4f;
		public const float MoveToWinnerDuration = 0.2f;
		public const float WarBurnStepDuration = 0.125f;
		public const float CountdownStartValue = 3f;
		private const int RoundsBeforeShuffle = 5;

		private readonly Random _rng;
		private readonly Deck _centerDeck;
		private readonly Queue<Card> _leftDeck = new();
		private readonly Queue<Card> _rightDeck = new();
		private readonly List<Card> _battlePile = new();

		private bool _playerOnLeft = true;
		private bool _sideSelected;

		private WarState _state = WarState.ChoosingSide;
		private RoundWinner _lastRoundWinner = RoundWinner.None;
		private BattleAnimPhase _battlePhase = BattleAnimPhase.None;

		private int _roundCount;

		// countdown
		private float _countdownValue;
		private float _countdownTimer;

		// war
		private int _warFaceDownRemaining;
		private int _warFaceDownPlaced;
		private float _warBurnTimer;

		// battle cards
		private Card? _leftFaceUp;
		private Card? _rightFaceUp;
		private bool _pendingWinnerIsLeft;

		// round / game over timers
		private float _roundResultTimer;

		// dealing animation
		private Card? _dealCardInFlight;
		private float _dealAnimTimer;

		// battle animation
		private float _battleAnimTimer;

		// ready flags
		private bool _playerReady;
		private bool _opponentReady;
		public bool RequireBothReady { get; set; } = false;

		// ---- public status text for UI ----
		public string SideStatusText { get; private set; } = "No side selected.";
		public string BottomStatusText { get; private set; } = "Click Left or Right to start.";

		// ---- public read-only state for UI / networking ----
		public WarState State => _state;
		public RoundWinner LastRoundWinner => _lastRoundWinner;
		public BattleAnimPhase CurrentBattlePhase => _battlePhase;

		public bool PlayerOnLeft => _playerOnLeft;
		public bool SideSelected => _sideSelected;

		public int LeftDeckCount => _leftDeck.Count;
		public int RightDeckCount => _rightDeck.Count;
		public int CenterDeckCount => _centerDeck.Count;

		public float CountdownValue => _countdownValue;
		public int WarFaceDownPlaced => _warFaceDownPlaced;

		public Card? LeftFaceUp => _leftFaceUp;
		public Card? RightFaceUp => _rightFaceUp;
		public bool PendingWinnerIsLeft => _pendingWinnerIsLeft;

		// Normalized [0,1] progress values for animations (UI uses these)
		public bool HasDealCardInFlight => _dealCardInFlight != null;
		public bool DealToLeftNext => _dealToLeftNext;
		public float DealProgress { get; private set; }

		public float BattleAnimProgress { get; private set; }

		// shuffle unlock
		public bool ShuffleUnlocked => _roundCount >= RoundsBeforeShuffle;

		// internal flag to know which side current in-flight deal is going to
		private bool _dealToLeftNext;

		public WarEngine(Random? rng = null)
		{
			_rng = rng ?? new Random();
			_centerDeck = new Deck(_rng);
			ResetGame();
		}

		// ---- external API --------------------------------------------------

		public void ResetGame()
		{
			_leftDeck.Clear();
			_rightDeck.Clear();
			_centerDeck.Reset();
			_battlePile.Clear();

			_roundCount = 0;

			_state = WarState.ChoosingSide;
			_lastRoundWinner = RoundWinner.None;

			_leftFaceUp = null;
			_rightFaceUp = null;

			_battlePhase = BattleAnimPhase.None;
			_battleAnimTimer = 0f;
			BattleAnimProgress = 0f;

			_dealCardInFlight = null;
			_dealAnimTimer = 0f;
			DealProgress = 0f;
			_dealToLeftNext = true;

			_warFaceDownRemaining = 0;
			_warFaceDownPlaced = 0;
			_warBurnTimer = 0f;

			_sideSelected = false;
			_playerReady = false;
			_opponentReady = false;

			SideStatusText = "No side selected.";
			BottomStatusText = "Click Left or Right to start.";
		}

		public void SelectSide(bool playerOnLeft)
		{
			_playerOnLeft = playerOnLeft;
			_sideSelected = true;

			SideStatusText = _playerOnLeft ? "You are LEFT side." : "You are RIGHT side.";
			BottomStatusText = "Dealing cards...";

			_leftDeck.Clear();
			_rightDeck.Clear();
			_centerDeck.Reset();
			_battlePile.Clear();

			_roundCount = 0;

			_dealCardInFlight = null;
			_dealAnimTimer = 0f;
			DealProgress = 0f;
			_dealToLeftNext = true;

			_state = WarState.Dealing;
		}

		/// <summary>
		/// Called every frame by the client with delta time in seconds.
		/// </summary>
		public void Tick(float dt)
		{
			if (dt <= 0f)
				dt = 0.016f;

			switch (_state)
			{
				case WarState.Dealing:
					UpdateDealing(dt);
					break;

				case WarState.WaitingForPlayerClick:
					// nothing animating, just waiting
					break;

				case WarState.Countdown:
					UpdateCountdown(dt);
					UpdateBattleAnimation(dt);
					break;

				case WarState.ShowingBattle:
					UpdateBattleAnimation(dt);
					break;

				case WarState.WarFaceDown:
					UpdateWarFaceDown(dt);
					break;

				case WarState.RoundResult:
					UpdateRoundResult(dt);
					break;

				case WarState.GameOver:
					UpdateGameOver(dt);
					break;
			}
		}

		/// <summary>
		/// Called when the local player clicks their deck area.
		/// </summary>
		public void OnPlayerDeckClicked()
		{
			if (_state != WarState.WaitingForPlayerClick)
				return;

			_playerReady = true;
			BottomStatusText = "Waiting...";

			TryBeginBattle();
		}

		/// <summary>
		/// For online version later: server can set this when other player is ready.
		/// </summary>
		public void SetOpponentReady(bool ready)
		{
			_opponentReady = ready;
			if (_state == WarState.WaitingForPlayerClick && _opponentReady)
			{
				TryBeginBattle();
			}
		}

		/// <summary>
		/// Shuffle the player's deck if allowed (returns true if shuffle actually happened).
		/// </summary>
		public bool TryShufflePlayerDeck()
		{
			if (!_sideSelected)
				return false;

			if (_state == WarState.ChoosingSide || _state == WarState.Dealing)
				return false;

			var deckToShuffle = _playerOnLeft ? _leftDeck : _rightDeck;
			if (deckToShuffle.Count <= 1)
				return false;

			var list = new List<Card>(deckToShuffle);
			deckToShuffle.Clear();
			ShuffleListInPlace(list);
			foreach (var card in list)
				deckToShuffle.Enqueue(card);

			BottomStatusText = "You shuffled your deck!";
			return true;
		}

		// ---- core flows ----------------------------------------------------

		private void StartNextBattle()
		{
			// Check if someone ran out of cards
			if (_leftDeck.Count == 0 || _rightDeck.Count == 0)
			{
				_state = WarState.GameOver;
				_lastRoundWinner = _leftDeck.Count > _rightDeck.Count
					? (_playerOnLeft ? RoundWinner.Player : RoundWinner.Opponent)
					: (_playerOnLeft ? RoundWinner.Opponent : RoundWinner.Player);

				BottomStatusText = "Game over!";
				_roundResultTimer = 3f;
				return;
			}

			_roundCount++;

			_battlePile.Clear();

			_leftFaceUp = _leftDeck.Dequeue();
			_rightFaceUp = _rightDeck.Dequeue();

			_battlePile.Add(_leftFaceUp.Value);
			_battlePile.Add(_rightFaceUp.Value);

			// Wait for player click
			_state = WarState.WaitingForPlayerClick;

			_playerReady = false;

			// Offline: opponent always ready
			_opponentReady = !RequireBothReady;

			BottomStatusText = "Click your deck to draw!";
		}

		private void TryBeginBattle()
		{
			if (!_playerReady || !_opponentReady)
				return;

			_state = WarState.Countdown;

			_battlePhase = BattleAnimPhase.MoveToCenter;
			_battleAnimTimer = 0f;
			BattleAnimProgress = 0f;

			// Short 1-second style countdown (1 -> flip)
			_countdownValue = 1f;
			_countdownTimer = 0.1f;

			_opponentReady = !RequireBothReady; // reset opponent ready if needed

			BottomStatusText = "Battle!";
		}

		private void EvaluateBattleAfterFlip()
		{
			if (_leftFaceUp == null || _rightFaceUp == null)
			{
				_lastRoundWinner = RoundWinner.Tie;
				_state = WarState.RoundResult;
				_battlePhase = BattleAnimPhase.None;
				BattleAnimProgress = 0f;
				_roundResultTimer = 1.0f;
				return;
			}

			int leftRank = (int)_leftFaceUp.Value.Rank;
			int rightRank = (int)_rightFaceUp.Value.Rank;

			if (leftRank == rightRank)
			{
				// WAR: 3 down + 1 up each
				_lastRoundWinner = RoundWinner.Tie;

				_warFaceDownRemaining = 3;
				_warFaceDownPlaced = 0;
				_warBurnTimer = 0f;

				_state = WarState.WarFaceDown;
				_battlePhase = BattleAnimPhase.None;
				BattleAnimProgress = 0f;

				BottomStatusText = "WAR! Each side puts 3 cards face down...";
				return;
			}

			if (leftRank > rightRank)
			{
				_pendingWinnerIsLeft = true;
				_lastRoundWinner = _playerOnLeft ? RoundWinner.Player : RoundWinner.Opponent;
			}
			else
			{
				_pendingWinnerIsLeft = false;
				_lastRoundWinner = _playerOnLeft ? RoundWinner.Opponent : RoundWinner.Player;
			}

			_battlePhase = BattleAnimPhase.AfterFlipPause;
			_battleAnimTimer = 0f;
			BattleAnimProgress = 0f;
		}

		private void UpdateWarFaceDown(float dt)
		{
			// Step 1: burn 3 face-down cards, one step at a time
			if (_warFaceDownRemaining > 0)
			{
				_warBurnTimer += dt;
				if (_warBurnTimer >= WarBurnStepDuration)
				{
					_warBurnTimer -= WarBurnStepDuration;

					if (_leftDeck.Count == 0 || _rightDeck.Count == 0)
					{
						_state = WarState.GameOver;
						_lastRoundWinner = _leftDeck.Count > _rightDeck.Count
							? (_playerOnLeft ? RoundWinner.Player : RoundWinner.Opponent)
							: (_playerOnLeft ? RoundWinner.Opponent : RoundWinner.Player);

						BottomStatusText = "Game over during war!";
						_roundResultTimer = 3f;
						return;
					}

					_battlePile.Add(_leftDeck.Dequeue());
					_battlePile.Add(_rightDeck.Dequeue());

					_warFaceDownRemaining--;
					_warFaceDownPlaced++;
				}

				return;
			}

			// Step 2: draw showdown cards
			if (_leftDeck.Count == 0 || _rightDeck.Count == 0)
			{
				_state = WarState.GameOver;
				_lastRoundWinner = _leftDeck.Count > _rightDeck.Count
					? (_playerOnLeft ? RoundWinner.Player : RoundWinner.Opponent)
					: (_playerOnLeft ? RoundWinner.Opponent : RoundWinner.Player);

				BottomStatusText = "Game over during war!";
				_roundResultTimer = 3f;
				return;
			}

			_leftFaceUp = _leftDeck.Dequeue();
			_rightFaceUp = _rightDeck.Dequeue();

			_battlePile.Add(_leftFaceUp.Value);
			_battlePile.Add(_rightFaceUp.Value);

			_state = WarState.ShowingBattle;
			_battlePhase = BattleAnimPhase.MoveToCenter;
			_battleAnimTimer = 0f;
			BattleAnimProgress = 0f;

			BottomStatusText = "WAR showdown!";
		}

		private void UpdateRoundResult(float dt)
		{
			_roundResultTimer -= dt;
			if (_roundResultTimer <= 0f)
			{
				_lastRoundWinner = RoundWinner.None;
				StartNextBattle();
			}
		}

		private void UpdateGameOver(float dt)
		{
			_roundResultTimer -= dt;
			if (_roundResultTimer <= 0f)
			{
				BottomStatusText = "Game over. Click Restart in GameHub to play again.";
			}
		}

		private void UpdateDealing(float dt)
		{
			if (_dealCardInFlight == null && _centerDeck.Count > 0)
			{
				if (_centerDeck.TryDraw(out var card))
				{
					_dealCardInFlight = card;
					_dealAnimTimer = 0f;
					DealProgress = 0f;
					// _dealToLeftNext already indicates target
				}
			}
			else if (_dealCardInFlight != null)
			{
				_dealAnimTimer += dt;
				DealProgress = DealAnimDuration <= 0f
					? 1f
					: Math.Clamp(_dealAnimTimer / DealAnimDuration, 0f, 1f);

				if (_dealAnimTimer >= DealAnimDuration)
				{
					var targetDeck = _dealToLeftNext ? _leftDeck : _rightDeck;
					targetDeck.Enqueue(_dealCardInFlight.Value);

					_dealCardInFlight = null;
					_dealAnimTimer = 0f;
					DealProgress = 0f;
					_dealToLeftNext = !_dealToLeftNext;
				}
			}

			if (_centerDeck.Count == 0 && _dealCardInFlight == null)
			{
				StartNextBattle();
			}
		}

		private void UpdateCountdown(float dt)
		{
			_countdownTimer -= dt;
			if (_countdownTimer <= 0f)
			{
				_countdownValue -= 1f;
				if (_countdownValue <= 0f)
				{
					_state = WarState.ShowingBattle;
					_battlePhase = BattleAnimPhase.Flip;
					_battleAnimTimer = 0f;
					BattleAnimProgress = 0f;
				}
				else
				{
					_countdownTimer = 0.5f;
				}
			}
		}

		private void UpdateBattleAnimation(float dt)
		{
			if (_battlePhase == BattleAnimPhase.None)
			{
				BattleAnimProgress = 0f;
				return;
			}

			_battleAnimTimer += dt;

			switch (_battlePhase)
			{
				case BattleAnimPhase.MoveToCenter:
					{
						float duration = MoveToCenterDuration;
						BattleAnimProgress = duration <= 0f
							? 1f
							: Math.Clamp(_battleAnimTimer / duration, 0f, 1f);

						if (_battleAnimTimer >= duration)
						{
							_battleAnimTimer = 0f;
							_battlePhase = BattleAnimPhase.FaceDownIdle;
							BattleAnimProgress = 0f;
						}
						break;
					}

				case BattleAnimPhase.FaceDownIdle:
					// For Countdown, we just sit; for war showdown we auto-flip after a short pause.
					if (_state == WarState.ShowingBattle)
					{
						const float WarFaceDownPause = 0.4f;
						if (_battleAnimTimer >= WarFaceDownPause)
						{
							_battleAnimTimer = 0f;
							_battlePhase = BattleAnimPhase.Flip;
							BattleAnimProgress = 0f;
						}
					}
					break;

				case BattleAnimPhase.Flip:
					{
						float duration = FlipDuration;
						BattleAnimProgress = duration <= 0f
							? 1f
							: Math.Clamp(_battleAnimTimer / duration, 0f, 1f);

						if (_battleAnimTimer >= duration)
						{
							_battleAnimTimer = 0f;
							BattleAnimProgress = 0f;
							EvaluateBattleAfterFlip();
						}
						break;
					}

				case BattleAnimPhase.AfterFlipPause:
					{
						float duration = AfterFlipPauseDuration;
						BattleAnimProgress = duration <= 0f
							? 1f
							: Math.Clamp(_battleAnimTimer / duration, 0f, 1f);

						if (_battleAnimTimer >= duration)
						{
							_battleAnimTimer = 0f;
							_battlePhase = BattleAnimPhase.MoveToWinner;
							BattleAnimProgress = 0f;
						}
						break;
					}

				case BattleAnimPhase.MoveToWinner:
					{
						float duration = MoveToWinnerDuration;
						BattleAnimProgress = duration <= 0f
							? 1f
							: Math.Clamp(_battleAnimTimer / duration, 0f, 1f);

						if (_battleAnimTimer >= duration)
						{
							var winnerDeck = _pendingWinnerIsLeft ? _leftDeck : _rightDeck;
							ShuffleListInPlace(_battlePile);
							foreach (var c in _battlePile)
								winnerDeck.Enqueue(c);

							_battlePile.Clear();

							_leftFaceUp = null;
							_rightFaceUp = null;

							_battlePhase = BattleAnimPhase.None;
							BattleAnimProgress = 0f;

							_state = WarState.RoundResult;
							_roundResultTimer = 1.0f;

							_warFaceDownPlaced = 0;
						}
						break;
					}
			}
		}

		// ---- helpers -------------------------------------------------------

		private void ShuffleListInPlace(List<Card> list)
		{
			for (int i = list.Count - 1; i > 0; i--)
			{
				int j = _rng.Next(i + 1);
				(list[i], list[j]) = (list[j], list[i]);
			}
		}
	}
}

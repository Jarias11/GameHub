using System;
using System.Collections.Generic;
using GameContracts;

namespace GameLogic.Blackjack
{
	/// <summary>
	/// Server-side room state for Blackjack, used by the TickableGameHandler.
	/// Up to 4 players per room.
	/// </summary>
	public sealed class BlackjackRoomState : IRoomState
	{
		public string RoomCode { get; }

		/// <summary>Main engine containing cards & logic.</summary>
		public BlackjackEngine Engine { get; }

		/// <summary>Room-level mapping: seat index 0..3 -> playerId ("P1".."P4") or null.</summary>
		public string?[] SeatPlayerIds { get; } = new string?[4];

		/// <summary>Convenience: how many seats are currently bound to some player.</summary>
		public int SeatedCount
		{
			get
			{
				int count = 0;
				for (int i = 0; i < SeatPlayerIds.Length; i++)
				{
					if (!string.IsNullOrEmpty(SeatPlayerIds[i]))
						count++;
				}
				return count;
			}
		}

		public bool GameStarted => Engine.Phase != BlackjackPhase.Lobby;

		public BlackjackRoomState(string roomCode, Random rng)
		{
			RoomCode = roomCode;
			Engine = new BlackjackEngine(rng);
		}

		public int GetOrAssignSeatForPlayer(string playerId)
		{
			// Already seated?
			for (int i = 0; i < SeatPlayerIds.Length; i++)
			{
				if (SeatPlayerIds[i] == playerId)
					return i;
			}

			// Find first free seat
			for (int i = 0; i < SeatPlayerIds.Length; i++)
			{
				if (SeatPlayerIds[i] == null)
				{
					SeatPlayerIds[i] = playerId;
					return i;
				}
			}

			// No seat available -> default to 0 (shouldn't happen if we cap joins correctly)
			return 0;
		}

		public void UnseatPlayer(string playerId)
		{
			for (int i = 0; i < SeatPlayerIds.Length; i++)
			{
				if (SeatPlayerIds[i] == playerId)
				{
					SeatPlayerIds[i] = null;
				}
			}
		}

		public bool TryGetSeatIndex(string playerId, out int seatIndex)
		{
			for (int i = 0; i < SeatPlayerIds.Length; i++)
			{
				if (SeatPlayerIds[i] == playerId)
				{
					seatIndex = i;
					return true;
				}
			}
			seatIndex = -1;
			return false;
		}
	}
}

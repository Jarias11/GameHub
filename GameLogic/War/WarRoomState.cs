// GameLogic/War/WarRoomState.cs
using GameLogic.War;

namespace GameLogic
{
	/// <summary>
	/// Server-side state for a single War room.
	/// </summary>
	public sealed class WarRoomState : IRoomState
	{
		public string RoomCode { get; }

		/// <summary>
		/// Core game rules & animation timing.
		/// The WarGameHandler will tick this and map it to WarStatePayload snapshots.
		/// </summary>
		public WarEngine Engine { get; }

		/// <summary>
		/// PlayerId of the player in the Left slot, or null if not assigned yet.
		/// </summary>
		public string? LeftPlayerId { get; set; }

		/// <summary>
		/// PlayerId of the player in the Right slot, or null if not assigned yet.
		/// </summary>
		public string? RightPlayerId { get; set; }

		/// <summary>
		/// True once both slots are filled and the engine has been initialized for gameplay.
		/// </summary>
		public bool GameStarted { get; set; }

		public WarRoomState(string roomCode)
		{
			RoomCode = roomCode;
			Engine = new WarEngine()
			{
				RequireBothReady = true
			};
			GameStarted = false;
		}

		/// <summary>
		/// Returns true if both player slots are occupied.
		/// </summary>
		public bool HasTwoPlayers =>
			!string.IsNullOrEmpty(LeftPlayerId) &&
			!string.IsNullOrEmpty(RightPlayerId);

		/// <summary>
		/// Convenience helper so the handler can check if a given PlayerId is on the Left side.
		/// </summary>
		public bool IsLeftPlayer(string playerId) => LeftPlayerId == playerId;

		/// <summary>
		/// Convenience helper so the handler can check if a given PlayerId is on the Right side.
		/// </summary>
		public bool IsRightPlayer(string playerId) => RightPlayerId == playerId;
	}
}

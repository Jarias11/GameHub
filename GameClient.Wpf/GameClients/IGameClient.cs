using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GameContracts;

namespace GameClient.Wpf
{
	public interface IGameClient
	{
		/// <summary>The game type this client handles (Pong, WordGuess, etc).</summary>
		GameType GameType { get; }

		/// <summary>The root UI element to show in the main window.</summary>
		FrameworkElement View { get; }

		/// <summary>
		/// Called once so the game client can send messages through the shared socket
		/// and check if the socket is open.
		/// </summary>
		void SetConnection(
			Func<HubMessage, Task> sendAsync,
			Func<bool> isSocketOpen);

		/// <summary>
		/// Called whenever the room or player changes (create/join).
		/// </summary>
		void OnRoomChanged(string? roomCode, string? playerId);

		/// <summary>
		/// Called for incoming messages; return true if handled.
		/// </summary>
		bool TryHandleMessage(HubMessage msg);

		/// <summary>Forwarded KeyDown from MainWindow (if needed).</summary>
		void OnKeyDown(KeyEventArgs e);

		/// <summary>Forwarded KeyUp from MainWindow (if needed).</summary>
		void OnKeyUp(KeyEventArgs e);
	}
}

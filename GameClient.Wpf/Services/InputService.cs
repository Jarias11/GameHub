using System.Collections.Generic;
using System.Windows.Input;

namespace GameClient.Wpf.ClientServices
{
	/// <summary>
	/// Global, static input tracker for the WPF client.
	/// Tracks which keys are currently held down so games can
	/// query input every tick instead of relying on single events.
	/// </summary>
	public static class InputService
	{
		// All keys currently held down.
		private static readonly HashSet<Key> _heldKeys = new();

		// Optional: last key that was pressed (for priority / tie-breaking).
		private static Key? _lastPressedKey;

		/// <summary>
		/// Call this from your UI's KeyDown handler.
		/// </summary>
		public static bool OnKeyDown(Key key)
		{
			// HashSet.Add returns true only if the key was NOT already present
		bool isNewPress = _heldKeys.Add(key);

		if (isNewPress)
		{
			_lastPressedKey = key;
		}

		return isNewPress;
		}

		/// <summary>
		/// Call this from your UI's KeyUp handler.
		/// </summary>
		public static void OnKeyUp(Key key)
		{
			_heldKeys.Remove(key);

			// If the key that was released was the last pressed,
			// we could optionally recompute, but in practice
			// you usually don't need _lastPressedKey for anything critical.
			if (_lastPressedKey == key)
			{
				_lastPressedKey = null;
			}
		}

		/// <summary>
		/// Returns true if the given key is currently held down.
		/// </summary>
		public static bool IsHeld(Key key) => _heldKeys.Contains(key);

		/// <summary>
		/// Returns a snapshot of currently held keys.
		/// Useful when you want to inspect multiple keys at once.
		/// </summary>
		public static IReadOnlyCollection<Key> GetHeldKeys()
		{
			// Return a copy so callers can't mutate the internal HashSet.
			return new List<Key>(_heldKeys).AsReadOnly();
		}

		/// <summary>
		/// Optionally, returns the last key that was pressed, if any.
		/// Can be useful for "priority" rules (e.g., last direction wins).
		/// </summary>
		public static Key? GetLastPressedKey() => _lastPressedKey;

		/// <summary>
		/// Clears all tracked input. Call if you lose focus or switch screens.
		/// </summary>
		public static void Clear()
		{
			_heldKeys.Clear();
			_lastPressedKey = null;
		}
	}
}

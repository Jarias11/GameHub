using GameContracts;

namespace GameClient.Wpf
{
	public class GameCardModel
	{
		public GameType GameType { get; init; }
		public GameCategory Category { get; init; }

		// UI bits
		public string Emoji { get; init; } = "";
		public string Name { get; init; } = "";
		public string Tagline { get; init; } = "";
		public string PlayersText { get; init; } = "";
	}
}

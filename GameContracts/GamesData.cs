using System;
using System.Collections.Generic;
using System.Linq;

namespace GameContracts;


public enum GameCategory
{
	Board,
	Card,
	Arcade,
	Word
}
public enum GameType
{
	Anagram,
	Chess,
	Word,
	Pong,
	WordGuess,
	TicTacToe,
	Snake,
	NumberTiles,
	Checkers,
	Jumps,
	JumpsOnline,
	SideScroller,
	War,
	WarOnline,
	Blackjack,
	Tetris,
	Uno,
	SpaceShooter,
	Pinball
}

public class GameInfo
{
	public GameType Type { get; set; }
	public string Name { get; set; } = string.Empty;
	public GameCategory Category { get; set; }


	// 🔹 UI metadata
	public string Emoji { get; set; } = "";
	public string Tagline { get; set; } = "";
	public string PlayersText { get; set; } = "";

	// 🔹 Behavior metadata (for later)
	public bool IsOnline { get; set; } = true;

	public int MaxPlayers { get; set; } = 0;
}

public static class GameCatalog
{
	public static readonly IReadOnlyList<GameInfo> All = new List<GameInfo> {
		new() {
			Type = GameType.Pong,
			Name = "Pong",
			Category = GameCategory.Arcade,
			Emoji = "🎮",
			Tagline = "Classic paddle battle",
			PlayersText = "2 Players",
			IsOnline = true
			// MaxPlayers defaults to 2
		},
		new() {
			Type = GameType.Snake,
			Name = "Snake",
			Category = GameCategory.Arcade,
			Emoji = "🐍",
			Tagline = "Grow and dodge walls",
			PlayersText = "1 Player (offline)",
			IsOnline = false
			// MaxPlayers defaults to 1
		},
		new() {
			Type = GameType.TicTacToe,
			Name = "Tic Tac Toe",
			Category = GameCategory.Board,
			Emoji = "⭕",
			Tagline = "X's and O's strategy",
			PlayersText = "2 Players",
			IsOnline = true
		},
		new() {
			Type = GameType.WordGuess,
			Name = "Word Guess",
			Category = GameCategory.Word,
			Emoji = "🔤",
			Tagline = "Wordle-style guessing",
			PlayersText = "2 Players",
			IsOnline = true
		},
		new() {
			Type = GameType.Anagram,
			Name = "Anagram",
			Category = GameCategory.Word,
			Emoji = "🔤",
			Tagline = "Unscramble the word",
			PlayersText = "2 Players",
			IsOnline = true
		},
		new() {
			Type = GameType.NumberTiles,
			Name = "2048",
			Category = GameCategory.Board,
			Emoji = "♟️",
			Tagline = "Match Number Tiles",
			PlayersText = "1 Player (offline)",
			IsOnline = false
		},
		new() {
			Type = GameType.Checkers,
			Name = "Checkers",
			Category = GameCategory.Board,
			Emoji = "♟️",
			Tagline = "Classic strategy game",
			PlayersText = "2 Players",
			IsOnline = true
		},
		new() {
			Type = GameType.Chess,
			Name = "Chess",
			Category = GameCategory.Board,
			Emoji = "♟️",
			Tagline = "Protect your king",
			PlayersText = "2 Players",
			IsOnline = true
		},
		new() {
			Type = GameType.Jumps,
			Name = "Jumps",
			Category = GameCategory.Arcade,
			Emoji = "🤸",
			Tagline = "Get as high as you can",
			PlayersText = "1 Player (offline)",
			IsOnline = false
		},
		new() {
			Type = GameType.JumpsOnline,
			Name = "Jumps Online",
			Category = GameCategory.Arcade,
			Emoji = "🤸",
			Tagline = "Get higher than your friends",
			PlayersText = "1-3 Players",
			IsOnline = true,
			MaxPlayers = 3
		},
		new() {
			Type = GameType.SideScroller,
			Name = "Side Scroller",
			Category = GameCategory.Arcade,
			Emoji = "🚀",
			Tagline = "Run and dodge obstacles",
			PlayersText = "1 Player (offline)",
			IsOnline = false
		},
		new() {
			Type = GameType.War,
			Name = "War",
			Category = GameCategory.Card,
			Emoji = "🃏",
			Tagline = "Classic War Card Game",
			PlayersText = "1 Player (offline)",
			IsOnline = false
		},
		new() {
			Type = GameType.WarOnline,
			Name = "War Online",
			Category = GameCategory.Card,
			Emoji = "🃏",
			Tagline = "Classic War Card Game",
			PlayersText = "2 Players",
			IsOnline = true
		},
		new() {
			Type = GameType.Blackjack,
			Name = "Blackjack",
			Category = GameCategory.Card,
			Emoji = "🃏",
			Tagline = "Dont bust!",
			PlayersText = "4 Players",
			IsOnline = true,
			MaxPlayers = 4
		},
		new() {
			Type = GameType.Tetris,
			Name = "Tetris",
			Category = GameCategory.Arcade,
			Emoji = "🧱",
			Tagline = "Stack the blocks",
			PlayersText = "1 Player (offline)",
			IsOnline = false
		},
		new()
		{
			Type = GameType.Uno,
			Name = "UNO",
			Category = GameCategory.Card,
			Emoji = "🃏",
			Tagline = "Classic card game",
			PlayersText = "2-4 Players",
			IsOnline = true,
			MaxPlayers = 4
		},
		new()
		{
			Type = GameType.SpaceShooter,
			Name = "Space Shooter",
			Category = GameCategory.Arcade,
			Emoji = "🚀",
			Tagline = "Take out the other spaceships",
			PlayersText = "2-4 Players",
			IsOnline = true,
			MaxPlayers = 4
		},
		new()
		{
			Type = GameType.Pinball,
			Name = "Pin Ball",
			Category = GameCategory.Arcade,
			Emoji = "🔮",
			Tagline = "Bounce the ball and score points",
			PlayersText = "1 Player (offline)",
			IsOnline = false
		}
	};

	public static GameInfo? Get(GameType type) =>
		All.FirstOrDefault(g => g.Type == type);

	public static int GetMaxPlayers(GameType type)
	{
		var info = Get(type);
		if (info == null) return 2; // safe fallback
		if (info.MaxPlayers > 0) return info.MaxPlayers;
		return info.IsOnline ? 2 : 1;
	}
}

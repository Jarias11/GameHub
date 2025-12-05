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
	Jumps
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
		},
		new() {
			Type = GameType.Snake,
			Name = "Snake",
			Category = GameCategory.Arcade,
			Emoji = "🐍",
			Tagline = "Grow and dodge walls",
			PlayersText = "1 Player (offline)",
			IsOnline = false
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
		new()
		{
			Type = GameType.Checkers,
			Name = "Checkers",
			Category = GameCategory.Board,
			Emoji = "♟️",
			Tagline = "Classic strategy game",
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
	};

	public static GameInfo? Get(GameType type) =>
		All.FirstOrDefault(g => g.Type == type);
}

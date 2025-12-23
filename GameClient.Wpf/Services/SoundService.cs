using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualBasic;

namespace GameClient.Wpf.Services
{
	public enum TetrisSoundEffect
	{
		PlacePiece,
		RowClear,
		RotatePiece,
		TetrisClear,
		LevelUp,
		MovePiece
	}

	public enum AnagramSoundEffect
	{
		Success,
		Wrong,
		ClockTicking,
		ClockAlarm,
		GameStart,
		Loser,
		Winner
	}

	public enum BlackjackSoundEffect
	{
		CardDealt,
		FlipCard,
		Hover,
		PokerChips
	}
	public enum CheckersSoundEffect
	{
		CheckersMove
	}

	/// <summary>
	/// Very simple central sound helper for GameHub.
	/// - Uses MediaPlayer for BGM + SFX.
	/// - Assumes .wav files are copied to the output under Assets/Sounds/...
	/// </summary>
	public static class SoundService
	{
		// Adjust these later or expose a settings UI if you want
		private static double _bgmVolume = 0.35;
		private static double _sfxVolume = 0.7;

		private static MediaPlayer? _bgmPlayer;

		// Cache for SFX players so we don't reopen files every time
		private static readonly Dictionary<string, MediaPlayer> _sfxPlayers = new();

		private static readonly string BaseSoundPath =
			Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Sounds");

		private static Uri? GetSoundUri(string relativePath)
		{
			try
			{
				var fullPath = Path.Combine(BaseSoundPath, relativePath);

				if (!File.Exists(fullPath))
				{
					// You can log this if you want to debug missing files
					return null;
				}

				return new Uri(fullPath, UriKind.Absolute);
			}
			catch
			{
				return null;
			}
		}

		// ─────────────────────────────────────────────────────────────
		// BGM (looping)
		// ─────────────────────────────────────────────────────────────

		public static void PlayTetrisBgm()
		{
			StopBgm();

			var uri = GetSoundUri(Path.Combine("Tetris", "BGM.wav"));
			if (uri == null)
				return;

			var player = new MediaPlayer();
			player.Open(uri);
			player.Volume = _bgmVolume;

			// Loop when finished
			player.MediaEnded += (s, e) =>
			{
				if (s is MediaPlayer p)
				{
					p.Position = TimeSpan.Zero;
					p.Play();
				}
			};

			_bgmPlayer = player;
			_bgmPlayer.Play();
		}

		public static void StopBgm()
		{
			if (_bgmPlayer != null)
			{
				_bgmPlayer.Stop();
				_bgmPlayer.Close();
				_bgmPlayer = null;
			}
		}

		// ─────────────────────────────────────────────────────────────
		// Tetris SFX
		// ─────────────────────────────────────────────────────────────

		public static void PlayTetrisEffect(TetrisSoundEffect effect)
		{
			string fileName = effect switch
			{
				TetrisSoundEffect.PlacePiece => "PlacePiece.wav",
				TetrisSoundEffect.RowClear => "RowClear.wav",
				TetrisSoundEffect.RotatePiece => "RotatePiece.wav",
				TetrisSoundEffect.TetrisClear => "TetrisClear.wav",
				TetrisSoundEffect.LevelUp => "LevelUp.wav",
				TetrisSoundEffect.MovePiece => "MovePiece.wav",
				_ => ""
			};

			if (string.IsNullOrWhiteSpace(fileName))
				return;

			var relativePath = Path.Combine("Tetris", fileName);
			PlayOneShot(relativePath);
		}

		// Generic helper, re-usable later for other games if you want.
		private static void PlayOneShot(string relativePath)
		{
			var key = relativePath.ToLowerInvariant();

			if (!_sfxPlayers.TryGetValue(key, out var player))
			{
				var uri = GetSoundUri(relativePath);
				if (uri == null)
					return;

				player = new MediaPlayer();
				player.Open(uri);
				player.Volume = _sfxVolume;
				_sfxPlayers[key] = player;
			}

			// Restart from start each time we play
			player.Stop();
			player.Position = TimeSpan.Zero;
			player.Play();
		}

		// Optional: expose volume controls if you want later
		public static void SetBgmVolume(double volume)
		{
			_bgmVolume = Math.Clamp(volume, 0.0, 1.0);
			if (_bgmPlayer != null)
			{
				_bgmPlayer.Volume = _bgmVolume;
			}
		}

		public static void SetSfxVolume(double volume)
		{
			_sfxVolume = Math.Clamp(volume, 0.0, 1.0);
			foreach (var kvp in _sfxPlayers)
			{
				kvp.Value.Volume = _sfxVolume;
			}
		}
		public static void FadeOutBgm(TimeSpan duration)
		{
			if (_bgmPlayer == null)
				return;

			// Don't stack multiple fades
			var player = _bgmPlayer;
			double startVolume = player.Volume;
			if (startVolume <= 0.0)
			{
				StopBgm();
				return;
			}

			int steps = 20; // number of fade steps
			double stepVolume = startVolume / steps;
			double intervalMs = duration.TotalMilliseconds / steps;

			var timer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(intervalMs)
			};

			int currentStep = 0;

			timer.Tick += (s, e) =>
			{
				if (_bgmPlayer == null)
				{
					timer.Stop();
					return;
				}

				currentStep++;

				double newVolume = startVolume - stepVolume * currentStep;
				if (newVolume <= 0.0 || currentStep >= steps)
				{
					_bgmPlayer.Volume = 0.0;
					timer.Stop();

					// fully stop + dispose
					_bgmPlayer.Stop();
					_bgmPlayer.Close();
					_bgmPlayer = null;
				}
				else
				{
					_bgmPlayer.Volume = newVolume;
				}
			};

			timer.Start();
		}

		public static void PlayAnagramEffect(AnagramSoundEffect effect)
		{
			string fileName = effect switch
			{
				AnagramSoundEffect.Success => "success.wav",
				AnagramSoundEffect.Wrong => "wrong.wav",
				AnagramSoundEffect.ClockTicking => "clockTicking.wav",
				AnagramSoundEffect.ClockAlarm => "clockAlarm.wav",
				AnagramSoundEffect.GameStart => "gameStart.wav",
				AnagramSoundEffect.Loser => "loser.wav",
				AnagramSoundEffect.Winner => "winner.wav",
				_ => ""
			};

			if (string.IsNullOrWhiteSpace(fileName))
				return;

			var relativePath = Path.Combine("Anagram", fileName);
			PlayOneShot(relativePath);
		}
		public static void PlayBlackjackEffect(BlackjackSoundEffect effect)
		{
			string fileName = effect switch
			{
				BlackjackSoundEffect.CardDealt => "cardDealt.wav",
				BlackjackSoundEffect.FlipCard => "flipCard.wav",
				BlackjackSoundEffect.Hover => "hover.wav",
				BlackjackSoundEffect.PokerChips => "pokerChips.wav",
				_ => ""
			};

			if (string.IsNullOrWhiteSpace(fileName))
				return;

			var relativePath = Path.Combine("Blackjack", fileName);
			PlayOneShot(relativePath);
		}
		public static void PlayCheckersEffect(CheckersSoundEffect effect)
		{
			string fileName = effect switch
			{
				CheckersSoundEffect.CheckersMove => "checkersMove.wav",
				_ => ""	
				};	
			if (string.IsNullOrWhiteSpace(fileName))
				return;

			
			var relativePath = Path.Combine("Checkers", fileName);
			PlayOneShot(relativePath);
		}


	}
}
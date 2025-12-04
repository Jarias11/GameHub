using System.Collections.Generic;
using GameContracts;

namespace GameLogic.WordGuess
{
	/// <summary>
	/// Pure WordGuess rules – no networking.
	/// </summary>
	public static class WordGuessLogic
	{
		public static LetterResult[] EvaluateGuess(string secret, string guess)
		{
			var result = new LetterResult[5];

			secret = secret.ToUpperInvariant();
			guess = guess.ToUpperInvariant();

			// First pass: mark greens and track remaining letters
			var remainingSecretCounts = new Dictionary<char, int>();

			for (int i = 0; i < 5; i++)
			{
				if (guess[i] == secret[i])
				{
					result[i] = LetterResult.Green;
				}
				else
				{
					if (!remainingSecretCounts.ContainsKey(secret[i]))
						remainingSecretCounts[secret[i]] = 0;
					remainingSecretCounts[secret[i]]++;
				}
			}

			// Second pass: yellows / greys
			for (int i = 0; i < 5; i++)
			{
				if (result[i] == LetterResult.Green)
					continue;

				char g = guess[i];
				if (remainingSecretCounts.TryGetValue(g, out var count) && count > 0)
				{
					result[i] = LetterResult.Yellow;
					remainingSecretCounts[g] = count - 1;
				}
				else
				{
					result[i] = LetterResult.Grey;
				}
			}

			return result;
		}
	}
}

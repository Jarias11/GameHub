namespace GameContracts;


// Result type for each letter
public enum LetterResult
{
	Grey,   // not in word
	Yellow, // in word, wrong position
	Green   // correct letter & position
}

// P1 sets the secret (5 letters)
public class WordGuessSetSecretPayload
{
	public string SecretWord { get; set; } = string.Empty; // expecting 5 letters
}

// P2 makes a guess
public class WordGuessGuessPayload
{
	public string Guess { get; set; } = string.Empty; // 5 letters
}

// Server sends back result for a guess
public class WordGuessResultPayload
{
	public string Guess { get; set; } = string.Empty;
	public LetterResult[] LetterResults { get; set; } = Array.Empty<LetterResult>();
	public int AttemptNumber { get; set; }
	public int MaxAttempts { get; set; }
	public bool IsCorrect { get; set; }
	public bool IsGameOver { get; set; }
	public string? Message { get; set; } // e.g. "You win!" or "No attempts left."
}
public class WordGuessResetPayload
{
	public string Message { get; set; } = "Game restarted. Waiting for a new secret word.";
}
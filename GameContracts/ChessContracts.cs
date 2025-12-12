namespace GameContracts
{
	public enum ChessColorDto
	{
		White = 0,
		Black = 1
	}

	/// <summary>
	/// P1 chooses which color to play (White or Black).
	/// Sent: client -> server
	/// MessageType: "ChessColorChoice"
	/// </summary>
	public class ChessColorChoicePayload
	{
		public string RoomCode { get; set; } = string.Empty;
		public ChessColorDto ChosenColor { get; set; }
	}

	/// <summary>
	/// Server tells both clients which PlayerId is White / Black.
	/// Sent: server -> clients
	/// MessageType: "ChessColorAssigned"
	/// </summary>
	public class ChessColorAssignedPayload
	{
		public string RoomCode { get; set; } = string.Empty;

		/// <summary>"P1" or "P2" (or whatever your PlayerId format is)</summary>
		public string WhitePlayerId { get; set; } = string.Empty;

		/// <summary>"P1" or "P2"</summary>
		public string BlackPlayerId { get; set; } = string.Empty;
	}

	/// <summary>
	/// A single move in board coordinates.
	/// Sent:
	///   client -> server: "ChessMove"
	///   server -> clients: "ChessMoveApplied"
	/// </summary>
	public class ChessMovePayload
	{
		public string RoomCode { get; set; } = string.Empty;

		/// <summary>0–7 row (0 is top, 7 is bottom, consistent with ChessState)</summary>
		public int FromRow { get; set; }
		public int FromCol { get; set; }
		public int ToRow { get; set; }
		public int ToCol { get; set; }

		/// <summary>Sender's PlayerId ("P1","P2"). Used so clients can ignore their own echo.</summary>
		public string PlayerId { get; set; } = string.Empty;
	}

	/// <summary>
	/// Sent when the server resets the chess room.
	/// MessageType: "ChessRestarted"
	/// </summary>
	public class ChessRestartedPayload
	{
		public string RoomCode { get; set; } = string.Empty;
	}
	public class ChessResignPayload
	{
		public string RoomCode { get; set; } = string.Empty;
		public string PlayerId { get; set; } = string.Empty; // who resigned
	}

	public class ChessResignedPayload
	{
		public string RoomCode { get; set; } = string.Empty;
		public string ResigningPlayerId { get; set; } = string.Empty;
		public string WinnerPlayerId { get; set; } = string.Empty;
	}
	// Client -> Server: offer a draw (once per ply)
	// MessageType: "ChessDrawOffer"
	public class ChessDrawOfferPayload
	{
		public string RoomCode { get; set; } = string.Empty;
		public string PlayerId { get; set; } = string.Empty; // who offered
	}

	// Server -> Clients: draw was offered
	// MessageType: "ChessDrawOffered"
	public class ChessDrawOfferedPayload
	{
		public string RoomCode { get; set; } = string.Empty;
		public string OfferingPlayerId { get; set; } = string.Empty;
		public int PlyIndex { get; set; } // MoveHistory.Count when offered
	}

	// Client -> Server: respond to an offer
	// MessageType: "ChessDrawResponse"
	public class ChessDrawResponsePayload
	{
		public string RoomCode { get; set; } = string.Empty;
		public string PlayerId { get; set; } = string.Empty; // responder
		public bool Accept { get; set; }
	}

	// Server -> Clients: offer declined
	// MessageType: "ChessDrawDeclined"
	public class ChessDrawDeclinedPayload
	{
		public string RoomCode { get; set; } = string.Empty;
		public string DecliningPlayerId { get; set; } = string.Empty;
	}

	// Server -> Clients: draw agreed
	// MessageType: "ChessDrawAgreed"
	public class ChessDrawAgreedPayload
	{
		public string RoomCode { get; set; } = string.Empty;
		public string OfferingPlayerId { get; set; } = string.Empty;
		public string AcceptingPlayerId { get; set; } = string.Empty;
	}

}

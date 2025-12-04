using GameContracts;

namespace GameLogic.TicTacToe
{
    public class TicTacToeRoomState : IRoomState
    {
        public string RoomCode { get; }

        // 9 cells (0–8) or [row * 3 + col]
        public char[] Cells { get; } = new char[9]; // you decide: ' ', 'X', 'O'

        public string CurrentPlayerId { get; set; } = "P1"; // start with host?
		public string? PlayerXId { get; set; }
public string? PlayerOId { get; set; }

        public bool IsGameOver { get; set; }
        public string? WinnerPlayerId { get; set; }
        public bool IsDraw { get; set; }

        public TicTacToeRoomState(string roomCode)
        {
            RoomCode = roomCode;

            // initialize board however you like
            for (int i = 0; i < Cells.Length; i++)
                Cells[i] = ' '; // empty
        }
    }
}

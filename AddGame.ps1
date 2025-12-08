param (
    # Used for class names, e.g. "SpaceShooter"
    [Parameter(Mandatory = $true)]
    [string]$GameKey,

    # Optional: display name used in the placeholder XAML text
    [string]$DisplayName
)

if (-not $DisplayName -or $DisplayName.Trim() -eq "") {
    $DisplayName = $GameKey
}

Write-Host "Creating empty game client for '$GameKey' (display name: '$DisplayName')..."

$gameClientsFolder = "GameClient.Wpf/GameClients"

if (-not (Test-Path $gameClientsFolder)) {
    throw "Could not find $gameClientsFolder (expected GameClient.Wpf/GameClients, run from src)."
}

$xamlPath       = Join-Path $gameClientsFolder "$GameKey`GameClient.xaml"
$codeBehindPath = Join-Path $gameClientsFolder "$GameKey`GameClient.xaml.cs"

if ((Test-Path $xamlPath) -or (Test-Path $codeBehindPath)) {
    Write-Warning "XAML or code-behind already exists for $GameKey`GameClient. Not overwriting."
    return
}

# Very simple placeholder XAML – you can replace later
$xamlTemplate = @"
<UserControl x:Class="GameClient.Wpf.GameClients.$GameKey`GameClient"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid>
        <TextBlock Text="$DisplayName placeholder"
                   HorizontalAlignment="Center"
                   VerticalAlignment="Center"
                   FontSize="24"
                   FontWeight="Bold" />
    </Grid>
</UserControl>
"@

$codeBehindTemplate = @"
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GameContracts;

namespace GameClient.Wpf.GameClients
{
    public partial class $GameKey`GameClient : UserControl, IGameClient
    {
        public GameType GameType => GameType.$GameKey;
        public FrameworkElement View => this;

        private Func<HubMessage, Task>? _sendAsync;
        private Func<bool>? _isSocketOpen;

        public $GameKey`GameClient()
        {
            InitializeComponent();
        }

        public void SetConnection(Func<HubMessage, Task> sendAsync, Func<bool> isSocketOpen)
        {
            _sendAsync = sendAsync;
            _isSocketOpen = isSocketOpen;
        }

        public void OnRoomChanged(string? roomCode, string? playerId)
        {
            // For offline games you can ignore this or use it later.
        }

        public void OnSocketClosed()
        {
            // For offline games you can ignore this or use it later.
        }
    }
}
"@

$xamlTemplate       | Set-Content $xamlPath -Encoding UTF8
$codeBehindTemplate | Set-Content $codeBehindPath -Encoding UTF8

Write-Host "✔ Created $xamlPath"
Write-Host "✔ Created $codeBehindPath"
Write-Host "✅ Done. Now just add the enum + catalog entry in GamesData.cs."

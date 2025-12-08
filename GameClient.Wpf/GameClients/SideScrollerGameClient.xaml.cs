using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media; // CompositionTarget
using GameContracts;
using GameClient.Wpf.ClientServices;   // InputService
using GameLogic.SideScroller;         // the engine
using SkiaSharp;
using SkiaSharp.Views.WPF;
using SkiaSharp.Views.Desktop;

namespace GameClient.Wpf.GameClients
{
    public partial class SideScrollerGameClient : UserControl, IGameClient
    {
        private Func<HubMessage, Task>? _sendAsync;
        private Func<bool>? _isSocketOpen;

        private string? _roomCode;
        private string? _playerId;

        // ── Game states ─────────────────────────────────────────────────────
        private enum GameState
        {
            Ready,
            Playing,
            Dead,
            Won
        }

        private GameState _state = GameState.Ready;

        // ── Fixed timestep loop (60 FPS target) ─────────────────────────────
        private readonly Stopwatch _stopwatch = new();
        private const double TargetDeltaSeconds = 1.0 / 60.0; // 60 FPS
        private double _accumulatorSeconds = 0.0;
        private bool _isRunning = false;

        // ── Core game engine (pure logic) ───────────────────────────────────
        private readonly SideScrollerEngine _engine = new();
        // Simple time accumulator for animation
        private float _animationTime = 0f;

        public SideScrollerGameClient()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // Start loop when control is loaded
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _isRunning = true;
            _accumulatorSeconds = 0.0;
            _stopwatch.Restart();
            _animationTime = 0f;
            CompositionTarget.Rendering += OnRendering;

            _state = GameState.Ready;
            InputService.Clear();
        }

        // Stop loop when control is removed / window closed
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _isRunning = false;
            CompositionTarget.Rendering -= OnRendering;
            _stopwatch.Stop();

            InputService.Clear();
        }

        /// <summary>
        /// Rendering callback from WPF. We use Stopwatch to run a fixed 60 FPS update loop.
        /// </summary>
        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_isRunning)
                return;

            // Time since last frame
            double elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;
            _stopwatch.Restart();

            _accumulatorSeconds += elapsedSeconds;

            // Run the simulation in fixed steps (can catch up if there was a hiccup)
            while (_accumulatorSeconds >= TargetDeltaSeconds)
            {
                _accumulatorSeconds -= TargetDeltaSeconds;

                UpdateGame((float)TargetDeltaSeconds);
            }

            // Request a redraw with the latest state
            GameSurface.InvalidateVisual();
        }

        private void UpdateGame(float dtSeconds)
        {

            var window = Window.GetWindow(this);
            bool isActive = window?.IsActive ?? false;
            if (!isActive)
            {
                InputService.Clear();
                return;
            }
            // Advance animation time even when we're on this screen
            _animationTime += dtSeconds;
            // ── 1) Snapshot input from InputService ─────────────────────────
            bool leftHeld =
                InputService.IsHeld(Key.A) ||
                InputService.IsHeld(Key.Left);

            bool rightHeld =
                InputService.IsHeld(Key.D) ||
                InputService.IsHeld(Key.Right);

            bool jumpHeld =
                InputService.IsHeld(Key.Space) ||
                InputService.IsHeld(Key.W) ||
                InputService.IsHeld(Key.Up);

            bool restartHeld =
                InputService.IsHeld(Key.R);
            

            // ── 2) View width for camera (engine needs this) ────────────────
            float viewWidth = (float)GameSurface.ActualWidth;
            if (viewWidth <= 0f)
            {
                // Fallback to XAML width if layout isn't ready yet
                viewWidth = 800f;
            }

            // ── 3) State machine ────────────────────────────────────────────
            switch (_state)
            {
                case GameState.Ready:
                    // Let the player see the level and press jump to begin
                    if (jumpHeld)
                    {
                        _state = GameState.Playing;
                        InputService.Clear(); // avoid buffered jump
                    }
                    break;

                case GameState.Playing:
                    {
                    
                        bool resetThisFrame = _engine.Update(
                            dtSeconds,
                            leftHeld,
                            rightHeld,
                            jumpHeld,
                            viewWidth);

                        // Fell into kill volume
                        if (resetThisFrame)
                        {
                            _state = GameState.Dead;
                            InputService.Clear();
                            _accumulatorSeconds = 0f;
                            _stopwatch.Restart();
                            break;
                        }

                        // Reached finish platform
                        if (_engine.LevelCompleted)
                        {
                            _state = GameState.Won;
                            InputService.Clear();
                        }
                        break;
                    }

                case GameState.Dead:
                case GameState.Won:
                    // Wait for restart (R key)
                    if (restartHeld)
                    {
                        ResetGame();
                        _state = GameState.Ready;
                        InputService.Clear();
                    }
                    break;
            }
        }

        // ── IGameClient plumbing ─────────────────────────────────────────────

        public GameType GameType => GameType.SideScroller;
        public FrameworkElement View => this;

        public void SetConnection(
            Func<HubMessage, Task> sendAsync,
            Func<bool> isSocketOpen)
        {
            _sendAsync = sendAsync;
            _isSocketOpen = isSocketOpen;
        }

        public void OnRoomChanged(string? roomCode, string? playerId)
        {
            _roomCode = roomCode;
            _playerId = playerId;

            ResetGame();
            _state = GameState.Ready;
            GameSurface.InvalidateVisual();
        }

        public void ResetGame()
        {
            // Reset simulation state
            _engine.Reset();

            // Reset timing + input
            _accumulatorSeconds = 0.0;
            _stopwatch.Restart();
            _animationTime = 0f;
            InputService.Clear();
        }

        public bool TryHandleMessage(HubMessage msg)
        {
            // No network handling yet
            return false;
        }

        // These now just forward to the global InputService
        public void OnKeyDown(KeyEventArgs e)
        {
            InputService.OnKeyDown(e.Key);
        }

        public void OnKeyUp(KeyEventArgs e)
        {
            InputService.OnKeyUp(e.Key);
        }

        // ── Skia rendering ──────────────────────────────────────────────────

        private void GameSurface_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info; // gives us pixel width/height of the surface

            // Clear to black
            canvas.Clear(SKColors.Black);

            DrawPlatforms(canvas);
            DrawStructures(canvas);
            DrawArenaButton(canvas);
            DrawEnemies(canvas);
            DrawBoss(canvas);
            DrawBossShots(canvas);
            DrawArenaWave(canvas);
            DrawPlayer(canvas);
            DrawDebugGrid(canvas, info);
            DrawHud(canvas, info);
        }

        private void DrawPlayer(SKCanvas canvas)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true
            };

            // Convert world space → screen space using cameraX
            float screenX = _engine.PlayerX - _engine.CameraX;

            float left = screenX - SideScrollerEngine.PlayerWidth / 2f;
            float top = _engine.PlayerY - SideScrollerEngine.PlayerHeight;
            float right = left + SideScrollerEngine.PlayerWidth;
            float bottom = top + SideScrollerEngine.PlayerHeight;

            var rect = new SKRect(left, top, right, bottom);
            canvas.DrawRect(rect, paint);
        }
        private void DrawEnemies(SKCanvas canvas)
        {
            var enemies = _engine.Enemies;
            var directions = _engine.EnemyDirections;
            var seesFlags = _engine.EnemySeesPlayerFlags;

            if (enemies is null || enemies.Count == 0)
                return;

            using var bodyPaint = new SKPaint
            {
                Color = new SKColor(0xFF, 0x55, 0x77), // enemy body color
                IsAntialias = true
            };

            using var outlinePaint = new SKPaint
            {
                Color = SKColors.Black,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2f
            };

            using var eyePaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true
            };

            using var eyeGlowPaint = new SKPaint
            {
                Color = new SKColor(0xFF, 0xFF, 0x66),
                IsAntialias = true
            };

            for (int i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                float dir = directions[i] >= 0f ? 1f : -1f;
                bool seesPlayer = seesFlags[i];

                // Convert to screen space
                float centerX = enemy.X - _engine.CameraX;
                float footY = enemy.FootY;
                float width = enemy.Width;
                float height = enemy.Height;

                float left = centerX - width / 2f;
                float right = centerX + width / 2f;
                float top = footY - height;

                // --- Body (rounded rect) ---
                float bodyTop = top + 10f;
                float bodyBottom = footY;
                var bodyRect = new SKRect(left, bodyTop, right, bodyBottom);
                canvas.DrawRoundRect(bodyRect, 6f, 6f, bodyPaint);
                canvas.DrawRoundRect(bodyRect, 6f, 6f, outlinePaint);

                // --- Head (circle) ---
                float headRadius = width * 0.4f;
                float headCenterX = centerX;
                float headCenterY = bodyTop - headRadius * 0.4f;
                canvas.DrawCircle(headCenterX, headCenterY, headRadius, bodyPaint);
                canvas.DrawCircle(headCenterX, headCenterY, headRadius, outlinePaint);

                // --- Eyes ---
                float eyeOffsetX = headRadius * 0.4f;
                float eyeRadius = headRadius * 0.18f;

                // Eye glows
                canvas.DrawCircle(headCenterX - eyeOffsetX, headCenterY, eyeRadius * 2f, eyeGlowPaint);
                canvas.DrawCircle(headCenterX + eyeOffsetX, headCenterY, eyeRadius * 2f, eyeGlowPaint);

                // Pupils
                canvas.DrawCircle(headCenterX - eyeOffsetX, headCenterY, eyeRadius, eyePaint);
                canvas.DrawCircle(headCenterX + eyeOffsetX, headCenterY, eyeRadius, eyePaint);

                // --- Arms (simple lines) ---
                float armY = (bodyTop + bodyBottom) / 2f;
                float armLength = width * 0.7f;

                canvas.DrawLine(
                    x0: centerX - width / 2f,
                    y0: armY,
                    x1: centerX - width / 2f - armLength * 0.3f,
                    y1: armY + armLength * 0.2f,
                    outlinePaint);

                canvas.DrawLine(
                    x0: centerX + width / 2f,
                    y0: armY,
                    x1: centerX + width / 2f + armLength * 0.3f,
                    y1: armY + armLength * 0.2f,
                    outlinePaint);

                // --- Legs (two small rectangles) ---
                float legWidth = width * 0.25f;
                float legHeight = height * 0.25f;

                var leftLeg = new SKRect(
                    centerX - legWidth * 1.2f,
                    footY - legHeight,
                    centerX - legWidth * 0.2f,
                    footY);
                var rightLeg = new SKRect(
                    centerX + legWidth * 0.2f,
                    footY - legHeight,
                    centerX + legWidth * 1.2f,
                    footY);

                canvas.DrawRect(leftLeg, bodyPaint);
                canvas.DrawRect(rightLeg, bodyPaint);
                canvas.DrawRect(leftLeg, outlinePaint);
                canvas.DrawRect(rightLeg, outlinePaint);

                // --- Vision cone (debug) ---
                DrawEnemyVisionCone(canvas, headCenterX, headCenterY, dir, seesPlayer);
            }
        }
        private void DrawEnemyVisionCone(SKCanvas canvas, float headCenterX, float headCenterY, float dir, bool seesPlayer)
        {
            // Range/height in pixels (same as engine)
            float rangePixels = SideScrollerEngine.EnemyVisionRangeBlocks * SideScrollerEngine.BlockSize;
            float halfHeightPixels = SideScrollerEngine.EnemyVisionHalfHeightBlocks * SideScrollerEngine.BlockSize;

            float tipX = headCenterX;
            float tipY = headCenterY;

            float baseCenterX = tipX + dir * rangePixels;
            float baseCenterY = tipY;

            float baseTopX = baseCenterX;
            float baseTopY = baseCenterY - halfHeightPixels;
            float baseBottomX = baseCenterX;
            float baseBottomY = baseCenterY + halfHeightPixels;

            using var fillPaint = new SKPaint
            {
                Color = seesPlayer
                    ? new SKColor(0xFF, 0x44, 0x44, 80) // red-ish when player is inside
                    : new SKColor(0xFF, 0xFF, 0x66, 50), // yellow-ish otherwise
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };

            using var strokePaint = new SKPaint
            {
                Color = SKColors.Yellow,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                IsAntialias = true
            };

            using var path = new SKPath();
            path.MoveTo(tipX, tipY);
            path.LineTo(baseTopX, baseTopY);
            path.LineTo(baseBottomX, baseBottomY);
            path.Close();

            canvas.DrawPath(path, fillPaint);
            canvas.DrawPath(path, strokePaint);
        }
        private void DrawBoss(SKCanvas canvas)
        {
            if (!_engine.BossActive)
                return;

            var boss = _engine.BossData;

            using var bodyPaint = new SKPaint
            {
                Color = _engine.BossHurtFlashing
         ? new SKColor(0xFF, 0x33, 0x33)    // flash red when hurt
         : new SKColor(0x55, 0xAA, 0xFF),   // normal blue-ish
                IsAntialias = true
            };

            using var outlinePaint = new SKPaint
            {
                Color = SKColors.Black,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3f
            };

            using var eyePaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true
            };

            using var eyeGlowPaint = new SKPaint
            {
                Color = new SKColor(0xFF, 0x33, 0x33),
                IsAntialias = true
            };

            float centerX = boss.X - _engine.CameraX;
            float footY = boss.FootY;
            float width = boss.Width;
            float height = boss.Height;

            float left = centerX - width / 2f;
            float right = centerX + width / 2f;
            float top = footY - height;

            // Body
            float bodyTop = top + 20f;
            float bodyBottom = footY;
            var bodyRect = new SKRect(left, bodyTop, right, bodyBottom);
            canvas.DrawRoundRect(bodyRect, 12f, 12f, bodyPaint);
            canvas.DrawRoundRect(bodyRect, 12f, 12f, outlinePaint);

            // Head
            float headRadius = width * 0.5f;
            float headCenterX = centerX;
            float headCenterY = bodyTop - headRadius * 0.4f;
            canvas.DrawCircle(headCenterX, headCenterY, headRadius, bodyPaint);
            canvas.DrawCircle(headCenterX, headCenterY, headRadius, outlinePaint);

            // Eyes
            float eyeOffsetX = headRadius * 0.45f;
            float eyeRadius = headRadius * 0.2f;

            // Red glowy eyes
            canvas.DrawCircle(headCenterX - eyeOffsetX, headCenterY, eyeRadius * 2f, eyeGlowPaint);
            canvas.DrawCircle(headCenterX + eyeOffsetX, headCenterY, eyeRadius * 2f, eyeGlowPaint);

            canvas.DrawCircle(headCenterX - eyeOffsetX, headCenterY, eyeRadius, eyePaint);
            canvas.DrawCircle(headCenterX + eyeOffsetX, headCenterY, eyeRadius, eyePaint);
        }

        private void DrawBossShots(SKCanvas canvas)
        {
            var shots = _engine.BossShots;
            if (shots == null || shots.Count == 0)
                return;

            using var shotPaint = new SKPaint
            {
                Color = SKColors.Red,
                IsAntialias = true
            };

            foreach (var s in shots)
            {
                float screenX = s.X - _engine.CameraX;
                canvas.DrawCircle(screenX, s.Y, s.Radius, shotPaint);
            }
        }
        private void DrawArenaButton(SKCanvas canvas)
        {
            if (!_engine.BossActive)
                return;

            if (!_engine.ArenaButtonEnabled && _engine.BossPhaseIndex > 1)
            {
                // After Phase1 we could optionally dim it; for now still draw base.
            }

            float worldX = _engine.ArenaButtonX;
            float worldY = _engine.ArenaButtonY;
            float radius = _engine.ArenaButtonRadius;

            if (radius <= 0f)
                return;

            float screenX = worldX - _engine.CameraX;
            float screenY = worldY;

            using var basePaint = new SKPaint
            {
                Color = new SKColor(0x44, 0x00, 0x00),
                IsAntialias = true
            };

            using var topPaint = new SKPaint
            {
                Color = _engine.ArenaButtonEnabled ? SKColors.Red : new SKColor(0x88, 0x22, 0x22),
                IsAntialias = true
            };

            using var outlinePaint = new SKPaint
            {
                Color = SKColors.Black,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2f
            };

            // Base (disk slightly below)
            var baseRect = new SKRect(
                screenX - radius * 1.2f,
                screenY - radius * 0.3f,
                screenX + radius * 1.2f,
                screenY + radius * 0.5f);
            canvas.DrawOval(baseRect, basePaint);
            canvas.DrawOval(baseRect, outlinePaint);

            // Top button (round)
            canvas.DrawCircle(screenX, screenY - radius * 0.4f, radius, topPaint);
            canvas.DrawCircle(screenX, screenY - radius * 0.4f, radius, outlinePaint);
        }
        private void DrawArenaWave(SKCanvas canvas)
        {
            if (!_engine.ArenaWaveActive)
                return;

            float worldX = _engine.ArenaButtonX;
            float worldY = _engine.ArenaButtonY;
            float radius = _engine.ArenaWaveRadius;

            if (radius <= 0f)
                return;

            float screenX = worldX - _engine.CameraX;
            float screenY = worldY;

            using var wavePaint = new SKPaint
            {
                Color = new SKColor(0xFF, 0x44, 0x44, 120),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 4f
            };

            canvas.DrawCircle(screenX, screenY, radius, wavePaint);
        }





        private void DrawPlatforms(SKCanvas canvas)
        {
            using var paint = new SKPaint { IsAntialias = true };

            foreach (var p in _engine.Platforms)
            {
                switch (p.Type)
                {
                    case PlatformType.Normal:
                        paint.Color = new SKColor(0x33, 0xFF, 0xAA);
                        break;
                    case PlatformType.Finish:
                        paint.Color = new SKColor(0xFF, 0xDD, 0x33);
                        break;
                    case PlatformType.CameraLock:
                        paint.Color = new SKColor(0xFF, 0x55, 0x55);
                        break;
                    case PlatformType.Moving:
                        paint.Color = new SKColor(0x55, 0xAA, 0xFF);
                        break;
                }

                float left = p.X - _engine.CameraX;
                float top = p.Y;
                float right = left + p.Width;
                float bottom = top + p.Height;

                var rect = new SKRect(left, top, right, bottom);
                canvas.DrawRect(rect, paint);
            }
        }

        private void DrawStructures(SKCanvas canvas)
        {
            using var paint = new SKPaint
            {
                Color = new SKColor(0x88, 0x88, 0xFF),
                IsAntialias = true
            };

            foreach (var s in _engine.Structures)
            {
                float left = s.X - _engine.CameraX;
                float top = s.Y;
                float right = left + s.Width;
                float bottom = top + s.Height;

                var rect = new SKRect(left, top, right, bottom);
                canvas.DrawRect(rect, paint);
            }
        }

        private void DrawDebugGrid(SKCanvas canvas, SKImageInfo info)
        {
            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 40), // white with low alpha
                IsAntialias = false,
                StrokeWidth = 1f,
                Style = SKPaintStyle.Stroke
            };

            float screenWidth = info.Width;
            float screenHeight = info.Height;

            // Vertical lines: every BlockSize in world space
            float worldMinX = _engine.CameraX;
            float worldMaxX = _engine.CameraX + screenWidth;

            float blockSize = SideScrollerEngine.BlockSize;
            float groundY = SideScrollerEngine.GroundY;

            float firstWorldX = (float)Math.Floor(worldMinX / blockSize) * blockSize;

            for (float worldX = firstWorldX; worldX <= worldMaxX; worldX += blockSize)
            {
                float screenX = worldX - _engine.CameraX;

                canvas.DrawLine(
                    x0: screenX,
                    y0: 0,
                    x1: screenX,
                    y1: screenHeight,
                    paint);
            }

            // Horizontal lines: from ground up in BlockSize steps
            for (float y = groundY; y >= 0; y -= blockSize)
            {
                canvas.DrawLine(
                    x0: 0,
                    y0: y,
                    x1: screenWidth,
                    y1: y,
                    paint);
            }
        }

        private void DrawHud(SKCanvas canvas, SKImageInfo info)
        {
            string? text = _state switch
            {
                GameState.Ready => "Press SPACE to Start",
                GameState.Dead => "You Fell! Press R to Restart",
                GameState.Won => "Level Complete! Press R to Restart",
                _ => null
            };

            if (text == null)
                return;

            using var paint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true,
                TextSize = 32,
                TextAlign = SKTextAlign.Center
            };

            float x = info.Width / 2f;
            float y = info.Height / 4f;
            canvas.DrawText(text, x, y, paint);
        }
    }
}

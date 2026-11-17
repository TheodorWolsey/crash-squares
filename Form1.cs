using System.Diagnostics;

namespace crash_squares;

public partial class Form1 : Form
{
    private const int SquareSize = 20;
    private const int PlayfieldSize = 600;
    private const int MinimumPlayfieldSize = 20;
    private const int MinimumRingPlayfieldSize = 120;
    private const float RingInnerRadiusRatio = 0.25f;
    private const double PlayfieldShrinkRate = 50;
    private const double Acceleration = 600;
    private const double FrictionDeceleration = 150;
    private const double CountdownDuration = 4;
    private const double DestroyEffectDuration = 0.35;
    private const double EndDelayDuration = 0.2;

    private readonly System.Windows.Forms.Timer movementTimer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<PlayerSquare> squares = [];
    private readonly List<PlayerSquare> removedSquares = [];
    private readonly List<DestroyEffect> destroyEffects = [];
    private readonly Button addSquareButton;
    private readonly Button removeSquaresButton;
    private readonly Button startGameButton;
    private readonly Button playfieldButton;
    private readonly ToolStripMenuItem squarePlayfieldMenuItem;
    private readonly ToolStripMenuItem ringPlayfieldMenuItem;
    private PlayfieldShape playfieldShape;
    private GameState gameState;
    private double countdownStartedAt;
    private double playfieldReduction;
    private double endDelayStartedAt;
    private bool waitingForEndDelay;
    private long lastTick;
    private int nextPlayerNumber = 3;

    public Form1()
    {
        InitializeComponent();

        Text = "Crash Squares";
        KeyPreview = true;
        DoubleBuffered = true;
        gameState = GameState.Waiting;

        Rectangle playfield = GetPlayfieldBounds();
        PointF redPosition = new(
            playfield.Left + (playfield.Width - SquareSize) / 2f - SquareSize * 2,
            playfield.Top + (playfield.Height - SquareSize) / 2f);
        squares.Add(new PlayerSquare("Red", Color.Red, redPosition, Keys.Up, Keys.Left, Keys.Down, Keys.Right, true));
        squares.Add(new PlayerSquare("Blue", Color.Blue, new PointF(redPosition.X + SquareSize * 4, redPosition.Y),
            Keys.W, Keys.A, Keys.S, Keys.D, true));
        KeepSquaresInPlayfield();

        addSquareButton = new Button
        {
            Location = new Point(12, 12),
            Size = new Size(176, 32),
            Text = "Add square"
        };
        addSquareButton.Click += AddSquare;
        Controls.Add(addSquareButton);

        removeSquaresButton = new Button
        {
            Location = new Point(12, 52),
            Size = new Size(176, 32),
            Text = "Remove added squares"
        };
        removeSquaresButton.Click += RemoveAddedSquares;
        Controls.Add(removeSquaresButton);

        squarePlayfieldMenuItem = new ToolStripMenuItem("Square");
        squarePlayfieldMenuItem.Click += (_, _) => SelectPlayfield(PlayfieldShape.Square);
        ringPlayfieldMenuItem = new ToolStripMenuItem("Ring");
        ringPlayfieldMenuItem.Click += (_, _) => SelectPlayfield(PlayfieldShape.Ring);
        var playfieldMenu = new ContextMenuStrip(components!);
        playfieldMenu.Items.AddRange([squarePlayfieldMenuItem, ringPlayfieldMenuItem]);

        playfieldButton = new Button
        {
            Location = new Point(12, 92),
            Size = new Size(176, 32),
            Text = "Field: Square",
            ContextMenuStrip = playfieldMenu
        };
        playfieldButton.Click += (_, _) =>
            playfieldMenu.Show(playfieldButton, new Point(0, playfieldButton.Height));
        Controls.Add(playfieldButton);

        startGameButton = new Button
        {
            Location = new Point(12, 132),
            Size = new Size(176, 36),
            Text = "Start Game!",
            Enabled = false
        };
        startGameButton.Click += StartGame;
        Controls.Add(startGameButton);
        UpdateStartButton();

        movementTimer = new System.Windows.Forms.Timer(components!)
        {
            Interval = 16
        };
        movementTimer.Tick += UpdateMovement;
        movementTimer.Start();
    }

    private enum PlayfieldShape
    {
        Square,
        Ring
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        bool handled = false;
        foreach (PlayerSquare square in squares)
        {
            handled |= SetDirection(square, key, true);
        }

        return handled || base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        foreach (PlayerSquare square in squares)
        {
            SetDirection(square, e.KeyCode, false);
        }

        base.OnKeyUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle playfield = GetPlayfieldBounds();
        if (playfieldShape == PlayfieldShape.Ring)
        {
            e.Graphics.FillEllipse(Brushes.Black, playfield);
            float innerRadius = GetRingInnerRadius(playfield);
            var hole = new RectangleF(
                playfield.Left + playfield.Width / 2f - innerRadius,
                playfield.Top + playfield.Height / 2f - innerRadius,
                innerRadius * 2,
                innerRadius * 2);
            using var holeBrush = new SolidBrush(BackColor);
            e.Graphics.FillEllipse(holeBrush, hole);
        }
        else
        {
            e.Graphics.FillRectangle(Brushes.Black, playfield);
        }

        foreach (PlayerSquare square in squares)
        {
            using var brush = new SolidBrush(square.Color);
            e.Graphics.FillRectangle(
                brush,
                square.Position.X,
                square.Position.Y,
                SquareSize,
                SquareSize);
        }

        foreach (DestroyEffect destroyEffect in destroyEffects)
        {
            double elapsed = clock.Elapsed.TotalSeconds - destroyEffect.StartedAt;
            double progress = Math.Clamp(elapsed / DestroyEffectDuration, 0, 1);
            foreach (DestroyEffect.Shard shard in destroyEffect.Shards)
            {
                int alpha = (int)Math.Clamp(255 * (1 - progress), 0, 255);
                using var brush = new SolidBrush(Color.FromArgb(alpha, shard.Color));
                e.Graphics.FillRectangle(brush, shard.Position.X, shard.Position.Y, shard.Size, shard.Size);
            }
        }

        string? overlayText = gameState switch
        {
            GameState.Countdown => GetCountdownText(),
            GameState.Finished => squares.Count == 1 ? $"{squares[0].Name} wins!" : null,
            _ => null
        };
        if (overlayText is not null)
        {
            Color overlayColor = gameState switch
            {
                GameState.Countdown => overlayText switch
                {
                    "3" => Color.Red,
                    "2" => Color.Red,
                    "1" => Color.Yellow,
                    "Go!" => Color.Green,
                    _ => Color.White
                },
                GameState.Finished when squares.Count == 1 => squares[0].Color,
                _ => Color.White
            };
            using var font = new Font(Font.FontFamily, 40, FontStyle.Bold);
            using var brush = new SolidBrush(overlayColor);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            e.Graphics.DrawString(overlayText, font, brush, playfield, format);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        KeepSquaresInPlayfield();
        Invalidate();
    }

    private void SelectPlayfield(PlayfieldShape shape)
    {
        if (shape == playfieldShape ||
            gameState is GameState.Countdown or GameState.Running)
        {
            return;
        }

        PlayfieldShape previousShape = playfieldShape;
        double previousReduction = playfieldReduction;
        PlayerSquare[] activeSquares = squares.ToArray();
        PointF[] previousPositions = activeSquares.Select(square => square.Position).ToArray();
        playfieldShape = shape;
        playfieldReduction = 0;
        squares.Clear();

        for (int i = 0; i < activeSquares.Length; i++)
        {
            PointF? position = FindAvailablePosition();
            if (position is null)
            {
                playfieldShape = previousShape;
                playfieldReduction = previousReduction;
                for (int restoreIndex = 0; restoreIndex < activeSquares.Length; restoreIndex++)
                {
                    activeSquares[restoreIndex].Position = previousPositions[restoreIndex];
                    squares.Add(activeSquares[restoreIndex]);
                }

                MessageBox.Show(this, "There is not enough room for all squares in that field.",
                    "Playfield full", MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdatePlayfieldButton();
                Invalidate();
                return;
            }

            activeSquares[i].Position = position.Value;
            activeSquares[i].Velocity = PointF.Empty;
            squares.Add(activeSquares[i]);
        }

        UpdatePlayfieldButton();
        KeepSquaresInPlayfield();
        Invalidate();
    }

    private void UpdatePlayfieldButton()
    {
        playfieldButton.Text = $"Field: {playfieldShape}";
        squarePlayfieldMenuItem.Checked = playfieldShape == PlayfieldShape.Square;
        ringPlayfieldMenuItem.Checked = playfieldShape == PlayfieldShape.Ring;
        playfieldButton.Enabled = gameState is not (GameState.Countdown or GameState.Running);
    }

    private void AddSquare(object? sender, EventArgs e)
    {
        Keys[]? controls = PromptForControls();
        if (controls is null)
        {
            return;
        }

        if (squares.Concat(removedSquares).Any(square => controls.Any(key =>
                key == square.Up || key == square.Left || key == square.Down || key == square.Right)))
        {
            MessageBox.Show(this, "Those keys are already assigned to another square.", "Controls in use",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        PointF? position = FindAvailablePosition();
        if (position is null)
        {
            MessageBox.Show(this, "There is no room for another square.", "Playfield full",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Color color;
        do
        {
            color = Color.FromArgb(
                Random.Shared.Next(64, 256),
                Random.Shared.Next(64, 256),
                Random.Shared.Next(64, 256));
        }
        while (color == Color.Red || color == Color.Blue);

        squares.Add(new PlayerSquare($"Player {nextPlayerNumber++}", color, position.Value,
            controls[0], controls[1], controls[2], controls[3], false));
        UpdateStartButton();
        Invalidate();
    }

    private void RemoveAddedSquares(object? sender, EventArgs e)
    {
        squares.RemoveAll(square => !square.IsOriginal);
        removedSquares.RemoveAll(square => !square.IsOriginal);
        nextPlayerNumber = 3;
        CheckForGameOver();
        UpdateStartButton();
        Invalidate();
    }

    private void StartGame(object? sender, EventArgs e)
    {
        if (squares.Count + removedSquares.Count < 2 ||
            gameState is GameState.Countdown or GameState.Running)
        {
            return;
        }

        playfieldReduction = 0;
        foreach (PlayerSquare square in removedSquares.ToArray())
        {
            PointF? position = FindAvailablePosition();
            if (position is null)
            {
                MessageBox.Show(this, "There is not enough room to respawn all squares.", "Playfield full",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateStartButton();
                return;
            }

            square.Position = position.Value;
            square.Velocity = PointF.Empty;
            square.MovingUp = false;
            square.MovingLeft = false;
            square.MovingDown = false;
            square.MovingRight = false;
            squares.Add(square);
            removedSquares.Remove(square);
        }

        foreach (PlayerSquare square in squares)
        {
            square.Velocity = PointF.Empty;
            square.MovingUp = false;
            square.MovingLeft = false;
            square.MovingDown = false;
            square.MovingRight = false;
        }

        waitingForEndDelay = false;
        countdownStartedAt = clock.Elapsed.TotalSeconds;
        gameState = GameState.Countdown;
        UpdateStartButton();
        Invalidate();
    }

    private string GetCountdownText()
    {
        double elapsed = clock.Elapsed.TotalSeconds - countdownStartedAt;
        return elapsed >= 3 ? "Go!" : (3 - (int)elapsed).ToString();
    }

    private void UpdateStartButton()
    {
        startGameButton.Enabled = squares.Count + removedSquares.Count > 1 &&
            gameState is not (GameState.Countdown or GameState.Running);
        UpdatePlayfieldButton();
    }

    private Keys[]? PromptForControls()
    {
        using var dialog = new Form
        {
            Text = "Choose controls",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(380, 145),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };

        var instruction = new Label
        {
            AutoSize = true,
            Location = new Point(12, 12),
            Text = "Press four distinct keys in order: up, left, down, right."
        };
        var keyInput = new TextBox
        {
            Location = new Point(12, 40),
            Size = new Size(356, 23),
            ReadOnly = true
        };
        var cancelButton = new Button
        {
            Location = new Point(293, 96),
            Size = new Size(75, 30),
            Text = "Cancel",
            DialogResult = DialogResult.Cancel
        };

        var selectedKeys = new List<Keys>(4);
        keyInput.PreviewKeyDown += (_, e) => e.IsInputKey = true;
        keyInput.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            Keys key = e.KeyCode;
            if (key is Keys.None or Keys.ShiftKey or Keys.ControlKey or Keys.Menu
                or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey
                or Keys.LMenu or Keys.RMenu || selectedKeys.Contains(key))
            {
                return;
            }

            selectedKeys.Add(key);
            keyInput.Text = string.Join(", ", selectedKeys.Select(ToKeyLabel));
            if (selectedKeys.Count == 4)
            {
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            }
        };

        dialog.Controls.AddRange([instruction, keyInput, cancelButton]);
        dialog.CancelButton = cancelButton;
        dialog.Shown += (_, _) => keyInput.Focus();
        return dialog.ShowDialog(this) == DialogResult.OK ? selectedKeys.ToArray() : null;
    }

    private static string ToKeyLabel(Keys key) => key switch
    {
        Keys.D0 => "0",
        Keys.D1 => "1",
        Keys.D2 => "2",
        Keys.D3 => "3",
        Keys.D4 => "4",
        Keys.D5 => "5",
        Keys.D6 => "6",
        Keys.D7 => "7",
        Keys.D8 => "8",
        Keys.D9 => "9",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.Oemtilde => "`",
        Keys.OemPipe => "\\",
        _ => key.ToString()
    };

    private PointF? FindAvailablePosition()
    {
        Rectangle playfield = GetPlayfieldBounds();
        var availablePositions = new List<PointF>();
        for (int y = playfield.Top; y + SquareSize <= playfield.Bottom; y += SquareSize)
        {
            for (int x = playfield.Left; x + SquareSize <= playfield.Right; x += SquareSize)
            {
                var candidate = new RectangleF(x, y, SquareSize, SquareSize);
                if (IsSquareInPlayfield(candidate) &&
                    squares.All(square => !candidate.IntersectsWith(
                        new RectangleF(square.Position.X, square.Position.Y, SquareSize, SquareSize))))
                {
                    availablePositions.Add(new PointF(x, y));
                }
            }
        }

        return availablePositions.Count == 0
            ? null
            : availablePositions[Random.Shared.Next(availablePositions.Count)];
    }

    private void UpdateMovement(object? sender, EventArgs e)
    {
        if (gameState == GameState.Countdown)
        {
            if (clock.Elapsed.TotalSeconds - countdownStartedAt >= CountdownDuration)
            {
                gameState = GameState.Running;
                UpdateStartButton();
            }

            Invalidate();
            return;
        }

        if (gameState != GameState.Running)
        {
            return;
        }

        long currentTick = clock.ElapsedTicks;
        double deltaTime = Math.Min(
            (currentTick - lastTick) / (double)Stopwatch.Frequency,
            0.05);
        lastTick = currentTick;
        playfieldReduction = Math.Min(
            PlayfieldSize,
            playfieldReduction + PlayfieldShrinkRate * deltaTime);

        foreach (DestroyEffect destroyEffect in destroyEffects)
        {
            destroyEffect.Update(deltaTime);
        }

        destroyEffects.RemoveAll(effect =>
            clock.Elapsed.TotalSeconds - effect.StartedAt >= DestroyEffectDuration);

        foreach (PlayerSquare square in squares)
        {
            UpdateSquare(square, deltaTime);
        }

        ResolveCollisions();
        if (waitingForEndDelay &&
            clock.Elapsed.TotalSeconds - endDelayStartedAt >= EndDelayDuration)
        {
            waitingForEndDelay = false;
            gameState = GameState.Finished;
            UpdateStartButton();
        }

        Invalidate();
    }

    private void ResolveCollisions()
    {
        bool collisionFound;
        do
        {
            collisionFound = false;
            for (int firstIndex = 0; firstIndex < squares.Count && !collisionFound; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1; secondIndex < squares.Count; secondIndex++)
                {
                    PlayerSquare first = squares[firstIndex];
                    PlayerSquare second = squares[secondIndex];
                    if (!GetSquareBounds(first).IntersectsWith(GetSquareBounds(second)))
                    {
                        continue;
                    }

                    PlayerSquare loser = GetSlowerSquare(first, second);
                    destroyEffects.Add(new DestroyEffect(loser.Position, loser.Color, clock.Elapsed.TotalSeconds));
                    squares.Remove(loser);
                    removedSquares.Add(loser);
                    collisionFound = true;
                    break;
                }
            }
        }
        while (collisionFound);

        CheckForGameOver();
    }

    private void CheckForGameOver()
    {
        if (gameState == GameState.Running && squares.Count <= 1 && !waitingForEndDelay)
        {
            waitingForEndDelay = true;
            endDelayStartedAt = clock.Elapsed.TotalSeconds;
        }
    }

    private static PlayerSquare GetSlowerSquare(PlayerSquare first, PlayerSquare second)
    {
        float deltaX = second.Position.X - first.Position.X;
        float deltaY = second.Position.Y - first.Position.Y;
        float distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);

        if (distance == 0)
        {
            deltaX = 1;
            deltaY = 0;
        }
        else
        {
            deltaX /= distance;
            deltaY /= distance;
        }

        float firstSpeedTowardCollision = Math.Max(
            0,
            first.Velocity.X * deltaX + first.Velocity.Y * deltaY);
        float secondSpeedTowardCollision = Math.Max(
            0,
            -(second.Velocity.X * deltaX + second.Velocity.Y * deltaY));

        return firstSpeedTowardCollision < secondSpeedTowardCollision ? first : second;
    }

    private static RectangleF GetSquareBounds(PlayerSquare square) =>
        new(square.Position.X, square.Position.Y, SquareSize, SquareSize);

    private enum GameState
    {
        Waiting,
        Countdown,
        Running,
        Finished
    }

    private void UpdateSquare(PlayerSquare square, double deltaTime)
    {
        float directionX = (square.MovingRight ? 1 : 0) - (square.MovingLeft ? 1 : 0);
        float directionY = (square.MovingDown ? 1 : 0) - (square.MovingUp ? 1 : 0);
        float directionLength = MathF.Sqrt(directionX * directionX + directionY * directionY);

        if (directionLength > 0)
        {
            directionX /= directionLength;
            directionY /= directionLength;
            square.Velocity.X += (float)(directionX * Acceleration * deltaTime);
            square.Velocity.Y += (float)(directionY * Acceleration * deltaTime);
        }

        float speed = MathF.Sqrt(
            square.Velocity.X * square.Velocity.X +
            square.Velocity.Y * square.Velocity.Y);
        float speedReduction = (float)(FrictionDeceleration * deltaTime);
        if (speed <= speedReduction)
        {
            square.Velocity = PointF.Empty;
        }
        else if (speed > 0)
        {
            float frictionScale = (speed - speedReduction) / speed;
            square.Velocity.X *= frictionScale;
            square.Velocity.Y *= frictionScale;
        }

        square.Position.X += (float)(square.Velocity.X * deltaTime);
        square.Position.Y += (float)(square.Velocity.Y * deltaTime);
        KeepSquareInPlayfield(square);
    }

    private Rectangle GetPlayfieldBounds()
    {
        int size = Math.Min(PlayfieldSize, Math.Min(ClientSize.Width, ClientSize.Height));
        int minimumSize = playfieldShape == PlayfieldShape.Ring
            ? MinimumRingPlayfieldSize
            : MinimumPlayfieldSize;
        size = Math.Max(Math.Min(minimumSize, size), size - (int)playfieldReduction);
        return new Rectangle(
            (ClientSize.Width - size) / 2,
            (ClientSize.Height - size) / 2,
            size,
            size);
    }

    private void KeepSquaresInPlayfield()
    {
        foreach (PlayerSquare square in squares)
        {
            KeepSquareInPlayfield(square);
        }
    }

    private void KeepSquareInPlayfield(PlayerSquare square)
    {
        Rectangle playfield = GetPlayfieldBounds();
        if (playfieldShape == PlayfieldShape.Ring)
        {
            KeepSquareInRing(square, playfield);
            return;
        }

        float minX = playfield.Left;
        float minY = playfield.Top;
        float maxX = Math.Max(minX, playfield.Right - SquareSize);
        float maxY = Math.Max(minY, playfield.Bottom - SquareSize);

        if (square.Position.X < minX || square.Position.X > maxX)
        {
            square.Position.X = Math.Clamp(square.Position.X, minX, maxX);
            square.Velocity.X = 0;
        }

        if (square.Position.Y < minY || square.Position.Y > maxY)
        {
            square.Position.Y = Math.Clamp(square.Position.Y, minY, maxY);
            square.Velocity.Y = 0;
        }
    }

    private void KeepSquareInRing(PlayerSquare square, Rectangle playfield)
    {
        float centerX = playfield.Left + playfield.Width / 2f;
        float centerY = playfield.Top + playfield.Height / 2f;
        float deltaX = square.Position.X + SquareSize / 2f - centerX;
        float deltaY = square.Position.Y + SquareSize / 2f - centerY;
        float distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        float halfDiagonal = SquareSize / MathF.Sqrt(2);
        float innerRadius = GetRingInnerRadius(playfield) + halfDiagonal;
        float outerRadius = playfield.Width / 2f - halfDiagonal;
        float targetDistance = Math.Clamp(distance, innerRadius, outerRadius);
        if (targetDistance == distance)
        {
            return;
        }

        if (distance == 0)
        {
            deltaX = 1;
            deltaY = 0;
        }
        else
        {
            deltaX /= distance;
            deltaY /= distance;
        }

        square.Position.X = centerX + deltaX * targetDistance - SquareSize / 2f;
        square.Position.Y = centerY + deltaY * targetDistance - SquareSize / 2f;

        float radialVelocity = square.Velocity.X * deltaX + square.Velocity.Y * deltaY;
        if ((distance < innerRadius && radialVelocity < 0) ||
            (distance > outerRadius && radialVelocity > 0))
        {
            square.Velocity.X -= radialVelocity * deltaX;
            square.Velocity.Y -= radialVelocity * deltaY;
        }
    }

    private bool IsSquareInPlayfield(RectangleF square)
    {
        if (playfieldShape == PlayfieldShape.Square)
        {
            Rectangle bounds = GetPlayfieldBounds();
            return square.Left >= bounds.Left && square.Top >= bounds.Top &&
                square.Right <= bounds.Right && square.Bottom <= bounds.Bottom;
        }

        Rectangle playfield = GetPlayfieldBounds();
        float centerX = playfield.Left + playfield.Width / 2f;
        float centerY = playfield.Top + playfield.Height / 2f;
        float deltaX = square.X + square.Width / 2f - centerX;
        float deltaY = square.Y + square.Height / 2f - centerY;
        float distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        float halfDiagonal = MathF.Sqrt(square.Width * square.Width + square.Height * square.Height) / 2f;
        return distance >= GetRingInnerRadius(playfield) + halfDiagonal &&
            distance <= playfield.Width / 2f - halfDiagonal;
    }

    private static float GetRingInnerRadius(Rectangle playfield) =>
        playfield.Width / 2f * RingInnerRadiusRatio;

    private static bool SetDirection(PlayerSquare square, Keys key, bool isMoving)
    {
        if (key == square.Up)
        {
            square.MovingUp = isMoving;
        }
        else if (key == square.Left)
        {
            square.MovingLeft = isMoving;
        }
        else if (key == square.Down)
        {
            square.MovingDown = isMoving;
        }
        else if (key == square.Right)
        {
            square.MovingRight = isMoving;
        }
        else
        {
            return false;
        }

        return true;
    }

    private sealed class DestroyEffect(PointF position, Color color, double startedAt)
    {
        public PointF Position { get; } = position;
        public Color Color { get; } = color;
        public double StartedAt { get; } = startedAt;
        public List<Shard> Shards { get; } = CreateShards(position, color);

        public void Update(double deltaTime)
        {
            foreach (Shard shard in Shards)
            {
                PointF position = shard.Position;
                PointF velocity = shard.Velocity;

                position.X += velocity.X * (float)deltaTime;
                position.Y += velocity.Y * (float)deltaTime;
                velocity.Y += 220f * (float)deltaTime;
                velocity.X *= 0.99f;
                velocity.Y *= 0.995f;

                shard.Position = position;
                shard.Velocity = velocity;
            }
        }

        private static List<Shard> CreateShards(PointF center, Color color)
        {
            var shards = new List<Shard>(12);
            for (int i = 0; i < 12; i++)
            {
                double angle = (Math.PI * 2 * i) / 12 + Random.Shared.NextDouble() * 0.6;
                float speed = Random.Shared.Next(80, 220);
                shards.Add(new Shard
                {
                    Position = new PointF(center.X + SquareSize / 2f, center.Y + SquareSize / 2f),
                    Velocity = new PointF(
                        (float)(Math.Cos(angle) * speed),
                        (float)(Math.Sin(angle) * speed)),
                    Size = Random.Shared.Next(2, 6),
                    Color = color
                });
            }

            return shards;
        }

        public sealed class Shard
        {
            public PointF Position;
            public PointF Velocity;
            public float Size { get; set; }
            public Color Color { get; set; }
        }
    }

    private sealed class PlayerSquare(
        string name,
        Color color,
        PointF position,
        Keys up,
        Keys left,
        Keys down,
        Keys right,
        bool isOriginal)
    {
        public string Name { get; } = name;
        public Color Color { get; } = color;
        public bool IsOriginal { get; } = isOriginal;
        public PointF Position = position;
        public PointF Velocity;
        public Keys Up { get; } = up;
        public Keys Left { get; } = left;
        public Keys Down { get; } = down;
        public Keys Right { get; } = right;
        public bool MovingUp { get; set; }
        public bool MovingLeft { get; set; }
        public bool MovingDown { get; set; }
        public bool MovingRight { get; set; }
    }
}

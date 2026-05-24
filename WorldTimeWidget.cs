using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        bool createdNew;
        using (new Mutex(true, "CodexWorldTimeWidget.SingleInstance", out createdNew))
        {
            if (!createdNew)
            {
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new WorldTimeForm());
        }
    }
}

internal sealed class WorldTimeForm : Form
{
    private const string PacificTimeZoneId = "Pacific Standard Time";
    private const string PacificClockMigrationId = "add-usa-pacific-clock-v1";
    private const int CornerRadius = 18;
    private const int CompactWidth = 268;
    private const int ExpandedWidth = 340;

    private readonly List<ClockItem> clocks = new List<ClockItem>();
    private readonly System.Windows.Forms.Timer timer;
    private readonly NotifyIcon trayIcon;
    private readonly ToolStripMenuItem showHideMenuItem;
    private readonly ToolStripMenuItem compactMenuItem;

    private List<ClockConfig> clockConfigs;
    private Panel header;
    private Panel clockPanel;
    private Label titleLabel;
    private Button settingsButton;
    private Button compactButton;
    private Button hideButton;
    private Button closeButton;
    private bool compactMode = true;
    private bool allowExit;
    private bool dragging;
    private bool hasStoredPosition;
    private Point dragStart;
    private Point storedPosition;

    [DllImport("Gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("Gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);

    public WorldTimeForm()
    {
        Text = "World Time Widget";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(24, 27, 33);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        clockConfigs = LoadClockConfigs();

        showHideMenuItem = new ToolStripMenuItem("Hide", null, delegate { ToggleVisibility(); });
        compactMenuItem = new ToolStripMenuItem("Compact view", null, delegate { ToggleCompactMode(); });
        ToolStripMenuItem settingsMenuItem = new ToolStripMenuItem("Settings", null, delegate { OpenSettings(); });
        ToolStripMenuItem exitMenuItem = new ToolStripMenuItem("Exit", null, delegate { ExitWidget(); });

        ContextMenuStrip trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(showHideMenuItem);
        trayMenu.Items.Add(compactMenuItem);
        trayMenu.Items.Add(settingsMenuItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitMenuItem);

        trayIcon = new NotifyIcon();
        trayIcon.Text = "World Time Widget";
        trayIcon.Icon = SystemIcons.Application;
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += delegate { ShowWidget(); };

        BuildLayout();
        BuildClockItems();
        LoadSettings();
        ApplyLayout();
        ApplyInitialPosition();

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 1000;
        timer.Tick += delegate { UpdateClocks(); };
        UpdateClocks();
        timer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000;
            return parameters;
        }
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        IntPtr region = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, CornerRadius, CornerRadius);
        Region oldRegion = Region;
        Region = Region.FromHrgn(region);
        DeleteObject(region);
        if (oldRegion != null)
        {
            oldRegion.Dispose();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        SaveSettings();
        if (!allowExit)
        {
            eventArgs.Cancel = true;
            HideWidget();
            return;
        }

        trayIcon.Visible = false;
        trayIcon.Dispose();
        base.OnFormClosing(eventArgs);
    }

    private void BuildLayout()
    {
        header = new Panel();
        header.Dock = DockStyle.Top;
        header.BackColor = Color.FromArgb(21, 26, 32);
        header.MouseDown += StartDrag;
        header.MouseMove += ContinueDrag;
        header.MouseUp += EndDrag;
        Controls.Add(header);

        titleLabel = new Label();
        titleLabel.AutoSize = false;
        titleLabel.ForeColor = Color.FromArgb(244, 247, 251);
        titleLabel.Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
        titleLabel.MouseDown += StartDrag;
        titleLabel.MouseMove += ContinueDrag;
        titleLabel.MouseUp += EndDrag;
        header.Controls.Add(titleLabel);

        settingsButton = CreateHeaderButton("S");
        settingsButton.Click += delegate { OpenSettings(); };
        header.Controls.Add(settingsButton);

        compactButton = CreateHeaderButton("F");
        compactButton.Click += delegate { ToggleCompactMode(); };
        header.Controls.Add(compactButton);

        hideButton = CreateHeaderButton("H");
        hideButton.Click += delegate { HideWidget(); };
        header.Controls.Add(hideButton);

        closeButton = CreateHeaderButton("x");
        closeButton.Click += delegate { ExitWidget(); };
        header.Controls.Add(closeButton);

        clockPanel = new Panel();
        clockPanel.AutoScroll = true;
        clockPanel.BackColor = BackColor;
        Controls.Add(clockPanel);
    }

    private Button CreateHeaderButton(string text)
    {
        Button button = new Button();
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(45, 55, 66);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(47, 58, 70);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(60, 72, 86);
        button.BackColor = Color.FromArgb(31, 39, 48);
        button.ForeColor = Color.FromArgb(200, 209, 220);
        button.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point);
        button.TabStop = false;
        return button;
    }

    private void BuildClockItems()
    {
        clockPanel.Controls.Clear();
        clocks.Clear();

        for (int i = 0; i < clockConfigs.Count; i++)
        {
            ClockItem clock = new ClockItem(clockConfigs[i]);
            Panel card = CreateCard(clock);
            clockPanel.Controls.Add(card);
            clocks.Add(clock);
        }
    }

    private Panel CreateCard(ClockItem clock)
    {
        Panel card = new Panel();
        card.BackColor = Color.FromArgb(16, 20, 25);
        card.Paint += delegate(object sender, PaintEventArgs args)
        {
            using (Pen pen = new Pen(Color.FromArgb(41, 49, 59)))
            {
                args.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            }
        };
        clock.Card = card;

        clock.CountryLabel = new Label();
        clock.CountryLabel.Text = clock.Label;
        clock.CountryLabel.ForeColor = clock.Accent;
        clock.CountryLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
        card.Controls.Add(clock.CountryLabel);

        clock.DateLabel = new Label();
        clock.DateLabel.ForeColor = Color.FromArgb(174, 184, 197);
        clock.DateLabel.Font = new Font("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
        card.Controls.Add(clock.DateLabel);

        clock.TimeLabel = new Label();
        clock.TimeLabel.ForeColor = Color.FromArgb(248, 250, 252);
        clock.TimeLabel.Font = new Font("Segoe UI", 18F, FontStyle.Bold, GraphicsUnit.Point);
        clock.TimeLabel.TextAlign = ContentAlignment.MiddleRight;
        card.Controls.Add(clock.TimeLabel);

        clock.ZoneLabel = new Label();
        clock.ZoneLabel.ForeColor = Color.FromArgb(174, 184, 197);
        clock.ZoneLabel.Font = new Font("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
        clock.ZoneLabel.TextAlign = ContentAlignment.MiddleRight;
        card.Controls.Add(clock.ZoneLabel);

        return card;
    }

    private void ApplyLayout()
    {
        SuspendLayout();

        int newWidth = compactMode ? CompactWidth : ExpandedWidth;
        int newHeight = GetWidgetHeight();
        Size = new Size(newWidth, newHeight);
        MinimumSize = new Size(newWidth, newHeight);
        MaximumSize = new Size(newWidth, newHeight);

        header.Height = compactMode ? 32 : 42;
        clockPanel.SetBounds(0, header.Height, newWidth, newHeight - header.Height);
        titleLabel.Text = compactMode ? "Times" : "World Time";
        titleLabel.Location = compactMode ? new Point(12, 7) : new Point(18, 11);
        titleLabel.Size = compactMode ? new Size(86, 20) : new Size(160, 22);
        titleLabel.Font = new Font("Segoe UI", compactMode ? 9F : 10F, FontStyle.Bold, GraphicsUnit.Point);

        int buttonSize = compactMode ? 24 : 28;
        int buttonTop = compactMode ? 4 : 8;
        settingsButton.SetBounds(newWidth - (buttonSize * 4) - 16, buttonTop, buttonSize, buttonSize);
        compactButton.SetBounds(newWidth - (buttonSize * 3) - 12, buttonTop, buttonSize, buttonSize);
        hideButton.SetBounds(newWidth - (buttonSize * 2) - 8, buttonTop, buttonSize, buttonSize);
        closeButton.SetBounds(newWidth - buttonSize - 4, buttonTop, buttonSize, buttonSize);
        compactButton.Text = compactMode ? "F" : "C";
        compactMenuItem.Checked = compactMode;

        int cardLeft = compactMode ? 8 : 12;
        int cardTop = compactMode ? 8 : 12;
        int cardWidth = newWidth - (cardLeft * 2);
        int cardHeight = compactMode ? 30 : 64;
        int cardGap = compactMode ? 8 : 10;

        for (int i = 0; i < clocks.Count; i++)
        {
            ClockItem clock = clocks[i];
            clock.Card.SetBounds(cardLeft, cardTop + (i * (cardHeight + cardGap)), cardWidth, cardHeight);
            clock.Card.Invalidate();

            if (compactMode)
            {
                clock.CountryLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point);
                clock.CountryLabel.SetBounds(10, 6, 92, 18);
                clock.DateLabel.Visible = false;
                clock.ZoneLabel.Visible = false;
                clock.TimeLabel.Font = new Font("Segoe UI", 14.5F, FontStyle.Bold, GraphicsUnit.Point);
                clock.TimeLabel.SetBounds(102, 2, cardWidth - 112, 26);
            }
            else
            {
                clock.CountryLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
                clock.CountryLabel.SetBounds(12, 10, 132, 18);
                clock.DateLabel.Visible = true;
                clock.DateLabel.SetBounds(12, 33, 132, 18);
                clock.TimeLabel.Font = new Font("Segoe UI", 18F, FontStyle.Bold, GraphicsUnit.Point);
                clock.TimeLabel.SetBounds(144, 5, 164, 32);
                clock.ZoneLabel.Visible = true;
                clock.ZoneLabel.SetBounds(144, 37, 164, 18);
            }
        }

        ResumeLayout();
        UpdateClocks();
    }

    private int GetWidgetHeight()
    {
        int headerHeight = compactMode ? 32 : 42;
        int cardHeight = compactMode ? 30 : 64;
        int gap = compactMode ? 8 : 10;
        int panelPadding = compactMode ? 16 : 24;
        int count = Math.Max(1, clockConfigs.Count);
        int contentHeight = panelPadding + (count * cardHeight) + ((count - 1) * gap);
        int targetHeight = headerHeight + contentHeight;
        int maxHeight = Math.Max(180, Screen.PrimaryScreen.WorkingArea.Height - 100);
        return Math.Min(targetHeight, maxHeight);
    }

    private void UpdateClocks()
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;

        foreach (ClockItem clock in clocks)
        {
            DateTimeOffset localTime = TimeZoneInfo.ConvertTime(utcNow, clock.TimeZone);
            TimeSpan offset = clock.TimeZone.GetUtcOffset(utcNow.UtcDateTime);
            clock.TimeLabel.Text = localTime.ToString(compactMode ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);
            clock.DateLabel.Text = localTime.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
            clock.ZoneLabel.Text = clock.Place + " | " + FormatOffset(offset);
        }
    }

    private static string FormatOffset(TimeSpan offset)
    {
        int minutes = (int)Math.Round(offset.TotalMinutes);
        string sign = minutes >= 0 ? "+" : "-";
        minutes = Math.Abs(minutes);
        int hours = minutes / 60;
        int remainingMinutes = minutes % 60;
        return string.Format(CultureInfo.InvariantCulture, "UTC{0}{1:00}:{2:00}", sign, hours, remainingMinutes);
    }

    private void ToggleCompactMode()
    {
        compactMode = !compactMode;
        ApplyLayout();
        SaveSettings();
    }

    private void OpenSettings()
    {
        using (WorldTimeSettingsForm settingsForm = new WorldTimeSettingsForm(clockConfigs))
        {
            DialogResult result = settingsForm.ShowDialog(this);
            if (result != DialogResult.OK)
            {
                return;
            }

            clockConfigs = settingsForm.ClockConfigs;
            SaveClockConfigs(clockConfigs);
            BuildClockItems();
            ApplyLayout();
            SaveSettings();
        }
    }

    private void ToggleVisibility()
    {
        if (Visible)
        {
            HideWidget();
        }
        else
        {
            ShowWidget();
        }
    }

    private void HideWidget()
    {
        SaveSettings();
        Hide();
        showHideMenuItem.Text = "Show";
    }

    private void ShowWidget()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        showHideMenuItem.Text = "Hide";
    }

    private void ExitWidget()
    {
        allowExit = true;
        Close();
    }

    private void StartDrag(object sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        dragging = true;
        dragStart = eventArgs.Location;
    }

    private void ContinueDrag(object sender, MouseEventArgs eventArgs)
    {
        if (!dragging)
        {
            return;
        }

        Point screen = PointToScreen(eventArgs.Location);
        Location = new Point(screen.X - dragStart.X, screen.Y - dragStart.Y);
    }

    private void EndDrag(object sender, MouseEventArgs eventArgs)
    {
        dragging = false;
    }

    private void LoadSettings()
    {
        string path = SettingsPath;
        if (!File.Exists(path))
        {
            return;
        }

        string[] lines = File.ReadAllLines(path);
        int left;
        int top;
        if (lines.Length >= 2 &&
            int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out left) &&
            int.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out top))
        {
            storedPosition = new Point(left, top);
            hasStoredPosition = true;
        }

        bool savedCompactMode;
        if (lines.Length >= 3 &&
            bool.TryParse(lines[2], out savedCompactMode))
        {
            compactMode = savedCompactMode;
        }
    }

    private void ApplyInitialPosition()
    {
        if (hasStoredPosition)
        {
            Location = storedPosition;
            return;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(workArea.Right - Width - 24, workArea.Top + 88);
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(AppFolder);
        File.WriteAllLines(SettingsPath, new[]
        {
            Left.ToString(CultureInfo.InvariantCulture),
            Top.ToString(CultureInfo.InvariantCulture),
            compactMode.ToString(CultureInfo.InvariantCulture)
        });
    }

    private static List<ClockConfig> LoadClockConfigs()
    {
        List<ClockConfig> configs = new List<ClockConfig>();
        if (File.Exists(ClockConfigPath))
        {
            string[] lines = File.ReadAllLines(ClockConfigPath);
            foreach (string line in lines)
            {
                ClockConfig config = ClockConfig.FromLine(line);
                if (config != null && IsValidTimeZone(config.TimeZoneId))
                {
                    configs.Add(config);
                }
            }
        }

        if (configs.Count == 0)
        {
            configs = DefaultClockConfigs();
            SaveClockConfigs(configs);
            MarkMigrationApplied(PacificClockMigrationId);
        }
        else if (EnsurePacificClock(configs))
        {
            SaveClockConfigs(configs);
        }

        return configs;
    }

    private static void SaveClockConfigs(List<ClockConfig> configs)
    {
        Directory.CreateDirectory(AppFolder);
        List<string> lines = new List<string>();
        foreach (ClockConfig config in configs)
        {
            lines.Add(config.ToLine());
        }

        File.WriteAllLines(ClockConfigPath, lines.ToArray());
    }

    private static bool IsValidTimeZone(string timeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static List<ClockConfig> DefaultClockConfigs()
    {
        List<ClockConfig> defaults = new List<ClockConfig>();
        defaults.Add(new ClockConfig("USA", "New York", "Eastern Standard Time", Color.FromArgb(84, 199, 236).ToArgb()));
        defaults.Add(CreatePacificClockConfig());
        defaults.Add(new ClockConfig("Morocco", "Casablanca", "Morocco Standard Time", Color.FromArgb(240, 179, 90).ToArgb()));
        defaults.Add(new ClockConfig("China", "Beijing", "China Standard Time", Color.FromArgb(126, 231, 135).ToArgb()));
        return defaults;
    }

    private static ClockConfig CreatePacificClockConfig()
    {
        return new ClockConfig("USA PST", "Los Angeles", PacificTimeZoneId, Color.FromArgb(184, 144, 255).ToArgb());
    }

    private static bool EnsurePacificClock(List<ClockConfig> configs)
    {
        if (HasAppliedMigration(PacificClockMigrationId))
        {
            return false;
        }

        if (HasClockTimeZone(configs, PacificTimeZoneId))
        {
            MarkMigrationApplied(PacificClockMigrationId);
            return false;
        }

        if (!IsValidTimeZone(PacificTimeZoneId))
        {
            return false;
        }

        configs.Add(CreatePacificClockConfig());
        MarkMigrationApplied(PacificClockMigrationId);
        return true;
    }

    private static bool HasClockTimeZone(List<ClockConfig> configs, string timeZoneId)
    {
        foreach (ClockConfig config in configs)
        {
            if (string.Equals(config.TimeZoneId, timeZoneId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAppliedMigration(string migrationId)
    {
        string path = MigrationsPath;
        if (!File.Exists(path))
        {
            return false;
        }

        string[] lines = File.ReadAllLines(path);
        foreach (string line in lines)
        {
            if (string.Equals(line.Trim(), migrationId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void MarkMigrationApplied(string migrationId)
    {
        if (HasAppliedMigration(migrationId))
        {
            return;
        }

        Directory.CreateDirectory(AppFolder);
        File.AppendAllLines(MigrationsPath, new[] { migrationId });
    }

    internal static int PaletteColor(int index)
    {
        int[] colors = new[]
        {
            Color.FromArgb(84, 199, 236).ToArgb(),
            Color.FromArgb(240, 179, 90).ToArgb(),
            Color.FromArgb(126, 231, 135).ToArgb(),
            Color.FromArgb(184, 144, 255).ToArgb(),
            Color.FromArgb(255, 139, 139).ToArgb(),
            Color.FromArgb(105, 217, 193).ToArgb(),
            Color.FromArgb(255, 211, 105).ToArgb()
        };
        return colors[Math.Abs(index) % colors.Length];
    }

    private static string AppFolder
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexWorldTimeWidget");
        }
    }

    private static string SettingsPath
    {
        get
        {
            return Path.Combine(AppFolder, "settings.txt");
        }
    }

    private static string ClockConfigPath
    {
        get
        {
            return Path.Combine(AppFolder, "clocks.tsv");
        }
    }

    private static string MigrationsPath
    {
        get
        {
            return Path.Combine(AppFolder, "migrations.txt");
        }
    }
}

internal sealed class WorldTimeSettingsForm : Form
{
    private readonly List<ClockConfig> configs;
    private readonly List<TimeZoneChoice> timeZoneChoices;
    private readonly ListBox clockList;
    private readonly TextBox labelBox;
    private readonly TextBox placeBox;
    private readonly ComboBox timeZoneBox;
    private int selectedIndex = -1;
    private bool loadingSelection;

    public WorldTimeSettingsForm(List<ClockConfig> currentConfigs)
    {
        Text = "World Time Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 374);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        configs = ClockConfig.CloneList(currentConfigs);
        timeZoneChoices = TimeZoneChoice.GetChoices();

        clockList = new ListBox();
        clockList.Location = new Point(14, 16);
        clockList.Size = new Size(190, 286);
        clockList.SelectedIndexChanged += delegate { SelectClock(clockList.SelectedIndex); };
        Controls.Add(clockList);

        Button addButton = new Button();
        addButton.Text = "Add";
        addButton.SetBounds(14, 314, 58, 28);
        addButton.Click += delegate { AddClock(); };
        Controls.Add(addButton);

        Button removeButton = new Button();
        removeButton.Text = "Remove";
        removeButton.SetBounds(78, 314, 70, 28);
        removeButton.Click += delegate { RemoveClock(); };
        Controls.Add(removeButton);

        Button upButton = new Button();
        upButton.Text = "Up";
        upButton.SetBounds(154, 314, 50, 28);
        upButton.Click += delegate { MoveClock(-1); };
        Controls.Add(upButton);

        Label labelCaption = new Label();
        labelCaption.Text = "Display name";
        labelCaption.SetBounds(224, 18, 160, 20);
        Controls.Add(labelCaption);

        labelBox = new TextBox();
        labelBox.SetBounds(224, 42, 306, 24);
        Controls.Add(labelBox);

        Label placeCaption = new Label();
        placeCaption.Text = "City or place";
        placeCaption.SetBounds(224, 80, 160, 20);
        Controls.Add(placeCaption);

        placeBox = new TextBox();
        placeBox.SetBounds(224, 104, 306, 24);
        Controls.Add(placeBox);

        Label timeZoneCaption = new Label();
        timeZoneCaption.Text = "Time zone";
        timeZoneCaption.SetBounds(224, 142, 160, 20);
        Controls.Add(timeZoneCaption);

        timeZoneBox = new ComboBox();
        timeZoneBox.SetBounds(224, 166, 306, 24);
        timeZoneBox.DropDownStyle = ComboBoxStyle.DropDown;
        timeZoneBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        timeZoneBox.AutoCompleteSource = AutoCompleteSource.ListItems;
        foreach (TimeZoneChoice choice in timeZoneChoices)
        {
            timeZoneBox.Items.Add(choice);
        }
        Controls.Add(timeZoneBox);

        Label hint = new Label();
        hint.Text = "Tip: type a city, country, or UTC offset in the time zone box, then choose the closest match.";
        hint.ForeColor = Color.FromArgb(90, 90, 90);
        hint.SetBounds(224, 202, 306, 42);
        Controls.Add(hint);

        Button defaultsButton = new Button();
        defaultsButton.Text = "Restore defaults";
        defaultsButton.SetBounds(224, 266, 120, 30);
        defaultsButton.Click += delegate { RestoreDefaults(); };
        Controls.Add(defaultsButton);

        Button saveButton = new Button();
        saveButton.Text = "Save";
        saveButton.SetBounds(368, 316, 76, 30);
        saveButton.Click += delegate { SaveAndClose(); };
        Controls.Add(saveButton);

        Button cancelButton = new Button();
        cancelButton.Text = "Cancel";
        cancelButton.SetBounds(454, 316, 76, 30);
        cancelButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancelButton);

        ClockConfigs = ClockConfig.CloneList(configs);
        RefreshClockList(0);
    }

    public List<ClockConfig> ClockConfigs { get; private set; }

    private void AddClock()
    {
        CommitSelectedClock();
        TimeZoneInfo local = TimeZoneInfo.Local;
        ClockConfig config = new ClockConfig("New clock", local.DisplayName, local.Id, WorldTimeForm.PaletteColor(configs.Count));
        configs.Add(config);
        RefreshClockList(configs.Count - 1);
    }

    private void RemoveClock()
    {
        if (selectedIndex < 0 || selectedIndex >= configs.Count)
        {
            return;
        }

        configs.RemoveAt(selectedIndex);
        if (configs.Count == 0)
        {
            configs.Add(new ClockConfig("New clock", TimeZoneInfo.Local.DisplayName, TimeZoneInfo.Local.Id, WorldTimeForm.PaletteColor(0)));
        }

        RefreshClockList(Math.Min(selectedIndex, configs.Count - 1));
    }

    private void MoveClock(int direction)
    {
        if (selectedIndex < 0 || selectedIndex >= configs.Count)
        {
            return;
        }

        int newIndex = selectedIndex + direction;
        if (newIndex < 0 || newIndex >= configs.Count)
        {
            return;
        }

        CommitSelectedClock();
        ClockConfig config = configs[selectedIndex];
        configs.RemoveAt(selectedIndex);
        configs.Insert(newIndex, config);
        RefreshClockList(newIndex);
    }

    private void RestoreDefaults()
    {
        configs.Clear();
        foreach (ClockConfig config in WorldTimeForm.DefaultClockConfigs())
        {
            configs.Add(config);
        }

        RefreshClockList(0);
    }

    private void SelectClock(int newIndex)
    {
        if (loadingSelection)
        {
            return;
        }

        CommitSelectedClock();
        selectedIndex = newIndex;
        LoadSelectedClock();
    }

    private void LoadSelectedClock()
    {
        loadingSelection = true;
        try
        {
            if (selectedIndex < 0 || selectedIndex >= configs.Count)
            {
                labelBox.Text = string.Empty;
                placeBox.Text = string.Empty;
                timeZoneBox.Text = string.Empty;
                return;
            }

            ClockConfig config = configs[selectedIndex];
            labelBox.Text = config.Label;
            placeBox.Text = config.Place;
            TimeZoneChoice choice = FindChoiceById(config.TimeZoneId);
            timeZoneBox.SelectedItem = choice;
            if (choice == null)
            {
                timeZoneBox.Text = config.TimeZoneId;
            }
        }
        finally
        {
            loadingSelection = false;
        }
    }

    private void CommitSelectedClock()
    {
        if (selectedIndex < 0 || selectedIndex >= configs.Count)
        {
            return;
        }

        ClockConfig config = configs[selectedIndex];
        config.Label = NormalizeText(labelBox.Text, "Clock");
        config.Place = NormalizeText(placeBox.Text, config.Label);
        TimeZoneChoice choice = ResolveChoice(timeZoneBox.Text);
        if (choice != null)
        {
            config.TimeZoneId = choice.Id;
            timeZoneBox.SelectedItem = choice;
        }
    }

    private void SaveAndClose()
    {
        CommitSelectedClock();
        for (int i = 0; i < configs.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(configs[i].Label))
            {
                configs[i].Label = "Clock";
            }

            if (ResolveChoice(configs[i].TimeZoneId) == null)
            {
                MessageBox.Show("One of the clocks has an invalid time zone. Choose a time zone from the list before saving.", "World Time Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        ClockConfigs = ClockConfig.CloneList(configs);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RefreshClockList(int indexToSelect)
    {
        loadingSelection = true;
        try
        {
            clockList.Items.Clear();
            foreach (ClockConfig config in configs)
            {
                clockList.Items.Add(config);
            }

            if (clockList.Items.Count > 0)
            {
                clockList.SelectedIndex = Math.Max(0, Math.Min(indexToSelect, clockList.Items.Count - 1));
                selectedIndex = clockList.SelectedIndex;
            }
            else
            {
                selectedIndex = -1;
            }
        }
        finally
        {
            loadingSelection = false;
        }

        LoadSelectedClock();
    }

    private TimeZoneChoice ResolveChoice(string text)
    {
        if (timeZoneBox.SelectedItem is TimeZoneChoice)
        {
            TimeZoneChoice selectedChoice = (TimeZoneChoice)timeZoneBox.SelectedItem;
            if (string.Equals(selectedChoice.ToString(), text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(selectedChoice.Id, text, StringComparison.OrdinalIgnoreCase))
            {
                return selectedChoice;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string query = text.Trim();
        foreach (TimeZoneChoice choice in timeZoneChoices)
        {
            if (string.Equals(choice.Id, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(choice.ToString(), query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(choice.DisplayName, query, StringComparison.OrdinalIgnoreCase))
            {
                return choice;
            }
        }

        foreach (TimeZoneChoice choice in timeZoneChoices)
        {
            if (choice.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return choice;
            }
        }

        return null;
    }

    private TimeZoneChoice FindChoiceById(string timeZoneId)
    {
        foreach (TimeZoneChoice choice in timeZoneChoices)
        {
            if (string.Equals(choice.Id, timeZoneId, StringComparison.OrdinalIgnoreCase))
            {
                return choice;
            }
        }

        return null;
    }

    private static string NormalizeText(string text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return text.Trim();
    }
}

internal sealed class ClockItem
{
    public ClockItem(ClockConfig config)
    {
        Label = config.Label;
        Place = config.Place;
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId);
        Accent = Color.FromArgb(config.ColorArgb);
    }

    public string Label { get; private set; }
    public string Place { get; private set; }
    public TimeZoneInfo TimeZone { get; private set; }
    public Color Accent { get; private set; }
    public Panel Card { get; set; }
    public Label CountryLabel { get; set; }
    public Label DateLabel { get; set; }
    public Label TimeLabel { get; set; }
    public Label ZoneLabel { get; set; }
}

internal sealed class ClockConfig
{
    public ClockConfig(string label, string place, string timeZoneId, int colorArgb)
    {
        Label = label;
        Place = place;
        TimeZoneId = timeZoneId;
        ColorArgb = colorArgb;
    }

    public string Label { get; set; }
    public string Place { get; set; }
    public string TimeZoneId { get; set; }
    public int ColorArgb { get; set; }

    public override string ToString()
    {
        return Label + " - " + Place;
    }

    public string ToLine()
    {
        return Encode(Label) + "\t" + Encode(Place) + "\t" + Encode(TimeZoneId) + "\t" + ColorArgb.ToString(CultureInfo.InvariantCulture);
    }

    public static ClockConfig FromLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        string[] parts = line.Split('\t');
        if (parts.Length < 3)
        {
            return null;
        }

        int color = WorldTimeForm.PaletteColor(0);
        if (parts.Length >= 4)
        {
            int parsedColor;
            if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedColor))
            {
                color = parsedColor;
            }
        }

        return new ClockConfig(Decode(parts[0]), Decode(parts[1]), Decode(parts[2]), color);
    }

    public static List<ClockConfig> CloneList(List<ClockConfig> configs)
    {
        List<ClockConfig> clone = new List<ClockConfig>();
        foreach (ClockConfig config in configs)
        {
            clone.Add(new ClockConfig(config.Label, config.Place, config.TimeZoneId, config.ColorArgb));
        }

        return clone;
    }

    private static string Encode(string value)
    {
        if (value == null)
        {
            value = string.Empty;
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string Decode(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            return string.Empty;
        }
    }
}

internal sealed class TimeZoneChoice
{
    private const string PacificTimeZoneId = "Pacific Standard Time";

    public TimeZoneChoice(TimeZoneInfo timeZone)
    {
        Id = timeZone.Id;
        DisplayName = timeZone.DisplayName;
    }

    public string Id { get; private set; }
    public string DisplayName { get; private set; }

    public override string ToString()
    {
        return DisplayName + " | " + Id + AliasSuffix;
    }

    private string AliasSuffix
    {
        get
        {
            if (string.Equals(Id, PacificTimeZoneId, StringComparison.OrdinalIgnoreCase))
            {
                return " | PST / PDT";
            }

            return string.Empty;
        }
    }

    public static List<TimeZoneChoice> GetChoices()
    {
        List<TimeZoneChoice> choices = new List<TimeZoneChoice>();
        foreach (TimeZoneInfo timeZone in TimeZoneInfo.GetSystemTimeZones())
        {
            choices.Add(new TimeZoneChoice(timeZone));
        }

        choices.Sort(delegate(TimeZoneChoice left, TimeZoneChoice right)
        {
            return string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        });

        return choices;
    }
}

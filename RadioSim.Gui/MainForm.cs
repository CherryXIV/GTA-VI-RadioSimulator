using System.Globalization;
using System.Runtime.InteropServices;
using RadioSim.Game;

namespace RadioSim;

public sealed class MainForm : Form
{
    ScheduleFile? sched;
    readonly Settings settings = Settings.Load();
    GameAudio? audio;
    RadioEngine? engine;
    string? playingStation;
    int playingSeed;

    // ---- controls
    readonly ListView stations = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill };
    readonly ListView podcasts = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill, ShowGroups = true, ShowItemToolTips = true };
    readonly TabControl leftTabs = new() { Dock = DockStyle.Fill };
    readonly TabPage stationsTab = new("Stations"), podcastsTab = new("Podcasts");
    readonly TextBox seedBox = new() { Width = 100 };
    readonly CheckBox shuffle = new() { Text = "Shuffle", Checked = true, AutoSize = true };
    readonly CheckBox rotation = new() { Text = "Play every song before repeating", AutoSize = true };
    readonly NumericUpDown hour = new() { Minimum = 0, Maximum = 23, Width = 50 };
    readonly ComboBox weather = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    readonly ComboBox djTalk = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    readonly TrackBar volume = new() { Minimum = 0, Maximum = 100, Value = 80, TickStyle = TickStyle.None, Width = 120, AutoSize = false, Height = 26 };
    readonly NumericUpDown renderMinutes = new() { Minimum = 1, Maximum = 600, Value = 30, Width = 55 };
    readonly Button play = new() { Text = "▶  Play", Width = 90, Height = 32 };
    readonly Button pause = new() { Text = "❚❚  Pause", Width = 90, Height = 32, Enabled = false };
    readonly Button skip = new() { Text = "⏭  Skip", Width = 90, Height = 32, Enabled = false };
    readonly Button stopBtn = new() { Text = "■  Stop", Width = 90, Height = 32, Enabled = false };
    readonly Button render = new() { Text = "Render WAV…", Width = 110, Height = 32 };
    readonly Label station = new() { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = Color.DimGray };
    readonly Label title = new() { AutoSize = true, Font = new Font("Segoe UI", 18f, FontStyle.Bold) };
    readonly Label kind = new() { AutoSize = true, Font = new Font("Segoe UI", 10f), ForeColor = Color.DimGray };
    readonly Label voices = new() { AutoSize = true, Font = new Font("Segoe UI", 10f), ForeColor = Color.SteelBlue };
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Height = 14 };
    readonly Label time = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f), Anchor = AnchorStyles.Right };
    readonly ListView upNext = MakeList(("Starts", 70), ("", 90), ("Item", 380), ("DJ", 300));
    readonly ListView history = MakeList(("Played", 70), ("", 90), ("Item", 380), ("DJ", 300));
    readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly System.Windows.Forms.Timer tick = new() { Interval = 50 };
    SplitContainer split = null!;

    // station logos (from the game's UI textures), the station's song list, the tray icon
    readonly ImageList logoList = new() { ColorDepth = ColorDepth.Depth32Bit };
    readonly Dictionary<string, Image> logos = new();
    readonly PictureBox logo = new() { SizeMode = PictureBoxSizeMode.Zoom, Width = 96, Height = 96, Margin = new Padding(0, 0, 12, 0) };
    readonly ListView songs = MakeList(("Artist", 200), ("Title", 260), ("Length", 60), ("DJ lines", 70));
    readonly TextBox songFilter = new() { PlaceholderText = "Filter songs…", Dock = DockStyle.Top };
    readonly TabPage songsTab = new("Songs on this station");
    readonly NotifyIcon tray = new() { Text = "GTA VI Radio Simulator" };
    readonly ToolStripMenuItem mediaKeysItem = new("Use the keyboard's &media keys") { CheckOnClick = true };
    readonly ToolStripMenuItem trayItem = new("Minimize to the notification &area") { CheckOnClick = true };
    readonly ToolStripMenuItem notifyItem = new("Show a &notification for each song") { CheckOnClick = true };
    readonly ToolStripMenuItem nowFileItem = new("Write \"now playing\" &files (for OBS etc.)") { CheckOnClick = true };

    static ListView MakeList(params (string, int)[] cols)
    {
        var l = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        foreach (var (t, w) in cols) l.Columns.Add(t, w);
        return l;
    }

    public MainForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);    // sizes below are at 100%; scaled to the display
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "GTA VI Radio Simulator";
        // all sizes of the program icon, so the title bar and taskbar each get a sharp one
        using (var ico = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
            if (ico != null) Icon = new Icon(ico);
        Width = 1180; Height = 760;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        weather.Items.AddRange(new object[] { "good", "bad" }); weather.SelectedIndex = 0;
        djTalk.Items.AddRange(new object[] { "Station default", "Never", "Sometimes (25%)", "Half the time", "Often (75%)", "Always" });
        djTalk.SelectedIndex = Math.Clamp(settings.DjTalk, 0, djTalk.Items.Count - 1);
        weather.SelectedItem = settings.Weather == "bad" ? "bad" : "good";
        rotation.Checked = settings.Rotation;
        volume.Value = Math.Clamp(settings.Volume, volume.Minimum, volume.Maximum);
        hour.Value = DateTime.Now.Hour;
        stations.Columns.Add("Station", 175); stations.Columns.Add("Songs", 55); stations.Columns.Add("Ads", 45);
        podcasts.Columns.Add("Episode", 220); podcasts.Columns.Add("Length", 55);

        // ---- layout
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 4, 0, 0) };
        void Add(Control c, string? label = null)
        {
            // each label stays on the same line as its control when the row wraps
            var pair = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            if (label != null) pair.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(10, 7, 2, 0) });
            c.Margin = new Padding(2, 3, 4, 0);
            pair.Controls.Add(c);
            options.Controls.Add(pair);
        }
        Add(seedBox, "Seed"); Add(shuffle); Add(hour, "Hour"); Add(weather, "Weather"); Add(djTalk, "DJ talk");
        Add(rotation);
        Add(volume, "Volume"); Add(renderMinutes, "Render minutes");

        var transport = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
        transport.Controls.AddRange(new Control[] { play, pause, skip, stopBtn, render });

        var now = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        now.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); now.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        now.Controls.Add(station, 0, 0); now.SetColumnSpan(station, 2);
        now.Controls.Add(title, 0, 1); now.SetColumnSpan(title, 2);
        now.Controls.Add(kind, 0, 2); now.SetColumnSpan(kind, 2);
        now.Controls.Add(voices, 0, 3); now.SetColumnSpan(voices, 2);
        now.Controls.Add(progress, 0, 4); now.Controls.Add(time, 1, 4);
        var nowRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 4, 0, 0) };
        nowRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); nowRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nowRow.Controls.Add(logo, 0, 0); nowRow.Controls.Add(now, 1, 0);
        var nowBox = new GroupBox { Text = "Now playing", Dock = DockStyle.Fill, AutoSize = true };
        nowBox.Controls.Add(nowRow);

        var lists = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 200 };
        var upBox = new GroupBox { Text = "Up next", Dock = DockStyle.Fill }; upBox.Controls.Add(upNext);
        var tabs = new TabControl { Dock = DockStyle.Fill, ShowToolTips = true };
        songsTab.ToolTipText = NightTip;
        songs.ShowItemToolTips = true;
        var hiTab = new TabPage("History"); hiTab.Controls.Add(history);
        songsTab.Controls.Add(songs); songsTab.Controls.Add(songFilter);
        tabs.TabPages.Add(hiTab); tabs.TabPages.Add(songsTab);
        lists.Panel1.Controls.Add(upBox); lists.Panel2.Controls.Add(tabs);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(6, 0, 6, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(options, 0, 0); right.Controls.Add(transport, 0, 1);
        right.Controls.Add(nowBox, 0, 2); right.Controls.Add(lists, 0, 3);

        split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        stationsTab.Controls.Add(stations); podcastsTab.Controls.Add(podcasts);
        leftTabs.TabPages.Add(stationsTab); leftTabs.TabPages.Add(podcastsTab);
        split.Panel1.Controls.Add(leftTabs); split.Panel2.Controls.Add(right);

        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&Choose game folder…", null, (_, _) => LoadGame(ask: true));
        file.DropDownItems.Add("&Read game files again", null, (_, _) => LoadGame(ask: false, rescan: true));
        file.DropDownItems.Add("Export station &logos…", null, (_, _) => ExportLogos());
        file.DropDownItems.Add("Open the \"now playing\" &folder", null, (_, _) => OpenNowPlayingFolder());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());
        menu.Items.Add(file);
        var radio = new ToolStripMenuItem("&Radio");
        radio.DropDownItems.Add(new ToolStripMenuItem("&Play / pause", null, (_, _) => PlayPause()) { ShortcutKeyDisplayString = "Space" });
        radio.DropDownItems.Add(new ToolStripMenuItem("&Skip", null, (_, _) => engine?.Skip()) { ShortcutKeyDisplayString = "Ctrl+Right" });
        radio.DropDownItems.Add(new ToolStripMenuItem("&Next station", null, (_, _) => Tune(+1)) { ShortcutKeyDisplayString = "Page Down" });
        radio.DropDownItems.Add(new ToolStripMenuItem("P&revious station", null, (_, _) => Tune(-1)) { ShortcutKeyDisplayString = "Page Up" });
        radio.DropDownItems.Add(new ToolStripMenuItem("S&top", null, (_, _) => StopPlayback()));
        menu.Items.Add(radio);
        var opts = new ToolStripMenuItem("&Options");
        opts.DropDownItems.AddRange(new ToolStripItem[] { mediaKeysItem, trayItem, notifyItem, nowFileItem });
        menu.Items.Add(opts);
        mediaKeysItem.Checked = settings.MediaKeys; trayItem.Checked = settings.MinimizeToTray;
        notifyItem.Checked = settings.SongNotifications; nowFileItem.Checked = settings.NowPlayingFile;

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Play / pause", null, (_, _) => PlayPause());
        trayMenu.Items.Add("Skip", null, (_, _) => engine?.Skip());
        trayMenu.Items.Add("Next station", null, (_, _) => Tune(+1));
        trayMenu.Items.Add("Previous station", null, (_, _) => Tune(-1));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Show window", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("Exit", null, (_, _) => Close());
        tray.ContextMenuStrip = trayMenu;
        tray.Icon = Icon;
        tray.Visible = true;
        var bar = new StatusStrip(); bar.Items.Add(status);

        Controls.Add(split); Controls.Add(menu); Controls.Add(bar);
        MainMenuStrip = menu;
        foreach (var b in new[] { play, pause, skip, stopBtn, render }) { b.AutoSize = true; b.MinimumSize = new Size(b.Width, b.Height); }
        ResumeLayout(false); PerformLayout();

        // ---- events
        play.Click += (_, _) => Play();
        stations.DoubleClick += (_, _) => Play();
        podcasts.DoubleClick += (_, _) => Play();
        pause.Click += (_, _) => { engine?.TogglePause(); pause.Text = engine?.Paused == true ? "▶  Resume" : "❚❚  Pause"; };
        skip.Click += (_, _) => engine?.Skip();
        stopBtn.Click += (_, _) => StopPlayback();
        render.Click += async (_, _) => await RenderWav();
        volume.ValueChanged += (_, _) => { engine?.SetVolume(volume.Value / 100.0); if (!selfTest) settings.Volume = volume.Value; };
        djTalk.SelectedIndexChanged += (_, _) => settings.DjTalk = djTalk.SelectedIndex;
        weather.SelectedIndexChanged += (_, _) => settings.Weather = (string)weather.SelectedItem!;
        rotation.CheckedChanged += (_, _) => settings.Rotation = rotation.Checked;
        mediaKeysItem.CheckedChanged += (_, _) => { settings.MediaKeys = mediaKeysItem.Checked; settings.Save(); UpdateMediaKeys(); };
        trayItem.CheckedChanged += (_, _) => { settings.MinimizeToTray = trayItem.Checked; settings.Save(); };
        notifyItem.CheckedChanged += (_, _) => { settings.SongNotifications = notifyItem.Checked; settings.Save(); };
        nowFileItem.CheckedChanged += (_, _) => { settings.NowPlayingFile = nowFileItem.Checked; settings.Save(); WriteNowPlaying(); };
        stations.SelectedIndexChanged += (_, _) => ShowSongs();
        songFilter.TextChanged += (_, _) => ShowSongs();
        tray.DoubleClick += (_, _) => RestoreFromTray();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && settings.MinimizeToTray) Hide(); };
        KeyPreview = true;
        KeyDown += OnKey;
        tick.Tick += (_, _) => UpdateProgress();
        Load += (_, _) =>
        {
            float k = DeviceDpi / 96f;                  // list columns and splitter are not auto-scaled
            int[] w = { 180, 52, 44 };
            for (int i = 0; i < w.Length; i++) stations.Columns[i].Width = (int)(w[i] * k);
            podcasts.Columns[0].Width = (int)(220 * k); podcasts.Columns[1].Width = (int)(52 * k);
            foreach (var l in new[] { upNext, history })
                for (int i = 0; i < l.Columns.Count; i++) l.Columns[i].Width = (int)(new[] { 70, 90, 380, 300 }[i] * k);
            for (int i = 0; i < songs.Columns.Count; i++) songs.Columns[i].Width = (int)(new[] { 200, 260, 60, 70 }[i] * k);
            split.SplitterDistance = (int)(310 * k);
            logo.Width = logo.Height = (int)(96 * k);
            logoList.ImageSize = new Size((int)(32 * k), (int)(32 * k));
            stations.SmallImageList = logoList;
            foreach (var lab in new[] { station, title, kind, voices }) lab.UseMnemonic = false;   // keep '&' in names
            LoadGame(ask: false);
        };
        FormClosing += (_, _) =>
        {
            if (!selfTest) settings.Save();
            StopPlayback(); audio?.Dispose();
            tray.Visible = false; tray.Dispose();
            UnregisterMediaKeys();
            WriteNowPlaying(clear: true);
        };
    }

    // ---- schedule / station list

    /// <summary>Reads the stations from the game folder (cached after the first time; read again
    /// by itself when the game files change, e.g. after an update).</summary>
    async void LoadGame(bool ask, bool rescan = false)
    {
        var folder = settings.GameFolder;
        if (ask || folder == null || GameIndex.FindGameFolder(folder) == null)
        {
            if (selfTest) { Close(); return; }                 // unattended: no one to answer a dialog
            using var dlg = new FolderBrowserDialog
            {
                Description = "Choose your GTA VI game folder (the one with audio.rpf and data.rpf)",
                UseDescriptionForTitle = true, SelectedPath = folder ?? "",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                if (sched == null) status.Text = "No game folder yet: File → Choose game folder…";
                return;
            }
            folder = dlg.SelectedPath;
        }
        var game = GameIndex.FindGameFolder(folder);
        if (game == null)
        {
            MessageBox.Show(this, $"audio.rpf and data.rpf are not in\n{folder}\n\nChoose the folder the game is installed in.",
                            "Game files not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        StopPlayback();
        settings.GameFolder = game; settings.Save();
        play.Enabled = render.Enabled = false;
        stations.Items.Clear(); podcasts.Items.Clear(); podcasts.Groups.Clear();
        status.Text = "Reading the game files…";
        try
        {
            sched = await Task.Run(() => GameIndex.Load(game, m => BeginInvoke(() => status.Text = "Reading the game files: " + m.Trim()), rescan));
        }
        catch (Exception e)
        {
            sched = null;
            status.Text = "Could not read the game files.";
            MessageBox.Show(this, e.Message, "Could not read the game files", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { play.Enabled = render.Enabled = true; }
        audio?.Dispose();
        audio = new GameAudio(game);
        await LoadLogos();
        foreach (var (name, s) in sched.Stations.OrderBy(k => k.Key))
        {
            var it = new ListViewItem(name) { Tag = name, ImageKey = name };
            it.SubItems.Add(s.SongRows().Count.ToString());
            it.SubItems.Add((s.Categories.GetValueOrDefault("advert")?.Count ?? 0).ToString());
            stations.Items.Add(it);
        }
        foreach (var (name, eps) in sched.Podcasts.OrderBy(k => k.Key))
        {
            var g = new ListViewGroup(name, $"{name}  ({eps.Count} episodes, {Clock(eps.Sum(e => e.Duration))})");
            podcasts.Groups.Add(g);
            for (int i = 0; i < eps.Count; i++)
            {
                var it = new ListViewItem(eps[i].Title, g) { Tag = (name, i), ToolTipText = $"{name}: {eps[i].Title}" };
                it.SubItems.Add(Clock(eps[i].Duration));
                podcasts.Items.Add(it);
            }
        }
        var last = stations.Items.Cast<ListViewItem>().FirstOrDefault(i => (string)i.Tag! == settings.LastStation);
        if (last != null) { last.Selected = true; last.EnsureVisible(); }
        else if (stations.Items.Count > 0) stations.Items[0].Selected = true;
        status.Text = $"{sched.Stations.Count} stations from {game}" + (logos.Count > 0 ? $", {logos.Count} logos" : "");
    }

    string? SelectedStation => stations.SelectedItems.Count > 0 ? (string)stations.SelectedItems[0].Tag! : null;

    bool PodcastsShown => leftTabs.SelectedTab == podcastsTab;

    /// <summary>The selected episode (podcast, index), or the first one in the list; null when
    /// there are no podcasts.</summary>
    (string name, int index)? SelectedEpisode =>
        podcasts.SelectedItems.Count > 0 ? ((string, int))podcasts.SelectedItems[0].Tag!
        : podcasts.Items.Count > 0 ? ((string, int))podcasts.Items[0].Tag! : null;

    Options CurrentOptions()
    {
        var o = new Options { StartHour = (double)hour.Value, Weather = (string)weather.SelectedItem!, FullRotation = rotation.Checked };
        double? c = djTalk.SelectedIndex switch { 1 => 0, 2 => 0.25, 3 => 0.5, 4 => 0.75, 5 => 1.0, _ => null };
        o.DjIntroChance = c; o.DjOutroChance = c;
        return o;
    }

    int ChooseSeed()
    {
        if (shuffle.Checked || !int.TryParse(seedBox.Text, out var s)) s = Random.Shared.Next();
        seedBox.Text = s.ToString(CultureInfo.InvariantCulture);
        return s;
    }

    // ---- playback

    void Play()
    {
        if (PodcastsShown) { PlayPodcast(); return; }
        if (sched == null || audio == null || SelectedStation is not { } name) return;
        StopPlayback();
        playingStation = name;
        if (!selfTest) { settings.LastStation = name; settings.Save(); }
        logo.Image = logos.GetValueOrDefault(name);
        playingSeed = ChooseSeed();
        StartEngine(new RadioEngine(sched.Stations[name], playingSeed, CurrentOptions(), audio),
                    $"{name}   ·   seed {playingSeed}", $"Playing {name} (seed {playingSeed}). Same seed + settings = same running order.");
    }

    /// <summary>Plays the selected podcast from the selected episode to its last one.</summary>
    void PlayPodcast()
    {
        if (sched == null || audio == null || SelectedEpisode is not var (name, index)) return;
        StopPlayback();
        playingStation = name;
        logo.Image = null;
        var eps = sched.Podcasts[name];
        StartEngine(new RadioEngine(Segment.Episodes(name, eps, index), audio),
                    $"{name}   ·   podcast", $"Playing {name} from episode {index + 1} of {eps.Count} to the last one.");
    }

    void StartEngine(RadioEngine eng, string header, string playingStatus)
    {
        history.Items.Clear(); upNext.Items.Clear();
        engine = eng;
        engine.SetVolume(volume.Value / 100.0);
        engine.Error += m => BeginInvoke(() => { status.Text = "Playback error: " + m; StopPlayback(); });
        ShowQueue(engine.Upcoming);
        station.Text = header;
        title.Text = "Tuning in…"; kind.Text = ""; voices.Text = "";
        try { engine.Start(); }
        catch (InvalidOperationException e)                // no sound device: say so instead of crashing
        {
            StopPlayback();
            upNext.Items.Clear();
            title.Text = "No sound"; status.Text = e.Message;
            return;
        }
        tick.Start();
        pause.Enabled = skip.Enabled = stopBtn.Enabled = true; pause.Text = "❚❚  Pause";
        status.Text = playingStatus;
        UpdateMediaKeys();
    }

    void StopPlayback()
    {
        tick.Stop();
        engine?.Dispose(); engine = null;
        pause.Enabled = skip.Enabled = stopBtn.Enabled = false;
        progress.Value = 0; time.Text = "";
        if (playingStation != null) { title.Text = "Stopped"; kind.Text = ""; voices.Text = ""; }
        if (shown != null) AddRow(history, shown, insertTop: true);
        shown = shownPiece = null;
        tray.Text = "GTA VI Radio Simulator";
        WriteNowPlaying();
        UpdateMediaKeys();
    }

    /// <summary>The item playing (its first piece, for History) and the piece of it being heard
    /// (a podcast episode plays in pieces).</summary>
    Planned? shown, shownPiece;

    void OnSegment(Planned p)
    {
        if (shown != null) AddRow(history, shown, insertTop: true);
        shown = shownPiece = p;
        var s = p.Segment;
        title.Text = Pretty(s);
        kind.Text = $"{KindName(s.Category)}   ·   {Clock(s.Whole > 0 ? s.Whole : s.Duration)}" + (s.Part.Length > 0 ? $"   ·   {s.Part}" : "");
        voices.Text = s.Voices.Count == 0 ? "" : string.Join("     ", s.Voices.Select(v => $"🎙 {v.Label} at {Clock(v.At)}"));
        var tip = $"{playingStation}\n{Pretty(s)}";
        tray.Text = tip.Length > 127 ? tip[..127] : tip;
        if (settings.SongNotifications && s.Category is "music" or "music_night" or "podcast" && !selfTest)
            tray.ShowBalloonTip(4000, playingStation ?? "", Pretty(s), ToolTipIcon.None);
        WriteNowPlaying();
    }

    void ShowQueue(IReadOnlyList<Planned> q)
    {
        upNext.BeginUpdate();
        upNext.Items.Clear();
        var prev = shownPiece?.Segment;
        foreach (var p in q)
        {
            if (!p.Segment.Continues(prev)) AddRow(upNext, p, insertTop: false);   // one row per episode
            prev = p.Segment;
        }
        upNext.EndUpdate();
    }

    static void AddRow(ListView l, Planned p, bool insertTop)
    {
        var s = p.Segment;
        var it = new ListViewItem(Clock(p.StartsAt));
        it.SubItems.Add(KindName(s.Category));
        it.SubItems.Add(Pretty(s));
        it.SubItems.Add(string.Join(", ", s.Voices.Select(v => v.Label.Replace("DJ ", ""))));
        if (s.Category is not ("music" or "music_night" or "podcast")) it.ForeColor = Color.DimGray;
        if (insertTop) l.Items.Insert(0, it); else l.Items.Add(it);
    }

    /// <summary>Runs every 50 ms: follows what the sound device is actually playing.</summary>
    void UpdateProgress()
    {
        if (engine is { Finished: true })                   // a podcast's last episode is over
        {
            var done = playingStation;
            StopPlayback();
            title.Text = "Finished"; status.Text = $"Played {done} to the end.";
            return;
        }
        if (engine?.Now is not var (p, heard)) return;
        if (!ReferenceEquals(p, shownPiece))
        {
            if (p.Segment.Continues(shownPiece?.Segment)) shownPiece = p;   // the episode's next piece
            else OnSegment(p);
            ShowQueue(engine.Upcoming);
        }
        // a piece of a longer item counts from the item's start
        var sg = p.Segment;
        double pos = Math.Min(heard, sg.Duration);
        double whole = sg.Whole > 0 ? sg.Whole : sg.Duration, at = sg.Whole > 0 ? sg.Start + pos : pos;
        progress.Value = (int)Math.Clamp(at / Math.Max(1, whole) * 1000, 0, 1000);
        progress.Maximum = 1000;
        time.Text = $"{Clock(at)} / {Clock(whole)}";
        // highlight the DJ line while it is talking
        var talking = p.Segment.Voices.Where((v, i) => pos >= v.At && pos < v.At + (engine.VoiceSeconds(p.Segment, i) ?? 12)).Any();
        voices.ForeColor = talking ? Color.OrangeRed : Color.SteelBlue;
    }

    // ---- render

    async Task RenderWav()
    {
        if (sched == null || audio == null) return;
        bool pod = PodcastsShown;
        string name;
        int epFrom = 0;
        if (pod) { if (SelectedEpisode is not var (pn, pi)) return; (name, epFrom) = (pn, pi); }
        else if (SelectedStation is { } sn) name = sn;
        else return;
        using var dlg = new SaveFileDialog { Filter = "WAV|*.wav", FileName = $"{name} {DateTime.Now:yyyy-MM-dd HHmm}.wav" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int seed = ChooseSeed();
        var opt = CurrentOptions();
        double minutes = (double)renderMinutes.Value;
        var order = pod ? Segment.Episodes(name, sched.Podcasts[name], epFrom) : new Scheduler(sched.Stations[name], seed, opt).Run();
        var ga = audio; var path = dlg.FileName;
        render.Enabled = false;
        try
        {
            await Task.Run(() =>
            {
                using var wav = new WavWriter(path);
                var tracklist = new Tracklist();
                double clock = 0;
                foreach (var seg in order)
                {
                    var pcm = ga.Render(seg);
                    tracklist.Add(seg, clock);
                    wav.Write(pcm);
                    clock += pcm.Length / 2.0 / GameAudio.Rate;
                    BeginInvoke(() => status.Text = $"Rendering {name}: {Clock(clock)} / {Clock(minutes * 60)}…");
                    if (clock >= minutes * 60) break;
                }
                tracklist.Write(Path.ChangeExtension(path, ".txt"), name, seed, clock);
            });
            status.Text = $"Wrote {path} and its timestamps ({Path.GetFileName(Path.ChangeExtension(path, ".txt"))})" + (pod ? "" : $", seed {seed}");
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Render failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { render.Enabled = true; }
    }

    // ---- self-test (muted playback + screenshot)

    bool selfTest;

    public void SelfTest(string png, string stationName)
    {
        selfTest = true;
        Shown += async (_, _) =>
        {
            for (int i = 0; i < 600 && stations.Items.Count == 0; i++) await Task.Delay(100);   // game files still loading
            foreach (ListViewItem it in stations.Items) it.Selected = (string)it.Tag! == stationName;
            if (sched?.Podcasts.ContainsKey(stationName) == true)      // a podcast: its first episode
            {
                leftTabs.SelectedTab = podcastsTab;
                foreach (ListViewItem it in podcasts.Items) it.Selected = ((string, int))it.Tag! == (stationName, 0);
            }
            volume.Value = 0;
            Play();
            await Task.Delay(9000);
            UpdateProgress();
            var timing = engine?.Now is var (_, heardAt) ? $"heard={heardAt:0.000}s buffered={engine.Buffered:0.000}s" : "nothing heard";
            using (var bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(png);
            }
            engine?.Skip();
            await Task.Delay(4000);
            status.Text += $"   [after skip: {title.Text}]";
            File.WriteAllText(Path.ChangeExtension(png, ".txt"),
                $"{timing}\nlogos={logos.Count} songs={songs.Items.Count} logo={(logo.Image != null ? "shown" : "none")}\nstation={station.Text}\ntitle={title.Text}\nkind={kind.Text}\nvoices={voices.Text}\nupnext={upNext.Items.Count}\nhistory={history.Items.Count}\nstatus={status.Text}\n");
            Close();
        };
    }

    // ---- station logos

    /// <summary>Logos read from the game's UI texture pool (cached as PNGs after the first time).</summary>
    async Task LoadLogos()
    {
        if (sched == null) return;
        var sc = sched;
        Dictionary<string, string> files;
        try { files = await Task.Run(() => StationLogos.Load(sc, m => BeginInvoke(() => status.Text = m))); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { files = new(); }
        logoList.Images.Clear();
        logo.Image = null;
        foreach (var img in logos.Values) img.Dispose();
        logos.Clear();
        foreach (var (name, path) in files)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var src = Image.FromStream(fs);
                var img = new Bitmap(src);                     // a copy, so the file isn't kept open
                logos[name] = img;
                logoList.Images.Add(name, img);
            }
            catch (Exception e) when (e is IOException or ArgumentException) { }
        }
    }

    void ExportLogos()
    {
        if (sched == null) return;
        using var dlg = new FolderBrowserDialog { Description = "Save the station logos (PNG) to this folder", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var (n, miss) = StationLogos.Export(sched, dlg.SelectedPath);
            status.Text = $"Wrote {n} station logos to {dlg.SelectedPath}" + (miss.Count > 0 ? $" (no logo: {string.Join(", ", miss)})" : "");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, e.Message, "Could not export the logos", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- songs on the selected station

    /// <summary>What the blue rows in "Songs on this station" are.</summary>
    const string NightTip = "Blue: the station's night playlist. The station's own scheduling decides when it moves "
                            + "between its day and night songs; the DJ may link the two (\"to night\" / \"to day\").";

    void ShowSongs()
    {
        songs.BeginUpdate();
        songs.Items.Clear();
        int total = 0;
        if (sched != null && SelectedStation is { } n && sched.Stations.TryGetValue(n, out var st))
        {
            var rows = st.SongRows();
            total = rows.Count;
            var f = songFilter.Text.Trim();
            foreach (var r in rows.OrderBy(x => x.Artist, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                var x = r.Song;
                if (f.Length > 0 && !$"{r.Artist} - {r.Title}".Contains(f, StringComparison.CurrentCultureIgnoreCase)) continue;
                var it = new ListViewItem(r.Artist.Length > 0 ? r.Artist : "—");
                it.SubItems.Add(r.Title.Length > 0 ? r.Title : r.InMix ? "—" : $"0x{x.Stream}");
                it.SubItems.Add(Clock(r.Duration));
                it.SubItems.Add(r.InMix ? "in DJ mix" : (x.Intros.Count + x.Outros.Count).ToString());
                if (x.Category == "music_night") { it.ForeColor = Color.SlateBlue; it.ToolTipText = NightTip; }
                else if (r.InMix) it.ToolTipText = $"Plays as part of the DJ mix \"{x.Name}\"";
                songs.Items.Add(it);
            }
        }
        songs.EndUpdate();
        songsTab.Text = songs.Items.Count == total ? $"Songs on this station ({total})" : $"Songs on this station ({songs.Items.Count} of {total})";
    }

    // ---- keyboard, media keys, tray

    void PlayPause()
    {
        if (engine == null) { Play(); return; }
        engine.TogglePause();
        pause.Text = engine.Paused ? "▶  Resume" : "❚❚  Pause";
    }

    /// <summary>Switch to the next (+1) or previous (-1) station in the list and play it, like
    /// flicking through the radio in the game.</summary>
    void Tune(int dir)
    {
        int n = stations.Items.Count;
        if (n == 0) return;
        int cur = playingStation is { } ps && engine != null
            ? stations.Items.Cast<ListViewItem>().ToList().FindIndex(i => (string)i.Tag! == ps)
            : stations.SelectedIndices.Count > 0 ? stations.SelectedIndices[0] : -1;
        int next = cur < 0 ? (dir > 0 ? 0 : n - 1) : ((cur + dir) % n + n) % n;
        foreach (ListViewItem it in stations.Items) it.Selected = it.Index == next;
        stations.Items[next].EnsureVisible();
        Play();
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (ActiveControl is TextBoxBase or NumericUpDown or ComboBox) return;
        switch (e.KeyCode)
        {
            case Keys.Space: PlayPause(); break;
            case Keys.Right when e.Control: engine?.Skip(); break;
            case Keys.PageDown: Tune(+1); break;
            case Keys.PageUp: Tune(-1); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    void RestoreFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    const int WmHotkey = 0x0312;
    static readonly (int id, uint vk)[] MediaKeys = { (1, 0xB3), (2, 0xB0), (3, 0xB2) };   // play/pause, next track, stop
    bool mediaRegistered;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>While a station is on (playing or paused), the media keys control it, even with
    /// the window in the background; when stopped they go back to other programs. A key another
    /// program already holds stays with it.</summary>
    void UpdateMediaKeys()
    {
        UnregisterMediaKeys();
        if (!settings.MediaKeys || !IsHandleCreated || engine == null) return;
        int ok = MediaKeys.Count(k => RegisterHotKey(Handle, k.id, 0x4000 /* MOD_NOREPEAT */, k.vk));
        mediaRegistered = true;
        if (ok < MediaKeys.Length) status.Text = "Some media keys are in use by another program; they stay with it.";
    }

    void UnregisterMediaKeys()
    {
        if (!mediaRegistered || !IsHandleCreated) return;
        foreach (var (id, _) in MediaKeys) UnregisterHotKey(Handle, id);
        mediaRegistered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
            switch ((int)m.WParam)
            {
                case 1: PlayPause(); return;
                case 2: engine?.Skip(); return;
                case 3: StopPlayback(); return;
            }
        base.WndProc(ref m);
    }

    // ---- "now playing" files for stream overlays

    static string NowPlayingDir => Path.Combine(Settings.DataDir, "Now playing");

    /// <summary>With the option on, keeps Data\Now playing\ up to date: song.txt (what is playing),
    /// station.txt and station.png (its logo), for a text or image source in OBS and the like.</summary>
    void WriteNowPlaying(bool clear = false)
    {
        if (!settings.NowPlayingFile) return;
        try
        {
            Directory.CreateDirectory(NowPlayingDir);
            bool on = !clear && engine != null && playingStation != null;
            File.WriteAllText(Path.Combine(NowPlayingDir, "song.txt"), on && shown != null ? Pretty(shown.Segment) : "");
            File.WriteAllText(Path.Combine(NowPlayingDir, "station.txt"), on ? playingStation : "");
            var png = Path.Combine(NowPlayingDir, "station.png");
            if (on && logos.TryGetValue(playingStation!, out var img)) img.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            else if (File.Exists(png)) File.Delete(png);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ExternalException) { }
    }

    void OpenNowPlayingFolder()
    {
        Directory.CreateDirectory(NowPlayingDir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(NowPlayingDir) { UseShellExecute = true });
    }

    // ---- formatting

    static string Clock(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    static string Pretty(Segment s) => s.Category is "music" or "music_night" or "podcast" ? s.Label
        : s.Label.StartsWith("0x") ? $"{KindName(s.Category)} {s.Label}" : $"{KindName(s.Category)} · {s.Label}";

    static string KindName(string cat) => cat switch
    {
        "music" => "Song",
        "music_night" => "Song (night)",
        "podcast" => "Podcast",
        "advert" => "Advert",
        "ident" => "Ident",
        "ident_night" => "Ident (night)",
        "identfromads" => "Ident",
        "identfromnews" => "Ident",
        "ident_to_ads" => "Ident",
        "identfromtalk" => "Ident",
        "dj_banter" => "DJ banter",
        "dj_banter_night" => "DJ banter (night)",
        "dj_banter_story" => "DJ story",
        "dj_banter_welcome" => "DJ welcome",
        "talk" => "DJ talk",
        "talk_night" => "DJ talk",
        "music_bed" => "Music bed",
        "showintro" or "showintro_night" => "Show intro",
        "showoutro" or "showoutro_night" => "Show outro",
        _ when cat.StartsWith("category_") => "Special",
        _ => cat,
    };
}

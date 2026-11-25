using System.Globalization;
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
        djTalk.SelectedIndex = 0;
        hour.Value = DateTime.Now.Hour;
        stations.Columns.Add("Station", 175); stations.Columns.Add("Songs", 55); stations.Columns.Add("Ads", 45);

        // ---- layout
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 4, 0, 0) };
        void Add(Control c, string? label = null)
        {
            // each label stays on the same line as its control when the row wraps
            var pair = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            if (label != null) pair.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(10, 7, 2, 0) });
            c.Margin = new Padding(2, 3, 4, 0);
            pair.Controls.Add(c);
            settings.Controls.Add(pair);
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
        var nowBox = new GroupBox { Text = "Now playing", Dock = DockStyle.Fill, AutoSize = true };
        nowBox.Controls.Add(now);

        var lists = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 200 };
        var upBox = new GroupBox { Text = "Up next", Dock = DockStyle.Fill }; upBox.Controls.Add(upNext);
        var hiBox = new GroupBox { Text = "History", Dock = DockStyle.Fill }; hiBox.Controls.Add(history);
        lists.Panel1.Controls.Add(upBox); lists.Panel2.Controls.Add(hiBox);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(6, 0, 6, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(settings, 0, 0); right.Controls.Add(transport, 0, 1);
        right.Controls.Add(nowBox, 0, 2); right.Controls.Add(lists, 0, 3);

        split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        var stBox = new GroupBox { Text = "Stations", Dock = DockStyle.Fill }; stBox.Controls.Add(stations);
        split.Panel1.Controls.Add(stBox); split.Panel2.Controls.Add(right);

        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&Choose game folder…", null, (_, _) => LoadGame(ask: true));
        file.DropDownItems.Add("&Read game files again", null, (_, _) => LoadGame(ask: false, rescan: true));
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());
        menu.Items.Add(file);
        var bar = new StatusStrip(); bar.Items.Add(status);

        Controls.Add(split); Controls.Add(menu); Controls.Add(bar);
        MainMenuStrip = menu;
        foreach (var b in new[] { play, pause, skip, stopBtn, render }) { b.AutoSize = true; b.MinimumSize = new Size(b.Width, b.Height); }
        ResumeLayout(false); PerformLayout();

        // ---- events
        play.Click += (_, _) => Play();
        stations.DoubleClick += (_, _) => Play();
        pause.Click += (_, _) => { engine?.TogglePause(); pause.Text = engine?.Paused == true ? "▶  Resume" : "❚❚  Pause"; };
        skip.Click += (_, _) => engine?.Skip();
        stopBtn.Click += (_, _) => StopPlayback();
        render.Click += async (_, _) => await RenderWav();
        volume.ValueChanged += (_, _) => engine?.SetVolume(volume.Value / 100.0);
        tick.Tick += (_, _) => UpdateProgress();
        Load += (_, _) =>
        {
            float k = DeviceDpi / 96f;                  // list columns and splitter are not auto-scaled
            int[] w = { 180, 52, 44 };
            for (int i = 0; i < w.Length; i++) stations.Columns[i].Width = (int)(w[i] * k);
            foreach (var l in new[] { upNext, history })
                for (int i = 0; i < l.Columns.Count; i++) l.Columns[i].Width = (int)(new[] { 70, 90, 380, 300 }[i] * k);
            split.SplitterDistance = (int)(310 * k);
            foreach (var lab in new[] { station, title, kind, voices }) lab.UseMnemonic = false;   // keep '&' in names
            LoadGame(ask: false);
        };
        FormClosing += (_, _) => { StopPlayback(); audio?.Dispose(); };
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
        stations.Items.Clear();
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
        foreach (var (name, s) in sched.Stations.OrderBy(k => k.Key))
        {
            var it = new ListViewItem(name) { Tag = name };
            it.SubItems.Add(s.Songs.Count.ToString());
            it.SubItems.Add((s.Categories.GetValueOrDefault("advert")?.Count ?? 0).ToString());
            stations.Items.Add(it);
        }
        if (stations.Items.Count > 0) stations.Items[0].Selected = true;
        status.Text = $"{sched.Stations.Count} stations from {game}";
    }

    string? SelectedStation => stations.SelectedItems.Count > 0 ? (string)stations.SelectedItems[0].Tag! : null;

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
        if (sched == null || audio == null || SelectedStation is not { } name) return;
        StopPlayback();
        playingStation = name;
        playingSeed = ChooseSeed();
        history.Items.Clear(); upNext.Items.Clear();
        engine = new RadioEngine(sched.Stations[name], playingSeed, CurrentOptions(), audio);
        engine.SetVolume(volume.Value / 100.0);
        engine.Error += m => BeginInvoke(() => { status.Text = "Playback error: " + m; StopPlayback(); });
        ShowQueue(engine.Upcoming);
        station.Text = $"{name}   ·   seed {playingSeed}";
        title.Text = "Tuning in…"; kind.Text = ""; voices.Text = "";
        engine.Start();
        tick.Start();
        pause.Enabled = skip.Enabled = stopBtn.Enabled = true; pause.Text = "❚❚  Pause";
        status.Text = $"Playing {name} (seed {playingSeed}). Same seed + settings = same running order.";
    }

    void StopPlayback()
    {
        tick.Stop();
        engine?.Dispose(); engine = null;
        pause.Enabled = skip.Enabled = stopBtn.Enabled = false;
        progress.Value = 0; time.Text = "";
        if (playingStation != null) { title.Text = "Stopped"; kind.Text = ""; voices.Text = ""; }
        if (shown != null) AddRow(history, shown, insertTop: true);
        shown = null;
    }

    Planned? shown;

    void OnSegment(Planned p)
    {
        if (shown != null) AddRow(history, shown, insertTop: true);
        shown = p;
        var s = p.Segment;
        title.Text = Pretty(s);
        kind.Text = $"{KindName(s.Category)}   ·   {Clock(s.Duration)}";
        voices.Text = s.Voices.Count == 0 ? "" : string.Join("     ", s.Voices.Select(v => $"🎙 {v.Label} at {Clock(v.At)}"));
    }

    void ShowQueue(IReadOnlyList<Planned> q)
    {
        upNext.BeginUpdate();
        upNext.Items.Clear();
        foreach (var p in q) AddRow(upNext, p, insertTop: false);
        upNext.EndUpdate();
    }

    static void AddRow(ListView l, Planned p, bool insertTop)
    {
        var s = p.Segment;
        var it = new ListViewItem(Clock(p.StartsAt));
        it.SubItems.Add(KindName(s.Category));
        it.SubItems.Add(Pretty(s));
        it.SubItems.Add(string.Join(", ", s.Voices.Select(v => v.Label.Replace("DJ ", ""))));
        if (s.Category is not ("music" or "music_night")) it.ForeColor = Color.DimGray;
        if (insertTop) l.Items.Insert(0, it); else l.Items.Add(it);
    }

    /// <summary>Runs every 50 ms: follows what the sound device is actually playing.</summary>
    void UpdateProgress()
    {
        if (engine?.Now is not var (p, heard)) return;
        if (!ReferenceEquals(p, shown)) { OnSegment(p); ShowQueue(engine.Upcoming); }
        double pos = Math.Min(heard, p.Segment.Duration), dur = Math.Max(1, p.Segment.Duration);
        progress.Value = (int)Math.Clamp(pos / dur * 1000, 0, 1000);
        progress.Maximum = 1000;
        time.Text = $"{Clock(pos)} / {Clock(p.Segment.Duration)}";
        // highlight the DJ line while it is talking
        var talking = p.Segment.Voices.Where((v, i) => pos >= v.At && pos < v.At + (engine.VoiceSeconds(p.Segment, i) ?? 12)).Any();
        voices.ForeColor = talking ? Color.OrangeRed : Color.SteelBlue;
    }

    // ---- render

    async Task RenderWav()
    {
        if (sched == null || audio == null || SelectedStation is not { } name) return;
        using var dlg = new SaveFileDialog { Filter = "WAV|*.wav", FileName = $"{name} {DateTime.Now:yyyy-MM-dd HHmm}.wav" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int seed = ChooseSeed();
        var opt = CurrentOptions();
        double minutes = (double)renderMinutes.Value;
        var st = sched.Stations[name]; var ga = audio; var path = dlg.FileName;
        render.Enabled = false;
        try
        {
            await Task.Run(() =>
            {
                using var wav = new WavWriter(path);
                var tracklist = new Tracklist();
                double clock = 0;
                foreach (var seg in new Scheduler(st, seed, opt).Run())
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
            status.Text = $"Wrote {path} and its song timestamps ({Path.GetFileName(Path.ChangeExtension(path, ".txt"))}), seed {seed}";
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
                $"{timing}\nstation={station.Text}\ntitle={title.Text}\nkind={kind.Text}\nvoices={voices.Text}\nupnext={upNext.Items.Count}\nhistory={history.Items.Count}\nstatus={status.Text}\n");
            Close();
        };
    }

    // ---- formatting

    static string Clock(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    static string Pretty(Segment s) => s.Category is "music" or "music_night" ? s.Label
        : s.Label.StartsWith("0x") ? $"{KindName(s.Category)} {s.Label}" : $"{KindName(s.Category)} · {s.Label}";

    static string KindName(string cat) => cat switch
    {
        "music" => "Song",
        "music_night" => "Song (night)",
        "advert" => "Advert",
        "ident" => "Ident",
        "ident_night" => "Ident (night)",
        "identfromads" => "Ident",
        "identfromnews" => "Ident",
        "ident_to_ads" => "Ident",
        "talk" => "DJ talk",
        "talk_night" => "DJ talk",
        "music_bed" => "Music bed",
        "showintro" or "showintro_night" => "Show intro",
        "showoutro" or "showoutro_night" => "Show outro",
        _ when cat.StartsWith("category_") => "Special",
        _ => cat,
    };
}

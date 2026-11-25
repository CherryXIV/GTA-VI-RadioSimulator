namespace RadioSim;

/// <summary>A segment with its place in the station's running order.</summary>
public sealed record Planned(Segment Segment, double StartsAt);

/// <summary>
/// Plays a station (or a podcast) on a background thread. Keeps the next segments planned ahead (so the window
/// can show what is coming) and decodes one segment ahead. What is playing and how far in comes
/// from the sound device's own position, so the window follows what is actually heard (the
/// device has up to 2 s of audio queued ahead).
/// </summary>
public sealed class RadioEngine : IDisposable
{
    const int Ahead = 50;
    readonly GameAudio audio;
    readonly IEnumerator<Segment> it;
    readonly LinkedList<Planned> queue = new();
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    CancellationTokenSource skip = new();
    WaveOutPlayer? player;
    Thread? thread;
    double planClock;
    volatile bool paused;
    double volume = 1;

    public event Action<string>? Error;

    /// <summary>Segments handed to the device, with the device frame each starts at.</summary>
    readonly List<(long start, Planned p)> sent = new();

    public RadioEngine(Station station, int seed, Options options, GameAudio audio)
        : this(new Scheduler(station, seed, options).Run(), audio) { }

    /// <summary>Plays any running order; a finite one (a podcast's episodes) ends, see <see cref="Finished"/>.</summary>
    public RadioEngine(IEnumerable<Segment> order, GameAudio audio)
    {
        this.audio = audio;
        it = order.GetEnumerator();
        Fill();
    }

    volatile bool done;

    /// <summary>The running order ran out and the device has played all of it.</summary>
    public bool Finished => done && player != null && player.Played >= player.Written;

    void Fill()
    {
        lock (gate)
            while (queue.Count < Ahead && it.MoveNext())
            {
                queue.AddLast(new Planned(it.Current, planClock));
                planClock += it.Current.Duration;
            }
    }

    Planned? prefetched;                        // taken off the queue to decode ahead

    /// <summary>The segment being heard and how many seconds into it, from the device position.
    /// Null before the first one is heard.</summary>
    public (Planned p, double pos)? Now
    {
        get
        {
            if (player == null) return null;
            lock (gate)
            {
                long played = player.Played;
                int i = sent.FindLastIndex(x => x.start <= played);
                if (i < 0) return null;
                if (i > 0)
                {
                    foreach (var (_, old) in sent.Take(i)) voiceLen.Remove(old.Segment);
                    sent.RemoveRange(0, i);                          // older ones are over
                }
                return (sent[0].p, (played - sent[0].start) / (double)GameAudio.Rate);
            }
        }
    }

    /// <summary>Seconds of audio handed to the device but not heard yet.</summary>
    public double Buffered => player == null ? 0 : (player.Written - player.Played) / (double)GameAudio.Rate;

    /// <summary>Everything after the segment being heard, in order.</summary>
    public IReadOnlyList<Planned> Upcoming
    {
        get
        {
            lock (gate)
            {
                long played = player?.Played ?? 0;
                var l = sent.Where(x => x.start > played).Select(x => x.p).ToList();
                if (prefetched != null) l.Add(prefetched);
                l.AddRange(queue);
                return l;
            }
        }
    }

    public void Start()
    {
        player = new WaveOutPlayer();
        player.SetVolume(volume);
        thread = new Thread(Loop) { IsBackground = true, Name = "RadioSim playback" };
        thread.Start();
    }

    readonly Dictionary<Segment, List<double>> voiceLen = new();

    float[] Render(Segment s)
    {
        var lens = new List<double>();
        var pcm = audio.Render(s, lens);
        lock (gate) voiceLen[s] = lens;
        return pcm;
    }

    /// <summary>Length of DJ line <paramref name="i"/> of a segment, once it has been decoded.</summary>
    public double? VoiceSeconds(Segment s, int i)
    {
        lock (gate) return voiceLen.TryGetValue(s, out var l) && i < l.Count ? l[i] : null;
    }

    Planned? Next()
    {
        lock (gate)
        {
            if (queue.Count == 0) return null;
            var p = queue.First!.Value; queue.RemoveFirst();
            return p;
        }
    }

    void Loop()
    {
        try
        {
            var cur = Next();
            Task<float[]>? curPcm = cur == null ? null : Task.Run(() => Render(cur.Segment));
            while (cur != null && !stop.IsCancellationRequested)
            {
                var pcm = curPcm!.Result;
                Fill();
                var nxt = Next();
                prefetched = nxt;
                curPcm = nxt == null ? null : Task.Run(() => Render(nxt.Segment));
                lock (gate) sent.Add((player!.Written, cur));
                using var both = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, skip.Token);
                player!.Play(pcm, both.Token);
                if (skip.IsCancellationRequested)
                {
                    lock (gate) { player.Flush(); sent.Clear(); }
                    skip.Dispose(); skip = new CancellationTokenSource();
                    // a piece of a longer item (a podcast episode): skip the rest of the item too
                    var last = cur.Segment;
                    bool dropped = false;
                    while (nxt != null && nxt.Segment.Continues(last)) { last = nxt.Segment; Fill(); nxt = Next(); dropped = true; }
                    if (dropped)
                    {
                        var n2 = nxt;
                        lock (gate) prefetched = n2;
                        curPcm = n2 == null ? null : Task.Run(() => Render(n2.Segment));
                    }
                }
                cur = nxt;
            }
            done = true;
        }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            Error?.Invoke(e.Message);
        }
        catch { }
    }

    public bool Paused => paused;

    public void TogglePause()
    {
        if (player == null) return;
        paused = !paused;
        if (paused) player.Pause(); else player.Resume();
    }

    public void Skip() { skip.Cancel(); if (paused) TogglePause(); }

    public void SetVolume(double v) { volume = v; player?.SetVolume(v); }

    public void Dispose()
    {
        stop.Cancel();
        try { player?.Flush(); } catch { }
        thread?.Join(2000);
        player?.Dispose();
        player = null;
    }
}

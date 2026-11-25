// bcpack-rs by Ahmet Dedeler (https://github.com/Ahmet-Dedeler, https://ahmetdedeler.com)
// Source: https://github.com/Ahmet-Dedeler/bcpack-rs
// SPDX-License-Identifier: MIT OR Apache-2.0
// If you copy, vendor or port this file, keep this header and credit bcpack-rs in your README.
//
// A C# port of bcpack-rs's BC7 path (src/rans.rs decode_lanes and decode_slice, src/format.rs,
// src/bc7.rs assemble / apply / pack), for RadioSim. The rANS lanes are a cycle-level model of the
// decoder in Microsoft's MIT-licensed silicon-rans Verilog (mdu_minirans.v). Tables: BcPackTables.cs.

namespace RadioSim.Game;

/// <summary>
/// Decodes BCPack, the Xbox Series texture codec: BC blocks entropy-coded with a multi-lane rANS
/// stream. Only BC7 chunks (codec 0x1C) are supported. Output matches bcpack-rs block for block.
/// </summary>
public static class BcPack
{
    // ---- rANS models (rans.rs Model) -------------------------------------------------------

    const uint L = 1 << 24;
    const int FeedWindow = 8;

    sealed class Model
    {
        readonly int at, nseg;

        public Model(int eab, int q) { at = (eab * 16 + q) * 10; nseg = eab + 2; }

        uint Base(int s) => BcPackTables.Base[at + s];

        public ushort Inverse(uint cf)
        {
            int seg = 0;
            for (int k = nseg - 1; k > 0; k--)
                if (cf >= Base(k)) { seg = k; break; }
            uint baseX = seg == 0 ? 0 : 1u << (seg - 1);
            uint dist = (cf - Base(seg) + BcPackTables.Rn[at + seg]) & 0xFFFF;
            uint steps = ((dist * BcPackTables.Rf[at + seg]) >> 15 >> BcPackTables.Rs[at + seg]) & 0x1FF;
            return (ushort)((baseX + steps) & 0x1FF);
        }

        public (uint f, uint cf) Forward(ushort sym)
        {
            uint s = sym;
            int seg = 32 - System.Numerics.BitOperations.LeadingZeroCount(s);
            uint st = seg == 0 ? 0 : 1u << (seg - 1);
            uint fa = BcPackTables.Freq[at + seg];
            uint cfa = (Base(seg) + fa * (s - st)) & 0xFFFF;
            return cfa == 0 ? ((fa - 1) & 0xFFFF, 1) : (fa, cfa);
        }

        public uint Advance(uint x, ushort sym)
        {
            var (f, cf) = Forward(sym);
            return f * (x >> 16) + (x & 0xFFFF) - cf;
        }
    }

    static readonly Model[] Models = Enumerable.Range(0, 9 * 16).Select(i => new Model(i / 16, i % 16)).ToArray();

    // ---- rANS lanes (rans.rs decode_lanes / phase1) ----------------------------------------

    const byte Idle = 0, Hdr0 = 1, Hdr1 = 2, Hdr2 = 3, Proc = 4, Drain = 5;

    sealed class Lane
    {
        public byte Ab, State, Ib, Q, Eab;
        public uint Target, InRem, OutRem, P0, P1, NSyms, FinalState;
        public bool IbFull, SymFull, Hdr3, Flush, Done, Go = true, Stall;
        public ushort Sym;
        public Model Model = Models[0];

        public bool NeedByte => !IbFull && State switch { Hdr0 or Hdr1 or Hdr2 => true, Proc => InRem > 0, _ => false };
    }

    /// <summary>The lanes' symbols, lane major: lane i is <c>[i * target, (i + 1) * target)</c>.</summary>
    static ushort[] DecodeLanes(ReadOnlySpan<byte> data, byte[] alphabetBits, int target)
    {
        int n = alphabetBits.Length;
        var syms = new ushort[n * target];
        var lanes = alphabetBits.Select(ab => new Lane { Ab = ab, Target = (uint)target }).ToArray();
        uint slots = 0;
        int pos = 0;
        long cycle = 0, maxCycles = 64L * ((long)n * target + data.Length) + 1000;
        long totalSyms = 0, lastChange = 0;
        (int, long, uint) lastProg = (-1, -1, uint.MaxValue);
        while (cycle < maxCycles)
        {
            // phase 0: forward update, feed, hand-off
            int served = 0;
            for (int i = 0; i < n; i++)
            {
                var ln = lanes[i];
                uint bit = 1u << i;
                bool blockedOut = ln.SymFull && (slots & bit) != 0;
                bool need = ln.NeedByte, valid = false;
                byte b = 0;
                if (need && !blockedOut && served < FeedWindow && pos < data.Length) { valid = true; b = data[pos]; }
                ln.Stall = (need && !valid) || blockedOut;
                if (ln.Stall) continue;
                if (ln.State is Proc or Drain) ln.P0 = ln.SymFull ? ln.Model.Advance(ln.P1, ln.Sym) : ln.P1;
                if (valid) { ln.Ib = b; ln.IbFull = true; pos++; served++; }
                if (ln.SymFull)
                {
                    slots |= bit;
                    syms[i * target + (int)ln.NSyms] = ln.Sym;
                    ln.NSyms++; totalSyms++;
                    ln.SymFull = false;
                }
            }
            // phase 1: header FSM, renormalisation, decode
            uint active = 0, stateSum = 0;
            bool finished = true;
            for (int i = 0; i < n; i++)
            {
                var ln = lanes[i];
                if (!ln.Stall) Phase1(i, ln);
                uint bit = 1u << i;
                if (!(ln.Done && !ln.SymFull && (slots & bit) == 0)) active |= bit;
                finished &= ln.Done && !ln.SymFull;
                stateSum += ln.State;
            }
            // lockstep symbol vector
            if (slots != 0 && (slots & active) == active) slots = 0;
            cycle++;
            if (finished)
            {
                if (lanes.Any(l => l.FinalState is not (L or L >> 8 or L >> 16)))
                    throw new InvalidDataException("BCPack: a lane ended in an illegal rANS state");
                for (int k = pos; k < data.Length; k++)
                    if (data[k] != 0) throw new InvalidDataException("BCPack: data after the end of the stream");
                return syms;
            }
            var prog = (pos, totalSyms, stateSum);
            if (prog != lastProg) { lastProg = prog; lastChange = cycle; }
            else if (cycle - lastChange > 64) throw new InvalidDataException($"BCPack: no progress after {pos} bytes");
        }
        throw new InvalidDataException("BCPack: cycle limit reached");
    }

    static void Phase1(int i, Lane ln)
    {
        switch (ln.State)
        {
            case Idle:
                if (ln.Go) { ln.Go = false; ln.State = Hdr0; ln.OutRem = ln.Target; ln.P0 = ln.P1 = 0; }
                break;
            case Hdr0:
                if (ln.IbFull)
                {
                    byte b = ln.Ib;
                    ln.IbFull = false; ln.InRem = 0; ln.State = Hdr1;
                    ln.Q = (byte)(b & 15);
                    ln.Hdr3 = (b & 0x20) != 0;
                    ln.Flush = (b & 0x10) != 0;
                    int e = (ln.Ab - (b >> 6)) & 15;
                    ln.Eab = (byte)(e < 10 ? e : 9);
                    if (ln.Eab > 8) throw new InvalidDataException($"BCPack: lane {i} header selects alphabet {ln.Eab}");
                }
                break;
            case Hdr1:
                if (ln.IbFull)
                {
                    ln.IbFull = false;
                    ln.Model = Models[ln.Eab * 16 + ln.Q];
                    if (ln.Hdr3) { ln.InRem = ln.Ib; ln.State = Hdr2; }
                    else { ln.InRem = ln.Ib + 1u; ln.State = Proc; }
                }
                break;
            case Hdr2:
                if (ln.IbFull)
                {
                    ln.IbFull = false;
                    ln.InRem = (((uint)ln.Ib << 8) | (ln.InRem & 0xFF)) + 257;
                    ln.State = Proc;
                }
                break;
            default:                                               // PROCESSING or DRAINING
            {
                byte st = ln.State;
                bool will = st == Proc && ln.P0 < L && ln.InRem > 0 && ln.IbFull;
                uint x = will ? ((ln.P0 & 0xFFFFFF) << 8) | ln.Ib : ln.P0;
                bool nsf = x > L && ln.OutRem > 0;
                if (nsf) { ln.OutRem--; ln.Sym = ln.Model.Inverse(x & 0xFFFF); }
                uint newOut = ln.OutRem;
                ln.P1 = x;
                ln.SymFull = nsf;
                if (st == Proc)
                {
                    if (will) { ln.InRem--; ln.IbFull = false; }
                    if (ln.InRem == 0)
                    {
                        if (newOut > 0)
                        {
                            if (x == L) { ln.State = Hdr0; if (ln.Flush) ln.P1 = 0; }
                            else ln.State = Drain;
                        }
                        else { ln.Done = true; ln.State = Idle; ln.FinalState = nsf ? ln.Model.Advance(x, ln.Sym) : x; }
                    }
                }
                else if (x <= L)
                {
                    if (newOut > 0) { ln.State = Hdr0; if (ln.Flush) { ln.P0 = 0; ln.P1 = 0; } }
                    else { ln.State = Idle; ln.Done = true; ln.FinalState = nsf ? ln.Model.Advance(x, ln.Sym) : x; }
                }
                break;
            }
        }
    }

    // ---- shared helpers (format.rs) --------------------------------------------------------

    static byte Interleave(int even, int odd)
    {
        int o = 0;
        for (int k = 0; k < 4; k++) o |= (even >> k & 1) << (2 * k) | (odd >> k & 1) << (2 * k + 1);
        return (byte)o;
    }

    static (int even, int odd) Deinterleave(int b)
    {
        int e = 0, o = 0;
        for (int k = 0; k < 4; k++) { e |= (b >> (2 * k) & 1) << k; o |= (b >> (2 * k + 1) & 1) << k; }
        return (e, o);
    }

    static int Unzigzag(int v) => (v & 1) != 0 ? -((v + 1) >> 1) : v >> 1;

    static int Residual(int v, int bits) => Unzigzag(v) & ((1 << bits) - 1);

    // ---- BC7 (bc7.rs) ----------------------------------------------------------------------

    static readonly byte[] Bc7AlphabetBits = { 3, 3, 7, 7, 7, 7, 7, 6, 6, 4, 5, 7, 7, 7, 7, 4 };
    static readonly int[] Subsets = { 3, 2, 3, 2, 1, 1, 1, 2 };
    static readonly int[,] ChannelBits = { { 4, 4, 4, 0 }, { 6, 6, 6, 0 }, { 5, 5, 5, 0 }, { 7, 7, 7, 0 }, { 5, 5, 5, 6 }, { 7, 7, 7, 8 }, { 7, 7, 7, 7 }, { 5, 5, 5, 5 } };
    static readonly int[,] IndexBits = { { 3, 0 }, { 3, 0 }, { 2, 0 }, { 2, 0 }, { 3, 2 }, { 2, 2 }, { 4, 0 }, { 2, 0 } };
    static readonly int[] PartitionBits = { 4, 6, 6, 6, 0, 0, 0, 6 };
    static readonly byte[] Anchor2 =
    {
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8,
        2, 2, 8, 8, 2, 2, 15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2,
        2, 15, 15, 15, 15, 15, 2, 2, 15,
    };
    static readonly byte[] Anchor3A =
    {
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5,
        15, 15, 8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5,
        10, 8, 13, 15, 12, 3, 3,
    };
    static readonly byte[] Anchor3B =
    {
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6,
        10, 15, 15, 10, 8, 15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15,
        15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
    };
    static readonly int[,] IndexLanes = { { 11, 12, 13, 14 }, { 6, 7, 8, 10 } };

    readonly record struct Control(int Mode, int Partition, int Rotation, int IndexSelection)
    {
        public int Subsets => BcPack.Subsets[Mode];
        public bool HasPBits => Mode is 0 or 1 or 3 or 6 or 7;

        public int[] Precision()
        {
            var p = new[] { ChannelBits[Mode, 0], ChannelBits[Mode, 1], ChannelBits[Mode, 2], ChannelBits[Mode, 3] };
            if (Rotation != 0) (p[3], p[Rotation - 1]) = (p[Rotation - 1], p[3]);
            else if (HasPBits) p[0]++;
            return p;
        }

        public int Anchors => Subsets switch
        {
            2 => 1 | 1 << Anchor2[Partition & 63],
            3 => 1 | 1 << Anchor3A[Partition & 63] | 1 << Anchor3B[Partition & 63],
            _ => 1,
        };
    }

    /// <summary>Control code (9 bits) → control, null for the codes the format doesn't use.</summary>
    static readonly Control?[] Controls = BuildControls();

    static Control?[] BuildControls()
    {
        var t = new Control?[512];
        var c = BcPackTables.Bc7Control;
        for (int k = 0; k + 6 <= c.Length; k += 6)
            t[(c[k + 1] & 15) << 5 | (c[k] & 31)] = new Control(c[k + 2], c[k + 3], c[k + 4], c[k + 5]);
        return t;
    }

    // a block state: [0] subsets, [1 + 8 * row + col] endpoints (R0 R1 G0 G1 B0 B1 A0 A1, 8-bit,
    // top-aligned), [25 + 16 * set + pixel] indices (4-bit, top-aligned)
    const int StateSize = 57, Ep = 1, Ix = 25;

    static readonly byte[][] Dictionary = Enumerable.Range(0, 255).Select(k =>
    {
        var s = new byte[StateSize];
        s[0] = BcPackTables.Bc7DictSubsets[k];
        Array.Copy(BcPackTables.Bc7DictEp, k * 24, s, Ep, 24);
        Array.Copy(BcPackTables.Bc7DictIx, k * 32, s, Ix, 32);
        return s;
    }).ToArray();

    static int? EndpointLane(int subsets, int row, int ch) => (row, ch) switch
    {
        (0 or 1, 3) when subsets == 3 => null,
        (0, _) => 2 + ch,
        (1, _) => 6 + ch,
        (2, 0) => 10,
        (2, 1) when subsets == 3 => 5,
        (2, 2) when subsets == 3 => 9,
        _ => null,
    };

    /// <summary>A BCPack BC7 chunk → <paramref name="unpackedLength"/> bytes of BC7 blocks in stream order.</summary>
    public static byte[] DecodeBc7(ReadOnlySpan<byte> chunk, int unpackedLength)
    {
        int target = unpackedLength / 8, n = target / 2;
        var syms = DecodeLanes(chunk, Bc7AlphabetBits, target);
        int Sym(int lane, int k) => syms[lane * target + k];
        var outp = new byte[unpackedLength];
        var hist = new byte[256][];
        for (int k = 0; k < 256; k++) hist[k] = new byte[StateSize];
        var zero = new byte[StateSize];
        for (int i = 0; i < n; i++)
        {
            int d = Interleave(Sym(0, 2 * i), Sym(0, 2 * i + 1)), x = Interleave(Sym(1, 2 * i), Sym(1, 2 * i + 1));
            int er = (x + Unzigzag(d)) & 0xFF, xr = x;
            var cc = Controls[(Sym(15, 2 * i + 1) & 15) << 5 | (Sym(15, 2 * i) & 31)];
            if (cc is not { } c) { hist[i & 255] = new byte[StateSize]; continue; }   // unknown control: zeros
            byte[] Pick(int r) => r == 0 ? zero : r <= i ? hist[(i - r) & 255] : Dictionary[r - i - 1];
            var st = Apply(c, Sym, i, Pick(er), Pick(xr));
            Pack(c, st, outp.AsSpan(i * 16, 16));
            hist[i & 255] = st;
        }
        return outp;
    }

    /// <summary>Block <paramref name="i"/>'s residuals applied to its endpoint and index references.</summary>
    static byte[] Apply(Control c, Func<int, int, int> sym, int i, byte[] er, byte[] xr)
    {
        int subsets = c.Subsets;
        var prec = c.Precision();
        var st = new byte[StateSize];
        st[0] = (byte)subsets;
        int common = Math.Min(subsets, (int)er[0]);
        for (int ch = 0; ch < 4; ch++)
        {
            int p = prec[ch];
            if (p == 0) continue;
            for (int row = 0; row < subsets; row++)
            {
                int from = row < common ? row : 0;
                var lane = EndpointLane(subsets, row, ch);
                for (int e = 0; e < 2; e++)
                {
                    int col = 2 * ch + e;
                    int res = lane is { } l ? Residual(sym(l, 2 * i + e), p) : 0;
                    int bse = er[Ep + 8 * from + col] >> (8 - p);
                    st[Ep + 8 * row + col] = (byte)(((bse + res) & (0xFF >> (8 - p))) << (8 - p));
                }
            }
        }
        for (int set = 0; set < 2; set++)
        {
            int b = IndexBits[c.Mode, set];
            if (b == 0) continue;
            for (int row = 0; row < 4; row++)
                for (int half = 0; half < 2; half++)
                {
                    var (even, odd) = Deinterleave(sym(IndexLanes[set, row], 2 * i + half));
                    var (left, right) = row < 3 ? (odd, even) : (even, odd);
                    for (int h = 0; h < 2; h++)
                    {
                        int px = 4 * row + 2 * half + h;
                        int res = Residual(h == 0 ? left : right, b);
                        int bse = xr[Ix + 16 * set + px] >> (4 - b);
                        st[Ix + 16 * set + px] = (byte)(((bse + res) & (0xF >> (4 - b))) << (4 - b));
                    }
                }
        }
        return st;
    }

    /// <summary>A state as a 16-byte BC7 block.</summary>
    static void Pack(Control c, byte[] st, Span<byte> outp)
    {
        UInt128 v = 0;
        int at = 0;
        void Put(uint value, int bits)
        {
            if (bits > 0 && at < 128) v |= ((UInt128)value & ((UInt128.One << bits) - 1)) << at;
            at += bits;
        }
        int mode = c.Mode, subsets = c.Subsets;
        var prec = c.Precision();
        Put(1u << mode, mode + 1);
        Put((uint)c.Partition, PartitionBits[mode]);
        if (mode is 4 or 5) Put((uint)c.Rotation, 2);
        if (mode == 4) Put((uint)c.IndexSelection, 1);
        uint Field(int row, int col, int p) => (uint)(st[Ep + 8 * row + col] >> (8 - p));
        var order = new[] { 0, 1, 2, 3 };
        if (c.Rotation != 0) (order[3], order[c.Rotation - 1]) = (order[c.Rotation - 1], order[3]);
        foreach (int ch in order)
        {
            int p = prec[ch];
            if (p == 0) continue;
            int drop = ch == 0 && c.HasPBits ? 1 : 0;            // red's lowest bit is the p-bit
            for (int e = 0; e < 2 * subsets; e++) Put(Field(e / 2, 2 * ch + e % 2, p) >> drop, p - drop);
        }
        if (mode == 1)
            for (int row = 0; row < subsets; row++) Put(Field(row, 0, prec[0]) & 1, 1);   // one shared p-bit per subset
        else if (c.HasPBits)
            for (int e = 0; e < 2 * subsets; e++) Put(Field(e / 2, e % 2, prec[0]) & 1, 1);
        int anchors = c.Anchors;
        void Set(int s)
        {
            int b = IndexBits[mode, s];
            for (int px = 0; px < 16; px++) Put((uint)(st[Ix + 16 * s + px] >> (4 - b)), b - (anchors >> px & 1));
        }
        if (mode == 4) { Set(1); Set(0); }
        else if (mode == 5) { Set(0); Set(1); }
        else Set(0);
        for (int k = 0; k < 16; k++) outp[k] = (byte)(v >> (8 * k));
    }
}

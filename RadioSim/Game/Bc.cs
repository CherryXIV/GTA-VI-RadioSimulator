namespace RadioSim.Game;

/// <summary>
/// Block-compressed texture decoders (BC1, BC3, BC7) to RGBA8, enough for the UI textures the
/// station logos use. BC7 follows the D3D11 BC7 format description: 8 modes with up to three
/// subsets, P-bits, component rotation and a separate alpha index set.
/// </summary>
public static class Bc
{
    public const int Bc1 = 71, Bc1Srgb = 72, Bc3 = 77, Bc3Srgb = 78, Bc7 = 98, Bc7Srgb = 99;

    /// <summary>Bytes per 4×4 block for a DXGI format this class decodes, else 0.</summary>
    public static int BlockBytes(int dxgi) => dxgi switch
    {
        Bc1 or Bc1Srgb => 8,
        Bc3 or Bc3Srgb or Bc7 or Bc7Srgb => 16,
        _ => 0,
    };

    /// <summary>Decode linear (row-major) blocks to <paramref name="w"/>×<paramref name="h"/> RGBA8.</summary>
    public static byte[]? Decode(int dxgi, ReadOnlySpan<byte> data, int w, int h)
    {
        int bb = BlockBytes(dxgi);
        if (bb == 0) return null;
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        if (data.Length < bw * bh * bb) return null;
        var outp = new byte[w * h * 4];
        Span<byte> px = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var blk = data.Slice((by * bw + bx) * bb, bb);
                switch (dxgi)
                {
                    case Bc1 or Bc1Srgb: DecodeBc1(blk, px, true); break;
                    case Bc3 or Bc3Srgb: DecodeBc1(blk[8..], px, false); DecodeAlpha(blk, px); break;
                    default: DecodeBc7(blk, px); break;
                }
                for (int y = 0; y < 4; y++)
                {
                    int py = by * 4 + y;
                    if (py >= h) break;
                    for (int x = 0; x < 4; x++)
                    {
                        int pxx = bx * 4 + x;
                        if (pxx >= w) break;
                        px.Slice((y * 4 + x) * 4, 4).CopyTo(outp.AsSpan((py * w + pxx) * 4));
                    }
                }
            }
        return outp;
    }

    // ---- BC1 / BC3

    static void Rgb565(ushort c, Span<int> o)
    {
        int r = c >> 11 & 31, g = c >> 5 & 63, b = c & 31;
        o[0] = r << 3 | r >> 2; o[1] = g << 2 | g >> 4; o[2] = b << 3 | b >> 2;
    }

    /// <summary>BC1 colour block. <paramref name="punchThrough"/>: c0 ≤ c1 selects the 3-colour +
    /// transparent mode (BC1 alone; BC2/BC3 colour blocks are always 4-colour).</summary>
    static void DecodeBc1(ReadOnlySpan<byte> b, Span<byte> px, bool punchThrough)
    {
        ushort c0 = Bin.U16(b, 0), c1 = Bin.U16(b, 2);
        Span<int> e0 = stackalloc int[3], e1 = stackalloc int[3];
        Rgb565(c0, e0); Rgb565(c1, e1);
        Span<int> pal = stackalloc int[16];
        for (int c = 0; c < 3; c++)
        {
            pal[c] = e0[c]; pal[4 + c] = e1[c];
            if (c0 > c1 || !punchThrough)
            {
                pal[8 + c] = (2 * e0[c] + e1[c] + 1) / 3;
                pal[12 + c] = (e0[c] + 2 * e1[c] + 1) / 3;
            }
            else
            {
                pal[8 + c] = (e0[c] + e1[c]) / 2;
                pal[12 + c] = 0;
            }
        }
        pal[3] = pal[7] = pal[11] = 255;
        pal[15] = c0 > c1 || !punchThrough ? 255 : 0;
        uint idx = Bin.U32(b, 4);
        for (int i = 0; i < 16; i++)
        {
            int k = (int)(idx >> (2 * i) & 3) * 4;
            for (int c = 0; c < 4; c++) px[i * 4 + c] = (byte)pal[k + c];
        }
    }

    /// <summary>BC3/BC4 alpha block into the A channel.</summary>
    static void DecodeAlpha(ReadOnlySpan<byte> b, Span<byte> px)
    {
        int a0 = b[0], a1 = b[1];
        Span<int> pal = stackalloc int[8];
        pal[0] = a0; pal[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1 + 3) / 7;
        else
        {
            for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1 + 2) / 5;
            pal[6] = 0; pal[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) px[i * 4 + 3] = (byte)pal[(int)(bits >> (3 * i) & 7)];
    }

    // ---- BC7

    ref struct Bits(ReadOnlySpan<byte> b)
    {
        readonly ReadOnlySpan<byte> b = b;
        int pos;
        public int Read(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++, pos++) v |= (b[pos >> 3] >> (pos & 7) & 1) << i;
            return v;
        }
    }

    //                                   mode: 0  1  2  3  4  5  6  7
    static readonly int[] NumSubsets = { 3, 2, 3, 2, 1, 1, 1, 2 };
    static readonly int[] PartBits = { 4, 6, 6, 6, 0, 0, 0, 6 };
    static readonly int[] RotBits = { 0, 0, 0, 0, 2, 2, 0, 0 };
    static readonly int[] IdxSelBits = { 0, 0, 0, 0, 1, 0, 0, 0 };
    static readonly int[] ColorBits = { 4, 6, 5, 7, 5, 7, 7, 5 };
    static readonly int[] AlphaBits = { 0, 0, 0, 0, 6, 8, 7, 5 };
    static readonly int[] EndPBits = { 1, 0, 0, 1, 0, 0, 1, 1 };
    static readonly int[] SharedPBits = { 0, 1, 0, 0, 0, 0, 0, 0 };
    static readonly int[] IndexBits = { 3, 3, 2, 2, 2, 2, 4, 2 };
    static readonly int[] IndexBits2 = { 0, 0, 0, 0, 3, 2, 0, 0 };

    static readonly int[] W2 = { 0, 21, 43, 64 };
    static readonly int[] W3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
    static readonly int[] W4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };
    static int[] Weights(int bits) => bits == 2 ? W2 : bits == 3 ? W3 : W4;

    static void DecodeBc7(ReadOnlySpan<byte> blk, Span<byte> px)
    {
        int mode = 0;
        while (mode < 8 && (blk[0] >> mode & 1) == 0) mode++;
        if (mode == 8) { px.Clear(); return; }           // reserved: transparent black
        var r = new Bits(blk);
        r.Read(mode + 1);
        int ns = NumSubsets[mode];
        int part = r.Read(PartBits[mode]);
        int rot = r.Read(RotBits[mode]);
        int idxSel = r.Read(IdxSelBits[mode]);
        int cb = ColorBits[mode], ab = AlphaBits[mode];
        int ne = ns * 2;
        Span<int> ep = stackalloc int[6 * 4];             // endpoint × RGBA
        for (int c = 0; c < 3; c++)
            for (int e = 0; e < ne; e++) ep[e * 4 + c] = r.Read(cb);
        for (int e = 0; e < ne; e++) ep[e * 4 + 3] = ab > 0 ? r.Read(ab) : 255;
        // P-bits: one per endpoint, or one per subset shared by its two endpoints
        Span<int> pb = stackalloc int[6];
        bool hasP = EndPBits[mode] != 0 || SharedPBits[mode] != 0;
        if (EndPBits[mode] != 0) for (int e = 0; e < ne; e++) pb[e] = r.Read(1);
        else if (SharedPBits[mode] != 0) for (int s = 0; s < ns; s++) pb[2 * s] = pb[2 * s + 1] = r.Read(1);
        for (int e = 0; e < ne; e++)
            for (int c = 0; c < 4; c++)
            {
                int bits = c < 3 ? cb : ab;
                if (bits == 0) continue;                  // alpha 255 already
                int v = ep[e * 4 + c];
                if (hasP) { v = v << 1 | pb[e]; bits++; }
                v <<= 8 - bits;
                ep[e * 4 + c] = v | v >> bits;
            }

        // primary indices (the anchor of each subset has one bit less)
        int ib = IndexBits[mode], ib2 = IndexBits2[mode];
        Span<int> idx = stackalloc int[16], idx2 = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            bool anchor = i == 0 || (ns == 2 && i == Anchor2[part])
                       || (ns == 3 && (i == Anchor3a[part] || i == Anchor3b[part]));
            idx[i] = r.Read(anchor ? ib - 1 : ib);
        }
        if (ib2 > 0)
            for (int i = 0; i < 16; i++) idx2[i] = r.Read(i == 0 ? ib2 - 1 : ib2);

        Span<int> col = stackalloc int[4];
        for (int i = 0; i < 16; i++)
        {
            int s = ns == 1 ? 0 : ns == 2 ? Part2[part * 16 + i] : Part3[part * 16 + i];
            int e0 = 2 * s * 4, e1 = (2 * s + 1) * 4;
            // mode 4 index selection: the 3-bit set drives colour and the 2-bit set alpha, or swapped
            int ci = idx[i], cbits = ib, ai = idx[i], abits = ib;
            if (ib2 > 0)
            {
                if (idxSel == 0) { ai = idx2[i]; abits = ib2; }
                else { ci = idx2[i]; cbits = ib2; }
            }
            int wc = Weights(cbits)[ci], wa = Weights(abits)[ai];
            for (int c = 0; c < 3; c++) col[c] = (ep[e0 + c] * (64 - wc) + ep[e1 + c] * wc + 32) >> 6;
            col[3] = (ep[e0 + 3] * (64 - wa) + ep[e1 + 3] * wa + 32) >> 6;
            if (rot != 0) (col[rot - 1], col[3]) = (col[3], col[rot - 1]);
            for (int c = 0; c < 4; c++) px[i * 4 + c] = (byte)col[c];
        }
    }

    // partition and anchor tables (BC7 format description)
    static readonly byte[] Part2 = {0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,1,1,1,0,1,1,1,0,1,1,1,0,1,1,1,0,0,0,1,0,0,1,1,0,0,1,1,0,1,1,1,0,0,0,0,0,0,0,1,0,0,0,1,0,0,1,1,0,0,1,1,0,1,1,1,0,1,1,1,1,1,1,1,0,0,0,1,0,0,1,1,0,1,1,1,1,1,1,1,0,0,0,0,0,0,0,1,0,0,1,1,0,1,1,1,0,0,0,0,0,0,0,0,0,0,0,1,0,0,1,1,0,0,1,1,0,1,1,1,1,1,1,1,1,1,1,1,0,0,0,0,0,0,0,1,0,1,1,1,1,1,1,1,0,0,0,0,0,0,0,0,0,0,0,1,0,1,1,1,0,0,0,1,0,1,1,1,1,1,1,1,1,1,1,1,0,0,0,0,0,0,0,0,1,1,1,1,1,1,1,1,0,0,0,0,1,1,1,1,1,1,1,1,1,1,1,1,0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,1,0,0,0,0,1,0,0,0,1,1,1,0,1,1,1,1,0,1,1,1,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,1,1,1,0,0,1,1,1,0,0,1,1,0,0,0,1,0,0,0,0,0,0,1,1,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,1,1,0,0,1,1,1,0,0,0,0,0,0,0,0,0,1,0,0,0,1,1,0,0,0,1,1,1,0,0,1,1,0,0,1,1,0,0,0,1,0,0,1,1,0,0,0,1,0,0,0,1,0,0,0,0,0,0,0,0,1,0,0,0,1,0,0,0,1,1,0,0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,0,0,0,1,1,0,1,1,0,0,1,1,0,1,1,0,0,0,0,0,1,0,1,1,1,1,1,1,0,1,0,0,0,0,0,0,0,1,1,1,1,1,1,1,1,0,0,0,0,0,1,1,1,0,0,0,1,1,0,0,0,1,1,1,0,0,0,1,1,1,0,0,1,1,0,0,1,1,1,0,0,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,0,0,0,1,1,1,1,0,0,0,0,1,1,1,1,0,1,0,1,1,0,1,0,0,1,0,1,1,0,1,0,0,0,1,1,0,0,1,1,1,1,0,0,1,1,0,0,0,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,1,0,1,0,1,0,1,1,0,1,0,1,0,1,0,0,1,1,0,1,0,0,1,0,1,1,0,1,0,0,1,0,1,0,1,1,0,1,0,1,0,1,0,0,1,0,1,0,1,1,1,0,0,1,1,1,1,0,0,1,1,1,0,0,0,0,1,0,0,1,1,1,1,0,0,1,0,0,0,0,0,1,1,0,0,1,0,0,1,0,0,1,1,0,0,0,0,1,1,1,0,1,1,1,1,0,1,1,1,0,0,0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0,0,0,1,1,1,1,0,0,1,1,0,0,0,0,1,1,0,1,1,0,0,1,1,0,1,0,0,1,1,0,0,1,0,0,0,0,0,1,1,0,0,1,1,0,0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,0,0,0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,0,0,0,0,0,0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,0,0,1,1,0,1,1,0,0,1,0,0,1,0,0,1,1,0,0,1,1,0,1,1,0,1,1,0,0,1,0,0,1,0,1,1,0,0,0,1,1,1,0,0,1,1,1,0,0,0,0,1,1,1,0,0,1,1,1,0,0,0,1,1,0,0,1,1,0,1,1,0,0,1,1,0,0,1,0,0,1,0,1,1,0,0,0,1,1,0,0,1,1,1,0,0,1,0,1,1,1,1,1,1,0,1,0,0,0,0,0,0,1,0,0,0,1,1,0,0,0,1,1,1,0,0,1,1,1,0,0,0,0,1,1,1,1,0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,1,1,1,1,0,0,0,0,0,0,1,0,0,0,1,0,1,1,1,0,1,1,1,0,0,1,0,0,0,1,0,0,0,1,1,1,0,1,1,1};
    static readonly byte[] Part3 = {0,0,1,1,0,0,1,1,0,2,2,1,2,2,2,2,0,0,0,1,0,0,1,1,2,2,1,1,2,2,2,1,0,0,0,0,2,0,0,1,2,2,1,1,2,2,1,1,0,2,2,2,0,0,2,2,0,0,1,1,0,1,1,1,0,0,0,0,0,0,0,0,1,1,2,2,1,1,2,2,0,0,1,1,0,0,1,1,0,0,2,2,0,0,2,2,0,0,2,2,0,0,2,2,1,1,1,1,1,1,1,1,0,0,1,1,0,0,1,1,2,2,1,1,2,2,1,1,0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,0,0,0,0,1,1,1,1,1,1,1,1,2,2,2,2,0,0,0,0,1,1,1,1,2,2,2,2,2,2,2,2,0,0,1,2,0,0,1,2,0,0,1,2,0,0,1,2,0,1,1,2,0,1,1,2,0,1,1,2,0,1,1,2,0,1,2,2,0,1,2,2,0,1,2,2,0,1,2,2,0,0,1,1,0,1,1,2,1,1,2,2,1,2,2,2,0,0,1,1,2,0,0,1,2,2,0,0,2,2,2,0,0,0,0,1,0,0,1,1,0,1,1,2,1,1,2,2,0,1,1,1,0,0,1,1,2,0,0,1,2,2,0,0,0,0,0,0,1,1,2,2,1,1,2,2,1,1,2,2,0,0,2,2,0,0,2,2,0,0,2,2,1,1,1,1,0,1,1,1,0,1,1,1,0,2,2,2,0,2,2,2,0,0,0,1,0,0,0,1,2,2,2,1,2,2,2,1,0,0,0,0,0,0,1,1,0,1,2,2,0,1,2,2,0,0,0,0,1,1,0,0,2,2,1,0,2,2,1,0,0,1,2,2,0,1,2,2,0,0,1,1,0,0,0,0,0,0,1,2,0,0,1,2,1,1,2,2,2,2,2,2,0,1,1,0,1,2,2,1,1,2,2,1,0,1,1,0,0,0,0,0,0,1,1,0,1,2,2,1,1,2,2,1,0,0,2,2,1,1,0,2,1,1,0,2,0,0,2,2,0,1,1,0,0,1,1,0,2,0,0,2,2,2,2,2,0,0,1,1,0,1,2,2,0,1,2,2,0,0,1,1,0,0,0,0,2,0,0,0,2,2,1,1,2,2,2,1,0,0,0,0,0,0,0,2,1,1,2,2,1,2,2,2,0,2,2,2,0,0,2,2,0,0,1,2,0,0,1,1,0,0,1,1,0,0,1,2,0,0,2,2,0,2,2,2,0,1,2,0,0,1,2,0,0,1,2,0,0,1,2,0,0,0,0,0,1,1,1,1,2,2,2,2,0,0,0,0,0,1,2,0,1,2,0,1,2,0,1,2,0,1,2,0,0,1,2,0,2,0,1,2,1,2,0,1,0,1,2,0,0,0,1,1,2,2,0,0,1,1,2,2,0,0,1,1,0,0,1,1,1,1,2,2,2,2,0,0,0,0,1,1,0,1,0,1,0,1,0,1,2,2,2,2,2,2,2,2,0,0,0,0,0,0,0,0,2,1,2,1,2,1,2,1,0,0,2,2,1,1,2,2,0,0,2,2,1,1,2,2,0,0,2,2,0,0,1,1,0,0,2,2,0,0,1,1,0,2,2,0,1,2,2,1,0,2,2,0,1,2,2,1,0,1,0,1,2,2,2,2,2,2,2,2,0,1,0,1,0,0,0,0,2,1,2,1,2,1,2,1,2,1,2,1,0,1,0,1,0,1,0,1,0,1,0,1,2,2,2,2,0,2,2,2,0,1,1,1,0,2,2,2,0,1,1,1,0,0,0,2,1,1,1,2,0,0,0,2,1,1,1,2,0,0,0,0,2,1,1,2,2,1,1,2,2,1,1,2,0,2,2,2,0,1,1,1,0,1,1,1,0,2,2,2,0,0,0,2,1,1,1,2,1,1,1,2,0,0,0,2,0,1,1,0,0,1,1,0,0,1,1,0,2,2,2,2,0,0,0,0,0,0,0,0,2,1,1,2,2,1,1,2,0,1,1,0,0,1,1,0,2,2,2,2,2,2,2,2,0,0,2,2,0,0,1,1,0,0,1,1,0,0,2,2,0,0,2,2,1,1,2,2,1,1,2,2,0,0,2,2,0,0,0,0,0,0,0,0,0,0,0,0,2,1,1,2,0,0,0,2,0,0,0,1,0,0,0,2,0,0,0,1,0,2,2,2,1,2,2,2,0,2,2,2,1,2,2,2,0,1,0,1,2,2,2,2,2,2,2,2,2,2,2,2,0,1,1,1,2,0,1,1,2,2,0,1,2,2,2,0};
    static readonly byte[] Anchor2 = {15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,2,8,2,2,8,8,15,2,8,2,2,8,8,2,2,15,15,6,8,2,8,15,15,2,8,2,2,2,15,15,6,6,2,6,8,15,15,2,2,15,15,15,15,15,2,2,15};
    static readonly byte[] Anchor3a = {3,3,15,15,8,3,15,15,8,8,6,6,6,5,3,3,3,3,8,15,3,3,6,10,5,8,8,6,8,5,15,15,8,15,3,5,6,10,8,15,15,3,15,5,15,15,15,15,3,15,5,5,5,8,5,10,5,10,8,13,15,12,3,3};
    static readonly byte[] Anchor3b = {15,8,8,3,15,15,3,8,15,15,15,15,15,15,15,8,15,8,15,3,15,8,15,8,3,15,6,10,15,15,10,8,15,3,15,10,10,8,9,10,6,15,8,15,3,6,6,8,15,3,15,15,15,15,15,15,15,15,15,15,3,15,15,8};
}

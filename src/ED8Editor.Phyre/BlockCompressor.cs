namespace ED8Editor.Phyre;

/// <summary>
/// Encodes and decodes the block-compressed formats the game's textures use.
///
/// A block covers four pixels by four. DXT1 stores two colours and a two-bit
/// index per pixel, the four shades being the two endpoints and two blends of
/// them; DXT5 puts an eight-byte alpha block in front, built the same way with
/// two endpoints and three-bit indices. The endpoints here are the extremes of
/// the block, which is exact for a flat block and close for anything smooth —
/// and <see cref="DecodeBc3"/> is what lets the editor check that.
/// </summary>
public static class BlockCompressor
{
    private const int BlockSide = 4;

    /// <summary>DXT5: an alpha block then a colour block, sixteen bytes a block.</summary>
    public static byte[] EncodeBc3(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        var blocksX = Math.Max(1, (width + 3) / BlockSide);
        var blocksY = Math.Max(1, (height + 3) / BlockSide);
        var output = new byte[blocksX * blocksY * 16];
        var cursor = 0;
        Span<byte> block = stackalloc byte[BlockSide * BlockSide * 4];
        for (var blockY = 0; blockY < blocksY; blockY++)
        {
            for (var blockX = 0; blockX < blocksX; blockX++)
            {
                ReadBlock(rgba, width, height, blockX, blockY, block);
                EncodeAlphaBlock(block, output.AsSpan(cursor, 8));
                EncodeColorBlock(block, output.AsSpan(cursor + 8, 8));
                cursor += 16;
            }
        }
        return output;
    }

    /// <summary>DXT1: a colour block alone, eight bytes a block, no alpha.</summary>
    public static byte[] EncodeBc1(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        var blocksX = Math.Max(1, (width + 3) / BlockSide);
        var blocksY = Math.Max(1, (height + 3) / BlockSide);
        var output = new byte[blocksX * blocksY * 8];
        var cursor = 0;
        Span<byte> block = stackalloc byte[BlockSide * BlockSide * 4];
        for (var blockY = 0; blockY < blocksY; blockY++)
        {
            for (var blockX = 0; blockX < blocksX; blockX++)
            {
                ReadBlock(rgba, width, height, blockX, blockY, block);
                EncodeColorBlock(block, output.AsSpan(cursor, 8));
                cursor += 8;
            }
        }
        return output;
    }

    /// <summary>Reads a DXT5 image back, to check what the encoder wrote.</summary>
    public static byte[] DecodeBc3(byte[] blocks, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var rgba = new byte[width * height * 4];
        var blocksX = Math.Max(1, (width + 3) / BlockSide);
        var blocksY = Math.Max(1, (height + 3) / BlockSide);
        var cursor = 0;
        for (var blockY = 0; blockY < blocksY; blockY++)
        {
            for (var blockX = 0; blockX < blocksX; blockX++)
            {
                DecodeBlock(
                    blocks.AsSpan(cursor + 8, 8),
                    blocks.AsSpan(cursor, 8),
                    rgba,
                    width,
                    height,
                    blockX,
                    blockY);
                cursor += 16;
            }
        }
        return rgba;
    }

    private static void ReadBlock(
        byte[] rgba, int width, int height, int blockX, int blockY, Span<byte> block)
    {
        for (var y = 0; y < BlockSide; y++)
        {
            for (var x = 0; x < BlockSide; x++)
            {
                // A block that runs past the image repeats its last pixel.
                var sourceX = Math.Min(width - 1, blockX * BlockSide + x);
                var sourceY = Math.Min(height - 1, blockY * BlockSide + y);
                var source = (sourceY * width + sourceX) * 4;
                var target = (y * BlockSide + x) * 4;
                block[target] = rgba[source];
                block[target + 1] = rgba[source + 1];
                block[target + 2] = rgba[source + 2];
                block[target + 3] = rgba[source + 3];
            }
        }
    }

    private static void EncodeColorBlock(ReadOnlySpan<byte> block, Span<byte> output)
    {
        var lowest = new[] { 255, 255, 255 };
        var highest = new[] { 0, 0, 0 };
        for (var pixel = 0; pixel < BlockSide * BlockSide; pixel++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var value = block[pixel * 4 + channel];
                lowest[channel] = Math.Min(lowest[channel], value);
                highest[channel] = Math.Max(highest[channel], value);
            }
        }

        var color0 = Pack565(highest[0], highest[1], highest[2]);
        var color1 = Pack565(lowest[0], lowest[1], lowest[2]);
        // The four-shade mode is the one where the first endpoint is the larger.
        if (color0 < color1) (color0, color1) = (color1, color0);
        var palette = BuildPalette(color0, color1);

        var indices = 0u;
        for (var pixel = 0; pixel < BlockSide * BlockSide; pixel++)
        {
            var best = 0;
            var bestDistance = int.MaxValue;
            for (var entry = 0; entry < 4; entry++)
            {
                var distance = 0;
                for (var channel = 0; channel < 3; channel++)
                {
                    var difference = block[pixel * 4 + channel] - palette[entry * 3 + channel];
                    distance += difference * difference;
                }
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = entry;
            }
            indices |= (uint)best << (pixel * 2);
        }

        output[0] = (byte)(color0 & 0xFF);
        output[1] = (byte)(color0 >> 8);
        output[2] = (byte)(color1 & 0xFF);
        output[3] = (byte)(color1 >> 8);
        output[4] = (byte)(indices & 0xFF);
        output[5] = (byte)((indices >> 8) & 0xFF);
        output[6] = (byte)((indices >> 16) & 0xFF);
        output[7] = (byte)((indices >> 24) & 0xFF);
    }

    private static void EncodeAlphaBlock(ReadOnlySpan<byte> block, Span<byte> output)
    {
        var lowest = 255;
        var highest = 0;
        for (var pixel = 0; pixel < BlockSide * BlockSide; pixel++)
        {
            var alpha = block[pixel * 4 + 3];
            lowest = Math.Min(lowest, alpha);
            highest = Math.Max(highest, alpha);
        }
        output[0] = (byte)highest;
        output[1] = (byte)lowest;
        var shades = AlphaPalette(highest, lowest);

        ulong indices = 0;
        for (var pixel = 0; pixel < BlockSide * BlockSide; pixel++)
        {
            var alpha = block[pixel * 4 + 3];
            var best = 0;
            var bestDistance = int.MaxValue;
            for (var entry = 0; entry < 8; entry++)
            {
                var distance = Math.Abs(alpha - shades[entry]);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = entry;
            }
            indices |= (ulong)best << (pixel * 3);
        }
        for (var index = 0; index < 6; index++)
        {
            output[2 + index] = (byte)(indices >> (index * 8));
        }
    }

    private static void DecodeBlock(
        ReadOnlySpan<byte> color,
        ReadOnlySpan<byte> alpha,
        byte[] rgba,
        int width,
        int height,
        int blockX,
        int blockY)
    {
        var color0 = color[0] | (color[1] << 8);
        var color1 = color[2] | (color[3] << 8);
        var palette = BuildPalette(color0, color1);
        var indices = (uint)(color[4] | (color[5] << 8) | (color[6] << 16) | (color[7] << 24));
        var shades = AlphaPalette(alpha[0], alpha[1]);
        ulong alphaIndices = 0;
        for (var index = 0; index < 6; index++)
        {
            alphaIndices |= (ulong)alpha[2 + index] << (index * 8);
        }

        for (var pixel = 0; pixel < BlockSide * BlockSide; pixel++)
        {
            var x = blockX * BlockSide + pixel % BlockSide;
            var y = blockY * BlockSide + pixel / BlockSide;
            if (x >= width || y >= height) continue;
            var entry = (int)((indices >> (pixel * 2)) & 3);
            var target = (y * width + x) * 4;
            rgba[target] = (byte)palette[entry * 3];
            rgba[target + 1] = (byte)palette[entry * 3 + 1];
            rgba[target + 2] = (byte)palette[entry * 3 + 2];
            rgba[target + 3] = (byte)shades[(int)((alphaIndices >> (pixel * 3)) & 7)];
        }
    }

    /// <summary>The four shades of a colour block: both endpoints and two blends.</summary>
    private static int[] BuildPalette(int color0, int color1)
    {
        var palette = new int[12];
        Unpack565(color0, palette, 0);
        Unpack565(color1, palette, 3);
        for (var channel = 0; channel < 3; channel++)
        {
            palette[6 + channel] = (2 * palette[channel] + palette[3 + channel]) / 3;
            palette[9 + channel] = (palette[channel] + 2 * palette[3 + channel]) / 3;
        }
        return palette;
    }

    /// <summary>
    /// The eight alpha shades. With the larger endpoint first the block holds six
    /// blends; with the smaller first it holds four, plus 0 and 255.
    /// </summary>
    private static int[] AlphaPalette(int alpha0, int alpha1)
    {
        var shades = new int[8];
        shades[0] = alpha0;
        shades[1] = alpha1;
        if (alpha0 > alpha1)
        {
            for (var entry = 1; entry < 7; entry++)
            {
                shades[entry + 1] = ((7 - entry) * alpha0 + entry * alpha1) / 7;
            }
        }
        else
        {
            for (var entry = 1; entry < 5; entry++)
            {
                shades[entry + 1] = ((5 - entry) * alpha0 + entry * alpha1) / 5;
            }
            shades[6] = 0;
            shades[7] = 255;
        }
        return shades;
    }

    private static int Pack565(int red, int green, int blue)
        => ((red >> 3) << 11) | ((green >> 2) << 5) | (blue >> 3);

    private static void Unpack565(int packed, int[] target, int offset)
    {
        var red = (packed >> 11) & 0x1F;
        var green = (packed >> 5) & 0x3F;
        var blue = packed & 0x1F;
        // Repeat the high bits into the low ones, the way a decoder expands them.
        target[offset] = (red << 3) | (red >> 2);
        target[offset + 1] = (green << 2) | (green >> 4);
        target[offset + 2] = (blue << 3) | (blue >> 2);
    }
}

namespace RetroDumper.Core.Util;

public static class Checksums
{
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// SFC のチェックサム。全バイトの単純和の下位 16bit。
    /// ROM サイズが 2 のべき乗でない場合、実機は端数部分を
    /// 全体が 2 のべき乗になるまで繰り返して計算する。
    /// </summary>
    public static ushort SnesChecksum(ReadOnlySpan<byte> rom)
    {
        if (rom.Length == 0) return 0;

        int pow2 = 1;
        while (pow2 * 2 <= rom.Length) pow2 *= 2;

        if (pow2 == rom.Length)
            return SumBytes(rom);

        // 下位の 2 のべき乗部分と、残りを繰り返して埋めた部分の和。
        ushort baseSum = SumBytes(rom[..pow2]);
        int remainder = rom.Length - pow2;

        int repeat = pow2 / remainder;
        ushort remainderSum = SumBytes(rom[pow2..]);

        // 端数がさらに 2 のべき乗でないケースは再帰的に畳み込む。
        if (repeat * remainder != pow2)
            return (ushort)(baseSum + SnesChecksum(rom[pow2..]) * (pow2 / NextPow2(remainder)));

        return (ushort)(baseSum + remainderSum * repeat);
    }

    private static int NextPow2(int value)
    {
        int p = 1;
        while (p < value) p *= 2;
        return p;
    }

    private static ushort SumBytes(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        foreach (byte b in data) sum += b;
        return (ushort)(sum & 0xFFFF);
    }

    /// <summary>GBA ヘッダのチェックサム（オフセット 0xA0～0xBC の補数和）。</summary>
    public static byte GbaHeaderChecksum(ReadOnlySpan<byte> header)
    {
        int sum = 0;
        for (int i = 0xA0; i <= 0xBC; i++)
            sum -= header[i];
        return (byte)((sum - 0x19) & 0xFF);
    }

    /// <summary>GB ヘッダのチェックサム（オフセット 0x134～0x14C）。</summary>
    public static byte GbHeaderChecksum(ReadOnlySpan<byte> header)
    {
        int sum = 0;
        for (int i = 0x134; i <= 0x14C; i++)
            sum = sum - header[i] - 1;
        return (byte)(sum & 0xFF);
    }
}

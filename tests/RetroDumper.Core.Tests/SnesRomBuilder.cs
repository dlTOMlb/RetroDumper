using RetroDumper.Core.Snes;
using RetroDumper.Core.Util;

namespace RetroDumper.Core.Tests;

/// <summary>テスト用の合成 SFC ROM を組み立てる。</summary>
public static class SnesRomBuilder
{
    /// <summary>ヘッダが置かれる ROM オフセット。</summary>
    public static long HeaderOffset(SnesMapper mapper) => mapper switch
    {
        SnesMapper.LoRom or SnesMapper.Sa1 or SnesMapper.Sdd1 => 0x7FC0,
        SnesMapper.HiRom or SnesMapper.Spc7110 => 0xFFC0,
        SnesMapper.ExHiRom => 0x40FFC0,
        _ => 0x7FC0,
    };

    public static byte MapModeByte(SnesMapper mapper) => mapper switch
    {
        SnesMapper.LoRom => 0x20,
        SnesMapper.HiRom => 0x21,
        SnesMapper.Sdd1 => 0x22,
        SnesMapper.Sa1 => 0x23,
        SnesMapper.ExHiRom => 0x25,
        SnesMapper.Spc7110 => 0x2A,
        _ => 0x20,
    };

    public static byte CartTypeByte(SnesMapper mapper) => mapper switch
    {
        SnesMapper.Sa1 => 0x35,   // SA-1 + RAM + バッテリ
        SnesMapper.Sdd1 => 0x43,  // S-DD1
        _ => 0x02,                // ROM + RAM + バッテリ
    };

    /// <summary>
    /// 指定マッパー・容量の ROM を作る。
    /// 中身は「オフセットから導いた値」なので、1 バイトでもずれれば検出できる。
    /// </summary>
    public static byte[] Build(
        SnesMapper mapper,
        long size,
        string title = "TEST CARTRIDGE",
        long saveSize = 0,
        bool fixChecksum = true)
    {
        var rom = new byte[size];

        // オフセット依存のパターン。単純な連番だとバンクずれを見逃すので
        // 上位バイトも混ぜる。
        for (long i = 0; i < size; i++)
            rom[i] = (byte)((i ^ (i >> 8) ^ (i >> 16) ^ 0x5A) & 0xFF);

        long h = HeaderOffset(mapper);

        // タイトル 21 バイト（空きはスペース埋め）
        for (int i = 0; i < 21; i++)
            rom[h + i] = (byte)(i < title.Length ? title[i] : ' ');

        rom[h + 0x15] = MapModeByte(mapper);
        rom[h + 0x16] = CartTypeByte(mapper);
        rom[h + 0x17] = (byte)SizeCode(size);
        rom[h + 0x18] = (byte)(saveSize > 0 ? SizeCode(saveSize) : 0);
        rom[h + 0x19] = 0x00;  // 国コード
        rom[h + 0x1A] = 0x33;  // 拡張ヘッダあり
        rom[h + 0x1B] = 0x00;  // バージョン

        // リセットベクタ ($FFFC)。必ず $8000 以降を指す。
        rom[h + 0x3C] = 0x00;
        rom[h + 0x3D] = 0x80;

        SetChecksum(rom, h, fixChecksum);
        return rom;
    }

    /// <summary>ヘッダ 0x17 のサイズコード。1024 &lt;&lt; code バイト。</summary>
    private static int SizeCode(long bytes)
    {
        int code = 0;
        while ((1024L << code) < bytes) code++;
        return code;
    }

    /// <summary>
    /// チェックサム欄を埋める。
    ///
    /// 補数との XOR が 0xFFFF になるのは C の値によらず常に成立するので、
    /// ヘッダ位置の判定は <paramref name="fixChecksum"/> が false でも通る。
    /// true のときは実データの総和とも一致させる（2 のべき乗サイズのみ）。
    /// </summary>
    private static void SetChecksum(byte[] rom, long h, bool fixChecksum)
    {
        ushort checksum = 0;

        if (fixChecksum)
        {
            rom[h + 0x1C] = rom[h + 0x1D] = rom[h + 0x1E] = rom[h + 0x1F] = 0;

            // チェックサム欄 4 バイトを 0 にした状態の総和を S0 とすると、
            // 最終的な総和は S0 + (C の 2 バイト和) + (~C の 2 バイト和) = S0 + 0x1FE。
            // これが C に等しくなればよい。
            ushort s0 = Checksums.SnesChecksum(rom);
            checksum = (ushort)((s0 + 0x1FE) & 0xFFFF);
        }
        else
        {
            checksum = 0x1234;
        }

        ushort complement = (ushort)(~checksum & 0xFFFF);
        rom[h + 0x1C] = (byte)(complement & 0xFF);
        rom[h + 0x1D] = (byte)(complement >> 8);
        rom[h + 0x1E] = (byte)(checksum & 0xFF);
        rom[h + 0x1F] = (byte)(checksum >> 8);
    }
}

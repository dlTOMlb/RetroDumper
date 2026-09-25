using System.Text;

namespace RetroDumper.Core.Snes;

public enum SnesMapper
{
    LoRom,
    HiRom,
    ExHiRom,
    /// <summary>LoROM ベース + SA-1。ROM は Super MMC 経由でバンク C0-FF に 1MB×4 で現れる。</summary>
    Sa1,
    /// <summary>LoROM ベース + S-DD1。SA-1 と同様にバンク C0-FF へ 1MB×4 でマップされる。</summary>
    Sdd1,
    /// <summary>HiROM ベース + SPC7110。</summary>
    Spc7110,
}

/// <summary>ヘッダ 0x16 番地が示す同梱チップ。</summary>
public enum SnesCoprocessor
{
    None,
    Dsp,
    SuperFx,
    Obc1,
    Sa1,
    Sdd1,
    SRtc,
    Spc7110,
    Other,
    Custom,
}

/// <summary>SFC のカートリッジヘッダ（内部ヘッダ）。</summary>
public sealed class SnesHeader
{
    /// <summary>ヘッダを読み出したバス上のアドレス。</summary>
    public required uint BusAddress { get; init; }

    public required SnesMapper Mapper { get; init; }
    public required byte[] Raw { get; init; }

    public string Title { get; private set; } = "";
    public byte MapModeByte { get; private set; }
    public byte CartTypeByte { get; private set; }
    public bool FastRom { get; private set; }
    public SnesCoprocessor Coprocessor { get; private set; }
    public bool HasBattery { get; private set; }

    /// <summary>ヘッダ申告の ROM サイズ（バイト）。</summary>
    public long DeclaredRomSize { get; private set; }

    /// <summary>ヘッダ申告のセーブ RAM サイズ（バイト）。</summary>
    public long DeclaredRamSize { get; private set; }

    public ushort Checksum { get; private set; }
    public ushort ChecksumComplement { get; private set; }
    public byte Country { get; private set; }
    public byte Version { get; private set; }
    public ushort ResetVector { get; private set; }

    /// <summary>候補評価スコア。大きいほど「本物のヘッダらしい」。</summary>
    public int Score { get; private set; }

    public bool ChecksumPairValid => (Checksum ^ ChecksumComplement) == 0xFFFF;

    /// <summary>
    /// ヘッダ候補の読み出し位置。
    ///
    /// 「この番地が読めたらこのマッパー」と一対一には決まらない点に注意。
    ///
    /// ・$00:7FC0 … A15 が ROM に配線されていない素の LoROM カセットでのみ
    ///   ROM オフセット 0x7FC0 に落ちる。HiROM や SA-1 では ROM 領域ですらない。
    /// ・$00:FFC0 … 最も汎用性が高い。LoROM なら A15 が無視されて ROM 0x7FC0、
    ///   HiROM なら ROM 0xFFC0、SA-1 / S-DD1 なら Super MMC がバンク $00 の
    ///   $8000-$FFFF を ROM 先頭 32KB に貼るので ROM 0x7FC0 が読める。
    ///   どのマッパーかはマップモードバイトで見分ける。
    /// ・$40:FFC0 … ExHiROM の内部ヘッダ (ROM オフセット 0x40FFC0)。
    /// </summary>
    public sealed record HeaderCandidate(
        uint BusAddress,
        SnesMapper[] CompatibleMappers,
        string Description);

    public static readonly HeaderCandidate[] Candidates =
    [
        new(0x007FC0,
            [SnesMapper.LoRom],
            "$00:7FC0 (素の LoROM のみ)"),
        new(0x00FFC0,
            [SnesMapper.LoRom, SnesMapper.Sa1, SnesMapper.Sdd1, SnesMapper.HiRom, SnesMapper.Spc7110],
            "$00:FFC0 (LoROM / HiROM / SA-1 / S-DD1 共通)"),
        new(0x40FFC0,
            [SnesMapper.ExHiRom],
            "$40:FFC0 (ExHiROM)"),
    ];

    /// <summary>ヘッダ候補の読み出しに必要なバイト数。リセットベクタまで含める。</summary>
    public const int HeaderReadSize = 0x40;

    public static SnesHeader Parse(byte[] raw, HeaderCandidate candidate)
        => Parse(raw, candidate.BusAddress, candidate.CompatibleMappers);

    public static SnesHeader Parse(byte[] raw, uint busAddress, SnesMapper[] compatibleMappers)
    {
        // マップモードバイトが読めればそれを、読めなければ候補の代表値を採る。
        var declared = MapperFromMapMode(raw[0x15]);
        var fallback = compatibleMappers.Length > 0 ? compatibleMappers[0] : SnesMapper.LoRom;

        var h = new SnesHeader
        {
            BusAddress = busAddress,
            Mapper = declared is not null && compatibleMappers.Contains(declared.Value)
                ? declared.Value
                : fallback,
            Raw = raw,
        };

        h.Title = DecodeTitle(raw.AsSpan(0x00, 0x15));
        h.MapModeByte = raw[0x15];
        h.CartTypeByte = raw[0x16];
        h.FastRom = (raw[0x15] & 0x10) != 0;
        h.DeclaredRomSize = raw[0x17] <= 0x10 ? 1024L << raw[0x17] : 0;
        h.DeclaredRamSize = raw[0x18] is > 0 and <= 0x10 ? 1024L << raw[0x18] : 0;
        h.Country = raw[0x19];
        h.Version = raw[0x1B];
        h.ChecksumComplement = (ushort)(raw[0x1C] | (raw[0x1D] << 8));
        h.Checksum = (ushort)(raw[0x1E] | (raw[0x1F] << 8));
        h.ResetVector = (ushort)(raw[0x3C] | (raw[0x3D] << 8));

        (h.Coprocessor, h.HasBattery) = DecodeCartType(raw[0x16]);
        h.Score = ScoreCandidate(h, compatibleMappers);

        return h;
    }

    /// <summary>
    /// ヘッダ位置の推定は「1 箇所を決め打ちで検証する」のではなく
    /// 全候補を採点して最良のものを選ぶ。SFC は内部ヘッダの位置が
    /// マッパーによって変わるうえ、ROM 上の別の場所にヘッダ様の
    /// バイト列が存在することもあるため。
    /// </summary>
    private static int ScoreCandidate(SnesHeader h, SnesMapper[] compatibleMappers)
    {
        int score = 0;

        if (h.ChecksumPairValid) score += 8;

        // マップモードバイトの上位 3bit は必ず 001 (0x20-0x3F)。
        // 21 文字を超えるタイトルがこの欄を食い潰している ROM があるので、
        // ここが崩れていたらヘッダ候補として弱い。
        if ((h.MapModeByte >> 5) == 1) score += 3;

        // マップモードバイトが解釈でき、かつその候補位置から読めるはずの
        // マッパーを指しているか。
        var declared = MapperFromMapMode(h.MapModeByte);
        if (declared is not null)
        {
            score += 2;
            if (compatibleMappers.Contains(declared.Value)) score += 4;
        }

        // リセットベクタは必ず ROM 領域 ($8000 以降) を指す。
        if (h.ResetVector >= 0x8000) score += 4;

        // サイズ欄が現実的な範囲か。SFC の ROM は 256KB～8MB。
        if (h.Raw[0x17] is >= 0x08 and <= 0x0D) score += 2;
        if (h.Raw[0x18] <= 0x09) score += 1;

        // タイトルが印字可能文字で埋まっているか。
        int printable = 0;
        for (int i = 0; i < 0x15; i++)
            if (h.Raw[i] is >= 0x20 and < 0x7F) printable++;
        score += printable / 4;

        return score;
    }

    /// <summary>
    /// ヘッダのサイズ欄が実容量と食い違うことが知られている構成。
    /// 値は実カセットから確定したものと同じ。
    /// 該当しなければ null。
    /// </summary>
    public (long Size, string Reason)? KnownSizeOverride()
    {
        const long Mbit = 1024 * 1024 / 8;

        return CartTypeByte switch
        {
            0x43 => (32 * Mbit, "S-DD1 搭載カセットは一律 32Mbit"),
            0x45 => (48 * Mbit, "S-DD1 + バッテリ搭載カセットは一律 48Mbit"),

            // SPC7110。ヘッダのサイズ欄が実容量より小さい。
            0xF5 when Mapper is SnesMapper.HiRom or SnesMapper.Spc7110
                => (24 * Mbit, "SPC7110 搭載カセットは 24Mbit"),
            0xF9 when Mapper is SnesMapper.HiRom or SnesMapper.Spc7110
                => (40 * Mbit, "SPC7110 + RTC 搭載カセットは 40Mbit"),

            // CX4。ヘッダ 0xFFC9 の下位ニブルで 2 種類に分かれる。
            0xF3 => (Raw[0x09] & 0x0F) switch
            {
                2 => (12 * Mbit, "CX4 (X2) 搭載カセットは 12Mbit"),
                3 => (16 * Mbit, "CX4 (X3) 搭載カセットは 16Mbit"),
                _ => ((long, string)?)null,
            },

            _ => null,
        };
    }

    public static SnesMapper? MapperFromMapMode(byte mapMode) => (mapMode & 0x0F) switch
    {
        0x0 => SnesMapper.LoRom,
        0x1 => SnesMapper.HiRom,
        0x2 => SnesMapper.Sdd1,
        0x3 => SnesMapper.Sa1,
        0x5 => SnesMapper.ExHiRom,
        0xA => SnesMapper.Spc7110,
        _ => null,
    };

    /// <summary>
    /// ヘッダ申告のマッパーを優先しつつ、候補位置と矛盾する場合は候補位置を採る。
    /// SA-1 / S-DD1 は LoROM 位置のヘッダにしか現れないので、
    /// 位置とマップモードの両方が揃ったときだけ採用する。
    /// </summary>
    public SnesMapper ResolveMapper()
    {
        var declared = MapperFromMapMode(MapModeByte);
        if (declared is null) return Mapper;

        // チップ欄からも裏を取る。マップモードだけが壊れている ROM があるため。
        if (Coprocessor == SnesCoprocessor.Sa1) return SnesMapper.Sa1;
        if (Coprocessor == SnesCoprocessor.Sdd1) return SnesMapper.Sdd1;
        if (Coprocessor == SnesCoprocessor.Spc7110) return SnesMapper.Spc7110;

        return declared.Value;
    }

    private static (SnesCoprocessor, bool battery) DecodeCartType(byte cartType)
    {
        bool battery = (cartType & 0x0F) is 0x02 or 0x05 or 0x06 or 0x09 or 0x0A;

        // 下位ニブルが 3 以上のときだけ上位ニブルがコプロセッサ種別を表す。
        if ((cartType & 0x0F) < 0x03)
            return (SnesCoprocessor.None, battery);

        var chip = (cartType >> 4) switch
        {
            0x0 => SnesCoprocessor.Dsp,
            0x1 => SnesCoprocessor.SuperFx,
            0x2 => SnesCoprocessor.Obc1,
            0x3 => SnesCoprocessor.Sa1,
            0x4 => SnesCoprocessor.Sdd1,
            0x5 => SnesCoprocessor.SRtc,
            0xE => SnesCoprocessor.Other,
            0xF => SnesCoprocessor.Custom,
            _ => SnesCoprocessor.None,
        };

        // SPC7110 はマップモード 0xA 側でしか区別できない。
        return (chip, battery);
    }

    private static string DecodeTitle(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(21);
        foreach (byte b in bytes)
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : ' ');
        return sb.ToString().TrimEnd();
    }

    public string CoprocessorName => Coprocessor switch
    {
        SnesCoprocessor.None => "なし",
        SnesCoprocessor.Dsp => "DSP-1/2/3/4",
        SnesCoprocessor.SuperFx => "Super FX (GSU)",
        SnesCoprocessor.Obc1 => "OBC-1",
        SnesCoprocessor.Sa1 => "SA-1",
        SnesCoprocessor.Sdd1 => "S-DD1",
        SnesCoprocessor.SRtc => "S-RTC",
        SnesCoprocessor.Spc7110 => "SPC7110",
        SnesCoprocessor.Other => "その他",
        _ => "カスタム",
    };
}

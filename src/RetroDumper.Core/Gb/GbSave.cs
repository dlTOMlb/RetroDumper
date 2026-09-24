using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Gb;

/// <summary>
/// ゲームボーイ / ゲームボーイカラーの外部 RAM（セーブ）の読み書き。
///
/// **手順は MBC ごとに違う。**値も段取りも RetroFreakDumper の
/// Gb.SaveDataController 各実装に合わせてある。推測で共通化しない。
///
///   None / HuC1 / HuC3 / ポケットカメラ
///       RAM の有効化もバンク切り替えも行わず、$A000 から素直に読み書きする。
///       上限 8KB。
///
///   MBC1
///       先に $6000 へ 1 を書いて RAM バンク切り替えモードにする。
///       $00FF へ 0x0A で有効化し、$4000 でバンクを選び、$A000 から 8KB ずつ。
///       終わったら $00FF へ 0、$6000 へ 0 を書いて戻す。上限 32KB。
///
///   MBC2
///       RAM が本体内蔵の 512×4bit。専用 opcode を使う。バンク切り替えは無い。
///       上位 4bit は存在しないので、書く値は 0xF0 で埋める。上限 512B。
///
///   MBC3 / MBC5
///       $00FF へ 0x0A、$4000 でバンク、$A000 から 8KB ずつ。
///       MBC1 と違いモード切り替えは行わない。上限は 32KB / 128KB。
///
///   MBC6
///       バンク選択が $0400。4KB ずつ。
/// </summary>
public static class GbSave
{
    private const uint RamEnable = 0x00FF;
    private const uint ModeSelect = 0x6000;
    private const uint SaveBase = 0xA000;

    /// <summary>MBC ごとの段取り。参照実装の各コントローラに対応する。</summary>
    private sealed record Profile(
        string Name,
        bool EnablesRam,
        uint? BankSelect,
        int ChunkSize,
        long MaxSize,
        bool UsesMbc2Commands = false,
        bool UsesModeSelect = false);

    private static readonly Profile Plain =
        new("None", EnablesRam: false, BankSelect: null, ChunkSize: 0x2000, MaxSize: 0x2000);

    private static readonly Profile Mbc1 =
        new("MBC1", true, 0x4000, 0x2000, 0x8000, UsesModeSelect: true);

    private static readonly Profile Mbc2 =
        new("MBC2", true, null, 512, 512, UsesMbc2Commands: true);

    private static readonly Profile Mbc3 = new("MBC3", true, 0x4000, 0x2000, 0x8000);

    private static readonly Profile Mbc5 = new("MBC5", true, 0x4000, 0x2000, 0x20000);

    private static readonly Profile Mbc6 = new("MBC6", true, 0x0400, 0x1000, 0x8000);

    /// <summary>
    /// カートリッジ種別（ヘッダ 0x147）から段取りを決める。
    /// 対応が無ければ null。
    /// </summary>
    private static Profile? ProfileFor(byte cartType) => cartType switch
    {
        0x00 or 0x08 or 0x09 => Plain,                  // ROM / ROM+RAM
        >= 0x01 and <= 0x03 => Mbc1,
        0x05 or 0x06 => Mbc2,
        >= 0x0F and <= 0x13 => Mbc3,
        >= 0x19 and <= 0x1E => Mbc5,
        0x20 => Mbc6,
        0xFC or 0xFE or 0xFF => Plain,                  // ポケットカメラ / HuC3 / HuC1
        _ => null,
    };

    /// <summary>その種別で扱えるセーブの上限。分からなければ 0。</summary>
    public static long MaxSize(byte cartType) => ProfileFor(cartType)?.MaxSize ?? 0;

    /// <summary>MBC2 か。RAM が 4bit で専用 opcode を使う。</summary>
    public static bool IsMbc2(byte cartType) => ProfileFor(cartType) == Mbc2;

    public static byte[] Read(
        IRfcaLink link, byte cartType, long size,
        IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var profile = Require(cartType, size);
        var result = new byte[size];

        Access(link, profile, size, (offset, length) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            uint opcode = profile.UsesMbc2Commands
                ? RfcaOpcode.GameBoyMbc2ExRamRead
                : RfcaOpcode.GameBoyRead;

            link.Read(opcode, SaveBase, result.AsSpan((int)offset, length));

            progress?.Report(new DumpProgress($"セーブ読み出し ({profile.Name})", offset + length, size));
        });

        return result;
    }

    /// <summary>セーブを書き、読み戻して照合する。</summary>
    public static void Write(
        IRfcaLink link, byte cartType, byte[] data,
        IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var profile = Require(cartType, data.Length);
        byte[] payload = data;

        if (profile.UsesMbc2Commands)
        {
            // MBC2 の RAM は 4bit。上位は存在しないので埋めておく。
            // 埋めずに書くと、読み戻したときに 0xF0 が立って一致しない。
            payload = (byte[])data.Clone();

            for (int i = 0; i < payload.Length; i++) payload[i] |= 0xF0;
        }

        Access(link, profile, payload.Length, (offset, length) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            uint opcode = profile.UsesMbc2Commands
                ? RfcaOpcode.GameBoyMbc2ExRamWrite
                : RfcaOpcode.GameBoyWrite;

            link.WriteSaveMemory(
                CartridgeKind.GameBoy, opcode, SaveBase,
                payload.AsSpan((int)offset, length));

            progress?.Report(new DumpProgress(
                $"セーブ書き込み ({profile.Name})", offset + length, payload.Length));
        });

        progress?.Report(new DumpProgress("書き込んだ内容を照合中", 0, payload.Length));

        var readBack = Read(link, cartType, payload.Length, progress, cancellationToken);

        for (int i = 0; i < payload.Length; i++)
            if (readBack[i] != payload[i])
                throw new RfcaException(
                    $"照合に失敗しました。{i:X4} 番地は 0x{payload[i]:X2} を書いたはずですが " +
                    $"0x{readBack[i]:X2} が読めました。" +
                    "カートリッジのセーブデータが中途半端な状態になっている可能性があります。");
    }

    private static Profile Require(byte cartType, long size)
    {
        var profile = ProfileFor(cartType)
            ?? throw new RfcaException(
                $"カートリッジ種別 0x{cartType:X2} のセーブ手順が分かりません。");

        if (size <= 0)
            throw new RfcaException("このカートリッジにはセーブ用の外部 RAM がありません。");

        if (size > profile.MaxSize)
            throw new RfcaException(
                $"セーブが大きすぎます。{profile.Name} で扱えるのは " +
                $"{profile.MaxSize} バイトまでですが、{size} バイトを指定されました。");

        return profile;
    }

    /// <summary>
    /// 段取りに従ってバンクごとに処理し、触った設定は必ず元へ戻す。
    ///
    /// 外部 RAM を有効にしたまま放置すると、以降の操作が意図せず
    /// セーブを書き換えうる。途中で失敗しても戻すこと。
    /// </summary>
    private static void Access(
        IRfcaLink link, Profile profile, long size, Action<long, int> forEachChunk)
    {
        uint write = RfcaOpcode.GameBoyWrite;

        if (profile.UsesModeSelect)
            link.WriteBankRegister(CartridgeKind.GameBoy, write, ModeSelect, 0x01);

        if (profile.EnablesRam)
            link.WriteBankRegister(CartridgeKind.GameBoy, write, RamEnable, 0x0A);

        try
        {
            long done = 0;
            int bank = 0;

            while (done < size)
            {
                int length = (int)Math.Min(profile.ChunkSize, size - done);

                if (profile.BankSelect is uint select)
                    link.WriteBankRegister(CartridgeKind.GameBoy, write, select, (byte)bank);

                forEachChunk(done, length);

                done += length;
                bank++;
            }
        }
        finally
        {
            if (profile.EnablesRam)
                link.WriteBankRegister(CartridgeKind.GameBoy, write, RamEnable, 0x00);

            if (profile.UsesModeSelect)
                link.WriteBankRegister(CartridgeKind.GameBoy, write, ModeSelect, 0x00);
        }
    }
}

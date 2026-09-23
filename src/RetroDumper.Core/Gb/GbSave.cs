using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Gb;

/// <summary>
/// ゲームボーイの外部 RAM（セーブ）の読み書き。
///
/// 手順は MBC 共通で次のとおり。RetroFreakDumper の
/// Gb.SaveDataController 各実装に合わせた。
///
///   1. $0000-$1FFF に 0x0A を書いて外部 RAM を有効にする
///   2. $4000-$5FFF に RAM バンク番号を書く
///   3. $A000 から 8KB 読む／書く
///   4. 済んだら $0000-$1FFF に 0 を書いて無効に戻す
///
/// MBC1 だけは、先に $6000-$7FFF に 1 を書いて RAM バンク切り替えモードに
/// しておく必要がある。戻すときは 0 を書く。
///
/// MBC2 は例外で、RAM が本体に内蔵された 512×4bit のため専用 opcode を使う。
/// 上位 4bit は存在しないので、書き込む値は 0xF0 で埋めておく。
/// </summary>
public static class GbSave
{
    private const uint RamEnable = 0x0000;
    private const uint BankSelect = 0x4000;
    private const uint ModeSelect = 0x6000;
    private const uint SaveBase = 0xA000;
    private const int BankSize = 0x2000;
    private const int Mbc2Size = 512;

    /// <summary>MBC2 か。カートリッジ種別 0x05 / 0x06。</summary>
    public static bool IsMbc2(byte cartType) => cartType is 0x05 or 0x06;

    /// <summary>MBC1 か。$6000 のモード切り替えが要る。</summary>
    public static bool IsMbc1(byte cartType) => cartType is >= 0x01 and <= 0x03;

    public static byte[] Read(
        IRfcaLink link, byte cartType, long size,
        IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (size <= 0)
            throw new RfcaException("このカートリッジにはセーブ用の外部 RAM がありません。");

        var result = new byte[size];

        Access(link, cartType, size, (bank, offset, length) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            uint opcode = IsMbc2(cartType)
                ? RfcaOpcode.GameBoyMbc2ExRamRead
                : RfcaOpcode.GameBoyRead;

            link.Read(opcode, SaveBase, result.AsSpan((int)offset, length));

            progress?.Report(new DumpProgress("セーブ読み出し", offset + length, size));
        });

        return result;
    }

    /// <summary>セーブを書き、読み戻して照合する。</summary>
    public static void Write(
        IRfcaLink link, byte cartType, byte[] data,
        IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (data.Length == 0)
            throw new RfcaException("書き込む内容がありません。");

        byte[] payload = data;

        if (IsMbc2(cartType))
        {
            // MBC2 の RAM は 4bit。上位は存在しないので埋めておく。
            // 埋めずに書くと、読み戻したときに 0xF0 が立って一致しない。
            payload = (byte[])data.Clone();

            for (int i = 0; i < payload.Length; i++) payload[i] |= 0xF0;
        }

        Access(link, cartType, payload.Length, (bank, offset, length) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            uint opcode = IsMbc2(cartType)
                ? RfcaOpcode.GameBoyMbc2ExRamWrite
                : RfcaOpcode.GameBoyWrite;

            link.WriteSaveMemory(
                CartridgeKind.GameBoy, opcode, SaveBase,
                payload.AsSpan((int)offset, length));

            progress?.Report(new DumpProgress("セーブ書き込み", offset + length, payload.Length));
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

    /// <summary>
    /// 外部 RAM を有効にし、バンクごとに処理し、必ず無効に戻す。
    ///
    /// 有効のまま放置すると、以降の操作が意図せずセーブを書き換えうる。
    /// 途中で失敗しても戻すこと。
    /// </summary>
    private static void Access(
        IRfcaLink link, byte cartType, long size, Action<int, long, int> forEachBank)
    {
        uint write = RfcaOpcode.GameBoyWrite;

        if (IsMbc1(cartType))
            link.WriteBankRegister(CartridgeKind.GameBoy, write, ModeSelect, 0x01);

        link.WriteBankRegister(CartridgeKind.GameBoy, write, RamEnable, 0x0A);

        try
        {
            int bankSize = IsMbc2(cartType) ? Mbc2Size : BankSize;
            long done = 0;
            int bank = 0;

            while (done < size)
            {
                int length = (int)Math.Min(bankSize, size - done);

                // MBC2 は 1 バンクしかないので切り替えない。
                if (!IsMbc2(cartType))
                    link.WriteBankRegister(CartridgeKind.GameBoy, write, BankSelect, (byte)bank);

                forEachBank(bank, done, length);

                done += length;
                bank++;
            }
        }
        finally
        {
            link.WriteBankRegister(CartridgeKind.GameBoy, write, RamEnable, 0x00);

            if (IsMbc1(cartType))
                link.WriteBankRegister(CartridgeKind.GameBoy, write, ModeSelect, 0x00);
        }
    }
}

using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Tests;

/// <summary>
/// GBA のセーブ装置のシミュレータ。
///
/// セーブ専用の opcode だけを受け付け、それ以外の書き込みは
/// 実機と同じく届かないものとして扱う。
/// ROM 側へ書けてしまわないことを確かめるために使う。
/// </summary>
public sealed class FakeGbaSaveCartridge(int saveSize) : IRfcaLink
{
    private readonly byte[] _save = new byte[saveSize];

    public bool AllowWrites { get; set; }

    public bool AllowSaveWrites { get; set; }

    /// <summary>検証用: 読み戻しで 1 バイトだけ化けさせる。照合の確認に使う。</summary>
    public int CorruptAt { get; set; } = -1;

    /// <summary>
    /// 検証用: 読むたびに値が変わる位置。実機で起きた読み出しの揺れを模す。
    /// </summary>
    public int UnstableAt { get; set; } = -1;

    private byte _jitter;

    /// <summary>
    /// 4kbit の EEPROM として振る舞うか。
    ///
    /// アダプタは EEPROM の書き込みで常に 64kbit 用の 14 ビットアドレスを送る。
    /// 4kbit の石はアドレスを 6 ビットしか見ないため、
    /// 余った下位 8 ビットがデータの 1 バイト目として取り込まれる。
    /// 2026-09-24 に実機で確かめた挙動をそのまま再現する。
    /// </summary>
    public bool Eeprom4kQuirk { get; init; }

    public byte[] Snapshot() => _save.ToArray();

    public void Preset(byte[] data) => data.CopyTo(_save, 0);

    public RfcaStatus GetStatus() =>
        new([0, 0, 0, 0, 4, 0, 0, 0, (byte)CartridgeKind.GameBoyAdvance, 0, 0, 0]);

    public RfcaStatus GetStatusWithRetry(int attempts = 3) => GetStatus();

    public CartridgeKind DetectCartridge() => CartridgeKind.GameBoyAdvance;

    public byte[] SendControl(uint opcode, uint address = 0, uint size = 0,
                              uint parameter = 0, uint headerField = 0x08) => new byte[8];

    /// <summary>容量に見合った、対応表にある石の ID を返す。</summary>
    /// <summary>
    /// ID の読み出しを何回に 1 回だけ成功させるか。
    /// 実機では 2 バイト目が落ち、メーカー番号の繰り返しが返ることがある。
    /// </summary>
    public int FlashIdGoodEvery { get; set; } = 1;

    /// <summary>検証用: ID を何回読まれたか。</summary>
    public int FlashIdReads { get; private set; }

    public int ReadGbaFlashId()
    {
        FlashIdReads++;

        int good = saveSize >= 131072 ? 0x1362 : 0xD4BF;

        if (FlashIdGoodEvery <= 1 || FlashIdReads % FlashIdGoodEvery == 0) return good;

        // 取りこぼし。2 バイト目が 1 バイト目（メーカー番号）の繰り返しになる。
        int maker = good & 0xFF;

        return (maker << 8) | maker;
    }

    public void WriteSaveMemory(CartridgeKind kind, uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (!AllowSaveWrites)
            throw new RfcaWriteBlockedException("セーブデータの書き込みが許可されていません");

        if (!SaveMemory.IsSaveWrite(kind, opcode, address))
            throw new RfcaWriteBlockedException(
                $"opcode 0x{opcode:X2} はセーブデータの領域ではありません");

        if (Eeprom4kQuirk && opcode == RfcaOpcode.GbaEepromWrite)
        {
            WriteAsEeprom4k(address, data);
            return;
        }

        data.CopyTo(_save.AsSpan((int)address));
    }

    /// <summary>
    /// 実機の 4kbit EEPROM 書き込みを再現する。
    ///
    /// アダプタは要求アドレスを 8 で割った値を 14 ビットのアドレス欄に載せる。
    /// 石はその上位 6 ビットだけをブロック番号として使い、
    /// 残り 8 ビットを 64 ビットデータの先頭として取り込む。
    /// 続けて本体のデータが入り、64 ビットを超えた分は落ちる。
    /// </summary>
    private void WriteAsEeprom4k(uint address, ReadOnlySpan<byte> data)
    {
        uint field = address / 8;
        int block = (int)(field >> 8) & 0x3F;
        byte leading = (byte)(field & 0xFF);

        Span<byte> stored = stackalloc byte[8];
        stored[0] = leading;

        for (int i = 1; i < 8 && i - 1 < data.Length; i++)
            stored[i] = data[i - 1];

        stored.CopyTo(_save.AsSpan(block * 8, 8));
    }

    public void WriteBankRegister(CartridgeKind kind, uint opcode, uint address, byte value,
                                  uint headerField = 0x08)
        => throw new RfcaWriteBlockedException("GBA にバンクレジスタはありません");

    public byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08)
    {
        var buffer = new byte[size];
        Read(opcode, address, buffer.AsSpan(), headerField);
        return buffer;
    }

    public void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08)
    {
        if (!SaveMemory.IsDedicatedSaveOpcode(CartridgeKind.GameBoyAdvance, opcode))
            throw new RfcaNakException($"未対応 opcode 0x{opcode:X2}", new byte[8]);

        _save.AsSpan((int)address, destination.Length).CopyTo(destination);

        if (CorruptAt >= address && CorruptAt < address + destination.Length)
            destination[(int)(CorruptAt - address)] ^= 0xFF;

        if (UnstableAt >= address && UnstableAt < address + destination.Length)
            destination[(int)(UnstableAt - address)] ^= _jitter++ % 2 == 0 ? (byte)0x00 : (byte)0xFF;
    }

    public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
        => throw new RfcaWriteBlockedException("この経路では書き込まない");

    public void WriteByte(uint opcode, uint address, byte value)
        => Write(opcode, address, stackalloc byte[] { value });

    public void CompletePendingWrite(int size, byte filler = 0xFF) { }
}

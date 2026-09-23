using RetroDumper.Core.Snes;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Tests;

/// <summary>
/// SFC カートリッジのバスデコードをシミュレートする。
///
/// 吸い出し側の「ROM オフセット → バスアドレス」変換と、
/// ここに書いた「バスアドレス → ROM オフセット」変換は互いに逆関数のはず。
/// 吸い出し結果が元 ROM と一致すれば、マッピングが正しいことになる。
///
/// ROM が応答しない番地は 0xFF を返す（オープンバス相当）。
/// </summary>
public sealed class FakeSnesCartridge : IRfcaLink
{
    private readonly byte[] _rom;
    private readonly SnesMapper _mapper;

    /// <summary>SA-1 / S-DD1 の Super MMC バンクレジスタ。電源投入時は 0,1,2,3。</summary>
    private readonly byte[] _mmc = [0, 1, 2, 3];

    /// <summary>検証用: 受け取った書き込みの記録。</summary>
    public List<(uint Opcode, uint Address, byte[] Data)> Writes { get; } = [];

    /// <summary>検証用: 読み出しリクエストの回数。</summary>
    public int ReadCount { get; private set; }

    public FakeSnesCartridge(byte[] rom, SnesMapper mapper)
    {
        _rom = rom;
        _mapper = mapper;
    }

    /// <summary>書き込み許可。既定は実機と同じく false（書き込み禁止）。</summary>
    public bool AllowWrites { get; set; }

    public RfcaStatus GetStatus() =>
        new([0, 0, 0, 0, 0, 0, 0, 0, (byte)CartridgeKind.SuperFamicom, 0, 0, 0]);

    public byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08)
    {
        var buffer = new byte[size];
        Read(opcode, address, buffer, headerField);
        return buffer;
    }

    public void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08)
    {
        if (opcode != RfcaOpcode.SnesRead)
            throw new RfcaNakException($"未対応の opcode 0x{opcode:X2}", new byte[8]);

        ReadCount++;

        for (int i = 0; i < destination.Length; i++)
        {
            uint bus = AdvanceWithinBank(address, i);
            long offset = Decode(bus);
            destination[i] = offset >= 0 && offset < _rom.Length ? _rom[offset] : (byte)0xFF;
        }
    }

    public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        // 実機と同じ順序で弾く。保護が効いていればバスには何も出ない。
        if (!AllowWrites)
            throw new RfcaWriteBlockedException(
                $"書き込み保護: opcode 0x{opcode:X2} addr 0x{address:X6}");

        if (opcode != RfcaOpcode.SnesWrite)
            throw new RfcaNakException($"未対応の opcode 0x{opcode:X2}", new byte[8]);

        Writes.Add((opcode, address, data.ToArray()));

        // SA-1 Super MMC ($2220-$2223) / S-DD1 ($4804-$4807)
        for (int i = 0; i < data.Length; i++)
        {
            uint target = address + (uint)i;
            uint low = target & 0xFFFF;

            if (_mapper == SnesMapper.Sa1 && low is >= 0x2220 and <= 0x2223)
                _mmc[low - 0x2220] = (byte)(data[i] & 0x07);
            else if (_mapper == SnesMapper.Sdd1 && low is >= 0x4804 and <= 0x4807)
                _mmc[low - 0x4804] = (byte)(data[i] & 0x07);
        }
    }

    public void WriteByte(uint opcode, uint address, byte value)
        => Write(opcode, address, stackalloc byte[] { value });

    /// <summary>検証用: 受け取った制御コマンド。</summary>
    public List<(uint Opcode, uint Parameter)> ControlCommands { get; } = [];

    public byte[] SendControl(uint opcode, uint address = 0, uint size = 0, uint parameter = 0, uint headerField = 0x08)
    {
        ControlCommands.Add((opcode, parameter));
        return new byte[8];
    }

    public void CompletePendingWrite(int size, byte filler = 0xFF) { }

    /// <summary>
    /// 連続読み出し中はバンク内でアドレスが進む。バンク境界はまたがない
    /// （実機のアドレスカウンタは 16bit なので折り返す）。
    /// </summary>
    private static uint AdvanceWithinBank(uint baseAddress, int delta)
    {
        uint bank = baseAddress >> 16;
        uint offset = (baseAddress + (uint)delta) & 0xFFFF;
        return (bank << 16) | offset;
    }

    /// <summary>バスアドレス → ROM オフセット。応答しない番地は -1。</summary>
    private long Decode(uint bus)
    {
        uint bank = (bus >> 16) & 0xFF;
        uint off = bus & 0xFFFF;

        return _mapper switch
        {
            SnesMapper.LoRom => DecodeLoRom(bank, off),
            SnesMapper.HiRom => DecodeHiRom(bank, off),
            SnesMapper.ExHiRom => DecodeExHiRom(bank, off),
            SnesMapper.Sa1 or SnesMapper.Sdd1 => DecodeMmc(bank, off),
            _ => -1,
        };
    }

    /// <summary>
    /// LoROM: A15 が ROM に配線されていないので $8000 未満でも同じデータが出る。
    /// これが $00:7FC0 でヘッダを読める理由であり、HiROM との判別材料でもある。
    /// バンク $7E/$7F は WRAM なので応答しない。
    /// </summary>
    private long DecodeLoRom(uint bank, uint off)
    {
        if (bank is 0x7E or 0x7F) return -1;
        return (bank & 0x7F) * 0x8000L + (off & 0x7FFF);
    }

    /// <summary>
    /// HiROM: バンク $40-$7D と $C0-$FF が ROM。
    /// バンク $00-$3F / $80-$BF は $8000 以降のみ ROM に落ちる。
    /// </summary>
    private long DecodeHiRom(uint bank, uint off)
    {
        if (bank is 0x7E or 0x7F) return -1;

        bool fullBank = bank is (>= 0x40 and <= 0x7D) or >= 0xC0;
        if (!fullBank && off < 0x8000) return -1;

        return (bank & 0x3F) * 0x10000L + off;
    }

    /// <summary>
    /// ExHiROM: A23 が半分を選ぶ。$80 以上のバンクが前半 4MB、
    /// $80 未満のバンクが後半。A22 (バンク bit6) は ROM のアドレス線ではない。
    /// </summary>
    private long DecodeExHiRom(uint bank, uint off)
    {
        if (bank is 0x7E or 0x7F) return -1;

        bool fullBank = bank is (>= 0x40 and <= 0x7D) or >= 0xC0;
        if (!fullBank && off < 0x8000) return -1;

        long half = bank >= 0x80 ? 0 : 0x400000;
        return half + (bank & 0x3F) * 0x10000L + off;
    }

    /// <summary>
    /// SA-1 / S-DD1: Super MMC が 1MB ブロック 4 本をバンク $C0-$FF に貼る。
    /// バンク $00-$1F の $8000-$FFFF はブロック 0 が LoROM 形式で見える
    /// （ここが $00:FFC0 でヘッダを読める理由）。
    /// </summary>
    private long DecodeMmc(uint bank, uint off)
    {
        if (bank >= 0xC0)
        {
            int slot = (int)((bank - 0xC0) >> 4);
            return _mmc[slot] * 0x100000L + (bank & 0x0F) * 0x10000L + off;
        }

        if (bank <= 0x1F && off >= 0x8000)
            return bank * 0x8000L + (off & 0x7FFF);

        return -1;
    }
}

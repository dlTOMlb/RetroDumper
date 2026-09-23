using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Tests;

/// <summary>
/// GBA / メガドライブのような、アドレスがそのまま ROM オフセットになる
/// カートリッジのシミュレータ。
///
/// ROM 終端より先の挙動を切り替えられる:
/// ミラー（アドレス折り返し）とオープンバス（アドレス値が読める）は
/// どちらも実在するので、容量判定ロジックの検証に両方要る。
/// </summary>
public sealed class FakeLinearCartridge : IRfcaLink
{
    public enum BeyondEnd
    {
        /// <summary>アドレスが折り返して先頭から読める。</summary>
        Mirror,

        /// <summary>A/D バスの残留値としてワードアドレスが読める（GBA）。</summary>
        OpenBus,

        /// <summary>アダプタがエラーを返す。</summary>
        Error,
    }

    private readonly byte[] _rom;
    private readonly uint _base;
    private readonly BeyondEnd _beyond;
    private readonly CartridgeKind _kind;

    public uint AcceptedOpcode { get; }

    public FakeLinearCartridge(
        byte[] rom,
        uint acceptedOpcode,
        uint baseAddress = 0,
        BeyondEnd beyond = BeyondEnd.Mirror,
        CartridgeKind kind = CartridgeKind.MegaDrive)
    {
        _rom = rom;
        AcceptedOpcode = acceptedOpcode;
        _base = baseAddress;
        _beyond = beyond;
        _kind = kind;
    }

    /// <summary>書き込み許可。既定は実機と同じく false（書き込み禁止）。</summary>
    public bool AllowWrites { get; set; }

    public RfcaStatus GetStatus() =>
        new([0, 0, 0, 0, 0, 0, 0, 0, (byte)_kind, 0, 0, 0]);

    public byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08)
    {
        var buffer = new byte[size];
        Read(opcode, address, buffer, headerField);
        return buffer;
    }

    /// <summary>
    /// 受け付けるフレーム 2 つ目のフィールド。null なら何でも通す。
    /// GBA は 0x00 でないと実機が拒否するので、その挙動を再現するときに設定する。
    /// </summary>
    public uint? RequiredHeaderField { get; init; }

    /// <summary>
    /// 受け付ける転送サイズ。null なら何でも通す。
    /// GBA は 512 固定なので、その挙動を再現するときに設定する。
    /// </summary>
    public int? RequiredSize { get; init; }

    /// <summary>検証用: 実際に発行されたリードの記録。</summary>
    public List<(uint Address, int Size, uint HeaderField)> Reads { get; } = [];

    /// <summary>
    /// 先頭から何回分の読み出しを化けさせるか。
    /// ウェイクアップ直後の 1 回目が化ける実機の挙動を再現する。
    /// </summary>
    public int CorruptFirstReads { get; init; }

    public void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08)
    {
        if (opcode != AcceptedOpcode)
            throw new RfcaNakException($"未対応の opcode 0x{opcode:X2}", new byte[8]);

        if (RequiredHeaderField is uint required && headerField != required)
            throw new RfcaNakException(
                $"ヘッダ値 0x{headerField:X2} は受け付けられません (要 0x{required:X2})",
                [0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0]);

        if (RequiredSize is int requiredSize && destination.Length != requiredSize)
            throw new RfcaNakException(
                $"サイズ {destination.Length} は受け付けられません (要 {requiredSize})",
                [0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0]);

        Reads.Add((address, destination.Length, headerField));

        // 実機で観測した「ウェイクアップ直後の読み出しが化ける」挙動の再現。
        if (Reads.Count <= CorruptFirstReads)
        {
            for (int i = 0; i < destination.Length; i++)
                destination[i] = (byte)(0xA5 ^ i);
            return;
        }

        if (address < _base)
            throw new RfcaNakException($"範囲外アドレス 0x{address:X8}", new byte[8]);

        long start = address - _base;

        if (start >= _rom.Length && _beyond == BeyondEnd.Error)
            throw new RfcaNakException($"ROM 範囲外 0x{address:X8}", new byte[8]);

        for (int i = 0; i < destination.Length; i++)
        {
            long offset = start + i;

            if (offset < _rom.Length)
            {
                destination[i] = _rom[offset];
            }
            else if (_beyond == BeyondEnd.Mirror)
            {
                destination[i] = _rom[offset % _rom.Length];
            }
            else
            {
                // ワードアドレスの下位 16bit がリトルエンディアンで読める。
                ushort word = (ushort)((offset >> 1) & 0xFFFF);
                destination[i] = (offset & 1) == 0 ? (byte)(word & 0xFF) : (byte)(word >> 8);
            }
        }
    }

    /// <summary>
    /// 検証用: バスに実際に出た書き込み。
    /// GBA の吸い出し経路ではここが 1 件も増えてはいけない。
    /// </summary>
    public List<(uint Opcode, uint Address, byte[] Data)> Writes { get; } = [];

    public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (!AllowWrites)
            throw new RfcaWriteBlockedException(
                $"書き込み保護: opcode 0x{opcode:X2} addr 0x{address:X8}");

        Writes.Add((opcode, address, data.ToArray()));
    }

    public void WriteByte(uint opcode, uint address, byte value)
        => Write(opcode, address, stackalloc byte[] { value });

    /// <summary>
    /// 検証用: 受け取った制御コマンド。
    ///
    /// ヘッダ値も記録する。以前はここを捨てていたため、
    /// 「0x2F が GBA に対して必ず拒否される 0x08 で送られている」という
    /// 不具合をテストで捕まえられなかった。
    /// </summary>
    public List<(uint Opcode, uint Parameter, uint HeaderField)> ControlCommands { get; } = [];

    public byte[] SendControl(uint opcode, uint address = 0, uint size = 0, uint parameter = 0, uint headerField = 0x08)
    {
        ControlCommands.Add((opcode, parameter, headerField));
        return new byte[8];
    }

    public void CompletePendingWrite(int size, byte filler = 0xFF)
    {
        if (!AllowWrites)
            throw new RfcaWriteBlockedException("書き込み保護: 保留中のライト手順を完了させません");

        Writes.Add((0, 0, new byte[size]));
    }
}

using RetroDumper.Core.Dumping;
using RetroDumper.Core.Gba;
using RetroDumper.Core.Probe;
using RetroDumper.Core.Snes;
using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// 「カートリッジに書き込まない」ことの検証。
///
/// GBA については、書き込み保護が無効（＝書き込み放題）の状態でも
/// 吸い出し経路が 1 バイトも書かないことまで確認する。
/// 保護は最後の砦であって、そもそも書く実装になっていないことが本筋なので。
/// </summary>
[Collection("GbaOpcodeState")]
public class WriteProtectionTests
{
    private const uint GbaOpcode = 0x33;

    private static DumpOptions Options() =>
        new() { ChunkSize = 1024, IncludeSaveRam = true, VerifyChecksum = false };

    private static byte[] BuildMinimalGbaRom(long size)
    {
        var rom = new byte[size];
        for (long i = 0; i < size; i++) rom[i] = (byte)((i ^ 0x5A) & 0xFF);

        rom[0x03] = 0xEA;
        Array.Clear(rom, 0x04, 0x9C);
        int target = GbaDumper.NintendoLogoChecksum;
        int p = 0x04;
        while (target > 0 && p < 0xA0)
        {
            int put = Math.Min(255, target);
            rom[p++] = (byte)put;
            target -= put;
        }
        rom[0xB2] = 0x96;
        return rom;
    }

    private static FakeLinearCartridge NewGbaCart(byte[] rom, bool allowWrites) =>
        new(rom, GbaOpcode, GbaDumper.DefaultRomBase,
            FakeLinearCartridge.BeyondEnd.Mirror, CartridgeKind.GameBoyAdvance)
        { AllowWrites = allowWrites };

    // ==================================================================
    // GBA は書き込まない
    // ==================================================================

    /// <summary>
    /// 書き込みが許可されていても、GBA の識別と吸い出しは
    /// バスに 1 バイトも書かない。
    /// </summary>
    [Fact]
    public void Gba_IdentifyAndDump_NeverWrite_EvenWhenWritesAreAllowed()
    {
        RfcaOpcode.GbaRead = GbaOpcode;
        var rom = BuildMinimalGbaRom(4 * 1024 * 1024);
        var cart = NewGbaCart(rom, allowWrites: true);

        var dumper = new GbaDumper();
        var options = Options();
        var info = dumper.Identify(cart, options);
        dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Empty(cart.Writes);
    }

    /// <summary>書き込み保護が有効でも、GBA の吸い出しは問題なく完走する。</summary>
    [Fact]
    public void Gba_Dump_SucceedsUnderWriteProtection()
    {
        RfcaOpcode.GbaRead = GbaOpcode;
        var rom = BuildMinimalGbaRom(2 * 1024 * 1024);
        var cart = NewGbaCart(rom, allowWrites: false);

        var dumper = new GbaDumper();
        var options = Options();
        var info = dumper.Identify(cart, options);
        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Equal(rom, result.Rom);
        Assert.Empty(cart.Writes);
    }

    // ==================================================================
    // opcode 探索は書き込む前に止まる
    // ==================================================================

    /// <summary>
    /// ライト系 opcode に当たったら、データ本体を送らずに例外で打ち切る。
    /// 書き込みが起きるのはデータ本体を送った瞬間なので、ここで止めれば
    /// カートリッジには何も書かれない。
    /// </summary>
    [Fact]
    public void Probe_StopsBeforeWriting_WhenOpcodeTurnsOutToBeWrite()
    {
        var cart = new WriteAckCartridge { AllowWrites = true };

        var ex = Assert.Throws<RfcaWriteOpcodePendingException>(
            () => OpcodeProbe.ProbeOne(cart, 0x40, 0x08000000, 16));

        Assert.Equal(0x40u, ex.Opcode);
        Assert.Empty(cart.Writes);
    }

    /// <summary>探索の全走査でも同じ。最初のライト系で打ち切る。</summary>
    [Fact]
    public void Probe_Scan_AbortsAtFirstWriteOpcode()
    {
        var cart = new WriteAckCartridge { AllowWrites = true };

        Assert.Throws<RfcaWriteOpcodePendingException>(
            () => OpcodeProbe.Scan(cart, 0x08000000, 16, 0x40, 0x50));

        Assert.Empty(cart.Writes);
    }

    /// <summary>
    /// 明示的に復帰を許可したときだけ、同期回復のための書き込みが起きる。
    /// 既定ではこの経路に入らない。
    /// </summary>
    [Fact]
    public void Probe_WritesOnlyWhenRecoveryIsExplicitlyAllowed()
    {
        var cart = new WriteAckCartridge { AllowWrites = true };

        var hit = OpcodeProbe.ProbeOne(cart, 0x40, 0x08000000, 16, allowRecoveryWrite: true);

        Assert.Equal(ProbeOutcome.WriteAck, hit.Outcome);
        Assert.Single(cart.Writes);
    }

    // ==================================================================
    // トランスポート層の遮断
    // ==================================================================

    [Fact]
    public void Link_BlocksWritesByDefault()
    {
        var cart = new FakeSnesCartridge(new byte[1024], SnesMapper.LoRom);

        Assert.False(cart.AllowWrites);
        Assert.Throws<RfcaWriteBlockedException>(() => cart.WriteByte(RfcaOpcode.SnesWrite, 0x2220, 0));
        Assert.Empty(cart.Writes);
    }

    /// <summary>
    /// SA-1 の 4MB 吸い出しは書き込み保護下でも完走する。
    /// 市販の SA-1 カセットはすべてこの経路に乗る。
    /// </summary>
    [Fact]
    public void Sa1_FourMegabyteDump_SucceedsUnderWriteProtection()
    {
        var rom = SnesRomBuilder.Build(SnesMapper.Sa1, 4 * 1024 * 1024);
        var cart = new FakeSnesCartridge(rom, SnesMapper.Sa1) { AllowWrites = false };

        var dumper = new SnesDumper();
        var options = new DumpOptions { ChunkSize = 1024, IncludeSaveRam = false };
        var info = dumper.Identify(cart, options);
        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Equal(rom, result.Rom);
        Assert.Empty(cart.Writes);
    }

    /// <summary>MMC の明示初期化は書き込みを伴うので、保護下では中止される。</summary>
    [Fact]
    public void Sa1_ForcedMmcInit_IsBlockedUnderWriteProtection()
    {
        var rom = SnesRomBuilder.Build(SnesMapper.Sa1, 4 * 1024 * 1024);
        var cart = new FakeSnesCartridge(rom, SnesMapper.Sa1) { AllowWrites = false };

        var dumper = new SnesDumper();
        var options = new DumpOptions { ChunkSize = 1024, IncludeSaveRam = false, ForceMmcInit = true };
        var info = dumper.Identify(cart, options);

        Assert.Throws<RfcaWriteBlockedException>(
            () => dumper.Dump(cart, info, options, null, CancellationToken.None));
        Assert.Empty(cart.Writes);
    }

    /// <summary>リード要求をすべてライト ACK で返すアダプタのふるまい。</summary>
    private sealed class WriteAckCartridge : IRfcaLink
    {
        public bool AllowWrites { get; set; }

    public bool AllowSaveWrites { get; set; }

    /// <summary>検証用: セーブ領域への書き込み。</summary>
    public List<(uint Opcode, uint Address, byte[] Data)> SaveWrites { get; } = [];

    public void WriteSaveMemory(CartridgeKind kind, uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (!AllowSaveWrites)
            throw new RfcaWriteBlockedException("セーブデータの書き込みが許可されていません");

        if (!SaveMemory.IsSaveWrite(kind, opcode, address))
            throw new RfcaWriteBlockedException(
                $"opcode 0x{opcode:X2} アドレス 0x{address:X6} はセーブデータの領域ではありません");

        SaveWrites.Add((opcode, address, data.ToArray()));
    }
        public List<(uint Opcode, uint Address, byte[] Data)> Writes { get; } = [];

        public RfcaStatus GetStatus() => new([0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0, 0, 0]);

        public byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08)
        {
            // ACK が 8 バイトのゼロ ＝ ライト要求として受理された合図。
            throw new RfcaNakException("ライト ACK", new byte[8]);
        }

        public void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08)
            => throw new RfcaNakException("ライト ACK", new byte[8]);

        public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
            => Writes.Add((opcode, address, data.ToArray()));

        public void WriteByte(uint opcode, uint address, byte value)
            => Write(opcode, address, stackalloc byte[] { value });

        public void WriteBankRegister(CartridgeKind kind, uint opcode, uint address, byte value,
                                      uint headerField = 0x08)
            => throw new RfcaWriteBlockedException("このシミュレータはバンク切り替えを扱いません");

        public byte[] SendControl(uint opcode, uint address = 0, uint size = 0, uint parameter = 0, uint headerField = 0x08)
            => new byte[8];

        public void CompletePendingWrite(int size, byte filler = 0xFF)
            => Writes.Add((0, 0, new byte[size]));
    }
}

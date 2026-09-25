using RetroDumper.Core.Gb;
using RetroDumper.Core.Snes;
using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// SFC のセーブ書き込みの宛先が、保護側で認められていること。
///
/// 窓の定義（SnesAddressMap.SramLayout）と、通してよい範囲の定義
/// （SaveMemory）は別の場所にある。片方だけ直すと、
/// 正しい宛先なのに弾かれる、あるいは弾くべき宛先が通る。
/// 両者が食い違っていないことをここで縛る。
/// </summary>
public sealed class SnesSaveGateTests
{
    private static readonly SnesMapper[] Mappers =
    [
        SnesMapper.LoRom, SnesMapper.HiRom, SnesMapper.ExHiRom,
        SnesMapper.Sa1, SnesMapper.Sdd1, SnesMapper.Spc7110,
    ];

    [Theory]
    [MemberData(nameof(AllMappers))]
    public void 書き込み窓のすべてが保護を通る(SnesMapper mapper)
    {
        var layout = SnesAddressMap.SramLayout(mapper)!.Value;

        for (long offset = 0; offset < layout.MaxSize; offset += layout.BytesPerBank)
        {
            uint address = SnesAddressMap.SramBusAddress(layout, offset, forWrite: true);

            Assert.True(
                SaveMemory.IsSaveWrite(CartridgeKind.SuperFamicom, layout.WriteOpcode, address),
                $"{mapper} のオフセット 0x{offset:X} (アドレス 0x{address:X6}) が弾かれました");
        }
    }

    /// <summary>窓の最後のバイトも通ること。境界の取り違えを防ぐ。</summary>
    [Theory]
    [MemberData(nameof(AllMappers))]
    public void 窓の末尾も保護を通る(SnesMapper mapper)
    {
        var layout = SnesAddressMap.SramLayout(mapper)!.Value;

        uint address = SnesAddressMap.SramBusAddress(
            layout, layout.MaxSize - 1, forWrite: true);

        Assert.True(
            SaveMemory.IsSaveWrite(CartridgeKind.SuperFamicom, layout.WriteOpcode, address));
    }

    /// <summary>ROM 領域は通さないこと。</summary>
    [Theory]
    [InlineData(0x008000u)]
    [InlineData(0x0F0000u)]
    [InlineData(0x808000u)]
    [InlineData(0xC00000u)]
    public void ROM領域は通さない(uint address)
    {
        Assert.False(SaveMemory.IsSaveWrite(CartridgeKind.SuperFamicom, RfcaOpcode.SnesWrite, address));
        Assert.False(SaveMemory.IsSaveWrite(CartridgeKind.SuperFamicom, RfcaOpcode.SnesExWrite, address));
    }

    /// <summary>本体側の WRAM（バンク $7E-$7F）は通さない。</summary>
    [Theory]
    [InlineData(0x7E0000u)]
    [InlineData(0x7F0000u)]
    public void 本体のWRAMは通さない(uint address)
        => Assert.False(
            SaveMemory.IsSaveWrite(CartridgeKind.SuperFamicom, RfcaOpcode.SnesWrite, address));

    public static TheoryData<SnesMapper> AllMappers()
    {
        var data = new TheoryData<SnesMapper>();
        foreach (var mapper in Mappers) data.Add(mapper);
        return data;
    }
}

/// <summary>
/// ゲームボーイのセーブ読み書き。
///
/// 外部 RAM は有効にしないと読めない。有効のまま放置すると、
/// 以降の操作が意図せずセーブを書き換えうるので、必ず戻すこと。
/// </summary>
public sealed class GbSaveTests
{
    private const byte Mbc3WithRam = 0x13;
    private const byte Mbc1WithRam = 0x03;
    private const byte Mbc2WithRam = 0x06;

    private static byte[] Pattern(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(i * 37 + (i >> 8) + 1);
        return data;
    }

    [Fact]
    public void 書き込んだ内容がそのまま読み戻せる()
    {
        var cart = new FakeGbSaveCartridge(0x8000, Mbc3WithRam) { AllowSaveWrites = true };
        var data = Pattern(0x8000);

        GbSave.Write(cart, Mbc3WithRam, data);

        Assert.Equal(data, cart.Snapshot());
        Assert.Equal(data, GbSave.Read(cart, Mbc3WithRam, 0x8000));
    }

    /// <summary>終わったら外部 RAM を必ず無効に戻すこと。</summary>
    [Fact]
    public void 終了時に外部RAMを無効へ戻す()
    {
        var cart = new FakeGbSaveCartridge(0x2000, Mbc3WithRam) { AllowSaveWrites = true };

        GbSave.Write(cart, Mbc3WithRam, Pattern(0x2000));

        Assert.False(cart.RamLeftEnabled);
    }

    /// <summary>失敗した場合でも戻すこと。有効のまま放置しない。</summary>
    [Fact]
    public void 失敗しても外部RAMを無効へ戻す()
    {
        var cart = new FakeGbSaveCartridge(0x2000, Mbc3WithRam);   // 許可していない

        Assert.Throws<RfcaWriteBlockedException>(
            () => GbSave.Write(cart, Mbc3WithRam, Pattern(0x2000)));

        Assert.False(cart.RamLeftEnabled);
    }

    /// <summary>MBC1 は RAM バンク切り替えモードにしてから触り、最後に戻す。</summary>
    [Fact]
    public void MBC1はモードを切り替えて戻す()
    {
        var cart = new FakeGbSaveCartridge(0x2000, Mbc1WithRam) { AllowSaveWrites = true };

        GbSave.Read(cart, Mbc1WithRam, 0x2000);

        Assert.Equal([(byte)0x01, (byte)0x00], cart.ModeWrites);
    }

    /// <summary>MBC3 はモード切り替えを持たないので書かないこと。</summary>
    [Fact]
    public void MBC3はモードを切り替えない()
    {
        var cart = new FakeGbSaveCartridge(0x2000, Mbc3WithRam) { AllowSaveWrites = true };

        GbSave.Read(cart, Mbc3WithRam, 0x2000);

        Assert.Empty(cart.ModeWrites);
    }

    /// <summary>
    /// MBC2 の RAM は 4bit しかない。上位を埋めずに書くと、
    /// 読み戻したときに 0xF0 が立って照合に失敗する。
    /// </summary>
    [Fact]
    public void MBC2は上位4bitを埋めて書く()
    {
        var cart = new FakeGbSaveCartridge(512, Mbc2WithRam) { AllowSaveWrites = true };
        var data = new byte[512];

        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0x0F);

        GbSave.Write(cart, Mbc2WithRam, data);

        Assert.All(cart.Snapshot(), b => Assert.Equal(0xF0, b & 0xF0));
    }

    /// <summary>
    /// **MBC2 のセーブ容量はヘッダから読めない。**
    ///
    /// MBC2 の RAM は 512×4bit で MBC2 チップに内蔵されており、外部 RAM ではない。
    /// そのためヘッダの RAM 容量欄 (0x149) は 0 になる。素直に読むと
    /// 「セーブが無い」と判断して吸い出せなくなる。
    /// 参照実装も同じ場所で 512 を決め打ちしている。
    /// </summary>
    [Theory]
    [InlineData((byte)0x05)]   // MBC2
    [InlineData((byte)0x06)]   // MBC2 + バッテリー
    public void MBC2の容量は512バイトと決め打つ(byte cartType)
    {
        Assert.Equal(512, GbSave.MaxSize(cartType));
        Assert.True(GbSave.IsMbc2(cartType));
    }

    /// <summary>
    /// **HuC1 / HuC3 / ポケットカメラはバンク切り替えを伴う。**
    ///
    /// 参照実装はこれらに専用の手順を持たず、基底クラスのまま
    /// 上限 8KB の素通し扱いにしている。だが実機のポケモンカードGB（HuC1）は
    /// セーブが 32KB あり、その形では「大きすぎる」として読めない
    /// （2026-09-25 実機）。資料でも HuC1 は MBC1 相当で、
    /// $0000-$1FFF で RAM を有効化し $4000 でバンクを選ぶ。
    /// </summary>
    [Theory]
    [InlineData((byte)0xFF)]   // HuC1
    [InlineData((byte)0xFE)]   // HuC3
    [InlineData((byte)0xFC)]   // ポケットカメラ
    public void HuC系は32KBまで扱える(byte cartType)
        => Assert.True(GbSave.MaxSize(cartType) >= 0x8000);

    [Fact]
    public void HuC1はバンクを切り替えて読む()
    {
        var cart = new FakeGbSaveCartridge(0x8000, 0xFF) { AllowSaveWrites = true };
        cart.Preset(Pattern(0x8000));

        Assert.Equal(Pattern(0x8000), GbSave.Read(cart, 0xFF, 0x8000));

        // 有効化したままにしない。
        Assert.False(cart.RamLeftEnabled);
    }

    /// <summary>MBC を持たない ROM+RAM は素通し。上限 8KB のまま。</summary>
    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x08)]
    [InlineData((byte)0x09)]
    public void MBCなしは8KBまで(byte cartType)
        => Assert.Equal(0x2000, GbSave.MaxSize(cartType));

    [Fact]
    public void 許可していなければ書き込めない()
    {
        var cart = new FakeGbSaveCartridge(0x2000, Mbc3WithRam);
        var before = cart.Snapshot();

        Assert.Throws<RfcaWriteBlockedException>(
            () => GbSave.Write(cart, Mbc3WithRam, Pattern(0x2000)));

        Assert.Equal(before, cart.Snapshot());
    }

    [Fact]
    public void 許可していなくても吸い出せる()
    {
        var cart = new FakeGbSaveCartridge(0x2000, Mbc3WithRam);
        cart.Preset(Pattern(0x2000));

        Assert.Equal(Pattern(0x2000), GbSave.Read(cart, Mbc3WithRam, 0x2000));
    }
}

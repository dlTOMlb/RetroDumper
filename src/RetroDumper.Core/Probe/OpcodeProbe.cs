using System.Buffers.Binary;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Probe;

public enum ProbeOutcome
{
    /// <summary>応答なし。未実装の opcode。</summary>
    NoResponse,

    /// <summary>リード要求として受理され、データが返ってきた。</summary>
    ReadAck,

    /// <summary>ライト要求として受理された（ACK が 8 バイトのゼロ）。</summary>
    WriteAck,

    /// <summary>応答はあったが肯定応答ではない。</summary>
    Nak,
}

public sealed record ProbeHit(
    uint Opcode,
    ProbeOutcome Outcome,
    byte[] Data,
    string Note)
{
    public bool IsUseful => Outcome is ProbeOutcome.ReadAck or ProbeOutcome.WriteAck;
}

/// <summary>
/// 未知のスロットのリード／ライト opcode を実機から探索する。
///
/// RFCA の opcode はバス（スロット）ごとに別番号が割り当てられており、
/// SFC=0x07/0x0C、MD=0x0D、マークIII・GG=0x2B/0x2C までは判明している。
/// GBA・GB・FC・PCE は未判明なので、総当たりで応答する番号を探す。
///
/// 判定はデータの中身で行う。GB と GBA は ROM 先頭に任天堂ロゴという
/// 固定バイト列を持つので、それが読めた opcode が正解だと断定できる。
/// </summary>
public static class OpcodeProbe
{
    /// <summary>GB / GBC の任天堂ロゴ。ROM の $0104 から。</summary>
    public static readonly byte[] GbLogo =
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B,
        0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
    ];

    /// <summary>GBA の任天堂ロゴ。ROM の $04 から。</summary>
    public static readonly byte[] GbaLogo =
    [
        0x24, 0xFF, 0xAE, 0x51, 0x69, 0x9A, 0xA2, 0x21,
        0x3D, 0x84, 0x82, 0x0A, 0x84, 0xE4, 0x09, 0xAD,
    ];

    /// <summary>PC エンジン Hu カードの判定に使える固定列はない。データの見た目で判断する。</summary>
    public const int DefaultProbeSize = 16;

    /// <summary>探索から除外する opcode。状態要求と、判明済みのライト系。</summary>
    private static readonly HashSet<uint> Excluded =
    [
        RfcaOpcode.Status,
        RfcaOpcode.SnesWrite,
        RfcaOpcode.SmsWrite,
    ];

    /// <summary>
    /// opcode を総当たりして、応答するものを列挙する。
    ///
    /// 注意: 当たった opcode がライト系だった場合、アダプタは続けて
    /// データ本体を待つ。同期を保つためにフィラーを送り返す必要があり、
    /// それが実際にカセットへの書き込みになる可能性がある。
    /// <paramref name="address"/> には書き込まれても無害な番地を指定すること。
    /// </summary>
    public static List<ProbeHit> Scan(
        IRfcaLink link,
        uint address,
        int size = DefaultProbeSize,
        uint firstOpcode = 0x00,
        uint lastOpcode = 0xFF,
        IProgress<(uint Opcode, int Total)>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowRecoveryWrite = false)
    {
        var hits = new List<ProbeHit>();

        for (uint opcode = firstOpcode; opcode <= lastOpcode; opcode++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((opcode, (int)(lastOpcode - firstOpcode + 1)));

            if (Excluded.Contains(opcode)) continue;

            var hit = ProbeOne(link, opcode, address, size, allowRecoveryWrite);
            if (hit.IsUseful) hits.Add(hit);
        }

        return hits;
    }

    /// <summary>
    /// 単一の opcode を試す。
    ///
    /// <paramref name="allowRecoveryWrite"/> が false（既定）のとき、
    /// ライト要求として受理されたら <see cref="RfcaWriteOpcodePendingException"/> を
    /// 投げて即座に打ち切る。この時点ではカートリッジには何も書かれていない。
    ///
    /// true にすると、同期を取り戻すためにフィラーを送って手順を完了させる。
    /// これは実際の書き込みになるので、マスク ROM のカセットでのみ使うこと。
    /// </summary>
    public static ProbeHit ProbeOne(
        IRfcaLink link, uint opcode, uint address, int size, bool allowRecoveryWrite = false)
    {
        try
        {
            byte[] data = link.Read(opcode, address, size);
            return new ProbeHit(opcode, ProbeOutcome.ReadAck, data, Describe(data));
        }
        catch (RfcaTimeoutException)
        {
            return new ProbeHit(opcode, ProbeOutcome.NoResponse, [], "応答なし");
        }
        catch (RfcaNakException ex)
        {
            // ACK が 8 バイトのゼロなら、ライト要求として受理されている。
            // アダプタはデータ本体を待っている状態。
            //
            // ここが分かれ目。書き込みが起きるのはデータ本体を送った瞬間であって、
            // 要求が受理された時点ではまだ何も書かれていない。
            // したがって「何も送らずにやめる」ことが、カートリッジを守る唯一の正解。
            //
            // ただし以後どんなバイトを送っても保留中の書き込みデータとして
            // 解釈されてしまうので、探索そのものを打ち切る必要がある。
            if (IsWriteAck(ex.Response))
            {
                if (!allowRecoveryWrite)
                    throw new RfcaWriteOpcodePendingException(opcode, address);

                RecoverFromWriteAck(link, size);
                return new ProbeHit(opcode, ProbeOutcome.WriteAck, [],
                    "ライト系 opcode（データ本体を要求された）");
            }

            return new ProbeHit(opcode, ProbeOutcome.Nak, ex.Response,
                $"肯定応答でない: {Convert.ToHexString(ex.Response)}");
        }
        catch (RfcaWriteBlockedException)
        {
            // 書き込み保護に弾かれた。探索としては「ライト系だった」という情報。
            return new ProbeHit(opcode, ProbeOutcome.WriteAck, [],
                "ライト系 opcode（書き込み保護により中止）");
        }
        catch (RfcaException ex)
        {
            return new ProbeHit(opcode, ProbeOutcome.NoResponse, [], ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // 既知の固定バイト列による自動特定
    // ------------------------------------------------------------------

    public sealed record OpcodeFinding(uint Opcode, uint BaseAddress, byte[] Evidence);

    /// <summary>
    /// GBA のリード opcode を特定する。ROM 先頭 +4 の任天堂ロゴを手がかりにする。
    /// GBA のシステムバスでは ROM は 0x08000000 に見えるが、
    /// アダプタがフラットな 0 番地起点を期待している可能性もあるため両方試す。
    /// </summary>
    public static OpcodeFinding? FindGbaRead(
        IRfcaLink link,
        IProgress<(uint Opcode, int Total)>? progress = null,
        CancellationToken cancellationToken = default,
        uint firstOpcode = 0x00)
        => FindByLogo(link, GbaLogo, [(0x08000000u, 0x04u), (0x00000000u, 0x04u)],
            progress, cancellationToken, firstOpcode);

    /// <summary>
    /// GB / GBC のリード opcode を特定する。ROM $0104 の任天堂ロゴを手がかりにする。
    /// </summary>
    public static OpcodeFinding? FindGameBoyRead(
        IRfcaLink link,
        IProgress<(uint Opcode, int Total)>? progress = null,
        CancellationToken cancellationToken = default,
        uint firstOpcode = 0x00)
        => FindByLogo(link, GbLogo, [(0x00000000u, 0x0104u)],
            progress, cancellationToken, firstOpcode);

    /// <summary>
    /// ロゴ照合による特定。ライト系 opcode に当たった時点で
    /// <see cref="RfcaWriteOpcodePendingException"/> を投げて打ち切る。
    ///
    /// 呼び出し側はアダプタの抜き差しを促したうえで、
    /// <paramref name="firstOpcode"/> に「当たった opcode + 1」を渡して
    /// 続きから再開できる。
    /// </summary>
    private static OpcodeFinding? FindByLogo(
        IRfcaLink link,
        byte[] logo,
        (uint Base, uint LogoOffset)[] layouts,
        IProgress<(uint Opcode, int Total)>? progress,
        CancellationToken cancellationToken,
        uint firstOpcode)
    {
        foreach (var (romBase, logoOffset) in layouts)
        {
            for (uint opcode = firstOpcode; opcode <= 0xFF; opcode++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((opcode, 256 * layouts.Length));

                if (Excluded.Contains(opcode)) continue;

                var hit = ProbeOne(link, opcode, romBase + logoOffset, logo.Length);
                if (hit.Outcome == ProbeOutcome.ReadAck && hit.Data.AsSpan().SequenceEqual(logo))
                    return new OpcodeFinding(opcode, romBase, hit.Data);
            }

            // 2 周目以降は必ず先頭から。
            firstOpcode = 0;
        }

        return null;
    }

    /// <summary>
    /// ライト opcode を推定する。判明している SFC・マークIII の例では
    /// リードとライトが近い番号に割り当てられているため、
    /// リード opcode の前後を優先的に試す。
    /// </summary>
    public static List<uint> SuggestWriteOpcodes(uint readOpcode)
    {
        var candidates = new List<uint>();
        for (uint delta = 1; delta <= 8; delta++)
        {
            if (readOpcode + delta <= 0xFF) candidates.Add(readOpcode + delta);
            if (readOpcode >= delta) candidates.Add(readOpcode - delta);
        }
        return candidates;
    }

    // ------------------------------------------------------------------
    // 内部処理
    // ------------------------------------------------------------------

    private static bool IsWriteAck(byte[] response)
    {
        if (response.Length != 8) return false;
        foreach (byte b in response)
            if (b != 0) return false;
        return true;
    }

    /// <summary>
    /// ライト要求として受理されてしまったときの復帰。
    /// アダプタが待っているバイト数だけ 0xFF を送って手順を完了させる。
    /// 0xFF はフラッシュメモリの消去済み値で、多くのマッパーレジスタでも
    /// 副作用が最小になる値。
    /// </summary>
    private static void RecoverFromWriteAck(IRfcaLink link, int size)
    {
        try
        {
            link.CompletePendingWrite(size);
        }
        catch (RfcaException)
        {
            // 復帰できなくても次の opcode 試行時に DrainInput で捨てられる。
        }
    }

    private static string Describe(byte[] data)
    {
        if (data.Length == 0) return "データなし";

        bool allSame = true;
        for (int i = 1; i < data.Length; i++)
            if (data[i] != data[0]) { allSame = false; break; }

        if (allSame) return $"全バイト 0x{data[0]:X2}（無効バスの可能性）";

        if (LooksLikeOpenBus(data)) return "オープンバス（アドレス値が読めている）";

        return Convert.ToHexString(data);
    }

    private static bool LooksLikeOpenBus(byte[] data)
    {
        if (data.Length < 4) return false;
        ushort first = BinaryPrimitives.ReadUInt16LittleEndian(data);
        for (int i = 2; i + 1 < data.Length; i += 2)
        {
            ushort word = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i));
            if (word != (ushort)(first + i / 2)) return false;
        }
        return true;
    }
}

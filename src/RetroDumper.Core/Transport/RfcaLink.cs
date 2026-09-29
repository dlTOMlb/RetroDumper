using System.Buffers.Binary;
using System.IO.Ports;

namespace RetroDumper.Core.Transport;

/// <summary>
/// レトロフリーク カートリッジアダプタ (RFCA) との仮想 COM ポート通信。
///
/// フレーム形式（すべてリトルエンディアン）:
///   リクエスト(20B): [u32 opcode][u32 0x08][u32 size][u32 addr][u32 size]
///   リード ACK (8B): [u32 0x00000000][u32 size]
///   ライト ACK (8B): 00 00 00 00 00 00 00 00
///   状態要求(12B)  : [u32 0x06][u32 0x00][u32 0x04]
///   状態応答(12B)  : byte[8] がカートリッジ種別
///
/// リードの癖: ACK を受け取ってもデータ本体はすぐに来ない。
/// 状態要求を「催促」として送ると、データ本体 size バイト＋状態応答 12 バイトが続けて返る。
/// これは実機（レトロフリーク本体）の挙動とは異なるが、PC 側からはこの手順でしか読めない。
/// </summary>
public sealed class RfcaLink : IRfcaLink, IDisposable
{
    public const int BaudRate = 115200;

    /// <summary>1 リクエストあたりの推奨転送サイズ。解析で観測された値と同じ 1KB。</summary>
    public const int DefaultChunkSize = 1024;

    private static readonly byte[] StatusRequest =
    [
        0x06, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x04, 0x00, 0x00, 0x00,
    ];

    private readonly SerialPort _port;
    private readonly object _gate = new();
    private bool _disposed;

    public string PortName { get; }

    /// <summary>
    /// <see cref="Write"/> / <see cref="WriteByte"/> を許可するか。
    /// **既定は false（書き込み禁止）**。
    ///
    /// **今このアプリに、この経路を通る書き込みは無い。**
    /// バンク切り替えは <see cref="WriteBankRegister"/>、
    /// セーブは <see cref="WriteSaveMemory"/> に分かれており、
    /// それぞれ別の判定を持つ。
    ///
    /// 画面にもこの設定は出していない。かつて「カートリッジへの書き込みを
    /// 禁止」というチェックがあったが、何も止めていない状態になったので外した。
    ///
    /// ここに残してあるのは最後の砦として。うっかり <see cref="Write"/> を
    /// 直接呼ぶコードが入れば、既定の false で例外になる。
    /// </summary>
    public bool AllowWrites { get; set; }

    /// <summary>
    /// バンク切り替えレジスタへの書き込みを許可するか。**既定は true**。
    ///
    /// <see cref="AllowWrites"/>（内容の書き換え）とは別物。
    /// MBC やマッパーのレジスタは揮発性で、カセットの内容は変わらない。
    /// バンクを選べないと 32KB より大きいゲームボーイのカセットは読めない。
    ///
    /// 対象範囲は <see cref="MapperRegister"/> が決めており、GBA は含まれない。
    /// </summary>
    public bool AllowBankSwitching { get; set; } = true;

    /// <summary>
    /// リード要求の肯定応答を待つ時間。
    ///
    /// opcode 総当たりでは未実装の番号が大量に空振りするので、
    /// 探索中だけ短くして全体の所要時間を抑える。
    /// </summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>通信内容のトレース出力先。解析・不具合調査用。</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// 直前の読み出しで、データ本体のあとに返ってきた状態応答 12 バイト。
    ///
    /// これまでエラー時にしか見ておらず、中身を捨てていた。
    /// 読み出しが「受理されたのに 0xFF しか返らない」ときの理由が
    /// ここに入っている可能性があるため、生のまま残す。
    /// </summary>
    public byte[] LastTrailingStatus { get; private set; } = [];

    public RfcaLink(string portName)
    {
        PortName = portName;
        // 設定は参照実装に合わせる。
        //
        // **バッファの大きさが効く。**既定の送信バッファは 2048 バイトしかなく、
        // フラッシュの 4096 バイト書き込みが収まらない。ドライバが吐き出すまで
        // Write がブロックし、1 秒のタイムアウトに掛かって失敗していた。
        // 失敗したまま再試行したところ、アダプタが USB から落ちた（2026-09-24 実機）。
        // 受信バッファも既定 4096 バイトで、大きな読み出しで取りこぼす余地があった。
        _port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
        {
            ReadBufferSize = BufferSize,
            WriteBufferSize = BufferSize,
            ReadTimeout = PortReadTimeout,
            WriteTimeout = PortWriteTimeout,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
        };
        _port.Open();

        // ポートを開くと DTR/RTS が立つ。CDC デバイスによってはこれが
        // リセット扱いになるため、落ち着くまで待ってから話しかける。
        // 直後に状態要求を投げると無応答になることがある。
        Thread.Sleep(SettleMilliseconds);
        DrainInput();
    }

    /// <summary>ポートを開いてからコマンドを送り始めるまでの待ち時間。</summary>
    public const int SettleMilliseconds = 300;

    /// <summary>
    /// 画面と自動検出に出すポートの一覧。
    ///
    /// macOS の <c>SerialPort.GetPortNames()</c> は、アダプタとは無関係な
    /// 擬似ポートまで返す。実測では Bluetooth-Incoming-Port、debug-console、
    /// wlan-debug の 3 つが常に並ぶ。
    ///
    /// <b>これらを自動検出に含めてはいけない</b>。
    /// 自動検出は全ポートへ状態要求を投げるので、Bluetooth のポートを開くと
    /// 接続待ちで止まる。アダプタを探す前に、無関係なポートで待たされる。
    /// </summary>
    public static string[] EnumeratePorts() =>
        SerialPort.GetPortNames().Where(IsCandidatePort).ToArray();

    /// <summary>
    /// アダプタでありうるポート名か。
    ///
    /// 名前で弾くだけに留める。Windows の COM1..COMn は名前から中身を
    /// 判断できないので、そのまま通す。実機で確認済みの経路は変えない。
    /// </summary>
    public static bool IsCandidatePort(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        foreach (string marker in NonAdapterPortMarkers)
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    /// <summary>
    /// アダプタではありえないポート名の断片。
    ///
    /// macOS で実測した 3 つ。増やすときは、実際に列挙されたものだけを足す。
    /// </summary>
    private static readonly string[] NonAdapterPortMarkers =
    [
        "Bluetooth",     // /dev/tty.Bluetooth-Incoming-Port
        "debug-console", // /dev/tty.debug-console
        "wlan-debug",    // /dev/tty.wlan-debug
    ];

    /// <summary>
    /// アダプタがまだ USB バスに存在するか。
    ///
    /// ポートが開いていても、デバイスが物理的に消えていれば列挙から外れる。
    /// 探索中はコマンドを送るたびにこれを確かめ、消えた時点で即座に止める。
    /// 止めないと以降の全 opcode が「応答なし」として記録され、
    /// どのコマンドが原因だったのか分からなくなる。
    /// </summary>
    public bool IsStillEnumerated
    {
        get
        {
            try
            {
                return SerialPort.GetPortNames()
                    .Any(p => string.Equals(p, PortName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>消えていれば <see cref="RfcaDisconnectedException"/> を投げる。</summary>
    public void EnsureAlive(uint? lastOpcode = null)
    {
        if (!IsStillEnumerated)
            throw new RfcaDisconnectedException(PortName, lastOpcode);
    }

    /// <summary>
    /// 全シリアルポートに状態要求を投げ、RFCA らしい応答を返したものを探す。
    ///
    /// PC に複数の COM ポートがあると、どれが RFCA なのかは見た目で分からない。
    /// 正規の 12 バイト応答を返すかどうかで判定する。
    /// </summary>
    public static string? FindAdapterPort(Action<string>? log = null)
    {
        foreach (string port in EnumeratePorts().OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            log?.Invoke($"{port} を確認中…");

            try
            {
                using var candidate = new RfcaLink(port);
                var status = candidate.GetStatusWithRetry();

                if (status.HasResponse)
                {
                    log?.Invoke($"{port}: RFCA の応答を確認 ({status})");
                    return port;
                }

                log?.Invoke($"{port}: 応答なし");
            }
            catch (Exception ex)
            {
                log?.Invoke($"{port}: 開けません ({ex.Message})");
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // 状態要求
    // ------------------------------------------------------------------

    /// <summary>状態要求を送り、挿入中カートリッジの種別を得る。</summary>
    public RfcaStatus GetStatus()
    {
        lock (_gate)
        {
            DrainInput();
            _port.Write(StatusRequest, 0, StatusRequest.Length);
            Trace?.Invoke("TX status");

            var buf = new byte[12];
            int got = TryReadExact(buf, 0, buf.Length, TimeSpan.FromMilliseconds(500));
            if (got == 0)
                return new RfcaStatus([]);
            if (got < 12)
                return new RfcaStatus(buf[..got]);

            Trace?.Invoke($"RX status {Convert.ToHexString(buf)}");
            return new RfcaStatus(buf);
        }
    }

    /// <summary>
    /// 応答が返るまで状態要求を数回試す。
    ///
    /// 接続直後の 1 回目は取りこぼすことがある。解析元は画面を
    /// 描き直すたびに状態要求を出しており、事実上リトライしていた。
    /// </summary>
    public RfcaStatus GetStatusWithRetry(int attempts = 3)
    {
        RfcaStatus last = default;

        for (int i = 0; i < attempts; i++)
        {
            last = GetStatus();
            if (last.HasResponse) return last;

            Trace?.Invoke($"状態要求 {i + 1} 回目に応答なし。再試行します");
            Thread.Sleep(150);
        }

        return last;
    }

    public CartridgeKind DetectCartridge() => GetStatusWithRetry().Kind;

    // ------------------------------------------------------------------
    // 制御コマンド
    // ------------------------------------------------------------------

    /// <summary>
    /// データ本体を伴わない制御コマンドを送る。
    ///
    /// リクエストの 5 つ目のフィールド（リード／ライトでは size の再掲）に
    /// <paramref name="parameter"/> を入れる。0x2F コマンドが
    /// この形で、size=0 / addr=0 / param=1 を送っている。
    /// </summary>
    /// <param name="headerField">
    /// フレーム 2 つ目のフィールド。全スロット共通で 8。
    ///
    /// 「GBA だけ 0x00 でないと受け付けない」と書いていた時期があるが誤り。
    /// 参照実装も全コマンドで 8 を入れている。
    /// 引数として残してあるのは、調査で値を振れるようにするため。
    /// </param>
    public byte[] SendControl(
        uint opcode, uint address = 0, uint size = 0, uint parameter = 0,
        uint headerField = 0x08)
    {
        lock (_gate)
        {
            DrainInput();

            var req = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(0), opcode);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4), headerField);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(8), size);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(12), address);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(16), parameter);
            _port.Write(req, 0, req.Length);
            Trace?.Invoke($"TX control op=0x{opcode:X2} hdr=0x{headerField:X2} " +
                          $"addr=0x{address:X6} size={size} param={parameter}");

            var ack = new byte[8];
            int got = TryReadExact(ack, 0, ack.Length, TimeSpan.FromMilliseconds(800));
            if (got == 0)
                throw new RfcaTimeoutException($"制御コマンド 0x{opcode:X2} に応答がありません");

            Trace?.Invoke($"RX control {Convert.ToHexString(ack.AsSpan(0, got))}");

            // ステータスを検証していなかったため、拒否されていても
            // 「送信完了」と報告してしまっていた。
            if (got >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(ack) == 0xFFFFFFFF)
                Trace?.Invoke($"WARN: 制御コマンド 0x{opcode:X2} は拒否されました");

            return ack[..got];
        }
    }

    /// <summary>
    /// スロットをウェイクアップさせる。解析で判明した 2F コマンドと同じ手順
    /// （送信後 0.5 秒待つ）。
    ///
    /// これを送るまでカートリッジはデータバスを駆動せず、
    /// どのアドレスを読んでも 0xFF しか返らない。実機で確認済み。
    /// </summary>
    public byte[] WakeSlot(uint? parameter = null)
    {
        uint param = parameter ?? ResolveWakeParameter();
        var kind = (CartridgeKind)(byte)param;

        // 参照実装の逆コンパイルで判明した正式な初期化手順。
        // スロットごとに送るものが違う。
        byte[] ack = kind switch
        {
            CartridgeKind.GameBoyAdvance => InitGbaSlot(),
            CartridgeKind.SuperFamicom => InitSnesSlot(),
            CartridgeKind.PcEngineHuCard => InitPceSlot(),
            _ => InitGenericSlot(param),
        };

        _awake = true;
        SettleAfterWake(kind);
        return ack;
    }

    /// <summary>
    /// GBA スロットの初期化。
    ///
    ///   0x04(param=0) → 0x05 → 200ms 待つ
    ///
    /// **0x2F は送らない。** GBA スロットには不要で、送っても拒否される。
    /// 以前これを「GBA が拒否するので別の手段があるはず」と誤解し、
    /// 存在しない初期化コマンドを長く探していた。
    /// </summary>
    private byte[] InitGbaSlot()
    {
        Trace?.Invoke("GBA スロットを初期化します (0x04 param=0 → 0x05)");

        var ack = SendSlotSelect(0);
        SendSlotCommit();
        Thread.Sleep(200);

        return ack;
    }

    /// <summary>
    /// SFC スロットの初期化。
    ///
    ///   0x04(param=1) → 0x2F(param=1) → 0x05 ×2
    ///
    /// 0x2F だけでも読めていたが、正式な手順はこちら。
    /// </summary>
    private byte[] InitSnesSlot()
    {
        // 0x2F だけを送る。**実機で動作実績があるのはこの手順**。
        //
        // 参照実装の正規手順は 0x04(1) → 0x2F(1) → 0x05 ×2 だが、
        // それに合わせたところカービィ3 を認識しなくなった（2026-09-23 実機）。
        // 本家は初期化後に SnesRead(0xC000, 0x4000) で 16KB を捨て読みし、
        // ヘッダ判定に失敗したら最大 5 回やり直す作りになっており、
        // 0x04 / 0x05 だけを真似ても同じにはならない。
        //
        // 動いているものを、動く根拠のない「正しさ」で置き換えない。
        Trace?.Invoke("SFC スロットをウェイクアップします (0x2F param=1)");

        var ack = SendControl(RfcaOpcode.SlotWakeup, address: 0, size: 0, parameter: 1);
        Thread.Sleep(WakeSettleMilliseconds);

        return ack;
    }

    /// <summary>
    /// SFC・GBA 以外のスロットを有効にする。
    ///
    /// **0x2F を使うのは SFC だけ。**参照実装の
    /// 各機種の初期化処理を見ると、GB / メガドライブ / ファミコン /
    /// PC エンジン / マークIII は揃って 0x04(1) → 0x05 → 200ms 待ち、で、
    /// 0x2F は送っていない。
    ///
    /// 以前はここでも 0x2F を送っていた。ゲームボーイでは**拒否され**、
    /// スロットが有効にならないままセーブを読んで全 0xFF になっていた
    /// （2026-09-24 ポケットモンスター ピカチュウで判明）。
    /// </summary>
    /// <summary>
    /// PC エンジンのスロットを有効にする。
    ///
    /// **ここだけ 0x04 を送らない。**参照実装は 0x05 だけを送り、
    /// 0x04(1) は「VDC 5V」を選んだときにしか送らない。
    /// 既定の手順に合わせる。
    /// </summary>
    private byte[] InitPceSlot()
    {
        Trace?.Invoke("PC エンジンのスロットを初期化します (0x05 のみ)");

        var ack = SendSlotCommit();
        Thread.Sleep(WakeSettleMilliseconds);

        return ack;
    }

    private byte[] InitGenericSlot(uint param)
    {
        Trace?.Invoke($"スロットを初期化します (0x04(1) → 0x05) [{(CartridgeKind)(byte)param}]");

        var ack = SendSlotSelect(1);
        SendSlotCommit();
        Thread.Sleep(WakeSettleMilliseconds);

        return ack;
    }

    /// <summary>
    /// スロット選択 (0x04)。**16 バイトフレーム**で、リード要求の 20 バイトとは違う。
    /// [u32 0x04][u32 4][u32 0][u32 param]、応答 8 バイト。
    /// </summary>
    public byte[] SendSlotSelect(byte param)
    {
        lock (_gate)
        {
            DrainInput();

            var req = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(0), RfcaOpcode.SlotSelect);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(12), param);

            _port.Write(req, 0, req.Length);
            Trace?.Invoke($"TX 0x04 param={param}");

            var ack = new byte[8];
            int got = TryReadExact(ack, 0, ack.Length, TimeSpan.FromMilliseconds(800));

            if (got == 0)
                throw new RfcaTimeoutException("スロット選択 (0x04) に応答がありません");

            Trace?.Invoke($"RX 0x04 {Convert.ToHexString(ack.AsSpan(0, got))}");
            return ack[..got];
        }
    }

    /// <summary>
    /// スロット確定 (0x05)。**12 バイトフレーム**。
    /// [u32 0x05][u32 0][u32 0]、応答 8 バイト。
    /// </summary>
    public byte[] SendSlotCommit()
    {
        lock (_gate)
        {
            DrainInput();

            var req = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(0), RfcaOpcode.SlotCommit);

            _port.Write(req, 0, req.Length);
            Trace?.Invoke("TX 0x05");

            var ack = new byte[8];
            int got = TryReadExact(ack, 0, ack.Length, TimeSpan.FromMilliseconds(800));

            if (got == 0)
                throw new RfcaTimeoutException("スロット確定 (0x05) に応答がありません");

            Trace?.Invoke($"RX 0x05 {Convert.ToHexString(ack.AsSpan(0, got))}");
            return ack[..got];
        }
    }

    /// <summary>
    /// 吸い出し終了時の後始末。SFC は 0x2F(0) も送る。
    /// </summary>
    public void ReleaseSlot(CartridgeKind kind)
    {
        try
        {
            if (kind == CartridgeKind.SuperFamicom)
                SendControl(RfcaOpcode.SlotWakeup, address: 0, size: 0, parameter: 0);

            SendSlotSelect(0);
            SendSlotCommit();
            _awake = false;
        }
        catch (RfcaException ex)
        {
            Trace?.Invoke($"スロットの解放に失敗: {ex.Message}");
        }
    }


    /// <summary>ウェイクアップ後にバスが安定するまでの待ち時間。</summary>
    public const int WakeSettleMilliseconds = 500;

    /// <summary>
    /// ウェイクアップ直後の捨て読み。
    ///
    /// 1 回目の読み出しはデータが化けることがある
    /// （SFC で $00:FFC0 の 'H' が 0x00 になる事例、
    ///  GBA でヘッダ全体が化ける事例を実機で確認）。
    ///
    /// 重要なのは **そのスロットで実際に通る opcode を使う** こと。
    /// 以前は常に SFC の 0x07 を使っていたため、GBA では拒否されて
    /// 捨て読みとして成立せず、最初の本番読み出しが化けていた。
    /// </summary>
    private void SettleAfterWake(CartridgeKind kind)
    {
        (uint Opcode, uint Address, int Size, uint HeaderField)? probe = kind switch
        {
            CartridgeKind.SuperFamicom => (RfcaOpcode.SnesRead, 0x00FFC0u, 64, 0x08u),
            CartridgeKind.MegaDrive => (RfcaOpcode.MegaDriveRead, 0x000000u, 64, 0x08u),
            CartridgeKind.MarkIIIOrGameGear => (RfcaOpcode.SmsRead, 0x000000u, 64, 0x08u),
            CartridgeKind.GameBoyAdvance when RfcaOpcode.GbaRead is uint gba
                => (gba, 0x000000u, RfcaOpcode.GbaBlockSize, RfcaOpcode.RequestHeaderField),
            _ => null,
        };

        if (probe is not var (opcode, address, size, headerField)) return;

        // 2 回読む。1 回目が化けても 2 回目以降が安定するのが実機の挙動。
        for (int i = 0; i < 2; i++)
        {
            try
            {
                Read(opcode, address, size, headerField);
            }
            catch (RfcaException ex)
            {
                Trace?.Invoke($"捨て読み {i + 1} 回目に失敗（無視して続行）: {ex.Message}");
                return;
            }
        }

        Trace?.Invoke($"{kind.ToDisplayName()} 用の捨て読みを完了しました");
    }

    private bool _awake;

    /// <summary>
    /// ウェイクアップコマンドに渡す値を固定する。null なら自動。
    ///
    /// 解析では SFC で 1 を送っていた。SFC の種別コードも 0x01 なので、
    /// この値が「有効化フラグ」なのか「スロット番号」なのかは区別がついていない。
    /// 自動のときは種別コードを使い、それで読めなければ 1 を試す。
    /// </summary>
    public uint? WakeParameter { get; set; }

    private uint ResolveWakeParameter()
    {
        if (WakeParameter is uint fixedValue) return fixedValue;

        // 種別コードがスロット番号を兼ねている可能性に賭ける。
        // SFC なら 0x01 になり、実機で動作確認済みの値と一致する。
        var kind = GetStatus().Kind;
        return kind.IsConnected() ? (uint)(byte)kind : 1u;
    }

    /// <summary>
    /// ウェイクアップをやり直す。パラメータを変えて試したいときに使う。
    /// </summary>
    public void ReWake(uint parameter)
    {
        _awake = false;
        WakeSlot(parameter);
    }

    /// <summary>
    /// まだウェイクアップしていなければ実行する。
    /// 読み書きの前に必ず通るので、呼び出し側が意識する必要はない。
    /// </summary>
    public void EnsureAwake(bool force = false)
    {
        if (!AutoWake)
        {
            // 切り分け中は何も送らない。「素の状態で読めるか」を見るため。
            _awake = true;
            return;
        }

        if (_awake && !force) return;

        Trace?.Invoke("スロットをウェイクアップします (opcode 0x2F)");
        WakeSlot();
    }

    /// <summary>
    /// 読み書きの前に自動でウェイクアップするか。既定は true。
    ///
    /// false にすると <see cref="EnsureAwake"/> は何も送らない。
    /// 「追加のコマンドを一切送らずに読めるか」を確かめる切り分けで使う。
    /// 本番の吸い出しでは true のままにすること。
    /// </summary>
    public bool AutoWake { get; set; } = true;

    /// <summary>
    /// ウェイクアップ済みの状態を破棄し、次の読み書きでやり直させる。
    ///
    /// 0x2F の param は「どのスロットを使うか」の指定なので、
    /// カートリッジを差し替えたら選び直さないと、前のスロットを
    /// 向いたまま読もうとして失敗する。
    /// 種別を取得し直すタイミングで必ず呼ぶこと。
    /// </summary>
    public void InvalidateWake()
    {
        if (_awake) Trace?.Invoke("ウェイクアップ状態を破棄しました。次の読み出しで選び直します");
        _awake = false;
    }

    // ------------------------------------------------------------------
    // リード
    // ------------------------------------------------------------------

    /// <summary>
    /// 指定 opcode のバスから <paramref name="address"/> を先頭に
    /// <paramref name="size"/> バイト読み出す。
    /// </summary>
    public byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08)
    {
        var buf = new byte[size];
        Read(opcode, address, buf, headerField);
        return buf;
    }

    /// <summary>
    /// GBA フラッシュのメーカー ID / デバイス ID を読む。取得できなければ -1。
    ///
    /// 要求は 12 バイトで、ヘッダ欄は 0、サイズ欄は 2。
    /// リード／ライトの 20 バイトとは形が違うので専用に組み立てる。
    /// 応答の受け取り方はリードと同じ（ACK → 状態要求で催促 → 本体 → 末尾）。
    ///
    /// フラッシュへ書く前に、対応している石かを確かめるために使う。
    /// 知らない ID の石に書くと、書けたように見えて壊れることがある。
    /// </summary>
    public int ReadGbaFlashId()
    {
        EnsureAwake();

        lock (_gate)
        {
            DrainInput();

            var req = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(0), RfcaOpcode.GbaFlashId);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(8), 2);

            _port.Write(req, 0, req.Length);
            Trace?.Invoke("TX op=0x26 (GBA フラッシュ ID)");

            Span<byte> ack = stackalloc byte[8];
            if (TryReadExact(ack, AckTimeout) != 8) return -1;

            if (BinaryPrimitives.ReadUInt32LittleEndian(ack) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(ack[4..]) != 2)
                return -1;

            _port.Write(StatusRequest, 0, StatusRequest.Length);

            Span<byte> id = stackalloc byte[2];
            if (TryReadExact(id, TimeSpan.FromMilliseconds(600)) != 2) return -1;

            Span<byte> trailing = stackalloc byte[12];
            TryReadExact(trailing, TimeSpan.FromMilliseconds(500));
            DrainInput();

            return id[0] | (id[1] << 8);
        }
    }

    /// <summary>リード結果を呼び出し側のバッファに直接書き込むオーバーロード。</summary>
    public void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08)
    {
        if (destination.Length == 0) return;

        // ウェイクアップ前はカートリッジがバスを駆動しないため 0xFF しか返らない。
        // 読み出しの前に必ず一度だけ済ませておく。
        EnsureAwake();

        lock (_gate)
        {
            DrainInput();

            uint size = (uint)destination.Length;
            SendRequest(opcode, address, size, headerField);

            // 1. リード ACK: [00 00 00 00][size]
            Span<byte> ack = stackalloc byte[8];
            ReadExact(ack, AckTimeout,
                $"opcode 0x{opcode:X2} のリード要求に応答がありません");

            uint ackStatus = BinaryPrimitives.ReadUInt32LittleEndian(ack);
            uint ackSize = BinaryPrimitives.ReadUInt32LittleEndian(ack[4..]);
            if (ackStatus != 0 || ackSize != size)
            {
                throw new RfcaNakException(
                    $"opcode 0x{opcode:X2} addr 0x{address:X6} が肯定応答を返しませんでした " +
                    $"(応答: {Convert.ToHexString(ack)})",
                    ack.ToArray());
            }

            // 2. データ本体の催促として状態要求を送る
            _port.Write(StatusRequest, 0, StatusRequest.Length);

            // 3. データ本体
            ReadExact(destination, EstimateTransferTimeout(destination.Length),
                $"addr 0x{address:X6} のデータ本体を受信できませんでした");

            // 4. 催促で送った状態要求への応答
            Span<byte> trailing = stackalloc byte[12];
            int got = TryReadExact(trailing, TimeSpan.FromMilliseconds(500));
            LastTrailingStatus = trailing[..Math.Max(got, 0)].ToArray();

            if (got == 12)
            {
                var status = new RfcaStatus(trailing.ToArray());
                if (status.IsFault)
                    Trace?.Invoke($"WARN: addr 0x{address:X6} 読み出し後の状態応答がエラーを示しています");
            }
            else if (got > 0)
            {
                Trace?.Invoke($"WARN: 末尾状態応答が {got} バイトしか来ませんでした");
            }

            // 応答の長さが機種によって違う可能性があるので、
            // 残っているものは必ず捨ててから次のリクエストに移る。
            // ここを条件付きにしていると、取りこぼしが次の読み出しの
            // 先頭に紛れ込んでデータが 1 バイトずれる。
            DrainInput();
        }
    }

    // ------------------------------------------------------------------
    // 探索用の柔軟なリード
    // ------------------------------------------------------------------

    /// <summary>リード要求に対するアダプタの返事。</summary>
    public enum ReadAckKind
    {
        /// <summary>応答なし。</summary>
        Silent,

        /// <summary>ステータス 0 ＝ 受理。データが続く。</summary>
        Accepted,

        /// <summary>ステータス 0xFFFFFFFF ＝ 汎用エラー。データは続かない。</summary>
        Rejected,

        /// <summary>8 バイトのゼロ ＝ ライト要求として受理された。</summary>
        WriteAck,

        /// <summary>解釈できない応答。</summary>
        Unknown,
    }

    public readonly record struct FlexibleReadResult(
        ReadAckKind Kind, uint AckStatus, uint AckSize, byte[] Data, byte[] RawAck);

    /// <summary>
    /// opcode 探索用のリード。
    ///
    /// 通常の <see cref="Read(uint, uint, Span{byte})"/> は「要求したサイズと
    /// 同じサイズで肯定応答が返ること」を成功条件にしている。探索中はそれが
    /// きつすぎて、「受理したが別のサイズを返す」応答を取りこぼす。
    /// ここではステータスだけを見て受理と判断し、アダプタが申告したサイズ分を読む。
    ///
    /// 受理された場合はデータ本体まで読み切るので、通信の同期は保たれる。
    /// </summary>
    public FlexibleReadResult TryReadFlexible(
        uint opcode, uint address, uint requestedSize, uint headerField = 0x08)
    {
        EnsureAwake();

        lock (_gate)
        {
            DrainInput();
            SendRequest(opcode, address, requestedSize, headerField);

            var ack = new byte[8];
            int got = TryReadExact(ack, 0, ack.Length, AckTimeout);

            if (got == 0)
                return new FlexibleReadResult(ReadAckKind.Silent, 0, 0, [], []);

            if (got < 8)
                return new FlexibleReadResult(ReadAckKind.Unknown, 0, 0, [], ack[..got]);

            uint ackStatus = BinaryPrimitives.ReadUInt32LittleEndian(ack);
            uint ackSize = BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4));

            if (ackStatus == 0xFFFFFFFF)
                return new FlexibleReadResult(ReadAckKind.Rejected, ackStatus, ackSize, [], ack);

            if (ackStatus == 0 && ackSize == 0)
                return new FlexibleReadResult(ReadAckKind.WriteAck, ackStatus, ackSize, [], ack);

            if (ackStatus != 0)
                return new FlexibleReadResult(ReadAckKind.Unknown, ackStatus, ackSize, [], ack);

            // 受理された。催促を送ってデータ本体を受け取る。
            // 読み切らないと次のコマンドとずれるので、必ず最後まで処理する。
            _port.Write(StatusRequest, 0, StatusRequest.Length);

            int length = (int)Math.Min(ackSize, 64 * 1024);
            var data = new byte[length];
            int read = TryReadExact(data, 0, length, EstimateTransferTimeout(length));

            Span<byte> trailing = stackalloc byte[12];
            TryReadExact(trailing, TimeSpan.FromMilliseconds(400));
            DrainInput();

            return new FlexibleReadResult(
                ReadAckKind.Accepted, ackStatus, ackSize, data[..read], ack);
        }
    }

    // ------------------------------------------------------------------
    // ライト
    // ------------------------------------------------------------------

    /// <summary>
    /// 指定 opcode のバスの <paramref name="address"/> へ書き込む。
    /// マッパー（SA-1 Super MMC、GB の MBC、SMS のバンクレジスタ）の制御に使う。
    /// </summary>
    /// <summary>
    /// バンク切り替えレジスタへ 1 バイト書く。**書き込み保護下でも通る。**
    ///
    /// 対象は <see cref="MapperRegister.IsBankRegister"/> が認める範囲だけ。
    /// ゲームボーイの MBC とマークIII のマッパーが該当する。
    /// これらは揮発性のレジスタで、カセットの内容は変わらない。
    /// バンクを選べなければ 32KB より大きいカセットを読めないため、
    /// 内容保護とは別の扱いにしている。
    ///
    /// GBA は対象外。<see cref="MapperRegister.IsBankRegister"/> が
    /// 常に false を返すので、この経路から GBA へ書き込むことはできない。
    /// </summary>
    /// <exception cref="RfcaWriteBlockedException">
    /// 範囲外のアドレス、または <see cref="AllowBankSwitching"/> が false のとき。
    /// </exception>
    /// <summary>
    /// セーブデータの書き込みを許可するか。既定は false。
    /// 利用者が明示的に有効にしたときだけ true になる。
    /// </summary>
    public bool AllowSaveWrites { get; set; }

    /// <summary>
    /// ポートを開き直し、スロットを選び直す。セーブの読み書きの前に送る。
    ///
    /// 参照実装は、セーブの読み書きのたびに
    /// **シリアルポートそのものを開き直している**。
    ///
    ///   Initialize()   : Open() → 0x04(0) → 0x05 → 200ms 待ち
    ///   終了時 : 0x05 → ポートを閉じる
    ///
    /// Open() はポートを開いて受信バッファを捨てる。USB CDC の状態が
    /// ここで一度リセットされる。こちらは接続時に開いたまま使い続けていた。
    ///
    /// EEPROM の読み書きが安定しない件で見つかった差分。
    /// 同じカセットを 2 回読んで 2 バイト違う、という現象が出ている。
    /// </summary>
    /// <summary>
    /// 本体送信の待ち時間。フラッシュで詰まる原因を切り分けるため変えられる。
    /// </summary>
    public int PayloadWriteTimeout
    {
        get => _port.WriteTimeout;
        set => _port.WriteTimeout = value;
    }

    /// <summary>実際に効いているポートの設定。切り分け用。</summary>
    public string PortSettings =>
        $"送信バッファ {_port.WriteBufferSize} / 受信バッファ {_port.ReadBufferSize} / " +
        $"読み {_port.ReadTimeout}ms / 書き {_port.WriteTimeout}ms";

    public void ReinitializeSlot()
    {
        lock (_gate)
        {
            ReopenPort();
        }

        EnsureAwake(force: true);
    }

    /// <summary>
    /// ポートを閉じて開き直す。失敗したら開いたままで続ける。
    ///
    /// 開き直しは通信の状態を捨てるための手段であって、目的ではない。
    /// ここで例外を投げると、元々できていた読み書きまで巻き添えになる。
    /// </summary>
    private void ReopenPort()
    {
        try
        {
            if (_port.IsOpen) _port.Close();

            // 閉じた直後に開くと拒まれることがあるので少し待つ。
            Thread.Sleep(ReopenGapMilliseconds);

            for (int attempt = 0; attempt < ReopenAttempts; attempt++)
            {
                try
                {
                    _port.Open();
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(ReopenGapMilliseconds);
                }
            }

            if (!_port.IsOpen)
            {
                Trace?.Invoke("WARN: ポートを開き直せませんでした");
                return;
            }

            // 開くと DTR/RTS が立ち、CDC によってはリセット扱いになる。
            Thread.Sleep(SettleMilliseconds);
            DrainInput();

            Trace?.Invoke("ポートを開き直しました");
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"WARN: ポートの開き直しに失敗しました ({ex.GetType().Name})");
        }
    }

    /// <summary>送受信バッファ。参照実装と同じ 1MB。</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>ポートの読み出しタイムアウト。参照実装と同じ。</summary>
    private const int PortReadTimeout = 3000;

    /// <summary>ポートの書き込みタイムアウト。参照実装と同じ。</summary>
    private const int PortWriteTimeout = 5000;

    /// <summary>ポートを閉じてから開くまでの間隔。</summary>
    private const int ReopenGapMilliseconds = 120;

    /// <summary>開き直しの試行回数。参照実装も 3 回試している。</summary>
    private const int ReopenAttempts = 3;

    /// <summary>
    /// セーブ領域へ書く。
    ///
    /// 許可と宛先の両方を見る。どちらか一方でも欠けたら、
    /// **要求フレームを組み立てる前に**弾く。シリアルポートには 1 バイトも出ない。
    /// </summary>
    public void WriteSaveMemory(CartridgeKind kind, uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;

        if (!AllowSaveWrites)
            throw new RfcaWriteBlockedException(
                "セーブデータの書き込みが許可されていません。" +
                $"opcode 0x{opcode:X2} アドレス 0x{address:X6} への " +
                $"{data.Length} バイトの書き込みを中止しました。");

        if (!SaveMemory.IsSaveWrite(kind, opcode, address))
            throw new RfcaWriteBlockedException(
                $"{kind.ToDisplayName()} の opcode 0x{opcode:X2} アドレス 0x{address:X6} は " +
                "セーブデータの領域ではありません。書き込みを中止しました。");

        Trace?.Invoke(
            $"セーブ書き込み: op=0x{opcode:X2} addr=0x{address:X6} " +
            $"{data.Length} バイト ({kind.ToDisplayName()})");

        WriteCore(opcode, address, data);
    }

    public void WriteBankRegister(CartridgeKind kind, uint opcode, uint address, byte value,
                                  uint headerField = 0x08)
    {
        if (MapperRegister.IsSaveMemory(kind, address))
            throw new RfcaWriteBlockedException(
                $"アドレス 0x{address:X4} はセーブデータの領域です。" +
                "バンク切り替えとして書き込むことはできません。");

        if (!MapperRegister.IsBankRegister(kind, address))
            throw new RfcaWriteBlockedException(
                $"{kind.ToDisplayName()} のアドレス 0x{address:X4} は " +
                "バンク切り替えレジスタではありません。書き込みを中止しました。");

        if (!AllowBankSwitching)
            throw new RfcaWriteBlockedException(
                "バンク切り替えが禁止されています。" +
                $"アドレス 0x{address:X4} への書き込みを中止しました。");

        Trace?.Invoke($"バンク切り替え: 0x{address:X4} <- 0x{value:X2} ({kind.ToDisplayName()})");
        WriteCore(opcode, address, stackalloc byte[] { value }, headerField);
    }

    public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;

        // 書き込み保護。要求フレームを組み立てる前に弾くので、
        // 禁止されている間はシリアルポートに 1 バイトも出ない。
        if (!AllowWrites)
            throw new RfcaWriteBlockedException(
                $"書き込み保護が有効です。opcode 0x{opcode:X2} アドレス 0x{address:X8} への " +
                $"{data.Length} バイトの書き込みを実行せずに中止しました。");

        WriteCore(opcode, address, data);
    }

    /// <summary>
    /// 実際にバスへ書く。**保護の判定はここでは行わない。**
    /// 呼び出す前に <see cref="AllowWrites"/> か
    /// <see cref="WriteBankRegister"/> の範囲判定を必ず通すこと。
    /// </summary>
    private void WriteCore(uint opcode, uint address, ReadOnlySpan<byte> data,
                           uint headerField = 0x08)
    {
        EnsureAwake();

        lock (_gate)
        {
            DrainInput();

            SendRequest(opcode, address, (uint)data.Length, headerField);

            // ライト要求 ACK。実機は 8 バイトのゼロを返すが、
            // 異なる応答でも処理を続行する（解析で観測された挙動と同じ）。
            // 待ち時間は参照実装の ReadTimeout に合わせる。
            Span<byte> ack = stackalloc byte[8];
            int got = TryReadExact(ack, AckWait);
            if (got == 0)
                throw new RfcaTimeoutException(
                    $"opcode 0x{opcode:X2} のライト要求に応答がありません");
            if (got == 8 && !IsAllZero(ack))
                Trace?.Invoke($"WARN: ライト要求 ACK が非ゼロ {Convert.ToHexString(ack)}");

            // データ本体送信。送り終えてから、送信バッファが空になるのを待つ。
            // 参照実装も送信の直後に必ず送信完了を待っている。
            _port.Write(data.ToArray(), 0, data.Length);
            WaitWriteDrained();

            // 書き込みの完了応答。フラッシュは消去と書き込みを伴うぶん待たされる。
            Span<byte> ack2 = stackalloc byte[8];
            int got2 = TryReadExact(ack2, WriteAckWait);
            if (got2 == 0)
                throw new RfcaTimeoutException(
                    $"addr 0x{address:X6} へのデータ書き込みに応答がありません");
            if (got2 == 8 && !IsAllZero(ack2))
                Trace?.Invoke($"WARN: ライトデータ ACK が非ゼロ {Convert.ToHexString(ack2)}");
        }
    }

    /// <summary>1 バイト書き込み。マッパーレジスタ操作の定番。</summary>
    public void WriteByte(uint opcode, uint address, byte value)
        => Write(opcode, address, stackalloc byte[] { value });

    /// <summary>
    /// ライト要求として受理されてしまったリクエストを完了させ、通信の同期を取り戻す。
    /// opcode 探索中、リードのつもりで送った要求がライト系だったときに使う。
    ///
    /// アダプタは要求したバイト数が届くまで次のコマンドを受け付けないため、
    /// <paramref name="filler"/> を <paramref name="size"/> バイト送って手順を終わらせる。
    /// これは実際にカートリッジへの書き込みになりうる。
    /// </summary>
    public void CompletePendingWrite(int size, byte filler = 0xFF)
    {
        if (!AllowWrites)
            throw new RfcaWriteBlockedException(
                $"書き込み保護が有効です。保留中のライト手順を完了させる {size} バイトの " +
                "送信を中止しました。アダプタを抜き差しして同期を取り直してください。");

        lock (_gate)
        {
            var data = new byte[size];
            Array.Fill(data, filler);
            _port.Write(data, 0, data.Length);
            Trace?.Invoke($"TX filler {size} バイト (0x{filler:X2}) でライト手順を完了");

            Span<byte> ack = stackalloc byte[8];
            TryReadExact(ack, TimeSpan.FromMilliseconds(600));
            DrainInput();
        }
    }

    // ------------------------------------------------------------------
    // 内部処理
    // ------------------------------------------------------------------

    /// <summary>
    /// リード／ライト要求の 20 バイトフレームを組んで送る。
    ///
    /// 2 つ目のフィールド (<paramref name="headerField"/>) の意味は不明。
    /// 観測された範囲ではリード・ライトとも常に 8 で、状態要求だけ 0 だった。
    /// 「これ以降のパラメータ長 - 4」と辻褄は合う。GBA のように
    /// 別のパラメータ構成を要求するコマンドがあるかもしれないので、
    /// 探索時に変えられるようにしてある。
    /// </summary>
    private void SendRequest(uint opcode, uint address, uint size, uint headerField = 0x08)
    {
        var req = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(0), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4), headerField);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(8), size);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(12), address);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(16), size);
        _port.Write(req, 0, req.Length);
        Trace?.Invoke($"TX op=0x{opcode:X2} addr=0x{address:X6} size={size}");
    }

    /// <summary>応答を待つ時間。参照実装の ReadTimeout と同じ。</summary>
    private static readonly TimeSpan AckWait = TimeSpan.FromMilliseconds(PortReadTimeout);

    /// <summary>
    /// 書き込みの完了応答を待つ時間。
    /// フラッシュの消去と書き込みは区画ごとに時間がかかるので長めに取る。
    /// </summary>
    private static readonly TimeSpan WriteAckWait = TimeSpan.FromMilliseconds(PortWriteTimeout);

    /// <summary>
    /// 送信バッファが空になるのを待つ。
    ///
    /// 参照実装は書き込みのたびにこれを行っている。
    /// バッファに積んだだけで次へ進むと、アダプタがまだ受け取り切っていない
    /// うちに次のコマンドを送ることになる。
    /// </summary>
    private void WaitWriteDrained()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(PortWriteTimeout);

        while (DateTime.UtcNow < deadline)
        {
            if (_port.BytesToWrite == 0) return;

            Thread.Sleep(0);
        }

        Trace?.Invoke("WARN: 送信バッファが空になりませんでした");
    }

    /// <summary>転送サイズに見合ったタイムアウト。115200bps ≒ 11.5KB/s。</summary>
    private static TimeSpan EstimateTransferTimeout(int bytes)
        => TimeSpan.FromMilliseconds(600 + bytes * 1000.0 / 11000.0);

    private void ReadExact(Span<byte> buffer, TimeSpan timeout, string errorMessage)
    {
        int got = TryReadExact(buffer, timeout);
        if (got != buffer.Length)
            throw new RfcaTimeoutException($"{errorMessage} ({got}/{buffer.Length} バイト受信)");
    }

    private int TryReadExact(Span<byte> buffer, TimeSpan timeout)
    {
        var rented = new byte[buffer.Length];
        int got = TryReadExact(rented, 0, buffer.Length, timeout);
        rented.AsSpan(0, got).CopyTo(buffer);
        return got;
    }

    /// <summary>
    /// 要求バイト数が揃うまで読む。デッドラインを過ぎたら読めた分だけ返す。
    /// SerialPort.Read は要求より少ない値を返しうるのでループが要る。
    /// </summary>
    private int TryReadExact(byte[] buffer, int offset, int count, TimeSpan timeout)
    {
        int total = 0;
        var deadline = DateTime.UtcNow + timeout;

        while (total < count)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            _port.ReadTimeout = Math.Max(1, (int)remaining.TotalMilliseconds);
            try
            {
                int n = _port.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        return total;
    }

    private void DrainInput()
    {
        try
        {
            if (_port.BytesToRead > 0)
            {
                Trace?.Invoke($"DRAIN {_port.BytesToRead} バイトの未処理受信データを破棄");
                _port.DiscardInBuffer();
            }
            _port.DiscardOutBuffer();
        }
        catch (InvalidOperationException)
        {
            // ポートが閉じられた。呼び出し側の Dispose 競合。
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            if (b != 0) return false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 終了時にスロット解放 (0x04(0) → 0x05) を送る実装を試したが、
        // SFC の初期化変更と併せて入れたところ認識不良を起こしたため外してある。
        // 解放しなくても次回接続時のウェイクアップで問題なく読めている。

        try { _port.Close(); } catch { /* 切断済み */ }
        _port.Dispose();
    }
}

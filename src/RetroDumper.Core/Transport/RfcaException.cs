namespace RetroDumper.Core.Transport;

public class RfcaException : Exception
{
    public RfcaException(string message) : base(message) { }
    public RfcaException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>リクエストに対して RFCA が期限内に応答しなかった。</summary>
public sealed class RfcaTimeoutException : RfcaException
{
    public RfcaTimeoutException(string message) : base(message) { }
}

/// <summary>RFCA が肯定応答以外を返した（未対応 opcode、範囲外アドレスなど）。</summary>
public sealed class RfcaNakException : RfcaException
{
    public byte[] Response { get; }

    public RfcaNakException(string message, byte[] response) : base(message)
        => Response = response;
}

/// <summary>
/// 書き込み保護が有効なときに書き込みを試みた。
/// カートリッジの内容を守るための遮断であり、通信上の異常ではない。
/// </summary>
public sealed class RfcaWriteBlockedException : RfcaException
{
    public RfcaWriteBlockedException(string message) : base(message) { }
}

/// <summary>
/// opcode 探索中、リードのつもりで送った要求がライト要求として受理された。
///
/// この時点ではまだカートリッジへの書き込みは起きていない。アダプタが
/// データ本体を待っている状態なので、ここで通信をやめれば書き込まれない。
/// ただし以後どんなバイトを送っても保留中の書き込みデータとして解釈され、
/// それが実際の書き込みになるため、アダプタを抜き差しするまで操作できない。
/// </summary>
public sealed class RfcaWriteOpcodePendingException : RfcaException
{
    public uint Opcode { get; }
    public uint Address { get; }

    public RfcaWriteOpcodePendingException(uint opcode, uint address)
        : base($"opcode 0x{opcode:X2} はライト系でした。アドレス 0x{address:X8} への書き込みが " +
               "保留状態になっています。カートリッジを保護するため通信を打ち切りました。" +
               "まだ何も書き込まれていません。アダプタを一度 USB から抜き差ししてください。")
    {
        Opcode = opcode;
        Address = address;
    }
}

/// <summary>
/// アダプタが USB バスから消えた。
///
/// ポート名が列挙から外れた状態。ドライバの問題ではなく、デバイスが
/// 再列挙に失敗している。未知の opcode を撃ち込んだ結果ファームが
/// リセット／ハングすると起こる。USB ケーブルの抜き差しで復帰する。
///
/// これが飛んだ時点で、直前に送ったコマンドが原因である可能性が高い。
/// 探索側はその opcode を記録して二度と送らないようにすること。
/// </summary>
public sealed class RfcaDisconnectedException : RfcaException
{
    /// <summary>切断の直前に送っていたコマンド（分かっている場合）。</summary>
    public uint? LastOpcode { get; }

    public RfcaDisconnectedException(string portName, uint? lastOpcode = null)
        : base($"{portName} が USB バスから消えました。" +
               (lastOpcode is uint op ? $"直前に送ったのは opcode 0x{op:X2} です。" : "") +
               "アダプタのファームが応答不能になっています。" +
               "USB ケーブルを一度抜いて挿し直してください。")
        => LastOpcode = lastOpcode;
}

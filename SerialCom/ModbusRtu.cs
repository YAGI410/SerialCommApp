using System;
using System.Collections.Generic;
using System.Linq;

namespace SerialCom
{
    /// <summary>
    /// 応答の判定結果（配線・設定の切り分け用）
    /// </summary>
    internal enum ModbusStatus
    {
        Ok,             // 正常応答
        NoResponse,     // 何も返ってこない
        EchoOnly,       // 自分の送信だけが戻ってきた
        CrcError,       // CRC 不一致（データ化け）
        Incomplete,     // 応答が途中で途切れた
        Exception,      // 例外応答（通信は正常）
        WrongSlave,     // 別の局番から応答
        WrongFunction,  // 想定外のファンクションコード
        LengthError,    // バイト数が要求と合わない
    }

    internal class ModbusResult
    {
        public ModbusStatus Status { get; set; }
        /// <summary>受信データの先頭に送信エコーがあった</summary>
        public bool HasEcho { get; set; }
        /// <summary>エコーを除いた応答フレーム</summary>
        public byte[] Frame { get; set; } = new byte[0];
        public byte ExceptionCode { get; set; }
        public ushort[] Registers { get; set; } = new ushort[0];
    }

    /// <summary>
    /// Modbus RTU のフレーム組み立てと応答解析
    /// </summary>
    internal static class ModbusRtu
    {
        public const byte ReadHoldingRegisters = 0x03;
        public const byte ReadInputRegisters = 0x04;
        public const int MaxReadQuantity = 125;

        /// <summary>
        /// CRC-16（多項式 0xA001、初期値 0xFFFF）
        /// </summary>
        public static ushort Crc16(IList<byte> data, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x0001) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
                }
            }
            return crc;
        }

        /// <summary>
        /// 末尾 2 バイト（下位、上位の順）の CRC が正しいか
        /// </summary>
        public static bool CheckCrc(IList<byte> frame)
        {
            if (frame.Count < 3) return false;
            var crc = Crc16(frame, frame.Count - 2);
            return frame[frame.Count - 2] == (byte)(crc & 0xFF) && frame[frame.Count - 1] == (byte)(crc >> 8);
        }

        /// <summary>
        /// レジスタ読み出し（03 / 04）の要求フレーム
        /// </summary>
        public static byte[] BuildReadRequest(byte slave, byte function, ushort start, ushort quantity)
        {
            var frame = new List<byte>
            {
                slave, function,
                (byte)(start >> 8), (byte)(start & 0xFF),
                (byte)(quantity >> 8), (byte)(quantity & 0xFF),
            };
            var crc = Crc16(frame, frame.Count);
            frame.Add((byte)(crc & 0xFF));
            frame.Add((byte)(crc >> 8));
            return frame.ToArray();
        }

        private static ushort RequestQuantity(byte[] request)
        {
            return (ushort)((request[4] << 8) | request[5]);
        }

        /// <summary>
        /// 受信データの先頭が送信エコーなら、その長さを返す（エコーがなければ 0）。
        /// 受信データがエコーの途中までしかない場合もエコー扱いにする。
        /// </summary>
        public static int EchoLength(byte[] request, IList<byte> received)
        {
            int n = Math.Min(request.Length, received.Count);
            if (n == 0) return 0;
            for (int i = 0; i < n; i++)
            {
                if (received[i] != request[i]) return 0;
            }
            return n;
        }

        /// <summary>
        /// エコーを除いた応答が揃ったか（正常応答または例外応答の長さに達したか）
        /// </summary>
        public static bool IsComplete(byte[] request, IList<byte> response)
        {
            if (response.Count < 5) return false;
            if (response[1] == (request[1] | 0x80)) return true;
            return response.Count >= 5 + RequestQuantity(request) * 2;
        }

        public static ModbusResult Analyze(byte[] request, IList<byte> received)
        {
            var result = new ModbusResult();
            if (received.Count == 0)
            {
                result.Status = ModbusStatus.NoResponse;
                return result;
            }

            int echo = EchoLength(request, received);
            result.HasEcho = echo == request.Length;
            var frame = received.Skip(result.HasEcho ? echo : 0).ToArray();
            result.Frame = frame;

            if (frame.Length == 0 || echo == received.Count)
            {
                result.Status = ModbusStatus.EchoOnly;
                return result;
            }

            byte function = request[1];
            ushort quantity = RequestQuantity(request);
            bool isException = frame.Length >= 2 && frame[1] == (function | 0x80);
            int expected = isException ? 5 : 5 + quantity * 2;

            if (!CheckCrc(frame))
            {
                result.Status = frame.Length < expected ? ModbusStatus.Incomplete : ModbusStatus.CrcError;
                return result;
            }
            if (frame[0] != request[0])
            {
                result.Status = ModbusStatus.WrongSlave;
                return result;
            }
            if (isException)
            {
                result.Status = ModbusStatus.Exception;
                result.ExceptionCode = frame[2];
                return result;
            }
            if (frame[1] != function)
            {
                result.Status = ModbusStatus.WrongFunction;
                return result;
            }
            if (frame[2] != quantity * 2 || frame.Length != expected)
            {
                result.Status = ModbusStatus.LengthError;
                return result;
            }

            result.Status = ModbusStatus.Ok;
            result.Registers = new ushort[quantity];
            for (int i = 0; i < quantity; i++)
            {
                result.Registers[i] = (ushort)((frame[3 + i * 2] << 8) | frame[4 + i * 2]);
            }
            return result;
        }

        public static string ExceptionName(byte code)
        {
            switch (code)
            {
                case 0x01: return "不正ファンクション";
                case 0x02: return "不正データアドレス";
                case 0x03: return "不正データ値";
                case 0x04: return "スレーブ機器異常";
                case 0x05: return "処理受付（ACK）";
                case 0x06: return "スレーブ機器ビジー";
                case 0x08: return "メモリパリティエラー";
                case 0x0A: return "ゲートウェイ経路なし";
                case 0x0B: return "ゲートウェイ先の応答なし";
                default: return "不明な例外";
            }
        }

        /// <summary>
        /// 判定結果の見出し
        /// </summary>
        public static string Title(ModbusResult result)
        {
            switch (result.Status)
            {
                case ModbusStatus.Ok: return "正常応答";
                case ModbusStatus.NoResponse: return "応答なし";
                case ModbusStatus.EchoOnly: return "送信エコーのみ（相手の応答なし）";
                case ModbusStatus.CrcError: return "CRC エラー（データ化け）";
                case ModbusStatus.Incomplete: return "応答が途中で途切れた";
                case ModbusStatus.Exception:
                    return "例外応答 0x" + result.ExceptionCode.ToString("X2") + "（" + ExceptionName(result.ExceptionCode) + "）";
                case ModbusStatus.WrongSlave:
                    return "別の局番から応答（局番 " + result.Frame[0] + "）";
                case ModbusStatus.WrongFunction: return "想定外のファンクションコード";
                case ModbusStatus.LengthError: return "データ長が要求と不一致";
                default: return result.Status.ToString();
            }
        }

        /// <summary>
        /// 考えられる原因・確認ポイント
        /// </summary>
        public static string Hint(ModbusResult result)
        {
            string hint;
            switch (result.Status)
            {
                case ModbusStatus.Ok:
                    hint = "配線・通信設定とも正常です。";
                    break;
                case ModbusStatus.NoResponse:
                    hint = "断線、A/B（+/-）の逆接続、局番違い、ボーレート・パリティ違い、相手機器の電源・通信設定を確認してください。";
                    break;
                case ModbusStatus.EchoOnly:
                    hint = "2線式で自分の送信が戻っています（PC 側の線は生きています）。相手側の配線、局番、通信設定を確認してください。";
                    break;
                case ModbusStatus.CrcError:
                    hint = "ボーレート・パリティ違い、終端抵抗なし、ノイズ、SG（GND）未接続を確認してください。";
                    break;
                case ModbusStatus.Incomplete:
                    hint = "タイムアウトが短いか、ノイズ・配線不良で欠落しています。タイムアウトを延ばして再確認してください。";
                    break;
                case ModbusStatus.Exception:
                    hint = "通信・配線は正常です。アドレス・個数・ファンクションが相手機器の仕様に合っているか確認してください。";
                    break;
                case ModbusStatus.WrongSlave:
                    hint = "局番の重複、または別の機器が応答しています。";
                    break;
                case ModbusStatus.WrongFunction:
                case ModbusStatus.LengthError:
                    hint = "CRC は正常なので通信はできています。相手機器の仕様を確認してください。";
                    break;
                default:
                    hint = "";
                    break;
            }
            if (result.HasEcho && result.Status != ModbusStatus.EchoOnly)
            {
                hint += "（送信エコーあり：2線式アダプターで自分の送信も受信しています）";
            }
            return hint;
        }
    }
}

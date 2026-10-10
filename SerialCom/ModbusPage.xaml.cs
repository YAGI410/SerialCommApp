using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using NumberBox = Microsoft.UI.Xaml.Controls.NumberBox;
using NumberBoxValueChangedEventArgs = Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs;

namespace SerialCom
{
    /// <summary>
    /// Modbus RTU の通信チェック（1 回の問い合わせと連続ポーリング）
    /// </summary>
    public sealed partial class ModbusPage : Page
    {
        private const String ButtonNameDisconnect = "Disconnect";
        private const String ButtonNameDisableReconnectToDevice = "Do not automatically reconnect to device that was just closed";
        private const int MaxLogLength = 100000;

        // 応答受信中のデータ（DataReceived も問い合わせ処理も UI スレッドで動くのでロック不要）
        private bool _collecting;
        private readonly List<byte> _rxBuffer = new List<byte>();
        private readonly List<int> _rxChunkEnds = new List<int>();
        private readonly List<double> _rxChunkTimes = new List<double>();
        private readonly Stopwatch _stopwatch = new Stopwatch();

        private bool _busy;
        private CancellationTokenSource _pollCancellation;

        // 統計
        private readonly Dictionary<ModbusStatus, int> _statCounts = new Dictionary<ModbusStatus, int>();
        private int _statSent;
        private readonly List<double> _statTimes = new List<double>();

        private EventHandlerForDevice _handler;
        private MainPage _mainPage;

        public ModbusPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _mainPage = (MainPage)e.Parameter;

            this.tbDatabit.Text = StaClass.Databit.ToString();
            this.tbPort.Text = StaClass.SerialDevInfomation != null ? StaClass.SerialDevInfomation.Name : "-";
            this.tbStopbit.Text = StaClass.Stopbit.ToString();
            this.tbParity.Text = StaClass.Parity.ToString();
            this.tbBaudrate.Text = StaClass.BaudRate.ToString();

            AttachHandler();
            UpdateConnectDisconnectButtonsAndList(!EventHandlerForDevice.Current.IsDeviceConnected);
            RequestPreview();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _pollCancellation?.Cancel();
            DetachHandler();
        }

        /// <summary>
        /// 現在のデバイスの受信・接続通知をこのページで受け取る
        /// </summary>
        private void AttachHandler()
        {
            DetachHandler();
            _handler = EventHandlerForDevice.Current;
            _handler.DataReceived += OnDataReceived;
            _handler.OnDeviceConnected = OnDeviceConnected;
            _handler.OnDeviceClose = OnDeviceClosing;
        }

        private void DetachHandler()
        {
            if (_handler != null)
            {
                _handler.DataReceived -= OnDataReceived;
                _handler = null;
            }
        }

        private async void bConnect_Click(object sender, RoutedEventArgs e)
        {
            if (StaClass.SerialDevInfomation == null)
            {
                _mainPage.NotifyUser("Setting でポートを選択してください", NotifyType.ErrorMessage);
                return;
            }

            EventHandlerForDevice.CreateNewEventHandlerForDevice();
            AttachHandler();

            try
            {
                Boolean openSuccess = await EventHandlerForDevice.Current.OpenDeviceAsync(StaClass.SerialDevInfomation);
                UpdateConnectDisconnectButtonsAndList(!openSuccess);
            }
            catch (Exception exception)
            {
                _mainPage.NotifyUser(exception.Message, NotifyType.ErrorMessage);
            }
        }

        private void bDisconnect_Click(object sender, RoutedEventArgs e)
        {
            _pollCancellation?.Cancel();

            var handler = EventHandlerForDevice.Current;
            if ((String)bDisconnect.Content == ButtonNameDisableReconnectToDevice)
            {
                handler.IsEnabledAutoReconnect = false;
            }

            handler.OnDeviceClose = null;
            handler.CloseDevice();

            bDisconnect.Content = ButtonNameDisconnect;
            UpdateConnectDisconnectButtonsAndList(true);
        }

        private void OnDeviceConnected(EventHandlerForDevice sender, DeviceInformation deviceInformation)
        {
            bDisconnect.Content = ButtonNameDisconnect;
            UpdateConnectDisconnectButtonsAndList(false);
        }

        private async void OnDeviceClosing(EventHandlerForDevice sender, DeviceInformation deviceInformation)
        {
            await _mainPage.Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal,
                new DispatchedHandler(() =>
                {
                    _pollCancellation?.Cancel();
                    if (bDisconnect.IsEnabled && EventHandlerForDevice.Current.IsEnabledAutoReconnect)
                    {
                        bDisconnect.Content = ButtonNameDisableReconnectToDevice;
                    }
                }));
        }

        private void UpdateConnectDisconnectButtonsAndList(Boolean enableConnectButton)
        {
            bConnect.IsEnabled = enableConnectButton;
            bDisconnect.IsEnabled = !bConnect.IsEnabled;
        }

        private void OnDataReceived(EventHandlerForDevice sender, byte[] data)
        {
            if (_collecting)
            {
                _rxBuffer.AddRange(data);
                _rxChunkEnds.Add(_rxBuffer.Count);
                _rxChunkTimes.Add(_stopwatch.Elapsed.TotalMilliseconds);
            }
            else
            {
                // 問い合わせ以外で届いたデータ（他の機器の通信など）
                AppendLog("RX", data, "（問い合わせ外）");
            }
        }

        private void Request_Changed(object sender, RoutedEventArgs e)
        {
            // InitializeComponent 中は他のコントロールがまだない
            if (tbBytes == null) return;
            RequestPreview();
        }

        private void NumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            Request_Changed(sender, null);
        }

        /// <summary>
        /// 入力内容から要求フレームを組み立てて表示する
        /// </summary>
        private byte[] RequestPreview()
        {
            String error;
            var request = BuildRequest(out error);
            tbRequestError.Text = error ?? "";
            var bytes = request ?? new byte[0];
            HomePage.FillByteGrid(dataGrid1, bytes, RequestHeaders);
            tbBytes.Text = bytes.Length.ToString();
            return request;
        }

        /// <summary>
        /// 読出し要求フレームの各バイトの意味（CRC は下位バイトが先）
        /// </summary>
        private static readonly String[] RequestHeaders =
        {
            "局番", "Func", "開始Hi", "開始Lo", "個数Hi", "個数Lo", "CRC Lo", "CRC Hi",
        };

        private byte[] BuildRequest(out String error)
        {
            int slave, start, quantity;
            if (!TryGetNumber(nbSlave, out slave))
            {
                error = "局番は 1～247 で入力してください";
                return null;
            }
            if (!TryParseNumber(tbStart.Text, 0, 0xFFFF, out start))
            {
                error = "開始アドレスは 0～65535（16進は 0x 付き）で入力してください";
                return null;
            }
            if (!TryGetNumber(nbQuantity, out quantity))
            {
                error = "個数は 1～125 で入力してください";
                return null;
            }
            byte function = cbFunction.SelectedIndex == 1 ? ModbusRtu.ReadInputRegisters : ModbusRtu.ReadHoldingRegisters;
            error = null;
            return ModbusRtu.BuildReadRequest((byte)slave, function, (ushort)start, (ushort)quantity);
        }

        /// <summary>
        /// 10 進、または 0x 付きの 16 進を読む
        /// </summary>
        private static bool TryParseNumber(String text, int min, int max, out int value)
        {
            text = (text ?? "").Trim();
            bool ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            return ok && value >= min && value <= max;
        }

        /// <summary>
        /// NumberBox の値を読む（範囲外は Minimum / Maximum に丸められる。空欄は NaN）
        /// </summary>
        internal static bool TryGetNumber(NumberBox box, out int value)
        {
            value = 0;
            if (double.IsNaN(box.Value)) return false;
            value = (int)Math.Round(box.Value);
            return value >= box.Minimum && value <= box.Maximum;
        }

        private async void b1Send_Click(object sender, RoutedEventArgs e)
        {
            var request = RequestPreview();
            int timeout;
            if (request == null || !CheckReady(out timeout)) return;

            SetBusy(true);
            try
            {
                await QueryAsync(request, timeout);
            }
            catch (Exception exception)
            {
                _mainPage.NotifyUser(exception.Message, NotifyType.ErrorMessage);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void bPollStart_Click(object sender, RoutedEventArgs e)
        {
            var request = RequestPreview();
            int timeout, count, interval;
            if (request == null || !CheckReady(out timeout)) return;
            if (!TryGetNumber(nbPollCount, out count))
            {
                _mainPage.NotifyUser("回数は 0～1000000 で入力してください", NotifyType.ErrorMessage);
                return;
            }
            if (!TryGetNumber(nbPollInterval, out interval))
            {
                _mainPage.NotifyUser("間隔は 0～60000 ms で入力してください", NotifyType.ErrorMessage);
                return;
            }

            ResetStatistics();
            _pollCancellation = new CancellationTokenSource();
            var token = _pollCancellation.Token;
            SetBusy(true);
            try
            {
                for (int i = 0; count == 0 || i < count; i++)
                {
                    if (token.IsCancellationRequested || !EventHandlerForDevice.Current.IsDeviceConnected) break;
                    await QueryAsync(request, timeout);
                    _mainPage.NotifyUser("Polling... " + (i + 1) + (count == 0 ? "" : " / " + count), NotifyType.StatusMessage);
                    if (count == 0 || i + 1 < count)
                    {
                        await Task.Delay(interval, token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stop ボタン・切断・画面移動
            }
            catch (Exception exception)
            {
                _mainPage.NotifyUser(exception.Message, NotifyType.ErrorMessage);
            }
            finally
            {
                _pollCancellation.Dispose();
                _pollCancellation = null;
                SetBusy(false);
                _mainPage.NotifyUser("Polling finished - 送信 " + _statSent + " 回", NotifyType.StatusMessage);
            }
        }

        private void bPollStop_Click(object sender, RoutedEventArgs e)
        {
            _pollCancellation?.Cancel();
        }

        private bool CheckReady(out int timeout)
        {
            timeout = 0;
            if (!EventHandlerForDevice.Current.IsDeviceConnected)
            {
                _mainPage.NotifyUser("Device is not connected", NotifyType.ErrorMessage);
                return false;
            }
            if (!TryGetNumber(nbTimeout, out timeout))
            {
                _mainPage.NotifyUser("タイムアウトは 50～10000 ms で入力してください", NotifyType.ErrorMessage);
                return false;
            }
            return true;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            b1Send.IsEnabled = !busy;
            bPollStart.IsEnabled = !busy;
            bPollStop.IsEnabled = busy && _pollCancellation != null;
        }

        /// <summary>
        /// 1 回問い合わせて、結果を表示・統計に加える
        /// </summary>
        private async Task QueryAsync(byte[] request, int timeoutMs)
        {
            var received = await TransactAsync(request, timeoutMs);
            var result = ModbusRtu.Analyze(request, received);

            // 応答時間：送信完了から、エコーを除いた応答の最初のバイトが届くまで
            double? responseTime = null;
            int echo = result.HasEcho ? request.Length : 0;
            for (int i = 0; i < _rxChunkEnds.Count; i++)
            {
                if (_rxChunkEnds[i] > echo)
                {
                    responseTime = _rxChunkTimes[i];
                    break;
                }
            }

            if (received.Length > 0)
            {
                AppendLog("RX", received, "");
            }
            AppendLog("--", new byte[0], ModbusRtu.Title(result));

            ShowResult(result, responseTime);
            AddStatistics(result, responseTime);
        }

        /// <summary>
        /// 要求を送信し、応答が揃うか、無応答のままタイムアウトするか、受信が途切れるまで待つ
        /// </summary>
        private async Task<byte[]> TransactAsync(byte[] request, int timeoutMs)
        {
            _rxBuffer.Clear();
            _rxChunkEnds.Clear();
            _rxChunkTimes.Clear();
            _collecting = true;
            try
            {
                var writer = new DataWriter(EventHandlerForDevice.Current.Device.OutputStream);
                try
                {
                    writer.WriteBytes(request);
                    await writer.StoreAsync();
                }
                finally
                {
                    writer.DetachStream();
                    writer.Dispose();
                }
                _stopwatch.Restart();
                sDate.Text = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
                AppendLog("TX", request, "");

                double gapMs = FrameGapMs();
                while (true)
                {
                    await Task.Delay(5);
                    double elapsed = _stopwatch.Elapsed.TotalMilliseconds;

                    int echo = ModbusRtu.EchoLength(request, _rxBuffer);
                    bool echoOnly = echo == _rxBuffer.Count;
                    if (echoOnly)
                    {
                        // まだ相手の応答が来ていない（エコーだけの場合を含む）
                        if (elapsed >= timeoutMs) break;
                        continue;
                    }

                    var response = _rxBuffer.Skip(echo == request.Length ? echo : 0).ToList();
                    if (ModbusRtu.IsComplete(request, response)) break;
                    // 受信が途切れたらフレームの終わりとみなす
                    if (elapsed - _rxChunkTimes[_rxChunkTimes.Count - 1] >= gapMs) break;
                    // 応答が途切れずに続く（ノイズなど）場合の上限
                    if (elapsed >= timeoutMs * 2 + 1000) break;
                }
                return _rxBuffer.ToArray();
            }
            finally
            {
                _collecting = false;
            }
        }

        /// <summary>
        /// フレームの区切りとみなす無受信時間。規格は 3.5 文字分だが、USB 変換器の遅延を考えて 50ms 以上にする
        /// </summary>
        private static double FrameGapMs()
        {
            int bits = 1 + StaClass.Databit + (StaClass.Parity == SerialParity.None ? 0 : 1)
                       + (StaClass.Stopbit == SerialStopBitCount.Two ? 2 : 1);
            double charMs = bits * 1000.0 / StaClass.BaudRate;
            return Math.Max(charMs * 3.5, 50);
        }

        private void ShowResult(ModbusResult result, double? responseTime)
        {
            tbResult.Text = ModbusRtu.Title(result);
            tbResult.Foreground = new SolidColorBrush(StatusColor(result.Status));
            tbHint.Text = ModbusRtu.Hint(result);
            tbResponseTime.Text = responseTime.HasValue ? "応答時間 " + responseTime.Value.ToString("0") + " ms" : "";

            int start;
            TryParseNumber(tbStart.Text, 0, 0xFFFF, out start);
            HomePage.FillByteGrid(dataGrid2, result.Frame, ResponseHeaders(result.Frame, start));
            tbRBytes.Text = result.Frame.Length.ToString();
            rDate.Text = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");

            if (result.Status == ModbusStatus.Ok)
            {
                var sb = new StringBuilder();
                sb.Append("Addr   HEX    符号なし  符号付き\r");
                for (int i = 0; i < result.Registers.Length; i++)
                {
                    var value = result.Registers[i];
                    sb.Append((start + i).ToString().PadLeft(5) + "  0x" + value.ToString("X4")
                              + value.ToString().PadLeft(9) + ((short)value).ToString().PadLeft(10) + "\r");
                }
                tbValues.Text = sb.ToString();
            }
            else
            {
                tbValues.Text = "";
            }
        }

        /// <summary>
        /// 応答フレームの各バイトの意味。FC の最上位ビットで正常応答と例外応答を見分ける。
        /// データは「アドレス Hi/Lo」、フレーム長を超えた分は「余分」とする
        /// </summary>
        private static List<String> ResponseHeaders(byte[] frame, int start)
        {
            var headers = new List<String>();
            if (frame.Length == 0) return headers;
            headers.Add("局番");
            if (frame.Length < 2) return headers;

            int length;
            if ((frame[1] & 0x80) != 0)
            {
                headers.Add("FC+80");
                headers.Add("例外コード");
                length = 5;
            }
            else
            {
                headers.Add("FC");
                headers.Add("バイト数");
                length = frame.Length >= 3 ? 3 + frame[2] + 2 : 5;
                for (int i = 0; i < length - 5; i++)
                {
                    headers.Add((start + i / 2) + (i % 2 == 0 ? " Hi" : " Lo"));
                }
            }
            headers.Add("CRC Lo");
            headers.Add("CRC Hi");
            while (headers.Count < frame.Length)
            {
                headers.Add("余分");
            }
            return headers;
        }

        private static Color StatusColor(ModbusStatus status)
        {
            switch (status)
            {
                case ModbusStatus.Ok: return Colors.SeaGreen;
                case ModbusStatus.Exception: return Colors.DarkOrange;   // 通信は正常、設定の問題
                default: return Colors.Red;
            }
        }

        private void ResetStatistics()
        {
            _statCounts.Clear();
            _statSent = 0;
            _statTimes.Clear();
            ShowStatistics();
        }

        private void AddStatistics(ModbusResult result, double? responseTime)
        {
            _statSent++;
            int count;
            _statCounts.TryGetValue(result.Status, out count);
            _statCounts[result.Status] = count + 1;
            if (result.Status == ModbusStatus.Ok && responseTime.HasValue)
            {
                _statTimes.Add(responseTime.Value);
            }
            ShowStatistics();
        }

        private int StatCount(params ModbusStatus[] statuses)
        {
            int sum = 0;
            foreach (var status in statuses)
            {
                int count;
                if (_statCounts.TryGetValue(status, out count)) sum += count;
            }
            return sum;
        }

        private void ShowStatistics()
        {
            int ok = StatCount(ModbusStatus.Ok);
            int exception = StatCount(ModbusStatus.Exception);
            tbStatSent.Text = "送信 " + _statSent;
            tbStatOk.Text = "正常 " + ok;
            tbStatNoResponse.Text = "応答なし " + StatCount(ModbusStatus.NoResponse, ModbusStatus.EchoOnly);
            tbStatCrc.Text = "CRC・途切れ " + StatCount(ModbusStatus.CrcError, ModbusStatus.Incomplete);
            tbStatException.Text = "例外 " + exception;
            tbStatOther.Text = "その他 " + StatCount(ModbusStatus.WrongSlave, ModbusStatus.WrongFunction, ModbusStatus.LengthError);

            // 例外応答は通信としては成功なので、エラー率には含めない
            tbStatErrorRate.Text = _statSent == 0
                ? "エラー率 -"
                : "エラー率 " + ((_statSent - ok - exception) * 100.0 / _statSent).ToString("0.0") + "%";
            tbStatTime.Text = _statTimes.Count == 0
                ? "応答時間 -"
                : "応答時間 " + _statTimes.Min().ToString("0") + " / " + _statTimes.Average().ToString("0") + " / "
                  + _statTimes.Max().ToString("0") + " ms（最小/平均/最大）";
        }

        private void bRClear_Click(object sender, RoutedEventArgs e)
        {
            tbResult.Text = "-";
            tbResult.ClearValue(TextBlock.ForegroundProperty);
            tbHint.Text = "";
            tbResponseTime.Text = "";
            HomePage.FillByteGrid(dataGrid2, new byte[0]);
            tbRBytes.Text = "0";
            tbValues.Text = "";
            tbLog.Text = "";
            if (!_busy) ResetStatistics();
        }

        /// <summary>
        /// 送受信ログに 1 行追加（新しいものが上）
        /// </summary>
        private void AppendLog(string direction, byte[] data, string note)
        {
            var hex = String.Join(" ", data.Select(b => b.ToString("X2")));
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + direction + " " + hex;
            if (note != "")
            {
                line += (hex == "" ? "" : "  ") + note;
            }

            var text = line + "\r" + tbLog.Text;
            if (text.Length > MaxLogLength)
            {
                text = text.Substring(0, MaxLogLength);
            }
            tbLog.Text = text;
        }
    }
}

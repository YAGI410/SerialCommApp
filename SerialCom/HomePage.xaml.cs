using Microsoft.Toolkit.Uwp.UI.Controls;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Windows.Devices.Enumeration;
using Windows.Devices.PointOfService;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// 空白ページの項目テンプレートについては、https://go.microsoft.com/fwlink/?LinkId=234238 を参照してください

namespace SerialCom
{
    /// <summary>
    /// それ自体で使用できる空白ページまたはフレーム内に移動できる空白ページ。
    /// </summary>
    public sealed partial class HomePage : Page
    {
        private const String ButtonNameDisconnect = "Disconnect";
        private const String ButtonNameDisableReconnectToDevice = "Do not automatically reconnect to device that was just closed";

        // 受信表示
        private static readonly TimeSpan FrameGap = TimeSpan.FromMilliseconds(200);
        private const int MaxLogLength = 100000;
        private readonly List<byte> _rFrame = new List<byte>();
        private DateTime _lastReceived = DateTime.MinValue;

        private ObservableCollection<ObservableCollection<byte>> _bytes;
        private ObservableCollection<bytesRow> _rows;

        private Boolean IsWriteTaskPending;
        DataWriter DataWriteObject = null;

        // 書き込みキャンセル用
        private CancellationTokenSource WriteCancellationTokenSource;
        private Object WriteCancelLock = new Object();



        private MainPage _mainPage;
        public HomePage()
        {
            this.InitializeComponent();
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            // base.OnNavigatedTo(e);
            _mainPage = (MainPage)e.Parameter;
            var newRow = new bytesRow();
            _rows = new ObservableCollection<bytesRow>();
            foreach (var item in _mainPage.bytes)
            {
                newRow.Bytes.Add(item.ToString());
            }
            _rows.Add(newRow);
            _bytes = new ObservableCollection<ObservableCollection<byte>>();
            _bytes.Add(_mainPage.bytes);
            DataGridRefresh();

            this.tbDatabit.Text = StaClass.Databit.ToString();
            this.tbPort.Text = StaClass.SerialDevInfomation != null ? StaClass.SerialDevInfomation.Name : "-";
            this.tbStopbit.Text = StaClass.Stopbit.ToString();
            this.tbParity.Text = StaClass.Parity.ToString();
            this.tbBaudrate.Text = StaClass.BaudRate.ToString();
        }

        private void bText_Click(object sender, RoutedEventArgs e)
        {
            string inputString = this.tbSending.Text;
            byte[] byteArray = Encoding.ASCII.GetBytes(inputString);
            // バイト配列の内容を16進数表記で表示
            foreach (byte b in byteArray)
            {
                _bytes[0].Add(b);
            }

            DataGridRefresh();
        }

        private void bClear_Click(object sender, RoutedEventArgs e)
        {
            _mainPage.bytes.Clear();
            //this.dataGrid1.Columns.Clear();
            //this.dataGrid1.ItemsSource = null;
            //this.tbBytes.Text = _bytes[0].Count.ToString();
            DataGridRefresh();
        }

        private void bhex_Click(object sender, RoutedEventArgs e)
        {
            byte newByte = 0;
            NumberStyles styles = NumberStyles.HexNumber;
            if (byte.TryParse(this.tbHex.Text,styles,null as IFormatProvider, out newByte))
            {
                _bytes[0].Add(newByte);
                DataGridRefresh();
                this.tbHex.Text = "";
            }
        }
        /// <summary>
        /// DataGridにMainを読み込み
        /// </summary>
        private void DataGridRefresh()
        {
            SendViewRefresh();
            this.tbSending.Text = "";
        }

        /// <summary>
        /// 送信データ（End Code 付き）を DataGrid と Byte 数に反映
        /// </summary>
        private void SendViewRefresh()
        {
            var sendBytes = BuildSendBytes();
            _rows = FillByteGrid(this.dataGrid1, sendBytes);
            this.tbBytes.Text = sendBytes.Length.ToString();
        }

        /// <summary>
        /// 選択中の End Code
        /// </summary>
        private byte[] GetEndCode()
        {
            if (rbEndCrLf.IsChecked == true) return new byte[] { 0x0D, 0x0A };
            if (rbEndCr.IsChecked == true) return new byte[] { 0x0D };
            return new byte[0];
        }

        /// <summary>
        /// 実際に送信するバイト列（入力データ + End Code）
        /// </summary>
        private byte[] BuildSendBytes()
        {
            return _bytes[0].Concat(GetEndCode()).ToArray();
        }

        private void EndCode_Checked(object sender, RoutedEventArgs e)
        {
            // InitializeComponent 中（IsChecked="True" の初期化）は表示対象がまだない
            if (_bytes == null) return;
            SendViewRefresh();
        }

        /// <summary>
        /// 1バイト1列で DataGrid に表示する（ヘッダーは ASCII、セルは 16進）
        /// </summary>
        private static ObservableCollection<bytesRow> FillByteGrid(DataGrid grid, IEnumerable<byte> data)
        {
            grid.Columns.Clear();
            grid.ItemsSource = null;

            var i = 1;
            var rows = new ObservableCollection<bytesRow>();
            var newRow1 = new bytesRow();

            foreach (var item in data)
            {
                DataGridTextColumn column1 = new DataGridTextColumn();

                var bytearray1 = new byte[] { item, };
                column1.Header = Encoding.ASCII.GetString(bytearray1); // 列1のヘッダー

                newRow1.Bytes.Add("0x" + item.ToString("X2"));
                column1.Binding = new Binding();
                column1.Binding.Source = newRow1.Bytes[i - 1];
                column1.Binding.Mode = BindingMode.TwoWay;
                grid.Columns.Add(column1);

                i++;
            }
            rows.Add(newRow1);
            grid.ItemsSource = rows;
            return rows;
        }

        private async void b1Send_Click(object sender, RoutedEventArgs e)
        {
            if (EventHandlerForDevice.Current != null && EventHandlerForDevice.Current.IsDeviceConnected)
            {
                try
                {
                    _mainPage.NotifyUser("Writing...", NotifyType.StatusMessage);

                    // 書き込み完了まで送信ボタンを無効にする
                    IsWriteTaskPending = true;
                    WriteCancellationTokenSource = new CancellationTokenSource();
                    DataWriteObject = new DataWriter(EventHandlerForDevice.Current.Device.OutputStream);
                    UpdateWriteButtonStates();

                    await WriteAsync(WriteCancellationTokenSource.Token);
                }
                catch (OperationCanceledException /*exception*/)
                {
                    NotifyWriteTaskCanceled();
                }
                catch (Exception exception)
                {
                    _mainPage.NotifyUser(exception.Message.ToString(), NotifyType.ErrorMessage);
                    Debug.WriteLine(exception.Message.ToString());
                }
                finally
                {
                    IsWriteTaskPending = false;
                    if (DataWriteObject != null)
                    {
                        DataWriteObject.DetachStream();
                        DataWriteObject = null;
                    }
                    WriteCancellationTokenSource?.Dispose();
                    WriteCancellationTokenSource = null;

                    UpdateWriteButtonStates();
                }
            }
            else
            {
                _mainPage.NotifyUser("Device is not connected", NotifyType.ErrorMessage);
            }
        }

        /// <summary>
        /// 書き込み中は送信ボタンを無効にする
        /// </summary>
        private void UpdateWriteButtonStates()
        {
            b1Send.IsEnabled = !IsPerformingWrite();
        }

        private void NotifyWriteTaskCanceled()
        {
            _mainPage.NotifyUser("Write request canceled", NotifyType.StatusMessage);
        }

        private Boolean IsPerformingWrite()
        {
            return (IsWriteTaskPending);
        }
        private async void bConnect_Click(object sender, RoutedEventArgs e)
        {
            if (StaClass.SerialDevInfomation == null)
            {
                _mainPage.NotifyUser("Setting でポートを選択してください", NotifyType.ErrorMessage);
                return;
            }

            // Create an EventHandlerForDevice to watch for the device we are connecting to
            // シングルトンクラス
            EventHandlerForDevice.CreateNewEventHandlerForDevice();

            // Get notified when the device was successfully connected to or about to be closed
            // デバイスが正常に接続されたとき、または閉じられようとしているときに通知を受け取ります
            EventHandlerForDevice.Current.OnDeviceConnected = OnDeviceConnected;
            EventHandlerForDevice.Current.OnDeviceClose = OnDeviceClosing;
            EventHandlerForDevice.Current.DataReceived += OnDataReceived;

            try
            {
                // It is important that the FromIdAsync call is made on the UI thread because the consent prompt, when present,
                // can only be displayed on the UI thread. Since this method is invoked by the UI, we are already in the UI thread.
                Boolean openSuccess = await EventHandlerForDevice.Current.OpenDeviceAsync(StaClass.SerialDevInfomation);

                // Disable connect button if we connected to the device
                UpdateConnectDisconnectButtonsAndList(!openSuccess);
            }
            catch (Exception exception)
            {
                _mainPage.NotifyUser(exception.Message, NotifyType.ErrorMessage);
            }
        }

        private void bDisconnect_Click(object sender, RoutedEventArgs e)
        {
            var handler = EventHandlerForDevice.Current;
            if ((String)bDisconnect.Content == ButtonNameDisableReconnectToDevice)
            {
                // 抜かれたデバイスの自動再接続をやめる
                handler.IsEnabledAutoReconnect = false;
            }

            // 手動切断なので「再接続しない」表示への切り替えは不要
            handler.OnDeviceClose = null;
            handler.DataReceived -= OnDataReceived;
            handler.CloseDevice();

            bDisconnect.Content = ButtonNameDisconnect;
            UpdateConnectDisconnectButtonsAndList(true);
        }

        /// <summary>
        /// 受信データを表示する。受信間隔が FrameGap 以上空いたら新しいフレームとして扱う。
        /// </summary>
        private void OnDataReceived(EventHandlerForDevice sender, byte[] data)
        {
            var now = DateTime.Now;
            if (now - _lastReceived > FrameGap)
            {
                _rFrame.Clear();
            }
            _lastReceived = now;
            _rFrame.AddRange(data);

            FillByteGrid(this.dataGrid2, _rFrame);
            this.tbRBytes.Text = _rFrame.Count.ToString();
            rDate.Text = now.ToString("yyyy/MM/dd HH:mm:ss");
            AppendLog("RX", data);
        }

        private void bRClear_Click(object sender, RoutedEventArgs e)
        {
            _rFrame.Clear();
            FillByteGrid(this.dataGrid2, _rFrame);
            this.tbRBytes.Text = "0";
            tbLog.Text = "";
        }

        /// <summary>
        /// 送受信ログに 1 行追加（新しいものが上）
        /// </summary>
        private void AppendLog(string direction, byte[] data)
        {
            var hex = String.Join(" ", data.Select(b => b.ToString("X2")));
            var ascii = new String(data.Select(b => b >= 0x20 && b < 0x7F ? (char)b : '.').ToArray());
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + direction + " " + hex + "  |" + ascii + "|";

            var text = line + "\r" + tbLog.Text;
            if (text.Length > MaxLogLength)
            {
                text = text.Substring(0, MaxLogLength);
            }
            tbLog.Text = text;
        }
        /// <summary>
        /// すべてのデバイスが列挙されている場合は、接続したリスト内のデバイスを選択します。
        /// それ以外の場合は、デバイス ウォッチャーからの EnumerationComplete イベントでデバイスの選択を処理します。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceInformation"></param>
        private void OnDeviceConnected(EventHandlerForDevice sender, DeviceInformation deviceInformation)
        {
            bDisconnect.Content = ButtonNameDisconnect;
            UpdateConnectDisconnectButtonsAndList(false);

            if (EventHandlerForDevice.Current.Device.PortName != "")
            {
                _mainPage.NotifyUser("Connected to - " +
                                    EventHandlerForDevice.Current.Device.PortName +
                                    " - " +
                                    EventHandlerForDevice.Current.DeviceInformation.Id, NotifyType.StatusMessage);
            }
            else
            {
                _mainPage.NotifyUser("Connected to - " +
                                    EventHandlerForDevice.Current.DeviceInformation.Id, NotifyType.StatusMessage);
            }
        }
        /// <summary>
        /// デバイスが閉じられました。デバイスに自動再接続する場合は、UIに反映されます。
        /// The device was closed. If we will autoreconnect to the device, reflect that in the UI
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceInformation"></param>
        private async void OnDeviceClosing(EventHandlerForDevice sender, DeviceInformation deviceInformation)
        {
            await _mainPage.Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal,
                new DispatchedHandler(() =>
                {
                    // We were connected to the device that was unplugged, so change the "Disconnect from device" button
                    // to "Do not reconnect to device"
                    if (bDisconnect.IsEnabled && EventHandlerForDevice.Current.IsEnabledAutoReconnect)
                    {
                        bDisconnect.Content = ButtonNameDisableReconnectToDevice;
                    }
                }));
        }
        /// <summary>
        /// 接続ボタンと切断ボタンの無効化
        /// </summary>
        /// <param name="enableConnectButton">The state of ButtonConnectToDevice</param>
        private void UpdateConnectDisconnectButtonsAndList(Boolean enableConnectButton)
        {
            bConnect.IsEnabled = enableConnectButton;
            bDisconnect.IsEnabled = !bConnect.IsEnabled;
        }
        private async Task WriteAsync(CancellationToken cancellationToken)
        {

            Task<UInt32> storeAsyncTask;

            var sendBytes = BuildSendBytes();
            if (sendBytes.Length != 0)
            {
                DataWriteObject.WriteBytes(sendBytes);

                // Don't start any IO if we canceled the task
                lock (WriteCancelLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Cancellation Token will be used so we can stop the task operation explicitly
                    // The completion function should still be called so that we can properly handle a canceled task
                    storeAsyncTask = DataWriteObject.StoreAsync().AsTask(cancellationToken);
                }

                UInt32 bytesWritten = await storeAsyncTask;
                sDate.Text = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
                AppendLog("TX", sendBytes);
                _mainPage.NotifyUser("Write completed - " + bytesWritten.ToString() + " bytes written", NotifyType.StatusMessage);
            }
            else
            {
                _mainPage.NotifyUser("No input received to write", NotifyType.StatusMessage);
            }

        }
    }
    public class bytesRow
    {
        public ObservableCollection<string> Bytes { get; set; } = new ObservableCollection<string>();
    }
}

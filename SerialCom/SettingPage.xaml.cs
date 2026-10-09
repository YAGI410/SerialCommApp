using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using Windows.Foundation;
using Windows.Foundation.Collections;
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
    public sealed partial class SettingPage : Page
    {
        public SettingPage()
        {
            this.InitializeComponent();
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
           LoadSerialPorts();

        }

        private async void LoadSerialPorts()
        {
            StaClass.portNames = new System.Collections.ObjectModel.ObservableCollection<string>();

            string selector = SerialDevice.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            foreach (var device in devices)
            {
                StaClass.portNames.Add(device.Name);
            }
            // コンボボックスにポート名を追加
            cbCom.ItemsSource = devices;
            cbBaudrate.ItemsSource = StaClass.BaudRates;
            cbParity.ItemsSource = StaClass.Parities;
            cbDatabit.ItemsSource = StaClass.Databits;
            cbStopbit.ItemsSource= StaClass.Stopbits;

            // 現在の設定を選択状態にする
            cbCom.SelectedItem = devices.FirstOrDefault(d => d.Id == StaClass.SerialDevInfomation?.Id);
            cbBaudrate.SelectedItem = StaClass.BaudRate;
            cbParity.SelectedItem = StaClass.Parity;
            cbDatabit.SelectedItem = StaClass.Databit;
            cbStopbit.SelectedItem = StaClass.Stopbit;
        }

        private void bOK_Click(object sender, RoutedEventArgs e)
        {
            if (cbCom.SelectedItem == null || cbBaudrate.SelectedItem == null || cbDatabit.SelectedItem == null
                || cbParity.SelectedItem == null || cbStopbit.SelectedItem == null)
            {
                MainPage.Current.NotifyUser("未選択の項目があります", NotifyType.ErrorMessage);
                return;
            }

            StaClass.BaudRate = (int)cbBaudrate.SelectedItem;
            StaClass.Databit = (int)cbDatabit.SelectedItem;
            StaClass.Parity = (SerialParity)cbParity.SelectedItem;
            StaClass.Stopbit = (SerialStopBitCount)cbStopbit.SelectedItem;

            // ポートはここでは開かない（Home の Connect で開く）
            var newDevInfo = (DeviceInformation)cbCom.SelectedItem;
            var portChanged = StaClass.SerialDevInfomation?.Id != newDevInfo.Id;
            StaClass.SerialDevInfomation = newDevInfo;

            var handler = EventHandlerForDevice.Current;
            if (handler.IsDeviceConnected && !portChanged)
            {
                // 接続中なら通信設定をその場で反映
                StaClass.ApplyTo(handler.Device);
                MainPage.Current.NotifyUser("設定を保存し、接続中のポートに反映しました", NotifyType.StatusMessage);
            }
            else if (handler.IsDeviceConnected)
            {
                MainPage.Current.NotifyUser("設定を保存しました。ポートの変更は Disconnect → Connect で反映されます", NotifyType.StatusMessage);
            }
            else
            {
                MainPage.Current.NotifyUser("設定を保存しました", NotifyType.StatusMessage);
            }
        }
    }
}

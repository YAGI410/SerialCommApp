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

            //
            cbCom.SelectedItem = StaClass.MySerial;
            cbBaudrate.SelectedItem = StaClass.BaudRate;
            cbParity.SelectedItem = StaClass.Parity;
            cbDatabit.SelectedItem = StaClass.Databit;
            cbStopbit.SelectedItem = StaClass.Stopbit;
        }

        private async void bOK_Click(object sender, RoutedEventArgs e)
        {
            StaClass.BaudRate = (int)cbBaudrate.SelectedItem;
            StaClass.Databit = (int)cbDatabit.SelectedItem;
            StaClass.Parity = (int)cbParity.SelectedItem;
            StaClass.Stopbit = (int)cbStopbit.SelectedItem;
            StaClass.SerialDevInfomation = (DeviceInformation)cbCom.SelectedItem;
            var newSerial = await SerialDevice.FromIdAsync(StaClass.SerialDevInfomation.Id);
            StaClass.MySerial = newSerial;
        }
    }
}

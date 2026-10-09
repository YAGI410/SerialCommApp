using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Devices.Enumeration;

namespace SerialCom
{
    static class StaClass
    {
        public static ObservableCollection<string> portNames { get; set; }
        public static ObservableCollection<int> BaudRates { get; }
        public static ObservableCollection<int> Databits { get; }
        public static ObservableCollection<SerialParity> Parities { get; }
        public static ObservableCollection<SerialStopBitCount> Stopbits { get; }

        public static DeviceInformation SerialDevInfomation  { get; set; }
        public static int BaudRate { get; set; } = 9600;
        public static int Databit { get; set; } = 7;
        public static SerialParity Parity { get; set; } = SerialParity.None;
        public static SerialStopBitCount Stopbit { get; set; } = SerialStopBitCount.One;
        public static int TimeOutValue { get; set; } = 0;

        static StaClass()
        {
            portNames = new ObservableCollection<string>();
            BaudRates = new ObservableCollection<int>() {
            4800,9600,19200,38400,115200,
            };
            Databits = new ObservableCollection<int>() {
            7,8
            };
            Parities = new ObservableCollection<SerialParity>() {
            SerialParity.None,SerialParity.Odd,SerialParity.Even
            };
            Stopbits = new ObservableCollection<SerialStopBitCount>() {
            SerialStopBitCount.One,SerialStopBitCount.Two
            };
        }

        /// <summary>
        /// 通信設定をシリアルデバイスに反映する
        /// </summary>
        public static void ApplyTo(SerialDevice device)
        {
            device.BaudRate = (uint)BaudRate;
            device.DataBits = (ushort)Databit;
            device.Parity = Parity;
            device.StopBits = Stopbit;
            device.Handshake = SerialHandshake.None;
            // 受信は短いタイムアウトで区切って、届いた分だけ読み出す
            device.ReadTimeout = TimeSpan.FromMilliseconds(50);
            device.WriteTimeout = TimeSpan.FromMilliseconds(1000);
        }
    }
}

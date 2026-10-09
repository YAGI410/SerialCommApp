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
        public static ObservableCollection<int> Parities { get; }
        public static ObservableCollection<int> Stopbits { get; }

        public static SerialDevice MySerial { get; set; }

        public static DeviceInformation SerialDevInfomation  { get; set; }
        public static int BaudRate { get; set; } = 9600;
        public static int Databit { get; set; } = 7;
        public static int Parity { get; set; } = 0;
        public static int Stopbit { get; set; } = 1;
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
            Parities = new ObservableCollection<int>() {
            0,1,2
            };
            Stopbits = new ObservableCollection<int>() {
            1,2
            };
        }
    }
}

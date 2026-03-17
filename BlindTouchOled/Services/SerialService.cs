using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace BlindTouchOled.Services
{
    public interface ISerialService
    {
        string[] GetAvailablePorts();
        bool IsPortPresent(string portName);
        bool ProbeConnection();
        bool Connect(string portName, int baudRate = 115200);
        void Disconnect();
        bool IsConnected { get; }
        event Action? ConnectionLost;
        Task SendDataAsync(byte[] data);
    }

    public class SerialService : ISerialService
    {
        private SerialPort? _serialPort;
        public event Action? ConnectionLost;

        public bool IsConnected => _serialPort?.IsOpen ?? false;

        public string[] GetAvailablePorts()
        {
            try
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    var allPorts = SerialPort.GetPortNames();
                    var usbPorts = GetUsbLikePortsFromWmi();

                    if (usbPorts.Length > 0)
                    {
                        return usbPorts;
                    }

                    // Fallback: if WMI is unavailable, avoid obvious onboard legacy ports like COM1.
                    return allPorts
                        .Where(p => !string.Equals(p, "COM1", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SerialPort not supported: {ex.Message}");
            }
            return Array.Empty<string>();
        }

        public bool IsPortPresent(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                return false;
            }

            try
            {
                return SerialPort.GetPortNames()
                    .Any(p => string.Equals(p, portName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static string[] GetUsbLikePortsFromWmi()
        {
            try
            {
                var results = new List<string>();
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                using var collection = searcher.Get();

                foreach (var item in collection)
                {
                    var name = item?["Name"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    // Keep USB/UART bridge style serial ports. This excludes built-in COM1 on most PCs.
                    var looksLikeUsbSerial =
                        name.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("UART", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("CH340", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("CP210", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("FTDI", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("CDC", StringComparison.OrdinalIgnoreCase);

                    if (!looksLikeUsbSerial)
                    {
                        continue;
                    }

                    var m = Regex.Match(name, @"\((COM\d+)\)", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        results.Add(m.Groups[1].Value.ToUpperInvariant());
                    }
                }

                return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public bool Connect(string portName, int baudRate = 115200)
        {
            try
            {
                Disconnect();
                _serialPort = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };
                _serialPort.DtrEnable = true;
                _serialPort.RtsEnable = true;
                _serialPort.Open();
                return true;
            }
            catch
            {
                Disconnect();
                return false;
            }
        }

        public void Disconnect()
        {
            if (_serialPort != null)
            {
                if (_serialPort.IsOpen)
                    _serialPort.Close();
                _serialPort.Dispose();
                _serialPort = null;
            }
        }

        public bool ProbeConnection()
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                return false;
            }

            try
            {
                // Accessing line state and queue stats often throws quickly after unplug on Windows USB-Serial.
                _ = _serialPort.BytesToWrite;
                _ = _serialPort.BytesToRead;
                _ = _serialPort.CDHolding;
                _ = _serialPort.CtsHolding;
                _ = _serialPort.DsrHolding;
                return true;
            }
            catch
            {
                HandleConnectionLost();
                return false;
            }
        }

        private void HandleConnectionLost()
        {
            Disconnect();
            try
            {
                ConnectionLost?.Invoke();
            }
            catch
            {
                // Keep serial layer safe and non-throwing.
            }
        }

        public async Task SendDataAsync(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            if (IsConnected && _serialPort != null && _serialPort.IsOpen)
            {
                // ログ: 先頭バイト（明るさ）とデータサイズを記録
                string logMsg = $"Serial TX: {data.Length} bytes, brightness_byte={data[0]}";
                BlindTouchOled.ViewModels.MainViewModel.FileLog(logMsg);

                try
                {
                    using (var cts = new System.Threading.CancellationTokenSource(1000))
                    {
                        await _serialPort.BaseStream.WriteAsync(data, 0, data.Length, cts.Token);
                        await _serialPort.BaseStream.FlushAsync(cts.Token);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Serial Send Error: {ex.Message}");
                    BlindTouchOled.ViewModels.MainViewModel.FileLog($"SerialService Exception: {ex.Message}");
                    HandleConnectionLost();
                }
            }
        }
    }
}

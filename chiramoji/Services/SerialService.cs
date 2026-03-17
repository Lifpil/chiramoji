using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Chiramoji.Services
{
    public interface ISerialService
    {
        string[] GetAvailablePorts();
        bool IsPortPresent(string portName);
        bool ProbeConnection();
        bool Connect(string portName, int baudRate = 115200);
        void Disconnect();
        bool IsConnected { get; }
        string ReadFirmwareVersion(int timeoutMs = 900);
        Task<(bool Success, string Message)> UploadMainPyAsync(string scriptText, CancellationToken cancellationToken = default);
        event Action? ConnectionLost;
        Task SendDataAsync(byte[] data);
    }

    public class SerialService : ISerialService
    {
        private SerialPort? _serialPort;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private string _lastRawReplTrace = string.Empty;
        private readonly List<string> _rawReplTraceHistory = new();
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

                Thread.Sleep(180);
                try
                {
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();
                }
                catch
                {
                }

                return true;
            }
            catch
            {
                Disconnect();
                return false;
            }
        }

        public string ReadFirmwareVersion(int timeoutMs = 900)
        {
            if (!IsConnected || _serialPort == null || !_serialPort.IsOpen)
            {
                return "Not connected";
            }

            var lockTaken = false;
            try
            {
                lockTaken = _sendLock.Wait(120);
                if (!lockTaken)
                {
                    return "Busy";
                }

                var detected = TryReadFirmwareVersionInternal(timeoutMs);
                if (!string.IsNullOrWhiteSpace(detected))
                {
                    return detected;
                }

                detected = TryReadFirmwareVersionInternal(Math.Max(1800, timeoutMs / 2));
                return string.IsNullOrWhiteSpace(detected) ? "鬯ｮ・｣陋ｹ繝ｻ・ｽ・ｽ繝ｻ・ｳ鬯ｮ・｢繝ｻ・ｧ郢晢ｽｻ繝ｻ・ｴ鬩幢ｽ｢隴趣ｽ｢繝ｻ・ｽ繝ｻ・ｻ" : detected;
            }
            catch
            {
                return "鬯ｮ・｣陋ｹ繝ｻ・ｽ・ｽ繝ｻ・ｳ鬯ｮ・｢繝ｻ・ｧ郢晢ｽｻ繝ｻ・ｴ鬩幢ｽ｢隴趣ｽ｢繝ｻ・ｽ繝ｻ・ｻ";
            }
            finally
            {
                if (lockTaken)
                {
                    _sendLock.Release();
                }
            }
        }

        private string? TryReadFirmwareVersionInternal(int timeoutMs)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                return null;
            }

            Thread.Sleep(40);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = string.Empty;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(60);
                text += _serialPort.ReadExisting();
                var m = Regex.Match(text, @"FW\s*:\s*v?(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    return $"v{m.Groups[1].Value}";
                }
            }

            return null;
        }

        public async Task<(bool Success, string Message)> UploadMainPyAsync(string scriptText, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(scriptText))
            {
                return (false, "FW script is empty.");
            }

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (!IsConnected || _serialPort == null || !_serialPort.IsOpen)
                {
                    return (false, "Device is not connected.");
                }

                // Reopen first so the device starts FW update from a clean serial stream.
                if (!ReopenCurrentPort())
                {
                    return (false, "Failed to reopen port before FW update.");
                }

                var packetMode = await TryUploadViaPacketProtocolAsync(scriptText, cancellationToken);
                if (packetMode.Supported)
                {
                    return (packetMode.Success, packetMode.Message);
                }

                // Fallback for old firmware that does not implement update packet protocol.
                var rawResult = await UploadViaRawReplAsync(scriptText, cancellationToken);
                if (rawResult.Success)
                {
                    return rawResult;
                }

                var packetReason = string.IsNullOrWhiteSpace(packetMode.Message)
                    ? "packet mode unsupported"
                    : packetMode.Message;
                return (false, $"packet: {packetReason} | raw: {rawResult.Message}");
            }
            catch (OperationCanceledException)
            {
                return (false, "FW update was canceled.");
            }
            catch (Exception ex)
            {
                return (false, $"FW update error: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task<(bool Supported, bool Success, string Message)> TryUploadViaPacketProtocolAsync(string scriptText, CancellationToken cancellationToken)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                return (false, false, "");
            }

            try
            {
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
            }
            catch
            {
            }

            byte[] scriptBytes = Encoding.UTF8.GetBytes(scriptText.Replace("\r\n", "\n"));

            await WriteUpdatePacketAsync(1, Array.Empty<byte>(), cancellationToken);
            var beginResp = await ReadForAsync(1200, cancellationToken);
            if (!beginResp.Contains("UPD:BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                var reason = string.IsNullOrWhiteSpace(beginResp)
                    ? "UPD:BEGIN response was empty"
                    : $"unexpected response: {Shorten(beginResp, 200)}";
                return (false, false, reason);
            }

            const int chunkSize = 1536;
            for (int i = 0; i < scriptBytes.Length; i += chunkSize)
            {
                int len = Math.Min(chunkSize, scriptBytes.Length - i);
                var chunk = new byte[len];
                Buffer.BlockCopy(scriptBytes, i, chunk, 0, len);
                await WriteUpdatePacketAsync(2, chunk, cancellationToken);
                await Task.Delay(2, cancellationToken);
            }

            await WriteUpdatePacketAsync(3, Array.Empty<byte>(), cancellationToken);
            var endResp = await ReadForAsync(3500, cancellationToken);

            if (endResp.Contains("UPD:OK", StringComparison.OrdinalIgnoreCase))
            {
                return (true, true, "FW written successfully (packet mode).");
            }

            if (endResp.Contains("UPD:ERR", StringComparison.OrdinalIgnoreCase))
            {
                return (true, false, $"FW update failed (packet mode). {Shorten(endResp, 200)}");
            }

            return (true, false, $"FW update response missing (packet mode). {Shorten(endResp, 200)}");
        }

        private async Task WriteUpdatePacketAsync(byte command, byte[] payload, CancellationToken cancellationToken)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                throw new InvalidOperationException("Serial port is not open.");
            }

            payload ??= Array.Empty<byte>();
            const int packetSize = 2049;
            const int maxPayload = packetSize - 11;
            if (payload.Length > maxPayload)
            {
                throw new InvalidOperationException($"Payload too large for update packet: {payload.Length} bytes.");
            }

            var packet = new byte[packetSize];
            var magic = Encoding.ASCII.GetBytes("CMJUPD::");
            Buffer.BlockCopy(magic, 0, packet, 0, 8);
            packet[8] = command;
            packet[9] = (byte)((payload.Length >> 8) & 0xFF);
            packet[10] = (byte)(payload.Length & 0xFF);
            if (payload.Length > 0)
            {
                Buffer.BlockCopy(payload, 0, packet, 11, payload.Length);
            }

            await _serialPort.BaseStream.WriteAsync(packet, 0, packet.Length, cancellationToken);
            await _serialPort.BaseStream.FlushAsync(cancellationToken);
        }

        private async Task<(bool Success, string Message)> UploadViaRawReplAsync(string scriptText, CancellationToken cancellationToken)
        {
            if (!await EnterRawReplAsync(cancellationToken))
            {
                await SafeWriteRawAsync("\x02", cancellationToken);
                return (false, BuildRawReplFailureMessage());
            }

            var code = BuildMainPyWriterCode(scriptText);
            await WriteRawAsync(code + "\x04", cancellationToken);

            var execResp = await ReadForAsync(6500, cancellationToken);
            if (!execResp.Contains("FW_UPDATE_OK", StringComparison.OrdinalIgnoreCase))
            {
                if (execResp.Contains("Traceback", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteRawAsync("\x02", cancellationToken);
                    return (false, $"FW鬯ｮ・ｫ繝ｻ・ｴ髯ｷ・ｴ郢晢ｽｻ繝ｻ・ｽ繝ｻ・ｽ郢晢ｽｻ繝ｻ・ｸ鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ鬮ｯ譎｢・｣・ｰ鬮ｮ諛ｶ・ｽ・｣郢晢ｽｻ繝ｻ・ｽ郢晢ｽｻ繝ｻ・ｾ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｼ鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｿ鬯ｮ・｣陋ｹ繝ｻ・ｽ・ｽ繝ｻ・ｳ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｭ鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｫ鬯ｮ・｣隲嶄ｴ・ｧ髯ｷ螟ｲ・ｽ・ｱ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・､鬮ｫ・ｰ髮具ｽｻ繝ｻ・ｽ繝ｻ・ｶ鬩包ｽｯ繝ｻ・ｶ郢晢ｽｻ繝ｻ・ｲ鬯ｯ・ｨ繝ｻ・ｾ髯ｷ闌ｨ・ｽ・ｷ郢晢ｽｻ繝ｻ・ｽ郢晢ｽｻ繝ｻ・ｺ鬯ｯ・ｨ繝ｻ・ｾ髯溘・螻ｮ繝ｻ・ｽ繝ｻ・ｺ髯句ｸ吶・繝ｻ・ｽ繝ｻ・ｼ郢晢ｽｻ繝ｻ・ｰ鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｾ鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ鬮ｯ・ｷ闔ｨ螟ｲ・ｽ・ｽ繝ｻ・ｱ鬮ｫ・ｨ繝ｻ・ｳ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｸ郢晢ｽｻ繝ｻ・ｲ鬩幢ｽ｢隴趣ｽ｢繝ｻ・ｽ繝ｻ・ｻ{Shorten(execResp, 200)}");
                }

                await WriteRawAsync("\x02", cancellationToken);
                return (false, $"FW鬯ｮ・ｫ繝ｻ・ｴ髯ｷ・ｴ郢晢ｽｻ繝ｻ・ｽ繝ｻ・ｽ郢晢ｽｻ繝ｻ・ｸ鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ鬮ｯ譎｢・｣・ｰ鬮ｮ諛ｶ・ｽ・｣郢晢ｽｻ繝ｻ・ｽ郢晢ｽｻ繝ｻ・ｾ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｼ鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｿ鬯ｯ・ｩ隰ｳ・ｾ繝ｻ・ｽ繝ｻ・ｨ鬮ｯ・ｷ闔・･雎撰ｽｺ郢晢ｽｻ繝ｻ・｣郢晢ｽｻ繝ｻ・｡鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ鬮ｫ・ｶ陷ｻ・ｵ繝ｻ・ｶ繝ｻ・｣郢晢ｽｻ繝ｻ・ｽ郢晢ｽｻ繝ｻ・｢驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｺ鬯ｯ・ｮ繝ｻ・ｫ郢晢ｽｻ繝ｻ・ｱ鬯ｯ・ｮ繝ｻ・ｦ郢晢ｽｻ繝ｻ・ｪ鬩搾ｽｵ繝ｻ・ｲ髯懶ｽ｣繝ｻ・､郢晢ｽｻ繝ｻ・ｸ郢晢ｽｻ繝ｻ・ｺ鬯ｯ・ｮ繝ｻ・ｦ郢晢ｽｻ繝ｻ・ｪ鬩包ｽｶ隰ｫ・ｾ繝ｻ・ｽ繝ｻ・ｪ鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ鬮ｯ譎｢・ｽ・ｶ髯ｷ・ｻ繝ｻ・ｻ郢晢ｽｻ繝ｻ・ｽ鬯ｪ・ｰ陷茨ｽｷ繝ｻ・ｽ繝ｻ・ｸ郢晢ｽｻ繝ｻ・ｺ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｧ鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ鬮ｯ・ｷ闔ｨ螟ｲ・ｽ・ｽ繝ｻ・ｱ鬮ｫ・ｨ繝ｻ・ｳ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｸ郢晢ｽｻ繝ｻ・ｲ鬩幢ｽ｢隴趣ｽ｢繝ｻ・ｽ繝ｻ・ｻ{Shorten(execResp, 200)}");
            }

            await WriteRawAsync("\x02", cancellationToken);
            await Task.Delay(100, cancellationToken);
            await WriteRawAsync("\x04", cancellationToken);

            return (true, "FW鬯ｩ蟷｢・ｽ・｢郢晢ｽｻ繝ｻ・ｧ鬮ｯ・ｷ繝ｻ・ｻ髣費｣ｰ繝ｻ・･郢晢ｽｻ繝ｻ・ｶ髫ｶ蜻ｵ・ｶ・｣繝ｻ・ｽ繝ｻ・ｸ郢晢ｽｻ繝ｻ・ｺ鬮ｯ譎｢・｣・ｰ鬮ｮ諛ｶ・ｽ・｣郢晢ｽｻ繝ｻ・ｽ郢晢ｽｻ繝ｻ・ｾ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｼ鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｿ鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｾ鬯ｩ謳ｾ・ｽ・ｵ郢晢ｽｻ繝ｻ・ｺ鬮ｯ・ｷ闔ｨ螟ｲ・ｽ・ｽ繝ｻ・ｱ鬮ｫ・ｨ繝ｻ・ｳ驛｢譎｢・ｽ・ｻ郢晢ｽｻ繝ｻ・ｸ郢晢ｽｻ繝ｻ・ｲ鬩幢ｽ｢隴趣ｽ｢繝ｻ・ｽ繝ｻ・ｻ(raw repl)");
        }

        private async Task<bool> EnterRawReplAsync(CancellationToken cancellationToken)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                return false;
            }

            _lastRawReplTrace = string.Empty;
            _rawReplTraceHistory.Clear();
            _rawReplTraceHistory.Add("phase=initial");

            // Phase 1: normal retries on current serial session.
            if (await TryEnterRawReplCoreAsync(cancellationToken))
            {
                return true;
            }

            // Phase 2: reopen port and retry once more.
            _rawReplTraceHistory.Add("phase=reopen");
            if (ReopenCurrentPort() && await TryEnterRawReplCoreAsync(cancellationToken))
            {
                return true;
            }

            // Phase 3: boot race fallback (for firmwares that disable Ctrl+C later in startup).
            _rawReplTraceHistory.Add("phase=boot-race");
            if (ReopenCurrentPort() && await TryEnterRawReplBootRaceAsync(cancellationToken))
            {
                return true;
            }

            if (_rawReplTraceHistory.Count > 0)
            {
                _lastRawReplTrace = string.Join(" | ", _rawReplTraceHistory);
            }

            return false;
        }

        private async Task<bool> TryEnterRawReplCoreAsync(CancellationToken cancellationToken)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                return false;
            }

            _lastRawReplTrace = string.Empty;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();
                }
                catch
                {
                }

                // Ensure we're in friendly REPL first, then enter RAW REPL.
                await SafeWriteRawAsync("\x03\x03", cancellationToken);
                await Task.Delay(120 + (attempt * 70), cancellationToken);
                await SafeWriteRawAsync("\x02", cancellationToken);
                await Task.Delay(70, cancellationToken);
                await SafeWriteRawAsync("\r\n", cancellationToken);
                await Task.Delay(50, cancellationToken);
                _ = await ReadForAsync(300, cancellationToken);

                await SafeWriteRawAsync("\x01", cancellationToken);
                var resp = await ReadForAsync(2200 + (attempt * 500), cancellationToken);
                var shortResp = Shorten(resp, 120);
                _lastRawReplTrace = $"attempt={attempt + 1}, resp={shortResp}";
                _rawReplTraceHistory.Add(_lastRawReplTrace);
                if (LooksLikeRawRepl(resp))
                {
                    _lastRawReplTrace = $"success: {_lastRawReplTrace}";
                    return true;
                }

                // Some boards need a soft reboot edge before accepting raw repl.
                await SafeWriteRawAsync("\x04", cancellationToken);
                await Task.Delay(120, cancellationToken);
            }

            if (_rawReplTraceHistory.Count > 0)
            {
                _lastRawReplTrace = string.Join(" | ", _rawReplTraceHistory);
            }

            return false;
        }

        private async Task<bool> TryEnterRawReplBootRaceAsync(CancellationToken cancellationToken)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                return false;
            }

            try
            {
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
            }
            catch
            {
            }

            // Soft reboot and then keep probing for a longer window.
            await SafeWriteRawAsync("\x04", cancellationToken);
            await Task.Delay(260, cancellationToken);

            for (int i = 0; i < 140; i++)
            {
                await SafeWriteRawAsync("\x03", cancellationToken);
                if ((i % 2) == 0)
                {
                    await SafeWriteRawAsync("\x03", cancellationToken);
                }

                if ((i % 3) == 0)
                {
                    await SafeWriteRawAsync("\x01", cancellationToken);
                }

                if ((i % 9) == 0)
                {
                    await SafeWriteRawAsync("\r\n", cancellationToken);
                }

                await Task.Delay(30, cancellationToken);

                var resp = await ReadForAsync(95, cancellationToken);
                var shortResp = Shorten(resp, 120);
                var trace = $"boot-race-{i + 1}, resp={shortResp}";
                _rawReplTraceHistory.Add(trace);
                _lastRawReplTrace = trace;
                if (LooksLikeRawRepl(resp))
                {
                    _lastRawReplTrace = $"success: {trace}";
                    return true;
                }
            }

            var tailResp = await ReadForAsync(420, cancellationToken);
            if (LooksLikeRawRepl(tailResp))
            {
                _lastRawReplTrace = $"success: boot-race-tail, resp={Shorten(tailResp, 120)}";
                _rawReplTraceHistory.Add(_lastRawReplTrace);
                return true;
            }

            return false;
        }

        private string BuildRawReplFailureMessage()
        {
            var baseMsg = string.IsNullOrWhiteSpace(_lastRawReplTrace)
                ? "RAW REPL was not entered."
                : $"RAW REPL was not entered. {_lastRawReplTrace}";

            bool allEmpty = _rawReplTraceHistory.Count > 0 && _rawReplTraceHistory.All(x => x.Contains("resp=(empty)", StringComparison.OrdinalIgnoreCase));
            if (allEmpty)
            {
                baseMsg += " Ctrl+C may be disabled on the device side.";
            }

            return baseMsg;
        }
        private bool ReopenCurrentPort()
        {
            try
            {
                if (_serialPort == null)
                {
                    return false;
                }

                var portName = _serialPort.PortName;
                var baudRate = _serialPort.BaudRate;

                Disconnect();
                Thread.Sleep(180);
                var ok = Connect(portName, baudRate);
                if (ok)
                {
                    Thread.Sleep(260);
                }

                return ok;
            }
            catch
            {
                return false;
            }
        }

        private static string Shorten(string? text, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "(empty)";
            }

            var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return oneLine.Length <= maxLen ? oneLine : oneLine[..maxLen] + "...";
        }

        private static bool LooksLikeRawRepl(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.Contains("raw repl", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("ctrl-b to exit", StringComparison.OrdinalIgnoreCase);
        }

        private async Task SafeWriteRawAsync(string text, CancellationToken cancellationToken)
        {
            try
            {
                await WriteRawAsync(text, cancellationToken);
            }
            catch
            {
            }
        }
        private static string BuildMainPyWriterCode(string scriptText)
        {
            var bytes = Encoding.UTF8.GetBytes(scriptText.Replace("\r\n", "\n"));
            var sb = new StringBuilder();
            sb.AppendLine("import binascii");
            sb.AppendLine("f=open('main.py','wb')");

            const int chunkSize = 192;
            for (int i = 0; i < bytes.Length; i += chunkSize)
            {
                int len = Math.Min(chunkSize, bytes.Length - i);
                string chunk = Convert.ToHexString(bytes, i, len).ToLowerInvariant();
                sb.Append("f.write(binascii.unhexlify('");
                sb.Append(chunk);
                sb.AppendLine("'))");
            }

            sb.AppendLine("f.close()");
            sb.AppendLine("print('FW_UPDATE_OK')");
            return sb.ToString();
        }

        private async Task WriteRawAsync(string text, CancellationToken cancellationToken)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                throw new InvalidOperationException("Serial port is not open.");
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            await _serialPort.BaseStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
            await _serialPort.BaseStream.FlushAsync(cancellationToken);
        }

        private async Task<string> ReadForAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                return string.Empty;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sb = new StringBuilder();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(35, cancellationToken);
                sb.Append(_serialPort.ReadExisting());
            }

            return sb.ToString();
        }

        private async Task<string?> ReadUntilAsync(string marker, int timeoutMs, CancellationToken cancellationToken)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                return null;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sb = new StringBuilder();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(30, cancellationToken);
                var part = _serialPort.ReadExisting();
                if (!string.IsNullOrEmpty(part))
                {
                    sb.Append(part);
                    if (sb.ToString().Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        return sb.ToString();
                    }
                }
            }

            return null;
        }

        public void Disconnect()
        {
            if (_serialPort != null)
            {
                if (_serialPort.IsOpen)
                {
                    _serialPort.Close();
                }

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

            _lastRawReplTrace = string.Empty;

            try
            {
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
            }
        }

        public async Task SendDataAsync(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            await _sendLock.WaitAsync();
            try
            {
                if (!IsConnected || _serialPort == null || !_serialPort.IsOpen)
                {
                    return;
                }

                string logMsg = $"Serial TX: {data.Length} bytes, brightness_byte={data[0]}";
                Chiramoji.ViewModels.MainViewModel.FileLog(logMsg);

                try
                {
                    using var cts = new CancellationTokenSource(1500);
                    await _serialPort.BaseStream.WriteAsync(data, 0, data.Length, cts.Token);
                    await _serialPort.BaseStream.FlushAsync(cts.Token);
                }
                catch (OperationCanceledException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Serial Send Timeout: {ex.Message}");
                    Chiramoji.ViewModels.MainViewModel.FileLog($"SerialService Timeout: {ex.Message}");
                }
                catch (TimeoutException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Serial Send Timeout: {ex.Message}");
                    Chiramoji.ViewModels.MainViewModel.FileLog($"SerialService Timeout: {ex.Message}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Serial Send Error: {ex.Message}");
                    Chiramoji.ViewModels.MainViewModel.FileLog($"SerialService Exception: {ex.Message}");
                    HandleConnectionLost();
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}



















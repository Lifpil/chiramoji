using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BlindTouchOled.Services;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;
using SkiaSharp;
using System.IO;
using System;
using SkiaSharp.Views.WPF;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BlindTouchOled.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly DispatcherTimer _throttleTimer;
        private readonly DispatcherTimer _autoConnectTimer;
        private bool _needsUpdate = false;
        private int _saveCounter = 0;
        private readonly ISerialService _serialService;
       private readonly IRenderService _renderService;
        private readonly ISettingsService _settingsService;
        private readonly IInputMonitor _inputMonitor;
        private readonly Models.AppSettings _settings;
        private readonly Services.KeyloggerService _keylogger;

        [ObservableProperty]
        private string _title = "Blind Touch OLED";

        [ObservableProperty]
        private ObservableCollection<string> _logMessages = new();

        private static readonly ActionBlock<string> _logQueue = new(msg =>
        {
            try { File.AppendAllText("app_debug.txt", msg, System.Text.Encoding.UTF8); } catch { }
        });

        public static void FileLog(string message)
        {
            _logQueue.Post($"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }

        public void Log(string message)
        {
            App.Current.Dispatcher.BeginInvoke(() => {
                LogMessages.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
            });
        }

        [ObservableProperty]
        private ObservableCollection<string> _availablePorts = new();

        private string? _selectedPort;
        public string? SelectedPort
        {
            get => _selectedPort;
            set {
                if (SetProperty(ref _selectedPort, value))
                {
                    _settings.LastComPort = value;
                    _settingsService.Save(_settings);
                }
            }
        }

        [ObservableProperty]
        private bool _isConnected;

        [ObservableProperty]
        private ObservableCollection<string> _availableFonts = new();

        private string _selectedFont = "Yu Gothic";
        public string SelectedFont
        {
            get => _selectedFont;
            set
            {
                if (SetProperty(ref _selectedFont, value))
                {
                    _settings.FontFamily = value;
                    ScheduleUpdate();
                }
            }
        }

        private int _cursorPosition = 0;
        private SKBitmap? _currentBitmap = null;
        private bool _isCursorVisible = true;
        private DispatcherTimer _cursorTimer;

        [ObservableProperty]
        private string _inputText = "";

        [ObservableProperty]
        private string _inputMode = "| A";

        [ObservableProperty]
        private WriteableBitmap? _previewImage;

        [ObservableProperty]
        private float _currentFontSize;

        partial void OnCurrentFontSizeChanged(float value)
        {
            _settings.FontSize = value;
            ScheduleUpdate();
        }

        [ObservableProperty]
        private byte _currentBrightness;

        partial void OnCurrentBrightnessChanged(byte value)
        {
            _settings.Brightness = value;
            if (IsImageModeApplied)
            {
                _ = SendAppliedImageOnceAsync();
            }
            else
            {
                ScheduleUpdate();
            }
        }

        [ObservableProperty]
        private string _monitorStatus = "Starting...";

        private byte[]? _pendingSerialData = null;
        private bool _isSerialBusy = false;
        private bool _isConnectionActionInProgress = false;
        [ObservableProperty]
        private string _rawBufferText = "";

        private SKBitmap? _imageBitmap;

        [ObservableProperty]
        private string _selectedImagePath = "";

        partial void OnSelectedImagePathChanged(string value)
        {
            OnPropertyChanged(nameof(HasSelectedImage));
        }

        [ObservableProperty]
        private WriteableBitmap? _selectedImagePreview;

        [ObservableProperty]
        private bool _isImageModeApplied = false;

        public bool HasSelectedImage => !string.IsNullOrWhiteSpace(SelectedImagePath);

        // ---- Visibility Settings ----
        public bool ShowConnectivity
        {
            get => _settings.ShowConnectivity;
            set
            {
                if (_settings.ShowConnectivity != value)
                {
                    _settings.ShowConnectivity = value;
                    OnPropertyChanged();
                    NotifyLayoutStateChanged();
                    _settingsService.Save(_settings);
                }
            }
        }
        public bool ShowDirectControl
        {
            get => _settings.ShowDirectControl;
            set { if (_settings.ShowDirectControl != value) { _settings.ShowDirectControl = value; OnPropertyChanged(); _settingsService.Save(_settings); } }
        }
        public bool ShowInputMode
        {
            get => _settings.ShowInputMode;
            set
            {
                if (_settings.ShowInputMode != value)
                {
                    _settings.ShowInputMode = value;
                    OnPropertyChanged();
                    NotifyLayoutStateChanged();
                    _settingsService.Save(_settings);
                }
            }
        }
        public bool ShowPreview
        {
            get => _settings.ShowPreview;
            set { if (_settings.ShowPreview != value) { _settings.ShowPreview = value; OnPropertyChanged(); _settingsService.Save(_settings); } }
        }
        public bool ShowTelemetry
        {
            get => _settings.ShowTelemetry;
            set { if (_settings.ShowTelemetry != value) { _settings.ShowTelemetry = value; OnPropertyChanged(); _settingsService.Save(_settings); } }
        }
        public bool ShowLogs
        {
            get => _settings.ShowLogs;
            set
            {
                if (_settings.ShowLogs != value)
                {
                    _settings.ShowLogs = value;
                    OnPropertyChanged();
                    NotifyLayoutStateChanged();
                    _settingsService.Save(_settings);
                }
            }
        }

        public bool ShowImageSection
        {
            get => _settings.ShowImageSection;
            set
            {
                if (_settings.ShowImageSection != value)
                {
                    _settings.ShowImageSection = value;
                    OnPropertyChanged();
                    NotifyLayoutStateChanged();
                    _settingsService.Save(_settings);
                }
            }
        }

        public bool IsAnyRightSectionVisible => ShowConnectivity || ShowImageSection;
        public bool IsBothRightSectionsVisible => ShowConnectivity && ShowImageSection;
        public bool OnlyConnectivityVisible => ShowConnectivity && !ShowImageSection;
        public bool OnlyImageVisible => !ShowConnectivity && ShowImageSection;
        public bool ShowRightLocalLog => ShowLogs && ShowInputMode && IsAnyRightSectionVisible && !IsBothRightSectionsVisible;
        public bool ShowBottomLog => ShowLogs && ShowInputMode && IsBothRightSectionsVisible;
        public bool ShowCenteredLog => ShowLogs && (!ShowInputMode || !IsAnyRightSectionVisible);
        public bool UseNarrowDisplayCard => ShowInputMode && !IsAnyRightSectionVisible;
        public bool UseCenteredRightColumn => !ShowInputMode;

        private void NotifyLayoutStateChanged()
        {
            OnPropertyChanged(nameof(IsAnyRightSectionVisible));
            OnPropertyChanged(nameof(IsBothRightSectionsVisible));
            OnPropertyChanged(nameof(OnlyConnectivityVisible));
            OnPropertyChanged(nameof(OnlyImageVisible));
            OnPropertyChanged(nameof(ShowRightLocalLog));
            OnPropertyChanged(nameof(ShowBottomLog));
            OnPropertyChanged(nameof(ShowCenteredLog));
            OnPropertyChanged(nameof(UseNarrowDisplayCard));
            OnPropertyChanged(nameof(UseCenteredRightColumn));
        }

        public bool AutoConnect
        {
            get => _settings.AutoConnect;
            set
            {
                if (_settings.AutoConnect != value)
                {
                    _settings.AutoConnect = value;
                    OnPropertyChanged();
                    _settingsService.Save(_settings);
                    if (value)
                    {
                        _ = TryAutoConnectAsync();
                    }
                }
            }
        }

        public bool StartWithWindows
        {
            get => _settings.StartWithWindows;
            set
            {
                if (_settings.StartWithWindows != value)
                {
                    _settings.StartWithWindows = value;
                    App.ApplyAutoStartSetting(value);
                    OnPropertyChanged();
                    _settingsService.Save(_settings);
                }
            }
        }

        public bool CloseButtonMinimizesToTray
        {
            get => _settings.CloseButtonMinimizesToTray;
            set
            {
                if (_settings.CloseButtonMinimizesToTray != value)
                {
                    _settings.CloseButtonMinimizesToTray = value;
                    OnPropertyChanged();
                    _settingsService.Save(_settings);
                }
            }
        }

        [ObservableProperty]
        private string _appVersion = "v1.0.0";

        [ObservableProperty]
        private string _firmwareVersion = "Detecting...";

        [ObservableProperty]
        private string _updateStatus = "Ready";

        // ---- 繧ｭ繝ｼ繝ｭ繧ｬ繝ｼ繝｢繝ｼ繝・----
        private bool _isKeyloggerMode = true;
        public bool IsKeyloggerMode
        {
            get => _isKeyloggerMode;
            set
            {
                if (SetProperty(ref _isKeyloggerMode, value))
                {
                    _keylogger.IsEnabled = value;
                    if (value)
                    {
                        _keylogger.ResetBuffer();
                        Log("繧ｭ繝ｼ繝ｭ繧ｬ繝ｼ繝｢繝ｼ繝・ ON");
                    }
                    else
                    {
                        Log("Keylogger mode OFF");
                    }
                }
            }
        }

        private bool _isImmediateClear = false;
        public bool IsImmediateClear
        {
            get => _isImmediateClear;
            set
            {
                if (SetProperty(ref _isImmediateClear, value))
                {
                    _keylogger.ClearOnEnter = value;
                }
            }
        }

        private bool _isResetOnClick = true;
        public bool IsResetOnClick
        {
            get => _isResetOnClick;
            set
            {
                if (SetProperty(ref _isResetOnClick, value))
                {
                    _keylogger.ResetOnClick = value;
                    _settings.ResetOnClick = value;
                }
            }
        }



        public MainViewModel()
        {
            try
            {
                _serialService = new SerialService();
                _renderService = new SkiaRenderService();
                _settingsService = new SettingsService();
                _inputMonitor = new InputMonitorService();
                _keylogger = new Services.KeyloggerService();
                _settings = _settingsService.Load();
                _serialService.ConnectionLost += OnSerialConnectionLost;

                CurrentFontSize = _settings.FontSize;
                CurrentBrightness = _settings.Brightness;
                IsResetOnClick = _settings.ResetOnClick;
                _keylogger.ResetOnClick = _settings.ResetOnClick;

                var japaneseFonts = System.Linq.Enumerable.Where(SKFontManager.Default.FontFamilies, f =>
                {
                    try
                    {
                        using var tf = SKTypeface.FromFamilyName(f);
                        if (tf == null) return false;
                        using var font = new SKFont(tf);
                        var glyphs = font.GetGlyphs("あ");
                        return glyphs != null && glyphs.Length > 0 && glyphs[0] != 0;
                    }
                    catch { return false; }
                }).OrderBy(f => f).ToList();

                AvailableFonts = new ObservableCollection<string>(japaneseFonts);
                if (AvailableFonts.Contains(_settings.FontFamily))
                    SelectedFont = _settings.FontFamily;

                _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _cursorTimer.Tick += (s, e) =>
                {
                    _isCursorVisible = !_isCursorVisible;
                    ScheduleUpdate();
                };
                _cursorTimer.Start();

                // 譖ｴ譁ｰ繧ｿ繧､繝槭・繧帝ｫ倬溷喧・・3ms -> 10ms・峨＠縺ｦ蜈･蜉帙ｒ蜊ｳ蠎ｧ縺ｫ蜿肴丐
                _throttleTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
                _throttleTimer.Tick += (s, e) =>
                {
                    if (_needsUpdate)
                    {
                        _needsUpdate = false;
                        UpdatePreview();
                        TriggerSend();
                        // 險ｭ螳壻ｿ晏ｭ倥・譖ｴ譁ｰ繝ｫ繝ｼ繝励°繧牙・繧企屬縺励※蛻･繧ｹ繝ｬ繝・ラ縺ｧ菴朱ｻ蠎ｦ縺ｫ陦後≧
                        if (++_saveCounter > 300) // 謨ｰ遘偵↓1蝗樒ｨ句ｺｦ
                        {
                            _saveCounter = 0;
                            Task.Run(() => _settingsService.Save(_settings));
                        }
                    }
                };
                _throttleTimer.Start();

                _autoConnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _autoConnectTimer.Tick += async (s, e) => await MonitorDeviceConnectionAsync();
                _autoConnectTimer.Start();

                // IME繝｢繝ｼ繝牙､画峩
                _inputMonitor.ModeChanged += (mode) => {
                    App.Current.Dispatcher.BeginInvoke(() => {
                        InputMode = mode;
                        _keylogger.UpdateImeMode(mode);
                    });
                };

                _inputMonitor.StatusChanged += (status) => {
                    App.Current.Dispatcher.BeginInvoke(() => {
                        MonitorStatus = status;
                    });
                };

                // App direct text synchronization (used when keylogger mode is OFF)
                _inputMonitor.FocusedTextChanged += (text, cursor) => {
                    App.Current.Dispatcher.BeginInvoke(() => {
                        RawBufferText = text;
                        if (!IsKeyloggerMode)
                        {
                            _isSyncingFromMonitor = true;
                            try { SyncTextFromUi(text, cursor); }
                            finally { _isSyncingFromMonitor = false; }
                        }
                    });
                };

                // 繧｢繝励Μ蛻・ｊ譖ｿ縺・竊・繧ｭ繝ｼ繝ｭ繧ｬ繝ｼ繝ｪ繧ｻ繝・ヨ
                _inputMonitor.ForegroundChanged += () => {
                    _keylogger.ResetBuffer();
                };

                // 繧ｭ繝ｼ繝ｭ繧ｬ繝ｼ繝舌ャ繝輔ぃ譖ｴ譁ｰ
                _keylogger.BufferChanged += (confirmed, pending) => {
                    App.Current.Dispatcher.BeginInvoke(() => {
                        if (!IsKeyloggerMode) return;
                        // Merge confirmed + pending composition into one display string.
                        string display = confirmed + pending;
                        _isSyncingFromMonitor = true;
                        try { SyncTextFromUi(display, display.Length); }
                        finally { _isSyncingFromMonitor = false; }
                    });
                };

                _inputMonitor.Start();
                _keylogger.Start();

                RefreshPorts();

                if (!string.IsNullOrEmpty(_settings.LastComPort) && AvailablePorts.Contains(_settings.LastComPort))
                {
                    SelectedPort = _settings.LastComPort;
                }
                else if (AvailablePorts.Any())
                {
                    SelectedPort = AvailablePorts.First();
                }

                if (AutoConnect)
                {
                    _ = TryAutoConnectAsync();
                }

                UpdatePreview();
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("crash.log", ex.ToString());
                throw;
            }
        }

        [RelayCommand]
        private void RefreshPorts()
        {
            var current = SelectedPort;
            AvailablePorts.Clear();
            foreach (var port in _serialService.GetAvailablePorts())
                AvailablePorts.Add(port);
            if (AvailablePorts.Contains(current ?? ""))
            {
                SelectedPort = current;
            }
            else if (!string.IsNullOrWhiteSpace(_settings.LastComPort) && AvailablePorts.Contains(_settings.LastComPort))
            {
                SelectedPort = _settings.LastComPort;
            }
            else if (AvailablePorts.Any())
            {
                SelectedPort = AvailablePorts.First();
            }
        }

        [RelayCommand]
        private async Task UpdateFirmwareAsync()
        {
            UpdateStatus = "Searching for updates...";
            await Task.Delay(1000);
            UpdateStatus = "Current firmware is up to date.";
        }

        [RelayCommand]
        private async Task ToggleConnectionAsync()
        {
            if (_isConnectionActionInProgress)
            {
                return;
            }

            _isConnectionActionInProgress = true;
            try
            {
            if (IsConnected)
            {
                _serialService.Disconnect();
                Title = "Blind Touch OLED";
                Log("Disconnected from device.");
            }
            else
            {
                if (!string.IsNullOrEmpty(SelectedPort))
                {
                    try
                    {
                        Log("Attempting to connect to device...");
                        Title = "Connecting...";
                        // Try 115200 baud first (most common for this device).
                        bool ok = await Task.Run(() => _serialService.Connect(SelectedPort, 115200));
                        if (ok)
                        {
                            Title = "Connected";
                            Log("Successfully connected to the device.");
                            if (IsImageModeApplied)
                            {
                                await SendAppliedImageOnceAsync();
                            }
                        }
                        else
                        {
                            await TryConnectAnyAvailablePortAsync(silent: false, ignoreBusy: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Title = $"Error: {ex.Message}";
                        Log($"Connection Exception: {ex.Message}");
                    }
                }
                else
                {
                    await TryConnectAnyAvailablePortAsync(silent: false, ignoreBusy: true);
                }
            }
            IsConnected = _serialService.IsConnected;
            }
            finally
            {
                _isConnectionActionInProgress = false;
            }
        }

        private async Task TryAutoConnectAsync()
        {
            if (!AutoConnect || IsConnected || _isConnectionActionInProgress)
            {
                return;
            }

            await TryConnectAnyAvailablePortAsync(silent: true, ignoreBusy: false);
        }

        private async Task TryConnectAnyAvailablePortAsync(bool silent, bool ignoreBusy)
        {
            if (IsConnected || (_isConnectionActionInProgress && !ignoreBusy))
            {
                return;
            }

            RefreshPorts();
            var candidates = BuildAutoConnectCandidates();
            if (candidates.Count == 0)
            {
                if (!silent)
                {
                    Title = "Device not found";
                    Log("No device found. Check cable/power and try again.");
                }
                return;
            }

            _isConnectionActionInProgress = true;
            try
            {
                if (!silent)
                {
                    Log("Searching and connecting to device...");
                    Title = "Connecting...";
                }

                foreach (var port in candidates)
                {
                    var ok = await Task.Run(() => _serialService.Connect(port, 115200));
                    IsConnected = _serialService.IsConnected;

                    if (ok)
                    {
                        SelectedPort = port;
                        _settings.LastComPort = port;
                        _settingsService.Save(_settings);
                        Title = "Connected";
                        Log(silent ? "Auto-connected to device." : "Successfully connected to the device.");
                        if (IsImageModeApplied)
                        {
                            await SendAppliedImageOnceAsync();
                        }
                        return;
                    }
                }

                if (!silent)
                {
                    Title = "Error: Could not connect to device";
                    Log("Failed to connect. Check cable/power and try again.");
                }
            }
            catch
            {
                if (!silent)
                {
                    Title = "Error: Could not connect to device";
                }
            }
            finally
            {
                _isConnectionActionInProgress = false;
            }
        }

        private async Task MonitorDeviceConnectionAsync()
        {
            if (IsConnected)
            {
                if (!_serialService.ProbeConnection())
                {
                    IsConnected = false;
                    Title = "Blind Touch OLED";
                    Log("Device disconnected.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(SelectedPort) &&
                    !_serialService.IsPortPresent(SelectedPort))
                {
                    _serialService.Disconnect();
                    IsConnected = false;
                    Title = "Blind Touch OLED";
                    Log("Device disconnected.");
                    return;
                }
            }

            await TryAutoConnectAsync();
        }

        private void OnSerialConnectionLost()
        {
            App.Current.Dispatcher.BeginInvoke(() =>
            {
                if (!IsConnected)
                {
                    return;
                }

                IsConnected = false;
                Title = "Blind Touch OLED";
                Log("Device disconnected.");
            });
        }

        private List<string> BuildAutoConnectCandidates()
        {
            var list = new List<string>();

            if (!string.IsNullOrWhiteSpace(SelectedPort) && AvailablePorts.Contains(SelectedPort))
            {
                list.Add(SelectedPort);
            }

            if (!string.IsNullOrWhiteSpace(_settings.LastComPort) &&
                AvailablePorts.Contains(_settings.LastComPort) &&
                !list.Contains(_settings.LastComPort))
            {
                list.Add(_settings.LastComPort);
            }

            foreach (var port in AvailablePorts)
            {
                if (!list.Contains(port))
                {
                    list.Add(port);
                }
            }

            return list;
        }

        [RelayCommand]
        private void ExitApplication()
        {
            if (App.Current.MainWindow is Views.MainWindow window)
            {
                window.ExitApplication();
            }
            else
            {
                App.Current.Shutdown();
            }
        }

        private bool _isSyncingFromMonitor = false;
        public bool IsSyncingFromMonitor => _isSyncingFromMonitor;

        [RelayCommand]
        private void SelectImage()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select image",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                SelectedImagePath = dialog.FileName;
                if (TryBuildImageBitmap(SelectedImagePath, out var bitmap))
                {
                    _imageBitmap?.Dispose();
                    _imageBitmap = bitmap;
                    SelectedImagePreview = _imageBitmap.ToWriteableBitmap();
                    if (IsImageModeApplied)
                    {
                        PreviewImage = SelectedImagePreview;
                    }
                    Log("Image selected.");
                }
                else
                {
                    Log("Failed to load image.");
                }
            }
        }

        [RelayCommand]
        private void CropImage()
        {
            if (!HasSelectedImage)
            {
                Log("Please select an image first.");
                return;
            }

            try
            {
                using var src = SKBitmap.Decode(SelectedImagePath);
                if (src == null)
                {
                    Log("Failed to load image for crop.");
                    return;
                }

                var dialog = new Views.ImageCropWindow(src);
                if (dialog.ShowDialog() == true && dialog.ResultBitmap != null)
                {
                    _imageBitmap?.Dispose();
                    _imageBitmap = dialog.ResultBitmap;
                    SelectedImagePreview = _imageBitmap.ToWriteableBitmap();
                    if (IsImageModeApplied)
                    {
                        PreviewImage = SelectedImagePreview;
                        _ = SendAppliedImageOnceAsync();
                    }
                    Log("Image cropped.");
                }
            }
            catch (Exception ex)
            {
                Log($"Crop error: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ClearSelectedImage()
        {
            bool wasImageMode = IsImageModeApplied;
            IsImageModeApplied = false;

            _imageBitmap?.Dispose();
            _imageBitmap = null;

            SelectedImagePath = "";
            SelectedImagePreview = null;

            Log("Image selection cleared.");

            if (wasImageMode)
            {
                ScheduleUpdate();
            }
        }

        [RelayCommand]
        private async Task ToggleImageModeAsync()
        {
            if (IsImageModeApplied)
            {
                IsImageModeApplied = false;
                Log("Image mode released.");
                ScheduleUpdate();
                return;
            }

            if (!HasSelectedImage)
            {
                Log("Please select an image first.");
                return;
            }

            if (_imageBitmap == null)
            {
                if (!TryBuildImageBitmap(SelectedImagePath, out var bitmap))
                {
                    Log("Failed to load image.");
                    return;
                }
                _imageBitmap = bitmap;
            }

            IsImageModeApplied = true;
            SelectedImagePreview = _imageBitmap.ToWriteableBitmap();
            PreviewImage = SelectedImagePreview;
            Log("Image mode applied.");

            if (IsConnected)
            {
                await SendAppliedImageOnceAsync();
            }
            else
            {
                Log("Image is ready. It will be sent when device is connected.");
            }
        }

        private async Task SendAppliedImageOnceAsync()
        {
            if (!IsConnected || _imageBitmap == null)
            {
                return;
            }

            byte mappedBrightness = GetMappedBrightness();
            var data = _renderService.Get1BitRawBytes(_imageBitmap, mappedBrightness);
            await _serialService.SendDataAsync(data);
            Log("Image sent to OLED.");
        }

        private bool TryBuildImageBitmap(string path, out SKBitmap bitmap)
        {
            bitmap = null!;
            try
            {
                using var src = SKBitmap.Decode(path);
                if (src == null || src.Width <= 0 || src.Height <= 0)
                {
                    return false;
                }

                var target = new SKBitmap(256, 64);
                using (var canvas = new SKCanvas(target))
                {
                    canvas.Clear(SKColors.Black);

                    float scale = Math.Min(256f / src.Width, 64f / src.Height);
                    float drawW = src.Width * scale;
                    float drawH = src.Height * scale;
                    float x = (256f - drawW) * 0.5f;
                    float y = (64f - drawH) * 0.5f;

                    using var paint = new SKPaint
                    {
                        IsAntialias = true,
                        FilterQuality = SKFilterQuality.High
                    };
                    canvas.DrawBitmap(src, new SKRect(x, y, x + drawW, y + drawH), paint);
                }

                for (int yy = 0; yy < 64; yy++)
                {
                    for (int xx = 0; xx < 256; xx++)
                    {
                        var p = target.GetPixel(xx, yy);
                        int lum = (p.Red * 299 + p.Green * 587 + p.Blue * 114) / 1000;
                        byte v = (byte)(lum >= 128 ? 255 : 0);
                        target.SetPixel(xx, yy, new SKColor(v, v, v));
                    }
                }

                bitmap = target;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool _isSending = false;
        private bool _pendingSend = false;

        [RelayCommand]
        private async Task SendTextAsync()
        {
            // This method is now largely superseded by TriggerSend directly using _currentBitmap.
            // It might be removed or refactored later if no longer needed.
            if (!IsConnected) return;
            if (_isSending) { _pendingSend = true; return; }

            _isSending = true;
            try
            {
                do
                {
                    _pendingSend = false;
                    string fullText = InputText;
                    string mode = InputMode;
                    float size = CurrentFontSize;
                    byte brightness = CurrentBrightness;
                    string font = SelectedFont;
                    int fullCursorPos = _cursorPosition;
                    bool blink = _isCursorVisible;

                    var (text, cursorPos) = GetDisplayState(fullText, fullCursorPos);

                    await Task.Run(async () =>
                    {
                        var skiaSw = System.Diagnostics.Stopwatch.StartNew();
                        using var skBitmap = _renderService.CreateBitmap(text, mode, size, font, cursorPos, blink);
                        byte[]? data = _renderService.Get1BitRawBytes(skBitmap);
                        skiaSw.Stop();

                        var serialSw = System.Diagnostics.Stopwatch.StartNew();
                        await _serialService.SendDataAsync(data);
                        serialSw.Stop();

                        if (skiaSw.ElapsedMilliseconds + serialSw.ElapsedMilliseconds > 20)
                            FileLog($"BgTask: Render {skiaSw.ElapsedMilliseconds}ms, SerialWrite {serialSw.ElapsedMilliseconds}ms");
                    });
                } while (_pendingSend);
            }
            finally { _isSending = false; }
        }

        private void ScheduleUpdate() => _needsUpdate = true;

        public void SyncTextFromUi(string text, int selectionStart)
        {
            if (IsImageModeApplied)
            {
                return;
            }

            bool changed = false;
            if (InputText != text) { InputText = text; changed = true; }
            if (_cursorPosition != selectionStart)
            {
                _cursorPosition = selectionStart;
                _isCursorVisible = true;
                _cursorTimer?.Stop();
                _cursorTimer?.Start();
                changed = true;
            }
            if (changed) ScheduleUpdate();
        }

        private (string text, int cursor) GetDisplayState(string input, int originalCursor)
        {
            if (string.IsNullOrEmpty(input)) return ("", 0);
            string normalized = input.Replace("\r\n", "\n");
            int safeCursor = Math.Clamp(originalCursor, 0, normalized.Length);

            int lineStart = normalized.LastIndexOf('\n', Math.Max(0, safeCursor - 1)) + 1;
            if (safeCursor == 0) lineStart = 0;
            int lineEnd = normalized.IndexOf('\n', safeCursor);
            if (lineEnd == -1) lineEnd = normalized.Length;

            string currentLine = normalized.Substring(lineStart, lineEnd - lineStart);
            int cursorInLine = safeCursor - lineStart;

            var clean = new System.Text.StringBuilder(currentLine.Length);
            int finalCursor = 0;
            for (int i = 0; i < currentLine.Length; i++)
            {
                char c = currentLine[i];
                bool isJunk = c == '\r' || c == '\n' || c == '\u200B' || c == '\uFFFC' || c == '\uFFFD';
                if (i < cursorInLine && !isJunk) finalCursor++;
                if (!isJunk) clean.Append(c == '\t' ? ' ' : c);
            }
            if (cursorInLine >= currentLine.Length) finalCursor = clean.Length;
            return (clean.ToString(), finalCursor);
        }

        partial void OnInputTextChanged(string value)
        {
            if (!IsImageModeApplied)
            {
                ScheduleUpdate();
            }
        }

        partial void OnInputModeChanged(string value)
        {
            if (!IsImageModeApplied)
            {
                ScheduleUpdate();
            }
        }

        private byte GetMappedBrightness()
        {
            byte mappedBrightness = CurrentBrightness;
            if (mappedBrightness > 0)
            {
                mappedBrightness = (byte)(11 + (mappedBrightness - 1) * 244 / 254);
            }
            return mappedBrightness;
        }

        private void UpdatePreview()
        {
            if (IsImageModeApplied)
            {
                if (_imageBitmap != null)
                {
                    PreviewImage = _imageBitmap.ToWriteableBitmap();
                }
                _pendingSerialData = null;
                return;
            }

            var (text, cursorPos) = GetDisplayState(InputText, _cursorPosition);
            
            // 蜿､縺・ン繝・ヨ繝槭ャ繝励ｒ蜃ｦ蛻・            _currentBitmap?.Dispose();

            // OLED縺ｮ迚ｩ逅・音諤ｧ繧定｣懈ｭ｣・壼､1莉･荳翫・蝣ｴ蜷医√ワ繝ｼ繝峨え繧ｧ繧｢蛛ｴ縺ｮ逋ｺ蜈我ｸ矩剞(11)縺ｫ繧ｪ繝輔そ繝・ヨ縺吶ｋ
            byte mappedBrightness = GetMappedBrightness();

            _currentBitmap = _renderService.CreateBitmap(text, InputMode, CurrentFontSize, SelectedFont, cursorPos, _isCursorVisible, mappedBrightness);
            
            // 繝・・繧ｿ繧偵す繝ｪ繧｢繝ｫ騾∽ｿ｡逕ｨ縺ｫ繧ｭ繝｣繝励メ繝｣
            _pendingSerialData = _renderService.Get1BitRawBytes(_currentBitmap, mappedBrightness);

            // UI陦ｨ遉ｺ逕ｨ縺ｫWriteableBitmap縺ｸ螟画鋤
            PreviewImage = _currentBitmap.ToWriteableBitmap();
            
            TriggerSend();
        }

        private async void TriggerSend()
        {
            if (IsImageModeApplied)
            {
                return;
            }

            if (_isSerialBusy || _pendingSerialData == null) return;

            if (_serialService.IsConnected)
            {
                _isSerialBusy = true;
                try
                {
                    // Clone before send to avoid races with next UI update.
                    byte[] dataToSend = (byte[])_pendingSerialData.Clone();
                    await _serialService.SendDataAsync(dataToSend);
                }
                finally
                {
                    _isSerialBusy = false;
                }
            }
        }
    }
}


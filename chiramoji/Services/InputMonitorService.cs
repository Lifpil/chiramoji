using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Text;
using System.Windows;
using System.Threading.Tasks;

namespace Chiramoji.Services
{
    public interface IInputMonitor
    {
        event Action<string>? ModeChanged;
        event Action<string>? StatusChanged;
        event Action<string, int>? FocusedTextChanged;
        event Action? ForegroundChanged;    // フォアグラウンドが切り替わった
        void Start();
        void Stop();
    }

    /// <summary>
    /// 他アプリへの入力を監視するサービス。
    ///
    /// 取得戦略（優先順）:
    ///   1. UIAutomation.FocusedElement  … Chrome/Edge/WPF/UWP/VSCode 等モダンアプリに対応
    ///   2. Win32 WM_GETTEXT             … Notepad 等クラシック Win32 Edit コントロール (クラス名 "Edit" 限定)
    ///   3. WH_KEYBOARD_LL フック        … 上記どちらも機能しない場合のキーストロークバッファ
    ///
    /// 【ValuePattern の扱い】
    ///   ControlType.Custom 要素の ValuePattern はアクセシビリティの説明文やヒントを返す場合があるため
    ///   TextPattern に限定する。ValuePattern は Edit / DataItem / Cell のみ使用する。
    ///
    /// IME 合成文字列は ImmGetCompositionString で別途取得し確定テキスト末尾に結合する。
    /// </summary>
    public class InputMonitorService : IInputMonitor
    {
        public event Action<string>? ModeChanged;
        public event Action<string>? StatusChanged;
        public event Action<string, int>? FocusedTextChanged;
        public event Action? ForegroundChanged;

        // ---- 共有状態 (ロックで保護) ----
        private readonly object _lock = new();
        private string _lastMode = "";
        private string? _lastReportedText = null;
        private int _lastReportedCursor = -1;

        private IntPtr _lastForeground = IntPtr.Zero;
        private IntPtr _lastFocusHwnd = IntPtr.Zero;

        // キーボードフック用バッファ (Strategy 3)
        private string _keyboardBuffer = "";
        private string _compositionText = "";

        // Strategy 1/2 が成功していれば true → フックはキーを無視する
        private bool _uiaWorkedLastCheck = false;

        private IntPtr _hookHandle = IntPtr.Zero;
        private LowLevelKeyboardProc? _hookProc;

        private System.Timers.Timer? _timer;
        private readonly int _myProcessId;
        private bool _isChecking = false;

        public InputMonitorService()
        {
            _myProcessId = Process.GetCurrentProcess().Id;
            _timer = new System.Timers.Timer(100);
            _timer.Elapsed += (s, e) => CheckAll();
        }

        public void Start()
        {
            Application.Current.Dispatcher.Invoke(InstallHook);
            _timer?.Start();
        }

        public void Stop()
        {
            _timer?.Stop();
            Application.Current.Dispatcher.Invoke(RemoveHook);
        }

        // ================================================================
        // WH_KEYBOARD_LL フック (Strategy 3: UIA も Win32 も取得できない場合)
        // ================================================================

        private void InstallHook()
        {
            _hookProc = HookCallback;
            IntPtr hMod = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName ?? "");
            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, hMod, 0);
            ViewModels.MainViewModel.FileLog(_hookHandle != IntPtr.Zero
                ? "[HOOK] WH_KEYBOARD_LL installed"
                : $"[HOOK] SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        }

        private void RemoveHook()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
                ViewModels.MainViewModel.FileLog("[HOOK] WH_KEYBOARD_LL removed");
            }
        }
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN))
            {
                var s = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                IntPtr hFore = GetForegroundWindow();
                if (hFore != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hFore, out uint pid);
                    if (pid != (uint)_myProcessId)
                    {
                        // Strategy 1/2 sync が安定動作している間はフック更新を抑制
                        lock (_lock)
                        {
                            bool highQualitySync = _uiaWorkedLastCheck && (_lastReportedText != null && !_lastReportedText.Contains("\uFFFC"));
                            if (highQualitySync) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                        }
                        
                        uint ftid = GetWindowThreadProcessId(hFore, out _);
                        GUITHREADINFO gui = new GUITHREADINFO();
                        gui.cbSize = Marshal.SizeOf(gui);
                        GetGUIThreadInfo(ftid, ref gui);
                        IntPtr hImeTarget = gui.hwndFocus != IntPtr.Zero ? gui.hwndFocus : hFore;

                        ProcessKeyFallback(s.vkCode, s.scanCode, hImeTarget);
                    }
                }
            }
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private void ProcessKeyFallback(uint vk, uint scanCode, IntPtr hImeTarget)
        {
            string comp = GetCompositionString(hImeTarget);
            bool imeComposing = !string.IsNullOrEmpty(comp);

            lock (_lock)
            {
                if (!imeComposing)
                {
                    switch (vk)
                    {
                        case 0x08: // VK_BACK
                            if (_keyboardBuffer.Length > 0)
                                _keyboardBuffer = _keyboardBuffer[..^1];
                            break;
                        case 0x1B: // VK_ESCAPE
                            _keyboardBuffer = "";
                            break;
                        case 0x0D: // VK_RETURN
                            break;
                        default:
                            byte[] keyState = new byte[256];
                            GetKeyboardState(keyState);
                            uint tid = GetWindowThreadProcessId(hImeTarget, out _);
                            IntPtr hkl = GetKeyboardLayout(tid);
                            var sb = new StringBuilder(4);
                            int r = ToUnicodeEx(vk, scanCode, keyState, sb, 4, 0, hkl);
                            if (r == 1 && !char.IsControl(sb[0]))
                                _keyboardBuffer += sb.ToString();
                            break;
                    }
                }

                _compositionText = comp;
                string display = _keyboardBuffer + _compositionText;
                ViewModels.MainViewModel.FileLog($"[STRATEGY-3] KeyFallback: Buffer='{_keyboardBuffer}', Comp='{_compositionText}', HWND={hImeTarget}");
                RaiseIfChanged(display, display.Length, "Hook: Typing");
            }
        }

        // ================================================================
        // メイン監視ループ (100ms タイマー)
        // ================================================================

        private void CheckAll()
        {
            if (_isChecking) return;
            _isChecking = true;
            try
            {
                IntPtr hFore = GetForegroundWindow();
                if (hFore == IntPtr.Zero) return;
                GetWindowThreadProcessId(hFore, out uint pid);

                if (pid == (uint)_myProcessId) return;

                // ---- フォアグラウンド変更 → 状態リセット ----
                bool foreChanged;
                lock (_lock) { foreChanged = hFore != _lastForeground; }
                if (foreChanged)
                {
                    lock (_lock)
                    {
                        _lastForeground = hFore;
                        _lastFocusHwnd = IntPtr.Zero;
                        _keyboardBuffer = "";
                        _compositionText = "";
                        _uiaWorkedLastCheck = false;
                        _lastReportedText = null;
                        _lastReportedCursor = -1;
                    }
                    ForegroundChanged?.Invoke();
                }

                // ---- フォーカスコントロールの特定 (IME用HWNDとしても使用) ----
                uint ftid = GetWindowThreadProcessId(hFore, out _);
                GUITHREADINFO gui = new GUITHREADINFO();
                gui.cbSize = Marshal.SizeOf(gui);
                GetGUIThreadInfo(ftid, ref gui);
                IntPtr hFocusCtrl = gui.hwndFocus != IntPtr.Zero ? gui.hwndFocus : hFore;
                IntPtr hImeTarget = gui.hwndFocus != IntPtr.Zero ? gui.hwndFocus : (gui.hwndCaret != IntPtr.Zero ? gui.hwndCaret : hFore);
                ViewModels.MainViewModel.FileLog($"[DIAG-FOCUS] hFore={hFore}, ftid={ftid}, hwndFocus={gui.hwndFocus}, hwndCaret={gui.hwndCaret} -> hImeTarget={hImeTarget}");

                // ---- IME モード ----
                ViewModels.MainViewModel.FileLog($"[DIAG-IME] Start GetImeMode for hImeTarget={hImeTarget}");
                string mode = GetImeMode(hImeTarget); 
                ViewModels.MainViewModel.FileLog($"[DIAG-IME] Result: {mode}");
                lock (_lock)
                {
                    if (mode != _lastMode)
                    {
                        ViewModels.MainViewModel.FileLog($"[IME] Mode changed: {mode} (HWND={hImeTarget})");
                        _lastMode = mode;
                        ModeChanged?.Invoke(mode);
                    }
                }
                string pName = "Unknown";
                try { pName = Process.GetProcessById((int)pid).ProcessName; } catch { }

                lock (_lock)
                {
                    if (hFocusCtrl != _lastFocusHwnd)
                    {
                        _lastFocusHwnd = hFocusCtrl;
                        try
                        {
                            char[] cls = new char[256];
                            GetClassName(hFocusCtrl, cls, 256);
                            ViewModels.MainViewModel.FileLog(
                                $"[FOCUS] App={pName}, Class={new string(cls).TrimEnd('\0')}, HWND={hFocusCtrl}");
                        }
                        catch { }
                    }
                }

                // ================================================================
                // PER-APP STRATEGY ROUTING
                // ================================================================
                string pNameLower = pName.ToLowerInvariant();
                
                // --- Phase 1: Notepad (メモ帳) & Phase 2: Hidemaru (秀丸) ---
                if (pNameLower == "notepad" || pNameLower.Contains("hidemaru"))
                {
                    IntPtr hC = hFocusCtrl;
                    if (pNameLower.Contains("hidemaru"))
                    {
                        char[] cNameArr = new char[256];
                        GetClassName(hC, cNameArr, 256);
                        string cls = new string(cNameArr).TrimEnd('\0').ToLowerInvariant();
                        if (!cls.Contains("client"))
                        {
                            EnumChildWindows(hFore, (hwnd, lp) => {
                                char[] cnArr = new char[256];
                                GetClassName(hwnd, cnArr, 256);
                                string n = new string(cnArr);
                                if (n.Contains("CLIENT")) { hC = hwnd; return false; }
                                return true;
                            }, IntPtr.Zero);
                        }
                    }

                    if (IsEditClassWindow(hC))
                    {
                        string win32Text = GetWindowText(hC);
                        string cleanWin32 = (win32Text ?? "").Replace("\uFFFC", "").Trim();
                        if (pNameLower == "notepad" || !string.IsNullOrEmpty(cleanWin32))
                        {
                            int win32Cursor = GetSelectionStart(hC);
                            PublishState(win32Text ?? "", win32Cursor, $"{pName}: Win32", hImeTarget);
                            return;
                        }
                    }
                }

                // --- Phase 3: Excel (エクセル) ---
                if (pNameLower == "excel")
                {
                    // Excel cells in edit mode may not report focus via GetGUIThreadInfo.
                    // Use EXCEL7 window as a reliable IME target for the active sheet.
                    IntPtr hDesk = FindWindowEx(hFore, IntPtr.Zero, "XLDESK", null);
                    IntPtr hExcel7 = FindWindowEx(hDesk, IntPtr.Zero, "EXCEL7", null);
                    
                    if (hExcel7 == IntPtr.Zero)
                    {
                        // Fallback search if MDI structure is hidden
                        EnumChildWindows(hFore, (hwnd, lp) => {
                            char[] cn = new char[256];
                            GetClassName(hwnd, cn, 256);
                            string name = new string(cn).TrimEnd('\0');
                            if (name.Contains("EXCEL7")) { hExcel7 = hwnd; return false; }
                            return true;
                        }, IntPtr.Zero);
                    }

                    if (hExcel7 != IntPtr.Zero)
                    {
                        hImeTarget = hExcel7;
                        ViewModels.MainViewModel.FileLog($"[EXCEL-PH3] Target EXCEL7 HWND={hExcel7}");
                    }
                }

                // ================================================================
                // Strategy 2: MSAA (Legacy Accessibility)
                // Hidemaru and older apps often expose text via MSAA more reliably than UIA.
                // ================================================================
                if (TryReadViaAccessible(hImeTarget, out string msaaText, out int msaaCursor))
                {
                    string cleanMsaa = msaaText.Replace("\uFFFC", "").Trim();
                    if (!string.IsNullOrEmpty(cleanMsaa))
                    {
                        ViewModels.MainViewModel.FileLog($"[DIAG-STRATEGY] MSAA Success: Len={msaaText.Length}, Cur={msaaCursor}");
                        PublishState(msaaText, msaaCursor, "MSAA: Sync", hImeTarget);
                        return;
                    }
                }


                // ================================================================
                // Strategy 1: UIAutomation (Standard API)
                // We walk up the tree to find the real text container.
                // ================================================================
                ViewModels.MainViewModel.FileLog($"[DIAG-STRATEGY] Attempting UIA (Strategy 1)");
                if (TryReadViaFocusedElement(out string uiaText, out int uiaCursor))
                {
                    string cleanUia = uiaText.Replace("\uFFFC", "").Trim();
                    if (!string.IsNullOrEmpty(cleanUia))
                    {
                        ViewModels.MainViewModel.FileLog($"[DIAG-STRATEGY] UIA Success: '{uiaText}', Cur={uiaCursor}");
                        PublishState(uiaText, uiaCursor, "UIA: Sync", hImeTarget);
                        return;
                    }
                    else
                    {
                        ViewModels.MainViewModel.FileLog($"[DIAG-STRATEGY] UIA returned only placeholders, ignoring.");
                    }
                }
                
                ViewModels.MainViewModel.FileLog($"[DIAG-STRATEGY] UIA Failed.");

                // ================================================================
                // Strategy 2: Win32 WM_GETTEXT / EM_GETSEL - Sync Source
                // ================================================================
                ViewModels.MainViewModel.FileLog($"[DIAG-STRATEGY] Attempting Win32 (Strategy 2) for HWND={hFocusCtrl}");
                if (IsEditClassWindow(hFocusCtrl))
                {
                    string win32Text = GetWindowText(hFocusCtrl);
                    if (!string.IsNullOrEmpty(win32Text))
                    {
                        int win32Cursor = GetSelectionStart(hFocusCtrl);
                        ViewModels.MainViewModel.FileLog($"[DIAG-STRATEGY] Win32 Success: '{win32Text}', Cur={win32Cursor}");
                        PublishState(win32Text, win32Cursor, "Win32: Sync", hImeTarget);
                        return;
                    }
                    else
                    {
                        ViewModels.MainViewModel.FileLog($"[DIAG-STRATEGY] Win32 empty/failed.");
                    }
                }

                ViewModels.MainViewModel.FileLog($"[DIAG-STRATEGY] All strategies failed. Mode: {(_uiaWorkedLastCheck ? "UIA_LAST" : "HOOK_FALLBACK")}");

                // ================================================================
                // Strategy 3: キーボードフック + IME 合成のみ
                //   フック本体は HookCallback → ProcessKeyFallback が担う
                //   ここでは合成文字列の変化だけ拾う
                // ================================================================
                lock (_lock)
                {
                    _uiaWorkedLastCheck = false;
                    string compOnly = GetCompositionString(hFocusCtrl);
                    if (compOnly != _compositionText)
                    {
                        _compositionText = compOnly;
                    }
                    
                    // Hook fallback 時も可能な限り最後のカーソル位置を維持する。不明な場合は末尾。
                    int fallbackCursor = (_lastReportedCursor >= 0 && _lastReportedCursor <= _keyboardBuffer.Length) 
                                       ? _lastReportedCursor 
                                       : _keyboardBuffer.Length;
                                       
                    string displayHook = _keyboardBuffer.Insert(Math.Clamp(fallbackCursor, 0, _keyboardBuffer.Length), _compositionText);
                    RaiseIfChanged(displayHook, fallbackCursor + _compositionText.Length, "Hook: Active");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CheckAll] {ex.Message}");
            }
            finally
            {
                _isChecking = false;
            }
        }

        private void PublishState(string text, int cursor, string strategy, IntPtr hImeTarget)
        {
            string comp = GetCompositionString(hImeTarget);
            string display = text.Insert(Math.Clamp(cursor, 0, text.Length), comp);
            int cur = cursor + comp.Length;

            lock (_lock)
            {
                _uiaWorkedLastCheck = true;
                _keyboardBuffer = text;
                _compositionText = comp;
                
                // CRITICAL: Do NOT set _lastReportedCursor here! 
                // It breaks the change detection inside RaiseIfChanged.
                RaiseIfChanged(display, cur, strategy);
            }
        }

        // ================================================================
        // UIAutomation Universal Tree-Walking Strategy
        // ================================================================

        private bool TryReadViaFocusedElement(out string text, out int cursor)
        {
            text = "";
            cursor = 0;
            try
            {
                AutomationElement? el = null;
                try 
                { 
                    var t = Task.Run(() => AutomationElement.FocusedElement);
                    if (!t.Wait(300)) return false;
                    el = t.Result;
                } catch { return false; }

                if (el == null) return false;
                try { if (el.Current.ProcessId == _myProcessId) return false; } catch { return false; }

                // --- Tree Walking Logic ---
                // Modern apps (Chrome, Discord) often focus a tiny fragment element.
                // We walk up to find the parent that actually holds the scrollable document/text.
                AutomationElement? current = el;
                string? lastBestText = null;
                int lastBestCursor = 0;

                for (int i = 0; i < 10; i++) // Walk up 10 levels
                {
                    if (current == null) break;

                    string cType = "UnknownType";
                    string cName = "UnknownName";
                    try { cType = current.Current.ControlType.ProgrammaticName; cName = current.Current.Name ?? ""; } catch { }

                    if (ExtractTextFromUiaElement(current, out text, out cursor))
                    {
                        bool isLabel = IsProbablyJustALabel(current, text);
                        ViewModels.MainViewModel.FileLog($"[UIA-WALK] Lvl {i}: {cType} '{cName}', TextLen={text.Length}, IsLabel={isLabel}");

                        if (!isLabel)
                        {
                            // If it's a known editor container, we found the prize.
                            if (cType.Contains("Document") || cType.Contains("Edit") || cType.Contains("Custom"))
                            {
                                ViewModels.MainViewModel.FileLog($"[UIA-WALK] Win at Lvl {i} (Editor Type)!");
                                return true;
                            }
                            
                            // Otherwise, save as potential fallback
                            if (text.Length > (lastBestText?.Length ?? -1))
                            {
                                lastBestText = text;
                                lastBestCursor = cursor;
                            }
                        }
                    }
                    try { current = TreeWalker.RawViewWalker.GetParent(current); } catch { break; }
                }

                if (lastBestText != null)
                {
                    text = lastBestText;
                    cursor = lastBestCursor;
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        private bool ExtractTextFromUiaElement(AutomationElement el, out string text, out int cursor)
        {
            text = ""; cursor = 0;
            if (el == null) return false;

            try
            {
                // 1. TextPattern (High Fidelity)
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tpObj))
                {
                    var tp = (TextPattern)tpObj;
                    text = tp.DocumentRange.GetText(4000) ?? "";
                    
                    // VS Code / Electron 'Accessibility Stub' Check
                    // If we get the "accessibility help" text, it's not the real editor.
                    if (IsAccessibilityHelpText(text)) { text = ""; return false; }

                    var sels = tp.GetSelection();
                    if (sels?.Length > 0)
                    {
                        var range = tp.DocumentRange.Clone();
                        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, sels[0], TextPatternRangeEndpoint.Start);
                        cursor = (range.GetText(4000) ?? "").Length;
                    }
                    else { cursor = text.Length; }
                    return true;
                }

                // 2. ValuePattern (Search bars, simple edits)
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj))
                {
                    text = ((ValuePattern)vpObj).Current.Value ?? "";
                    if (IsAccessibilityHelpText(text)) { text = ""; return false; }
                    
                    cursor = text.Length;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private bool IsProbablyJustALabel(AutomationElement el, string text)
        {
            try 
            {
                // DOCUMENT and EDIT controls are NEVER labels, even if short
                var ct = el.Current.ControlType;
                if (ct == ControlType.Document || ct == ControlType.Edit || ct == ControlType.Custom) return false;

                if (text.Length > 100) return false; 
                string name = el.Current.Name ?? "";
                if (!string.IsNullOrEmpty(name) && text.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
                
                string helpText = el.Current.HelpText ?? "";
                if (!string.IsNullOrEmpty(helpText) && text.Trim().Equals(helpText.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            } catch { }
            return false;
        }

        private bool IsAccessibilityHelpText(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            if (t.Contains("エディターにアクセスできません") || 
                t.Contains("Shift+Alt+F1") ||
                t.Contains("accessibility mode") ||
                t.Contains("screen reader optimization"))
                return true;
            return false;
        }

        // ================================================================
        // IAccessible (MSAA) による読み取り
        // ================================================================

        private bool TryReadViaAccessible(IntPtr hWnd, out string text, out int cursor)
        {
            text = "";
            cursor = 0;
            if (hWnd == IntPtr.Zero) return false;
            try
            {
                Guid guid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
                // OBJID_CLIENT
                if (AccessibleObjectFromWindow(hWnd, 4294967292, ref guid, out object accObj) == 0)
                {
                    IAccessible acc = (IAccessible)accObj;
                    if (acc != null)
                    {
                        try {
                            if (acc.get_accValue(0, out string val) == 0 && val != null)
                            {
                                text = val;
                                cursor = text.Length;
                                return true;
                            }
                        } catch { /* vtable or threading mismatch */ }
                    }
                }
            }
            catch { }
            return false;
        }

        // ================================================================
        // IME ヘルパー
        // ================================================================

        private string GetCompositionString(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return "";
            IntPtr hImc = ImmGetContext(hWnd);
            if (hImc == IntPtr.Zero)
            {
                // Try parent window if focus window doesn't have IMC
                IntPtr hParent = GetParent(hWnd);
                if (hParent != IntPtr.Zero) hImc = ImmGetContext(hParent);
            }
            
            if (hImc == IntPtr.Zero) return "";
            try
            {
                int len = ImmGetCompositionString(hImc, GCS_COMPSTR, null!, 0);
                if (len <= 0) return "";
                byte[] buf = new byte[len];
                ImmGetCompositionString(hImc, GCS_COMPSTR, buf, (uint)len);
                return Encoding.Unicode.GetString(buf);
            }
            finally
            {
                ImmReleaseContext(hWnd, hImc);
            }
        }


        private const int WM_IME_CONTROL = 0x0283;
        private const int IMC_GETOPENSTATUS = 0x0005;

        private string GetImeMode(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return "| A";

            // High Priority: WM_IME_CONTROL (Works for Chrome/Electron)
            IntPtr hDefIme = ImmGetDefaultIMEWnd(hWnd);
            if (hDefIme != IntPtr.Zero)
            {
                if (SendMessage(hDefIme, WM_IME_CONTROL, IMC_GETOPENSTATUS, 0) != 0)
                    return "|あ";
            }
            
            IntPtr hImc = IntPtr.Zero;
            if (hImc == IntPtr.Zero)
            {
                IntPtr hImeWnd = ImmGetDefaultIMEWnd(hWnd);
                if (hImeWnd != IntPtr.Zero) hImc = ImmGetContext(hImeWnd);
            }

            if (hImc == IntPtr.Zero)
            {
                // Fallback: GetForegroundWindow context
                IntPtr hFore = GetForegroundWindow();
                hImc = ImmGetContext(hFore);
            }

            if (hImc == IntPtr.Zero) return "| A";
            
            bool open = ImmGetOpenStatus(hImc);
            ImmReleaseContext(hWnd, hImc);
            return open ? "|あ" : "| A";
        }

        // ================================================================
        // 内部ユーティリティ
        // ================================================================

        /// <summary>ロック内から呼ぶこと。変化があった場合のみイベントを発行する。</summary>
        private void RaiseIfChanged(string text, int cursor, string strategy)
        {
            StatusChanged?.Invoke(strategy);
            if (text == _lastReportedText && cursor == _lastReportedCursor) return;
            ViewModels.MainViewModel.FileLog($"[DIAG-EVENT] RaiseFocusedTextChanged: TextLength={text.Length}, Cur={cursor}, Strategy={strategy}");
            _lastReportedText = text;
            _lastReportedCursor = cursor;
            FocusedTextChanged?.Invoke(text, cursor);
        }

        /// <summary>
        /// Win32 ウィンドウのクラス名が Edit 系かどうかを判定する。
        /// Notepad 等の標準 Edit コントロールのみ許可し、
        /// ウィンドウタイトルを返すメインウィンドウ等を除外する。
        /// </summary>
        private bool IsEditClassWindow(IntPtr hWnd)
        {
            char[] cls = new char[256];
            // Use SendMessageTimeout with WM_GETTEXT as a heuristic if the target app is hanging,
            // but for ClassName, GetClassName logic is usually safe. 
            // However, to be extra safe against OS-level weirdness:
            int res = GetClassName(hWnd, cls, 256);
            if (res == 0) return false;
            string name = new string(cls).TrimEnd('\0').ToLowerInvariant();
            ViewModels.MainViewModel.FileLog($"[DIAG-CLASS] HWND={hWnd}, Class='{name}'");
 
            // 伝統的な Edit / RichEdit 系
            if (name.Contains("edit")) return true;
            if (name.Contains("richedit")) return true;
            if (name.Contains("scintilla")) return true; 

            // エディタ
            if (name.Contains("hm32client") || name.Contains("hm64client")) return true;
            if (name.Contains("sakura")) return true;
            if (name.Contains("mery")) return true;
            if (name.Contains("xyzzy")) return true;
            if (name.Contains("notepad")) return true; 

            // モダン/カスタム系
            if (name.Contains("avalon")) return true; 
            if (name.Contains("textarea")) return true;
            if (name.Contains("tedit") || name.Contains("tmemo") || name.Contains("trich") || name.Contains("trichedit")) return true;
            if (name.Contains("pb")) return true;
            // Chrome / Electron 系の特定サブクラス (Win32 APIが効く場合がある)
            if (name.Contains("renderwidget")) return true;
            
            return false;
        }

        // ================================================================
        // Win32 テキスト取得 (クラシック Edit コントロール)
        // ================================================================

        private string GetWindowText(IntPtr hWnd)
        {
            int len;
            // Increase timeout slightly to 400ms
            IntPtr res = SendMessageTimeout(hWnd, WM_GETTEXTLENGTH, 0, 0, SMTO_ABORTIFHUNG, 400, out len);
            if (res == IntPtr.Zero || len <= 0) return "";
            if (len > 10000) len = 10000;

            var sb = new StringBuilder(len + 1);
            res = SendMessageTimeout(hWnd, WM_GETTEXT, len + 1, sb, SMTO_ABORTIFHUNG, 400, out _);
            if (res == IntPtr.Zero) return "";
            return sb.ToString();
        }

        private int GetSelectionStart(IntPtr hWnd)
        {
            int start;
            IntPtr res = SendMessageTimeout(hWnd, EM_GETSEL, out start, out _, SMTO_ABORTIFHUNG, 400, out _);
            if (res == IntPtr.Zero) return 0;
            return start;
        }

        // ================================================================
        // Win32 API 宣言
        // ================================================================

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL   = 13;
        private const int WM_KEYDOWN       = 0x0100;
        private const int WM_SYSKEYDOWN    = 0x0104;
        private const int WM_GETTEXT       = 0x000D;
        private const int WM_GETTEXTLENGTH = 0x000E;
        private const int EM_GETSEL        = 0x00B0;
        private const uint GCS_COMPSTR     = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode, scanCode, flags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint dwId, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);

        [ComImport, Guid("618736e0-3c3d-11cf-810c-00aa00389b71"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IAccessible
        {
            [PreserveSig] int get_accParent([MarshalAs(UnmanagedType.IDispatch)] out object ppdispParent);
            [PreserveSig] int get_accChildCount(out int pcountChildren);
            [PreserveSig] int get_accChild(object childId, [MarshalAs(UnmanagedType.IDispatch)] out object ppdispChild);
            [PreserveSig] int get_accName(object childId, [MarshalAs(UnmanagedType.BStr)] out string pszName);
            [PreserveSig] int get_accValue(object childId, [MarshalAs(UnmanagedType.BStr)] out string pszValue);
            [PreserveSig] int get_accDescription(object childId, [MarshalAs(UnmanagedType.BStr)] out string pszDescription);
            [PreserveSig] int get_accRole(object childId, out object pvarRole);
            [PreserveSig] int get_accState(object childId, out object pvarState);
            [PreserveSig] int get_accHelp(object childId, [MarshalAs(UnmanagedType.BStr)] out string pszHelp);
            [PreserveSig] int get_accHelpTopic(out string pszHelpFile, object childId, out int pidTopic);
            [PreserveSig] int get_accKeyboardShortcut(object childId, [MarshalAs(UnmanagedType.BStr)] out string pszKeyboardShortcut);
            [PreserveSig] int accFocus(out object pvarChild);
            [PreserveSig] int accSelection(out object pvarChildren);
            [PreserveSig] int get_accDefaultAction(object childId, [MarshalAs(UnmanagedType.BStr)] out string pszDefaultAction);
            [PreserveSig] int accSelect(int flagsSelect, object childId);
            [PreserveSig] int accLocation(out int pxLeft, out int pyTop, out int pcxWidth, out int pcyHeight, object childId);
            [PreserveSig] int accNavigate(int navDir, object childId, out object pvarEndUpAt);
            [PreserveSig] int accHitTest(int xLeft, int yTop, out object pvarChild);
            [PreserveSig] int accDoDefaultAction(object childId);
            [PreserveSig] int put_accName(object childId, string szName);
            [PreserveSig] int put_accValue(object childId, string szValue);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern bool GetKeyboardState(byte[] lpKeyState);

        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
            StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, int wParam, int lParam, uint fuFlags, uint uTimeout, out int lpdwResult);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, int wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, out int lpdwResult);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, out int wParam, out int lParam, uint fuFlags, uint uTimeout, out int lpdwResult);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, StringBuilder lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, out int wParam, out int lParam);

        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll")]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

        [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
        public static extern bool ImmGetOpenStatus(IntPtr hIMC);

        [DllImport("imm32.dll")]
        private static extern int ImmGetCompositionString(IntPtr hIMC, uint dwIndex, byte[]? lpBuf, uint dwBufLen);
    }
}

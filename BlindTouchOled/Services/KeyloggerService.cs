using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Linq;

namespace BlindTouchOled.Services
{
    public class KeyloggerService
    {
        public event Action<string, string>? BufferChanged;

        private string _confirmed = "";
        private string _romajiPending = "";
        private bool _pendingReset = false;
        private bool _isKanaMode = false;

        public bool ClearOnEnter { get; set; } = false;
        public bool ResetOnClick { get; set; } = true;
        public bool IsEnabled { get; set; } = true;

        private IntPtr _keyboardHookHandle = IntPtr.Zero;
        private IntPtr _mouseHookHandle = IntPtr.Zero;
        private LowLevelKeyboardProc? _keyboardHookProc;
        private LowLevelMouseProc? _mouseHookProc;
        private readonly int _myPid;

        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;

        private const uint VK_BACK = 0x08;
        private const uint VK_ESCAPE = 0x1B;
        private const uint VK_RETURN = 0x0D;
        private const uint VK_SPACE = 0x20;
        private const uint VK_SHIFT = 0x10;
        private const uint VK_CONTROL = 0x11;
        private const uint VK_MENU = 0x12;
        private const uint VK_CAPITAL = 0x14;
        private const uint VK_LWIN = 0x5B;
        private const uint VK_RWIN = 0x5C;
        private const uint VK_TAB = 0x09;
        private const uint VK_KANJI = 0x19;
        private const uint VK_CONVERT = 0x1C;
        private const uint VK_NONCONVERT = 0x1D;
        private const uint VK_OEM_COPY = 0xF2;
        private const uint VK_OEM_AUTO = 0xF3;
        private const uint VK_OEM_ENLW = 0xF4;

        private static readonly HashSet<char> Vowels = new() { 'a', 'i', 'u', 'e', 'o' };

        public KeyloggerService()
        {
            _myPid = Process.GetCurrentProcess().Id;
        }

        public void Start()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _keyboardHookProc = OnKeyboardHook;
                IntPtr hMod = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName ?? "");
                _keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc, hMod, 0);

                _mouseHookProc = OnMouseHook;
                _mouseHookHandle = SetWindowsHookExMouse(WH_MOUSE_LL, _mouseHookProc, hMod, 0);

                ViewModels.MainViewModel.FileLog(_keyboardHookHandle != IntPtr.Zero ? "[KL] Hook OK" : "[KL] Hook Error");
            });
        }

        public void Stop()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_keyboardHookHandle != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHookHandle); _keyboardHookHandle = IntPtr.Zero; }
                if (_mouseHookHandle != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHookHandle); _mouseHookHandle = IntPtr.Zero; }
            });
        }

        public void UpdateImeMode(string modeString)
        {
            bool newMode = modeString.Contains("あ");
            if (_isKanaMode && !newMode)
            {
                FlushRomaji();
            }
            _isKanaMode = newMode;
        }

        private void FlushRomaji()
        {
            if (_romajiPending.Length > 0)
            {
                _confirmed += _romajiPending;
                _romajiPending = "";
                FireChanged();
            }
        }

        public void ResetBuffer() { _confirmed = ""; _romajiPending = ""; _pendingReset = false; FireChanged(); }

        private IntPtr OnKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN) && IsEnabled)
            {
                var s = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                IntPtr hFore = GetForegroundWindow();
                if (hFore != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hFore, out uint pid);
                    if (pid != (uint)_myPid) ProcessKey(s.vkCode, s.scanCode, hFore);
                }
            }
            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && IsEnabled && ResetOnClick)
            {
                int msg = (int)wParam;
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN) ResetBuffer();
            }
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        private void ProcessKey(uint vk, uint scanCode, IntPtr hFore)
        {
            if (vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU || vk == VK_LWIN || vk == VK_RWIN) return;

            // --- IME切り替えキーの直接監視 ---
            bool modeChanged = false;
            if (vk == VK_KANJI || vk == VK_OEM_AUTO || vk == VK_OEM_ENLW) // 半角/全角
            {
                _isKanaMode = !_isKanaMode;
                modeChanged = true;
            }
            else if (vk == VK_CONVERT) // 変換キー -> かな
            {
                _isKanaMode = true;
                modeChanged = true;
            }
            else if (vk == VK_NONCONVERT || vk == VK_CAPITAL) // 無変換/英数キー -> 英数
            {
                _isKanaMode = false;
                modeChanged = true;
            }

            if (modeChanged)
            {
                FlushRomaji(); 
                return;
            }

            // 特殊キー（Tab, 矢印, Fキー等）でバッファを確定
            bool isNavigation = (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == VK_TAB;
            bool isFunction = (vk >= 0x70 && vk <= 0x87);

            if (isNavigation || isFunction)
            {
                FlushRomaji();
                return;
            }

            if (_pendingReset && vk != VK_BACK) { _confirmed = ""; _romajiPending = ""; _pendingReset = false; }

            switch (vk)
            {
                case VK_RETURN: _romajiPending = ""; _pendingReset = !ClearOnEnter; if (ClearOnEnter) _confirmed = ""; break;
                case VK_ESCAPE: _confirmed = ""; _romajiPending = ""; _pendingReset = false; break;
                case VK_BACK:
                    if (_romajiPending.Length > 0) _romajiPending = _romajiPending[..^1];
                    else if (_confirmed.Length > 0) _confirmed = _confirmed[..^1];
                    break;
                case VK_SPACE:
                    if (!_isKanaMode) _confirmed += " ";
                    break;
                default:
                    AppendChar(vk, scanCode, hFore);
                    return;
            }
            FireChanged();
        }

        private void AppendChar(uint vk, uint scanCode, IntPtr hFore)
        {
            byte[] keyState = new byte[256];
            bool isShift = (GetKeyState((int)VK_SHIFT) & 0x8000) != 0;
            if (isShift) keyState[VK_SHIFT] = 0x80;
            if ((GetKeyState((int)VK_CAPITAL) & 0x0001) != 0) keyState[VK_CAPITAL] = 0x01;

            uint tid = GetWindowThreadProcessId(hFore, out _);
            var sb = new StringBuilder(4);
            int r = ToUnicodeEx(vk, scanCode, keyState, sb, 4, 0, GetKeyboardLayout(tid));
            if (r <= 0) return;
            string rawChar = sb.ToString();
            if (rawChar.Length == 0 || char.IsControl(rawChar[0])) return;

            // 文字判定
            bool isAsciiLetter = rawChar.Length == 1 && ((rawChar[0] >= 'a' && rawChar[0] <= 'z') || (rawChar[0] >= 'A' && rawChar[0] <= 'Z'));

            if (_isKanaMode)
            {
                if (isShift && isAsciiLetter)
                {
                    // かなモード + Shift + 英字 -> 半角英大文字
                    FlushRomaji();
                    _confirmed += rawChar;
                }
                else
                {
                    // かなモード + それ以外(記号、または通常のローマ字)
                    string lc = rawChar.ToLowerInvariant();
                    // nルール
                    if (_romajiPending == "n" && lc.Length > 0 && !Vowels.Contains(lc[0]) && lc[0] != 'y' && lc[0] != 'n')
                    {
                        _confirmed += "ん"; _romajiPending = "";
                    }
                    _romajiPending += lc;
                    TryConvertRomaji();
                }
            }
            else
            {
                // 英数モード
                FlushRomaji();
                _confirmed += rawChar;
            }
            FireChanged();
        }

        private void TryConvertRomaji()
        {
            bool progress = true;
            while (progress && _romajiPending.Length > 0)
            {
                progress = false;
                if (_romajiPending.Length >= 2 && _romajiPending[0] != 'n' && _romajiPending[0] == _romajiPending[1])
                {
                    _confirmed += "っ"; _romajiPending = _romajiPending[1..]; progress = true; continue;
                }
                for (int len = Math.Min(_romajiPending.Length, 4); len >= 1; len--)
                {
                    string key = _romajiPending[..len];
                    if (RomajiMap.TryGetValue(key, out string? hira))
                    {
                        _confirmed += hira; _romajiPending = _romajiPending[len..]; progress = true; break;
                    }
                }
                if (!progress)
                {
                    bool isPossiblePrefix = RomajiMap.Keys.Any(k => k.StartsWith(_romajiPending));
                    if (!isPossiblePrefix)
                    {
                        _confirmed += _romajiPending[..1]; _romajiPending = _romajiPending[1..]; progress = true;
                    }
                }
            }
        }

        private void FireChanged() => BufferChanged?.Invoke(_confirmed, _romajiPending);

        private static readonly Dictionary<string, string> RomajiMap = new()
        {
            {"a","あ"},{"i","い"},{"u","う"},{"e","え"},{"o","お"},
            {"ka","か"},{"ki","き"},{"ku","く"},{"ke","け"},{"ko","こ"},
            {"sa","さ"},{"si","し"},{"su","す"},{"se","せ"},{"so","そ"},{"shi","し"},
            {"ta","た"},{"ti","ち"},{"tu","つ"},{"te","て"},{"to","と"},{"chi","ち"},{"tsu","つ"},
            {"na","な"},{"ni","に"},{"nu","ぬ"},{"ne","ね"},{"no","の"},
            {"ha","は"},{"hi","ひ"},{"fu","ふ"},{"hu","ふ"},{"he","へ"},{"ho","ほ"},
            {"ma","ま"},{"mi","み"},{"mu","む"},{"me","ね"},{"mo","も"},
            {"ya","や"},{"yu","ゆ"},{"yo","よ"},
            {"ra","ら"},{"ri","り"},{"ru","る"},{"re","れ"},{"ro","ろ"},
            {"wa","わ"},{"wi","ゐ"},{"we","ゑ"},{"wo","を"},
            {"nn","ん"},{"n'","ん"},
            {"ga","が"},{"gi","ぎ"},{"gu","ぐ"},{"ge","げ"},{"go","ご"},
            {"za","ざ"},{"zi","じ"},{"zu","ず"},{"ze","ぜ"},{"zo","ぞ"},{"ji","じ"},
            {"da","だ"},{"di","ぢ"},{"du","づ"},{"de","で"},{"do","ど"},
            {"ba","ば"},{"bi","び"},{"bu","ぶ"},{"be","べ"},{"bo","ぼ"},
            {"pa","ぱ"},{"pi","ぴ"},{"pu","ぷ"},{"pe","ぺ"},{"po","ぽ"},
            {"kya","きゃ"},{"kyu","きゅ"},{"kyo","きょ"},{"sha","しゃ"},{"shu","しゅ"},{"sho","しょ"},
            {"cha","ちゃ"},{"chu","ちゅ"},{"cho","ちょ"},{"nya","にゃ"},{"nyu","にゅ"},{"nyo","にょ"},
            {"hya","ひゃ"},{"hyu","ひゅ"},{"hyo","ひょ"},{"mya","みゃ"},{"myu","みゅ"},{"myo","みょ"},
            {"rya","りゃ"},{"ryu","りゃ"},{"ryo","りょ"},{"gya","ぎゃ"},{"gyu","ぎゅ"},{"gyo","ぎょ"},
            {"ja","じゃ"},{"ju","じゅ"},{"jo","じょ"},{"bya","びゃ"},{"byu","びゅ"},{"byo","びょ"},
            {"pya","ぴゃ"},{"pyu","ぴゅ"},{"pyo","ぴょ"},
            {"la","ぁ"},{"li","ぃ"},{"lu","ぅ"},{"le","ぇ"},{"lo","ぉ"},{"xa","ぁ"},{"xi","ぃ"},{"xu","ぅ"},{"xe","ぇ"},{"xo","ぉ"},
            {"lya","ゃ"},{"lyu","ゅ"},{"lyo","ょ"},{"ltu","っ"},{"xtu","っ"},
            {".","。"},{",","、"},{"-","ー"},{"/","・"},{"[","「"},{"]","」"},{"!","！"},{"?","？"},
            {"(","（"},{")","）"},{"=","＝"},{"\"","”"},{"#","＃"},{"$","＄"},{"%","％"},{"&","＆"},{"'","’"},
            {"^","＾"},{"\\","￥"},{"|","｜"},{"@","＠"},{"`","｀"},{"{","｛"},{"}","｝"},{":","："},{";","；"},
            {"+","＋"},{"*","＊"},{"<","＜"},{">","＞"},{"_","＿"}
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, Delegate lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx")] private static extern IntPtr SetWindowsHookExMouse(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
        [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")] private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Chiramoji.Services
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
                if (_keyboardHookHandle != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_keyboardHookHandle);
                    _keyboardHookHandle = IntPtr.Zero;
                }

                if (_mouseHookHandle != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_mouseHookHandle);
                    _mouseHookHandle = IntPtr.Zero;
                }
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

        private void ForceImeOpenStatus(IntPtr hWnd, bool isOpen)
        {
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            IntPtr hImc = ImmGetContext(hWnd);
            if (hImc == IntPtr.Zero)
            {
                return;
            }

            try
            {
                ImmSetOpenStatus(hImc, isOpen);
            }
            finally
            {
                ImmReleaseContext(hWnd, hImc);
            }
        }

        private void FlushRomaji()
        {
            if (_romajiPending.Length == 0)
            {
                return;
            }

            TryConvertRomaji();
            if (_romajiPending == "n")
            {
                _confirmed += "ん";
                _romajiPending = "";
            }
            else if (_romajiPending.Length > 0)
            {
                _confirmed += _romajiPending;
                _romajiPending = "";
            }

            FireChanged();
        }

        public void ResetBuffer()
        {
            _confirmed = "";
            _romajiPending = "";
            _pendingReset = false;
            FireChanged();
        }

        private IntPtr OnKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN) && IsEnabled)
            {
                var keyInfo = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                IntPtr hFore = GetForegroundWindow();
                if (hFore != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hFore, out uint pid);
                    if (pid != (uint)_myPid)
                    {
                        ProcessKey(keyInfo.vkCode, keyInfo.scanCode, hFore);
                    }
                }
            }

            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && IsEnabled && ResetOnClick)
            {
                int msg = (int)wParam;
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN)
                {
                    ResetBuffer();
                }
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        private void ProcessKey(uint vk, uint scanCode, IntPtr hFore)
        {
            if (vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU || vk == VK_LWIN || vk == VK_RWIN)
            {
                return;
            }

            bool modeChanged = false;
            if (vk == VK_KANJI || vk == VK_OEM_AUTO || vk == VK_OEM_ENLW)
            {
                _isKanaMode = !_isKanaMode;
                ForceImeOpenStatus(hFore, _isKanaMode);
                modeChanged = true;
            }
            else if (vk == VK_CONVERT)
            {
                _isKanaMode = true;
                ForceImeOpenStatus(hFore, true);
                modeChanged = true;
            }
            else if (vk == VK_NONCONVERT || vk == VK_CAPITAL)
            {
                _isKanaMode = false;
                ForceImeOpenStatus(hFore, false);
                modeChanged = true;
            }

            if (modeChanged)
            {
                FlushRomaji();
                return;
            }

            bool isNavigation = (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == VK_TAB;
            bool isFunction = vk >= 0x70 && vk <= 0x87;
            if (isNavigation || isFunction)
            {
                FlushRomaji();
                return;
            }

            if (_pendingReset && vk != VK_BACK)
            {
                _confirmed = "";
                _romajiPending = "";
                _pendingReset = false;
            }

            switch (vk)
            {
                case VK_RETURN:
                    FlushRomaji();
                    _pendingReset = !ClearOnEnter;
                    if (ClearOnEnter)
                    {
                        _confirmed = "";
                    }
                    break;
                case VK_ESCAPE:
                    _confirmed = "";
                    _romajiPending = "";
                    _pendingReset = false;
                    break;
                case VK_BACK:
                    if (_romajiPending.Length > 0)
                    {
                        _romajiPending = _romajiPending[..^1];
                    }
                    else if (_confirmed.Length > 0)
                    {
                        _confirmed = _confirmed[..^1];
                    }
                    break;
                case VK_SPACE:
                    if (!_isKanaMode)
                    {
                        _confirmed += " ";
                    }
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
            if (isShift)
            {
                keyState[VK_SHIFT] = 0x80;
            }

            if ((GetKeyState((int)VK_CAPITAL) & 0x0001) != 0)
            {
                keyState[VK_CAPITAL] = 0x01;
            }

            uint tid = GetWindowThreadProcessId(hFore, out _);
            var sb = new StringBuilder(4);
            int r = ToUnicodeEx(vk, scanCode, keyState, sb, 4, 0, GetKeyboardLayout(tid));
            if (r <= 0)
            {
                return;
            }

            string rawChar = sb.ToString();
            if (rawChar.Length == 0 || char.IsControl(rawChar[0]))
            {
                return;
            }

            bool isAsciiLetter = rawChar.Length == 1 && ((rawChar[0] >= 'a' && rawChar[0] <= 'z') || (rawChar[0] >= 'A' && rawChar[0] <= 'Z'));
            if (_isKanaMode)
            {
                if (isShift && isAsciiLetter)
                {
                    FlushRomaji();
                    _confirmed += rawChar;
                }
                else
                {
                    string lc = rawChar.ToLowerInvariant();
                    if (_romajiPending == "n" && lc == "'")
                    {
                        _confirmed += "ん";
                        _romajiPending = "";
                        FireChanged();
                        return;
                    }

                    if (_romajiPending == "n" && lc.Length > 0 && !Vowels.Contains(lc[0]) && lc[0] != 'y' && lc[0] != 'n')
                    {
                        _confirmed += "ん";
                        _romajiPending = "";
                    }

                    _romajiPending += lc;
                    TryConvertRomaji();
                }
            }
            else
            {
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
                    _confirmed += "っ";
                    _romajiPending = _romajiPending[1..];
                    progress = true;
                    continue;
                }

                for (int len = Math.Min(_romajiPending.Length, 4); len >= 1; len--)
                {
                    string key = _romajiPending[..len];
                    if (RomajiMap.TryGetValue(key, out string? hira))
                    {
                        _confirmed += hira;
                        _romajiPending = _romajiPending[len..];
                        progress = true;
                        break;
                    }
                }

                if (!progress)
                {
                    bool isPossiblePrefix = RomajiMap.Keys.Any(k => k.StartsWith(_romajiPending));
                    if (!isPossiblePrefix)
                    {
                        _confirmed += _romajiPending[..1];
                        _romajiPending = _romajiPending[1..];
                        progress = true;
                    }
                }
            }
        }

        private void FireChanged() => BufferChanged?.Invoke(_confirmed, _romajiPending);

        private static readonly Dictionary<string, string> RomajiMap = new()
        {
            {"a","あ"},{"i","い"},{"u","う"},{"e","え"},{"o","お"},{"yi","い"},{"ye","いぇ"},
            {"ka","か"},{"ca","か"},{"ki","き"},{"ku","く"},{"cu","く"},{"qu","く"},{"ke","け"},{"ko","こ"},{"co","こ"},
            {"sa","さ"},{"si","し"},{"shi","し"},{"ci","し"},{"su","す"},{"se","せ"},{"ce","せ"},{"so","そ"},
            {"ta","た"},{"ti","ち"},{"chi","ち"},{"tu","つ"},{"tsu","つ"},{"te","て"},{"to","と"},
            {"na","な"},{"ni","に"},{"nu","ぬ"},{"ne","ね"},{"no","の"},
            {"ha","は"},{"hi","ひ"},{"hu","ふ"},{"fu","ふ"},{"he","へ"},{"ho","ほ"},
            {"ma","ま"},{"mi","み"},{"mu","む"},{"me","め"},{"mo","も"},
            {"ya","や"},{"yu","ゆ"},{"yo","よ"},
            {"ra","ら"},{"ri","り"},{"ru","る"},{"re","れ"},{"ro","ろ"},
            {"wa","わ"},{"wi","うぃ"},{"wu","う"},{"we","うぇ"},{"wo","を"},
            {"nn","ん"},{"n'","ん"},
            {"ga","が"},{"gi","ぎ"},{"gu","ぐ"},{"ge","げ"},{"go","ご"},
            {"za","ざ"},{"zi","じ"},{"ji","じ"},{"zu","ず"},{"ze","ぜ"},{"zo","ぞ"},
            {"da","だ"},{"di","ぢ"},{"du","づ"},{"de","で"},{"do","ど"},
            {"ba","ば"},{"bi","び"},{"bu","ぶ"},{"be","べ"},{"bo","ぼ"},
            {"pa","ぱ"},{"pi","ぴ"},{"pu","ぷ"},{"pe","ぺ"},{"po","ぽ"},
            {"xa","ぁ"},{"la","ぁ"},{"xi","ぃ"},{"li","ぃ"},{"xu","ぅ"},{"lu","ぅ"},{"xe","ぇ"},{"le","ぇ"},{"xo","ぉ"},{"lo","ぉ"},
            {"xka","ヵ"},{"lka","ヵ"},{"xke","ヶ"},{"lke","ヶ"},{"xtu","っ"},{"ltu","っ"},{"ltsu","っ"},
            {"xya","ゃ"},{"lya","ゃ"},{"xyi","ぃ"},{"lyi","ぃ"},{"xyu","ゅ"},{"lyu","ゅ"},{"xye","ぇ"},{"lye","ぇ"},{"xyo","ょ"},{"lyo","ょ"},{"xwa","ゎ"},{"lwa","ゎ"},
            {"va","ヴぁ"},{"vi","ヴぃ"},{"vyi","ヴぃ"},{"vu","ヴ"},{"ve","ヴぇ"},{"vye","ヴぇ"},{"vo","ヴぉ"},
            {"kya","きゃ"},{"kyi","きぃ"},{"kyu","きゅ"},{"kye","きぇ"},{"kyo","きょ"},
            {"sya","しゃ"},{"sha","しゃ"},{"syi","しぃ"},{"syu","しゅ"},{"shu","しゅ"},{"sye","しぇ"},{"she","しぇ"},{"syo","しょ"},{"sho","しょ"},
            {"cya","ちゃ"},{"tya","ちゃ"},{"cha","ちゃ"},{"cyi","ちぃ"},{"tyi","ちぃ"},{"cyu","ちゅ"},{"tyu","ちゅ"},{"chu","ちゅ"},{"cye","ちぇ"},{"tye","ちぇ"},{"che","ちぇ"},{"cyo","ちょ"},{"tyo","ちょ"},{"cho","ちょ"},
            {"nya","にゃ"},{"nyi","にぃ"},{"nyu","にゅ"},{"nye","にぇ"},{"nyo","にょ"},
            {"hya","ひゃ"},{"hyi","ひぃ"},{"hyu","ひゅ"},{"hye","ひぇ"},{"hyo","ひょ"},
            {"mya","みゃ"},{"myi","みぃ"},{"myu","みゅ"},{"mye","みぇ"},{"myo","みょ"},
            {"tha","てゃ"},{"thi","てぃ"},{"thu","てゅ"},{"the","てぇ"},{"tho","てょ"},
            {"rya","りゃ"},{"ryi","りぃ"},{"ryu","りゅ"},{"rye","りぇ"},{"ryo","りょ"},
            {"dha","でゃ"},{"dhi","でぃ"},{"dhu","でゅ"},{"dhe","でぇ"},{"dho","でょ"},
            {"fya","ふゃ"},{"fyi","ふぃ"},{"fyu","ふゅ"},{"fye","ふぇ"},{"fyo","ふょ"},
            {"gya","ぎゃ"},{"gyi","ぎぃ"},{"gyu","ぎゅ"},{"gye","ぎぇ"},{"gyo","ぎょ"},
            {"ja","じゃ"},{"jya","じゃ"},{"zya","じゃ"},{"jyi","じぃ"},{"zyi","じぃ"},{"ju","じゅ"},{"jyu","じゅ"},{"zyu","じゅ"},{"je","じぇ"},{"jye","じぇ"},{"zye","じぇ"},{"jo","じょ"},{"jyo","じょ"},{"zyo","じょ"},
            {"dya","ぢゃ"},{"dyi","ぢぃ"},{"dyu","ぢゅ"},{"dye","ぢぇ"},{"dyo","ぢょ"},
            {"bya","びゃ"},{"byi","びぃ"},{"byu","びゅ"},{"bye","びぇ"},{"byo","びょ"},
            {"pya","ぴゃ"},{"pyi","ぴぃ"},{"pyu","ぴゅ"},{"pye","ぴぇ"},{"pyo","ぴょ"},
            {"fa","ふぁ"},{"fi","ふぃ"},{"fe","ふぇ"},{"fo","ふぉ"},
            {"qa","くぁ"},{"qi","くぃ"},{"qyi","くぃ"},{"qe","くぇ"},{"qye","くぇ"},{"qo","くぉ"},
            {"wha","うぁ"},{"whi","うぃ"},{"whu","う"},{"whe","うぇ"},{"who","うぉ"},
            {"qya","くゃ"},{"qyu","くゅ"},{"qyo","くょ"},
            {"vya","ヴゃ"},{"vyu","ヴゅ"},{"vyo","ヴょ"},
            {"tsa","つぁ"},{"tsi","つぃ"},{"tse","つぇ"},{"tso","つぉ"},
            {".","。"},{",","、"},{"-","ー"},{"/","・"},{"[","「"},{"]","」"},{"!","！"},{"?","？"},
            {"(","（"},{")","）"},{"=","＝"},{"\"","”"},{"#","＃"},{"$","＄"},{"%","％"},{"&","＆"},{"'","’"},
            {"^","＾"},{"\\","￥"},{"|","｜"},{"@","＠"},{"`","｀"},{"{","｛"},{"}","｝"},{":","："},{";","；"},
            {"+","＋"},{"*","＊"},{"<","＜"},{">","＞"},{"_","＿"}
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, Delegate lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx")]
        private static extern IntPtr SetWindowsHookExMouse(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll")]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll")]
        private static extern bool ImmSetOpenStatus(IntPtr hIMC, bool fOpen);
    }
}

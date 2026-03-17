using System;
using SkiaSharp;
using System.IO;

namespace BlindTouchOled.Services
{
    public interface IRenderService
    {
        SKBitmap CreateBitmap(string text, string modeText, float fontSize, string fontFamily = "Yu Gothic", int cursorPosition = 0, bool isCursorVisible = false, byte brightness = 15);
        byte[] GetRawBytes(SKBitmap bitmap, byte brightness = 15);
        byte[] Get1BitRawBytes(SKBitmap bitmap, byte brightness = 15);
    }

    public class SkiaRenderService : IRenderService
    {
        private const int WIDTH = 256;
        private const int HEIGHT = 64;
        private float _scrollOffset = 0f;

        public SKBitmap CreateBitmap(string text, string modeText, float fontSize, string fontFamily = "Yu Gothic", int cursorPosition = 0, bool isCursorVisible = false, byte brightness = 15)
        {
            var bitmap = new SKBitmap(WIDTH, HEIGHT);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Black);
                
                // 基本フォント（ユーザー設定）とモード用フォント
                string effectiveFont = string.IsNullOrEmpty(fontFamily) ? "Yu Gothic" : fontFamily;
                string modeFont = "Yu Gothic";

                // 明るさを色に反映 (0-255 をそのまま使用)
                byte previewColor = brightness;
                var displayColor = new SKColor(previewColor, previewColor, previewColor);

                using (var modePaint = new SKPaint
                {
                    Color = displayColor,
                    TextSize = 20,
                    IsAntialias = false,
                    Typeface = SKTypeface.FromFamilyName(modeFont, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                })
                {
                    // モード表示は20px固定なので中央位置を固定
                    float yPosMode = 42; 
                    float xMode = WIDTH - 36; 
                    canvas.DrawText(modeText, xMode, yPosMode, modePaint);
                   
                    float availableWidthForMain = xMode - 10;
                    canvas.Save();
                    canvas.ClipRect(new SKRect(0, 0, availableWidthForMain, HEIGHT));

                    using (var mainPaint = new SKPaint
                    {
                        Color = displayColor,
                        TextSize = fontSize,
                        IsAntialias = true, 
                        Typeface = SKTypeface.FromFamilyName(effectiveFont, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                    })
                    {
                        // 垂直中央寄せ：単純にサイズに合わせた固定位置計算にする（ドリフト防止）
                        float yPosMain = (HEIGHT / 2) + (fontSize * 0.35f);
                       int safeCursorPos = Math.Clamp(cursorPosition, 0, text.Length);
                        string textBeforeCursor = text.Substring(0, safeCursorPos);
                        float cursorXOffset = mainPaint.MeasureText(textBeforeCursor);

                        float rightEdge = availableWidthForMain - 4;
                        float leftEdge = 8;
                        // 決定論的なスクロール計算（累積誤差によるドリフトを防止）
                        float viewWidth = rightEdge - leftEdge;
                        float curX = cursorXOffset;

                        // 現在の座標が枠外なら、その分だけオフセットを「上書き」する（相対加算しないことでドリフト防止）
                        if (curX + _scrollOffset > (viewWidth - 4))
                        {
                            _scrollOffset = (viewWidth - 4) - curX;
                        }
                        else if (curX + _scrollOffset < 0)
                        {
                            _scrollOffset = -curX;
                        }

                        // 文字が短い場合は左端に固定
                        float totalWidth = mainPaint.MeasureText(text);
                        if (totalWidth <= viewWidth)
                        {
                            _scrollOffset = 0;
                        }
                        else
                        {
                            // はみ出している場合の範囲制限
                            float minOffset = viewWidth - totalWidth - 4;
                            if (_scrollOffset < minOffset) _scrollOffset = minOffset;
                            if (_scrollOffset > 0) _scrollOffset = 0;
                        }

                        float xPosMain = leftEdge + (float)Math.Round(_scrollOffset);
                       canvas.DrawText(text, xPosMain, yPosMain, mainPaint);

                        if (isCursorVisible)
                        {
                            float cursorCurrentX = xPosMain + cursorXOffset;
                            // バックティック: ベースラインの上70%から下10%まで（文字高さに合わせた位置）
                            float cursorTopY = yPosMain - fontSize * 0.70f;
                            float cursorBottomY = yPosMain + fontSize * 0.10f;
                            using (var cursorPaint = new SKPaint { Color = displayColor, StrokeWidth = 1, IsAntialias = false })
                            {
                                canvas.DrawLine(cursorCurrentX, cursorTopY, cursorCurrentX, cursorBottomY, cursorPaint);
                            }
                        }
                    }
                    canvas.Restore();
                }
            }
            return bitmap;
        }

        public byte[] GetRawBytes(SKBitmap bitmap, byte brightness = 15)
        {
            byte[] output = new byte[WIDTH * HEIGHT / 2];
            byte bValue = (byte)(brightness > 15 ? 15 : brightness);
            for (int y = 0; y < HEIGHT; y++)
            {
                for (int x = 0; x < WIDTH; x += 2)
                {
                    byte p1 = (byte)(bitmap.GetPixel(x, y).Red > 128 ? bValue : 0);
                    byte p2 = (byte)(bitmap.GetPixel(x + 1, y).Red > 128 ? bValue : 0);
                    output[(y * WIDTH / 2) + (x / 2)] = (byte)((p1 << 4) | p2);
                }
            }
            return output;
        }

        public byte[] Get1BitRawBytes(SKBitmap bitmap, byte brightness = 15)
        {
            // [0] = Brightness (0-15)
            // [1..2048] = Pixel Data
            byte[] output = new byte[1 + (WIDTH * HEIGHT / 8)];
            output[0] = brightness;

            for (int y = 0; y < HEIGHT; y++)
            {
                for (int x = 0; x < WIDTH; x += 8)
                {
                    byte b = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        // プレビュー用に色が暗くなっていても、一定の赤みがあれば白として送信
                        if (bitmap.GetPixel(x + bit, y).Red > 10)
                            b |= (byte)(1 << (7 - bit));
                    }
                    output[1 + (y * WIDTH / 8) + (x / 8)] = b;
                }
            }
            return output;
        }
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using SkiaSharp;
using SkiaSharp.Views.WPF;

namespace BlindTouchOled.Views
{
    public partial class ImageCropWindow : Window
    {
        private readonly SKBitmap _source;
        public SKBitmap? ResultBitmap { get; private set; }

        public ImageCropWindow(SKBitmap source)
        {
            InitializeComponent();
            _source = source.Copy();
            RefreshPreview();
        }

        private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded)
            {
                return;
            }
            RefreshPreview();
        }

        private void RefreshPreview()
        {
            ResultBitmap?.Dispose();
            ResultBitmap = BuildCroppedBitmap();
            PreviewImage.Source = ResultBitmap.ToWriteableBitmap();
        }

        private SKBitmap BuildCroppedBitmap()
        {
            float zoom = (float)ZoomSlider.Value;
            float nx = (float)(OffsetXSlider.Value / 100.0);
            float ny = (float)(OffsetYSlider.Value / 100.0);

            var target = new SKBitmap(256, 64);
            using var canvas = new SKCanvas(target);
            canvas.Clear(SKColors.Black);

            // Base scale fits inside OLED frame without enlarging the original image.
            float fitScale = Math.Min(256f / _source.Width, 64f / _source.Height);
            fitScale = Math.Min(1f, fitScale);
            float scale = fitScale * Math.Max(0.01f, zoom);

            float drawW = _source.Width * scale;
            float drawH = _source.Height * scale;

            // Pan must work both when image is larger (crop) and smaller (move inside black area).
            float panX = Math.Abs(256f - drawW) * 0.5f;
            float panY = Math.Abs(64f - drawH) * 0.5f;

            float x = ((256f - drawW) * 0.5f) + (nx * panX);
            float y = ((64f - drawH) * 0.5f) + (ny * panY);

            using (var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None })
            {
                canvas.DrawBitmap(_source, new SKRect(x, y, x + drawW, y + drawH), paint);
            }

            // OLED output preview is strictly monochrome.
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

            return target;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            ResultBitmap?.Dispose();
            ResultBitmap = null;
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _source.Dispose();
        }
    }
}

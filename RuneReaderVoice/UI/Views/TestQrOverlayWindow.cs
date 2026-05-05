// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.

using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenCvSharp;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using Window = Avalonia.Controls.Window;

namespace RuneReaderVoice.UI.Views;

internal sealed class TestQrOverlayWindow : Window
{
    private readonly Image _image;
    private readonly TextBlock _label;
    private readonly QRCodeWriter _writer = new();

    public TestQrOverlayWindow()
    {
        Title = "RuneReader Voice Test QR";
        Width = 120;
        Height = 140;
        Topmost = true;
        CanResize = true;
        Background = Brushes.White;

        _image = new Image
        {
            Width = 120,
            Height = 120,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        _label = new TextBlock
        {
            Foreground = Brushes.Black,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };

        Content = new Border
        {
            Background = Brushes.White,
            Padding = new Avalonia.Thickness(18),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    _image,
                    _label,
                }
            }
        };
    }

    public void SetQrText(string encodedQrText, string label)
    {
        _image.Source = RenderQr(encodedQrText, 360);
        _label.Text = label;
    }

    private Bitmap RenderQr(string encodedQrText, int size)
    {
        var matrix = _writer.encode(encodedQrText, BarcodeFormat.QR_CODE, size, size, new EncodingOptions
        {
            Margin = 2,
            
            PureBarcode = true,
        }.Hints);

        using var mat = new Mat(size, size, MatType.CV_8UC1, Scalar.White);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (matrix[x, y])
                    mat.Set(y, x, (byte)0);
            }
        }

        Cv2.ImEncode(".png", mat, out var pngBytes);
        return new Bitmap(new MemoryStream(pngBytes));
    }
}

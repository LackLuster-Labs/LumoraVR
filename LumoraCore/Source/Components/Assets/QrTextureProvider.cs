// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Helpers;

namespace Lumora.Core.Assets;

// A QR code as a texture: black modules on white, with the four light modules round it that phone scanners need.
// The text is handed over by Show and kept in this machine's memory only, never in a synced member: it is a
// two-factor secret, and a synced field is a field anything reading the world can see. -xlinka
[ComponentCategory("Hidden")]
public sealed class QrTextureProvider : DynamicAssetProvider<TextureAsset>
{
    private const int QuietZone = 4;
    private const int TargetPixels = 512;

    private string _text = string.Empty;

    // Modules across, quiet zone included; 0 until something was shown.
    public int Modules { get; private set; }

    public QrTextureProvider()
    {
        LocalManualUpdate = true;
    }

    // Must run on the world's thread.
    public void Show(string text)
    {
        _text = text ?? string.Empty;
        RunManualUpdate();
    }

    protected override void OnAssetCreated(TextureAsset asset) { }

    protected override void UpdateAsset(TextureAsset asset)
    {
        var modules = _text.Length == 0 ? null : QrCode.Encode(_text, QrErrorLevel.M);
        int count = modules?.GetLength(0) ?? 0;
        int across = count + QuietZone * 2;
        int scale = System.Math.Max(2, TargetPixels / across);
        int size = across * scale;
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = 255;
        if (modules != null)
        {
            for (int row = 0; row < count; row++)
            {
                for (int column = 0; column < count; column++)
                {
                    if (!modules[row, column])
                        continue;
                    int top = (row + QuietZone) * scale;
                    int left = (column + QuietZone) * scale;
                    for (int y = top; y < top + scale; y++)
                    {
                        int at = (y * size + left) * 4;
                        for (int x = 0; x < scale; x++, at += 4)
                            pixels[at] = pixels[at + 1] = pixels[at + 2] = 0;
                    }
                }
            }
        }
        Modules = modules == null ? 0 : across;
        asset.SetImageData(pixels, size, size, mipmaps: true);
    }

    protected override void OnAssetCleared()
    {
        _text = string.Empty;
        Modules = 0;
    }
}

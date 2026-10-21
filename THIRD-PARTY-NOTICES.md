# Third-party notices

## Adapted source

`src/DynamicIsland/Services/Lyrics/LyricsMatcher.cs` adapts the lyric candidate scoring and query building of
[AF Media Bar](https://github.com/Fervent-Tempo/AF-Media-Bar) (`LyricsMetadataScore`, `LyricsSearch`,
`LyricsSearchQueryPolicy`). `Platform/ProcessTable.cs` follows the process snapshot approach of its
`SpectrumCaptureProcessResolver`.

```text
MIT License

Copyright (c) 2026 AmorFate

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Packages

| Package | Version | License | Source |
| --- | --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | https://github.com/CommunityToolkit/dotnet |
| F23.StringSimilarity | 7.0.1 | MIT | https://github.com/feature23/StringSimilarity.NET |
| Lyricify.Lyrics.Helper | 0.2.0 | Apache-2.0 | https://github.com/WXRIW/Lyricify-Lyrics-Helper |
| NAudio.Wasapi | 3.1.0 | MIT | https://github.com/naudio/NAudio |

## Online services

Lyrics are looked up by track title, artist, album and duration on QQ Music, NetEase Cloud Music (both through
unofficial endpoints wrapped by Lyricify) and [LRCLIB](https://lrclib.net). Each source can be turned off in Settings.

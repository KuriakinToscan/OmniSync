# Componentes de terceiros

O OmniSync é distribuído sob a [licença MIT](LICENSE). O executável único (`OmniSync.exe` e `OmniSync` para Linux) embute os componentes abaixo, cada um sob a sua própria licença. Esta lista vale para a versão 2.0.2 e foi tirada de `dotnet list package --include-transitive`.

| Componente | Versão | Licença | Copyright |
| --- | --- | --- | --- |
| [.NET Runtime](https://github.com/dotnet/runtime) | 10.0 | MIT ([avisos de terceiros](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT)) | .NET Foundation and Contributors |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (Avalonia, Desktop, Win32, X11, FreeDesktop, Native, Skia, HarfBuzz, Themes.Fluent, Remote.Protocol) | 12.1.3 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| [Avalonia.Controls.DataGrid](https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid) | 12.1.2 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| [Avalonia.Fonts.Inter](https://github.com/AvaloniaUI/Avalonia) | 12.1.3 | MIT (pacote); a fonte [Inter](https://github.com/rsms/inter) é SIL Open Font License 1.1 | Pacote: The AvaloniaUI Project; fonte: The Inter Project Authors |
| [Avalonia.Angle.Windows.Natives](https://github.com/AvaloniaUI/angle) (só Windows) | 2.1.27548 | BSD-3-Clause ([texto](licenses/ANGLE-LICENSE.txt)) | Copyright 2018 The ANGLE Project Authors |
| [SkiaSharp](https://github.com/mono/SkiaSharp) e bibliotecas nativas | 3.119.4 | MIT ([avisos de terceiros](licenses/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt)) | Copyright (c) 2015-2016 Xamarin, Inc.; Copyright (c) 2017-2018 Microsoft Corporation |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) e bibliotecas nativas | 8.3.1.3 | MIT ([avisos de terceiros](licenses/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt)) | Copyright (c) 2015-2016 Xamarin, Inc.; Copyright (c) 2017-2018 Microsoft Corporation |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | 2026.0.0 | MIT | Copyright © Renci 2010-2026 |
| [BouncyCastle.Cryptography](https://github.com/bcgit/bc-csharp) | 2.7.0 | MIT | Copyright © Legion of the Bouncy Castle Inc. 2000-2026 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | (c) .NET Foundation and Contributors |
| [MicroCom.Runtime](https://github.com/kekekeks/MicroCom) | 0.11.6 | MIT | Copyright 2021 © Nikita Tsukanov |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) (só Linux) | 0.94.1 | MIT | Tom Deseyn |
| [Microsoft.Extensions.Logging.Abstractions e DependencyInjection.Abstractions](https://github.com/dotnet/runtime) | 8.0 | MIT | © Microsoft Corporation |
| [Microsoft.IO.RecyclableMemoryStream](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream) | 3.0.1 | MIT | © Microsoft Corporation |

Os pacotes `AvaloniaUI.DiagnosticsSupport` (só em Debug) e `Avalonia.BuildServices` (só na compilação) não entram no executável publicado.

## Texto da licença MIT

Vale para todos os componentes marcados como MIT acima, cada um com o seu próprio aviso de copyright.

```
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

## SIL Open Font License 1.1 (fonte Inter)

Texto completo em <https://openfontlicense.org/open-font-license-official-text/> e no [repositório da Inter](https://github.com/rsms/inter/blob/master/LICENSE.txt).

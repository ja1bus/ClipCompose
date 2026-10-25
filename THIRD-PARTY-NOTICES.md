# Third-party notices / 第三者ソフトウェアについて

The MIT License in [LICENSE](LICENSE) applies only to the source code and files in this repository authored by ja1bus. It does not change the license of the third-party software listed below.

[LICENSE](LICENSE) の MIT License は、このリポジトリに含まれる ja1bus が作成したソースコードとファイルだけに適用されます。以下の第三者のソフトウェアのライセンスは変更しません。

## Runtime / 実行時

ClipCompose uses only .NET 10 and its Windows Desktop shared framework (WPF, Windows Forms, System.Text.Json). They are provided by Microsoft under the MIT License and are not included in this repository. The Microsoft Store package includes the .NET runtime. When you build and run the app from this repository, it uses the .NET 10 Desktop Runtime installed on the PC.

ClipCompose が使うのは .NET 10 と Windows Desktop 共有フレームワーク（WPF・Windows Forms・System.Text.Json）だけです。これらは Microsoft が MIT License で提供しているもので、このリポジトリには含まれていません。Microsoft Store のパッケージには .NET のランタイムを含めています。このリポジトリからビルドして実行する場合は、PC にインストールされている .NET 10 Desktop Runtime を使います。

- .NET: https://github.com/dotnet/runtime (MIT)
- WPF: https://github.com/dotnet/wpf (MIT)
- Windows Forms: https://github.com/dotnet/winforms (MIT)

The .NET runtime itself includes third-party components; their notices are in https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT.

.NET のランタイム自体にも第三者のコンポーネントが含まれています。その表記は https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT にあります。

License of .NET, WPF and Windows Forms / .NET・WPF・Windows Forms のライセンス:

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

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

Fonts are not bundled. The app uses fonts installed in Windows (Yu Gothic UI, Segoe UI, Segoe Fluent Icons / Segoe MDL2 Assets, Consolas).

フォントは同梱していません。Windows にインストールされているフォント（Yu Gothic UI・Segoe UI・Segoe Fluent Icons / Segoe MDL2 Assets・Consolas）を使います。

## Tests only / テストのみ

The following NuGet packages are referenced by `Tests/ClipCompose.Tests.csproj`. They are restored from nuget.org at build time, are not included in this repository, and are not part of the app.

次の NuGet パッケージは `Tests/ClipCompose.Tests.csproj` から参照しています。ビルド時に nuget.org から取得するもので、このリポジトリにもアプリにも含まれていません。

| Package | Version | License |
|---|---|---|
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |
| xunit | 2.9.2 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |

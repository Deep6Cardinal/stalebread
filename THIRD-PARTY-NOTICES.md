# Third-party notices

StaleBread bundles Microsoft .NET runtime components and uses System.Management. These are distributed under their upstream MIT license/third-party notices:

- https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
- https://github.com/dotnet/wpf/blob/main/LICENSE.TXT
- https://github.com/dotnet/winforms/blob/main/LICENSE.TXT

The portable package includes copied upstream license/notice text in `Licenses/`.

Program code and original project artwork are covered by the root `LICENSE` (MIT). This does not replace licenses for third-party components or the recorded sound.

Recorded audio attribution and CC BY 4.0 terms are in [source asset credits](src/StaleBread.App/Assets/LICENSES.md). In a compiled portable package, the same credits are at `Assets/LICENSES.md`, and the full CC BY 4.0 text is at `Licenses/CC-BY-4.0.txt`. The sound license is separate from the program; no endorsement is implied. Keep the attribution and license texts with redistributed packages.

xUnit and Microsoft.NET.Test.Sdk are development-only and are not included in the consumer package. FFmpeg was used only to derive the WAV asset and is not distributed or required at runtime.

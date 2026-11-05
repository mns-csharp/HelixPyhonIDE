# Third-party notices

HelixPython is distributed under the MIT License in [LICENSE](LICENSE). The components below keep
their own licenses; the HelixPython license does not replace them.

## Runtime dependencies

| Component | License | License and source |
| --- | --- | --- |
| Anthropic .NET SDK 12.40.0 | MIT | [License](https://github.com/anthropics/anthropic-sdk-csharp/blob/main/LICENSE) |
| LibGit2Sharp 0.32.0 | MIT | [License](https://github.com/libgit2/libgit2sharp/blob/master/LICENSE.md) |
| LibGit2 native binaries 2.0.324 | GNU GPL version 2 with a linking exception | [License and exception](https://github.com/libgit2/libgit2/blob/main/COPYING) |
| Newtonsoft.Json 13.0.3 | MIT | [License](https://github.com/JamesNK/Newtonsoft.Json/blob/master/LICENSE.md) |
| PDFsharp 1.50.5147 | MIT | [License](https://www.pdfsharp.net/pdfsharp-1.5/Licensing.ashx) |

These packages are restored from NuGet by the project files. Their transitive dependencies carry
their own license metadata and notices in the NuGet packages. The Git integration package brings
the LibGit2 native binaries at runtime; the linking exception is part of the LibGit2 license.

## Build and test dependencies

The test projects use Microsoft.NET.Test.Sdk 17.8.0, MSTest.TestAdapter 3.1.1, MSTest.TestFramework
3.1.1, and their transitive Microsoft test-platform packages. These are distributed under the MIT
License. They are development dependencies and are not required by the IDE at runtime.

## GUI Designer icons

The built-in WinForms GUI Designer includes 16 × 16 control icons from SharpDevelop's designer UI.
It does not include or depend on the SharpDevelop WPF Designer library.

- `assets/product/toolbox/*.png`: control icons by Bartosz Korczyński, MIT License.
- The pointer fallback icon is by AlphaSierraPapa, MIT License.
- The categorized, alphabetical, properties, and events icons are Fugue Icons by Yusuke
  Kamiyamane, licensed under [Creative Commons Attribution 3.0](https://creativecommons.org/licenses/by/3.0/).

The source code and artwork in this repository are provided under the MIT License unless a
component-specific notice above states otherwise.

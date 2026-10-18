# Third-party components

The project's own code is MIT licensed. This does not relicense dependencies.

## OneScript runtime

OneScript 1.9.4, based on commit `04d903852c31337391ef47aee821aa8769725170`
of [EvilBeaver/OneScript](https://github.com/EvilBeaver/OneScript), is distributed
as the private `1.9.4-zbs.1` net472 build under MPL-2.0. The modified source,
license, security patch and build instructions are in `vendor/onescript` and
in the portable package's `licenses/onescript` directory.

The obsolete DotNetZip dependency was replaced by ProDotNetZip 1.20.0.
Its combined license notice is preserved as `ProDotNetZip-LICENSE.txt`.
The global OneScript installation is not part of the build input. No external
`logos`, `json`, `tempfiles`, `asserts`, opm or OVM script libraries are bundled.

## .NET and capture dependencies

`vendor/licenses/index.json` identifies resolved runtime NuGet packages, their
metadata and the license/notice texts collected from the restored packages.
These files are copied to `licenses/nuget` in the portable application.
Silk.NET and Ultz notices are also in `lib/net472/THIRD-PARTY-CAPTURE.txt`.
WebView2 SDK binaries retain Microsoft's SDK license and notices; the separately
installed Evergreen Runtime is governed by its own terms.

The software uses Windows and .NET Framework APIs supplied by the operating
system. Reference assemblies and Windows SDK build contracts are build-time
dependencies, not bundled replacements for Windows or .NET Framework.

## Build and test tools

Development dependencies are pinned in `package-lock.json`, `requirements-dev.txt`,
`global.json` and the project files. npm/Python test tools are not included in
the portable application. GitHub Actions are pinned by commit in the public
workflow. The VS Code adapter's MIT license is in `integrations/vscode/LICENSE.txt`.

The application's image-generation providers and Codex CLI are not sublicensed
by this project. Users install/authenticate them separately and follow their terms.

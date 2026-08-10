# Third-Party Notices

RavensPort itself is licensed under the MIT License (see [LICENSE](LICENSE)).

It depends on the third-party components listed below. The `win-x64` self-contained
single-file build **bundles these components (and the .NET runtime) into the produced
`RavensPort.exe`**, so any redistribution of that binary is a redistribution of these
components and must carry these notices.

Everything **bundled** in that binary is either permissive open source (MIT, Apache-2.0 or
BSD-3-Clause) or redistributable under Microsoft's own terms, and none of it imposes
source-disclosure obligations on RavensPort. RavensPort's own source therefore remains under
the MIT License.

One component is **not** bundled and **is** copyleft — the Proton Pass CLI, which you install
yourself and RavensPort merely runs. See [Optional external
tools](#optional-external-tools-not-bundled) below.

## Runtime dependencies (bundled in the published executable)

### .NET runtime and managed libraries

| Component | Version | License |
|---|---|---|
| .NET Runtime / ASP.NET Core / WPF (`Microsoft.NETCore.App`, `Microsoft.AspNetCore.App`, `Microsoft.WindowsDesktop.App`) | 8.0.x | MIT |
| Yarp.ReverseProxy | 2.3.0 | MIT |
| ModelContextProtocol | 2.0.0 | Apache-2.0 |
| ModelContextProtocol.Core | 2.0.0 | Apache-2.0 |
| ModelContextProtocol.AspNetCore | 2.0.0 | Apache-2.0 |
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | MIT |
| Microsoft.Extensions.Caching.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.Options | 10.0.10 | MIT |
| Microsoft.Extensions.Primitives | 10.0.10 | MIT |
| System.Diagnostics.DiagnosticSource | 10.0.10 | MIT |
| System.IO.Hashing | 8.0.0 | MIT |
| System.IO.Pipelines | 10.0.10 | MIT |
| System.Management | 7.0.2 | MIT |
| System.Net.ServerSentEvents | 10.0.10 | MIT |
| System.Text.Encodings.Web | 10.0.10 | MIT |
| System.Text.Json | 10.0.10 | MIT |
| Google.Apis.Auth | 1.75.0 | Apache-2.0 |
| Google.Apis | 1.75.0 | Apache-2.0 |
| Google.Apis.Core | 1.75.0 | Apache-2.0 |
| IdentityModel.OidcClient | 6.0.0 | Apache-2.0 |
| IdentityModel | 7.0.0 | Apache-2.0 |
| Newtonsoft.Json | 13.0.4 | MIT |
| CommunityToolkit.Mvvm | 8.4.0 | MIT |

The `Microsoft.Extensions.*`, `System.Text.*` and `System.Diagnostics.DiagnosticSource`
packages resolve **above** the 8.0 shared framework, so they ship as real assemblies beside
`RavensPort.dll` rather than being satisfied by the runtime. That is why they are listed
separately from the framework row above.

### Windows SDK projection

| Component | Version | License |
|---|---|---|
| Microsoft.Windows.SDK.NET.Ref (`Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll`) | 10.0.19041.x | Windows SDK licence terms — <https://aka.ms/WinSDKLicenseURL> |

Listed apart from the table above because it is the one bundled component that is **not**
open source. It is the C#/WinRT projection that `TargetFramework` pulls in — see
`Directory.Build.props`, where the `10.0.19041.0` platform version exists to make
`Windows.Security.Credentials.KeyCredentialManager` reachable. Microsoft's terms permit
redistributing it as part of a Windows application, which is what RavensPort does; it carries
no copyleft and no source-disclosure obligation. The version tracks the .NET SDK's targeting
pack rather than a pinned `PackageReference`, so it is not recorded in `packages.lock.json`.

### Go components (statically linked into `onepassword.dll`)

`onepassword.dll` is built with `go build -buildmode=c-shared`, which links the Go runtime and
every Go dependency **into that DLL statically**. The DLL is then embedded in `RavensPort.exe`,
so all of the following are redistributed with the binary even though none appears in a
`packages.lock.json`. Versions come from `src/OnePasswordNative/go.mod`.

| Component | Version | License |
|---|---|---|
| Go runtime and standard library | 1.26.5 | BSD-3-Clause |
| github.com/1password/onepassword-sdk-go | v0.4.1 | MIT |
| github.com/extism/go-sdk | v1.7.1 | BSD-3-Clause |
| github.com/tetratelabs/wazero | v1.11.0 | Apache-2.0 |
| github.com/tetratelabs/wabin | v0.0.0-20230304001439-f6f874872834 | Apache-2.0 |
| github.com/dylibso/observe-sdk/go | v0.0.0-20240828172851-9145d8ad07e1 | Apache-2.0 |
| github.com/gobwas/glob | v0.2.3 | MIT |
| github.com/ianlancetaylor/demangle | v0.0.0-20251118225945-96ee0021ea0f | BSD-3-Clause |
| go.opentelemetry.io/proto/otlp | v1.9.0 | Apache-2.0 |
| golang.org/x/sys | v0.44.0 | BSD-3-Clause |
| google.golang.org/protobuf | v1.36.11 | BSD-3-Clause |

## Required NOTICE passthrough

Apache-2.0 section 4(d) requires that a component's own `NOTICE` file be carried into
redistributions. Of every Apache-2.0 component above, exactly one ships a `NOTICE`, and its
contents are reproduced here in full:

**github.com/tetratelabs/wazero**

```
wazero
Copyright 2020-2023 wazero authors
```

## Build/test-only dependencies (not shipped in the executable)

| Component | Version | License |
|---|---|---|
| xunit (and xunit.core / xunit.assert / xunit.abstractions / xunit.analyzers / xunit.runner.visualstudio) | 2.5.3 | Apache-2.0 |
| Microsoft.NET.Test.Sdk (and Microsoft.CodeCoverage / Microsoft.TestPlatform.*) | 17.8.0 | MIT |
| Microsoft.AspNetCore.TestHost | 8.0.11 | MIT |
| coverlet.collector | 6.0.0 | MIT |
| Moq | 4.20.72 | BSD-3-Clause |
| Castle.Core | 5.1.1 | Apache-2.0 |
| NuGet.Frameworks | 6.5.0 | Apache-2.0 |
| github.com/stretchr/testify | v1.11.1 | MIT |
| github.com/davecgh/go-spew | v1.1.1 | ISC |
| github.com/pmezard/go-difflib | v1.0.0 | BSD-3-Clause |
| github.com/google/go-cmp | v0.7.0 | BSD-3-Clause |
| gopkg.in/yaml.v3 | v3.0.1 | MIT and Apache-2.0 |

The Go entries are test-only dependencies of the Go module: they appear in
`src/OnePasswordNative/go.sum` but not in the `require` block of `go.mod`, so `go build
-buildmode=c-shared` does not link them into `onepassword.dll`.

## Optional external tools (not bundled)

RavensPort stores its configuration in a password manager. For Proton Pass, it reaches it by running the
`pass-cli` command-line tool as a separate child process. This tool is **not** part of
`RavensPort.exe` and is **not** redistributed with it.

| Component | License | How it is obtained |
|---|---|---|
| Proton Pass CLI (`pass-cli`) | **GPL-3.0-or-later** | Installed by you, with `winget install Proton.PassCLI` or from Proton |

### Proton Pass CLI and the GPL

RavensPort neither downloads nor redistributes pass-cli. Versions up to 4.3.0 could fetch a
pinned release on request; that was removed in 4.4.0, and the app now installs no software
at all. It locates whichever `pass-cli` you installed and runs it.

Corresponding source for whatever version you have is at
<https://github.com/protonpass/pass-cli>. RavensPort does **not** modify pass-cli, link
against it, or incorporate any part of it — it is executed as an independent program over
a process boundary, which is aggregation rather than a combined work. RavensPort itself
therefore remains under the MIT License.

## Trademarks

RavensPort bundles the 1Password and Proton Pass marks as image resources
(`src/RavensPort.App/Assets/onepassword-logo.png`, `proton-pass-logo.png`) and shows them on the
screens that connect to, or ask for a credential for, that manager.

Those marks belong to AgileBits Inc. and Proton AG respectively. They are used nominatively — to
identify which password manager a screen is talking about — and no endorsement, affiliation, or
sponsorship is claimed or implied. Neither company is a party to this project. If you redistribute a
modified RavensPort, the marks stay theirs and their brand guidelines, not the MIT License, govern
what you may do with them.

## What the licenses require of you

**MIT** — include the copyright notice and permission notice when redistributing.

**BSD-3-Clause** — when redistributing in binary form, reproduce the copyright notice, the
list of conditions and the disclaimer in the documentation or other materials provided with
the distribution; this file is that material, and both the installer and the MSIX package
place it beside `RavensPort.exe`. BSD-3-Clause adds a third condition MIT does not have: the
names of the copyright holders and contributors — here the Go Authors, Extism and the Moq
authors — may **not** be used to endorse or promote RavensPort without their prior written
permission.

**ISC** — functionally equivalent to MIT; retain the copyright and permission notice.

**Apache-2.0** — when redistributing, include a copy of the Apache-2.0 license, retain
existing copyright/patent/attribution notices, and state significant changes if you
modified the component. Apache-2.0 also grants an explicit patent licence, which MIT
does not. If an upstream component ships a `NOTICE` file, its contents must be passed
along — see [Required NOTICE passthrough](#required-notice-passthrough) above, which
reproduces the only one that exists in this dependency set. RavensPort does not modify any
of these components.

**Windows SDK licence terms** — Microsoft's own terms, not an open-source licence. They allow
the WinRT projection assemblies to be distributed as part of a Windows application. They
impose no source-disclosure obligation and do not affect the licence of RavensPort's own code.

Full licence texts:

- MIT: https://opensource.org/licenses/MIT
- Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0
- BSD-3-Clause: https://opensource.org/licenses/BSD-3-Clause
- ISC: https://opensource.org/licenses/ISC
- Windows SDK: https://aka.ms/WinSDKLicenseURL

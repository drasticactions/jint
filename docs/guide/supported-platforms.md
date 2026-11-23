# Supported Platforms

## Jint

The core package targets .NET 10. An application on .NET Framework, a .NET Standard runtime or .NET 8 stays on
the Jint 4.x line.

## Optional packages

`Jint.HtmlParser`, `Jint.DevTools`, `Jint.Browser`, and `Jint.Browser.Mcp` target
.NET 10.

`Jint.Browser.Tool` is distributed as a native executable for Linux (glibc), macOS and Windows, on x64
and arm64. Installing its NuGet package requires the .NET 10 SDK; standalone release downloads require
no .NET installation. See [tool installation](../packages/jint-browser-tool/installation.md).

## Native AOT

The core `Jint` package and `Jint.DevTools` support Native AOT, with documented
limitations for reflection-based CLR interop. The experimental `Jint.HtmlParser` library is also marked
AOT-compatible and verified through a packed native consumer. The browser libraries do not declare
general trimming or AOT compatibility. The closed `jint-browser` tool is published
and exercised as Native AOT separately.

See [Native AOT and trimming](../reference/native-aot.md).

# Third-party notices

Reverse X is not itself MIT-licensed (see `LICENSE`). It uses the components below under their own licenses.

## REA (`rea-agents` 6.4.0)

- Project: https://github.com/morluto/rea · npm: https://www.npmjs.com/package/rea-agents
- License: MIT
- How it is used: Reverse X launches the unmodified npm package (`npx rea-agents@6.4.0`) as an external process
  (MCP server over stdio, and `rea <command> --json`). REA's code is not copied into or modified by this repository.
  REA bundles further third-party components under their own licenses (see its `licenses/` and `third_party/` folders).

```
MIT License

Copyright (c) 2026 morluto

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

## Test-only NuGet packages

xUnit, Microsoft.NET.Test.Sdk (Apache-2.0 / MIT) — used by `tests/` only, not shipped.

## Fonts

None bundled yet. Fonts added in later phases will be listed here with their OFL license files.

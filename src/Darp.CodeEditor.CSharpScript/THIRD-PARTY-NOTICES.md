# Third-party notices

Darp.CodeEditor.CSharpScript ships files from other projects. Their licenses follow.

## Visual Studio Code

`Languages/csharp.tmLanguage.json` and `Languages/csharp.language-configuration.json` are copied unchanged from Visual
Studio Code, release tag `1.145.0` (https://github.com/microsoft/vscode/tree/1.145.0/extensions/csharp): the extension
`csharp`'s `syntaxes/csharp.tmLanguage.json` and `language-configuration.json`. Only the file names changed.

```
MIT License

Copyright (c) 2015 - present Microsoft Corporation

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

## dotnet/csharp-tmLanguage

Visual Studio Code converted `Languages/csharp.tmLanguage.json` from https://github.com/dotnet/csharp-tmLanguage, at
commit `c2719309e91d2e26be21fce0ecaaca86c7b48ce4`, as the file's `version` says.

```
MIT License

Copyright (c) 2016 .NET Foundation

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

## Libraries in `tools/csharp-server`

The language server bundles these libraries, each under the MIT license below. The notices a library carries for code
it took from others are in this package under `licenses/<library>/`.

- Humanizer.Core: Copyright (c) .NET Foundation and Contributors
- Microsoft.CodeAnalysis.Common, .CSharp, .CSharp.Features, .CSharp.Workspaces, .Elfie, .Features, .Scripting.Common,
  .Workspaces.Common: Copyright (c) Microsoft Corporation
- Microsoft.DiaSymReader: Copyright (c) Microsoft Corporation
- System.Composition.AttributedModel, .Convention, .Hosting, .Runtime, .TypedParts: Copyright (c) Microsoft Corporation
- System.Configuration.ConfigurationManager: Copyright (c) Microsoft Corporation
- System.Diagnostics.EventLog: Copyright (c) Microsoft Corporation
- System.Security.Cryptography.ProtectedData: Copyright (c) Microsoft Corporation

```
MIT License

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

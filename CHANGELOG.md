# Changelog

## 0.1.0 (2026-11-03)


### ⚠ BREAKING CHANGES

* get language features from language servers over LSP
* draw completion, signature help and hover inside the window

### Features

* add a JSON-RPC and LSP layer for language servers and the editor client ([1609e58](https://github.com/rosslight/Darp.CodeEditor/commit/1609e58ffd5d1149d6156ab2b0a30b1aed7bf755))
* add a JSON-RPC and LSP layer for language servers and the editor client ([509056a](https://github.com/rosslight/Darp.CodeEditor/commit/509056a5817e4bfc8c920ee8575d748a3b75b97b))
* add auto-indent, bracket matching, auto-closing pairs and toggle comment ([b700de0](https://github.com/rosslight/Darp.CodeEditor/commit/b700de0adf63571587fe8edffaeaf144fc15d13c))
* add editor prototype ([01cef73](https://github.com/rosslight/Darp.CodeEditor/commit/01cef738b45bdeeb5fde78fee114a46560dde69e))
* add the C# script language server ([4b54234](https://github.com/rosslight/Darp.CodeEditor/commit/4b5423416c97fb9e51b8e3e7c6e90816ecfcd10e))
* add the C# script language server ([8a83b80](https://github.com/rosslight/Darp.CodeEditor/commit/8a83b8079025c182a8d4532d9974c98c8350a885))
* add the client lifecycle and restarts with backoff ([861b9b6](https://github.com/rosslight/Darp.CodeEditor/commit/861b9b64577571699652573918a7c3dc1fbc6571))
* add the end-to-end input latency probe ([3a7c12f](https://github.com/rosslight/Darp.CodeEditor/commit/3a7c12f4afb4ad4f8d187f61a39bdf987e5d6e2b))
* benchmark overlapping requests in two documents ([e3c4110](https://github.com/rosslight/Darp.CodeEditor/commit/e3c41100fe619b059c01928ad6ab860c4fff76be))
* benchmark the C# script server against the in-process service ([1ca8828](https://github.com/rosslight/Darp.CodeEditor/commit/1ca8828ee236d1617f16985715003bfd85a99765))
* benchmark the script runner ([c97b8e5](https://github.com/rosslight/Darp.CodeEditor/commit/c97b8e52728edffaebc375ea5e7d8a4259d4b0f8))
* color the visible lines in the frame that shows them ([5382201](https://github.com/rosslight/Darp.CodeEditor/commit/53822012a34d47aa4e217a07cf6a035761dcef70))
* **csharp:** ship the helpers in Darp.CodeEditor.CSharp, ReadyToRun on one bundled runtime ([6a605ab](https://github.com/rosslight/Darp.CodeEditor/commit/6a605aba79e224520e0f0d2d913137e685c3421f))
* **demo:** edit Luau through luau-lsp with the demo's Luau API ([66bc2b0](https://github.com/rosslight/Darp.CodeEditor/commit/66bc2b0de0d958455e8f8e788fe15cda6772cd20))
* describe the C# script environment once for hosts and the language server ([697904c](https://github.com/rosslight/Darp.CodeEditor/commit/697904ca6f1233d7dfe84163aad9aed392a63770))
* draw completion, signature help and hover inside the window ([fc08476](https://github.com/rosslight/Darp.CodeEditor/commit/fc084762b4f3ec369a27b5a555f87956695454eb))
* end a language server without shutdown ([209fcd8](https://github.com/rosslight/Darp.CodeEditor/commit/209fcd821d043abf284f3baebee14ed63c40c2e9))
* fail the build when a script reference needs another shared framework ([f34f1d2](https://github.com/rosslight/Darp.CodeEditor/commit/f34f1d2c39e54dcf5cbeadc20b8e2b84f22f9e62))
* follow the app's light and dark variant with VS Code themes ([2e81b8a](https://github.com/rosslight/Darp.CodeEditor/commit/2e81b8a46215f42cea902014a97a6f219e6da5b4))
* get language features from language servers over LSP ([b5efa5e](https://github.com/rosslight/Darp.CodeEditor/commit/b5efa5ec6f9814f0935b48372cd08727009f0504))
* **luau:** add Darp.CodeEditor.Luau for Luau editing through luau-lsp ([1bc937b](https://github.com/rosslight/Darp.CodeEditor/commit/1bc937b0ceea6fab4d622958552ac98f986ace05))
* **luau:** carry luau-lsp in Darp.CodeEditor.Luau for every platform ([ed12206](https://github.com/rosslight/Darp.CodeEditor/commit/ed1220691ccbde3a8c8ae6a8a6cc420d08d0b5be))
* **luau:** Luau editing through luau-lsp with a host-defined API ([132fc16](https://github.com/rosslight/Darp.CodeEditor/commit/132fc160b12b73433efdea119ab17f6eafd47074))
* model the requests a server sends to its client ([759a422](https://github.com/rosslight/Darp.CodeEditor/commit/759a422401d29c1ac82fa11cb109e3fabe4652a5))
* **packaging:** ship the C# helpers and luau-lsp in their packages ([27eabcd](https://github.com/rosslight/Darp.CodeEditor/commit/27eabcd81183dae2338538d167d2192d47570153))
* read hover contents that a server sends as a plain string ([4f9cb94](https://github.com/rosslight/Darp.CodeEditor/commit/4f9cb94a042088a153474e6c50bd63bf6b6aaaba))
* request semantic tokens of a whole document ([d56400a](https://github.com/rosslight/Darp.CodeEditor/commit/d56400a0c0a4bb64c6e3ce81b5c9b9b3f5f25b28))
* restart a language server that stopped answering or reading ([0cc322e](https://github.com/rosslight/Darp.CodeEditor/commit/0cc322eb2e01d0a8ac22ece644b184e005f76f71))
* run C# scripts in a helper process ([50845dd](https://github.com/rosslight/Darp.CodeEditor/commit/50845dd1b7ed210a7b1c667f9bdda381a3064edf))
* run C# scripts in a helper process ([7e86b21](https://github.com/rosslight/Darp.CodeEditor/commit/7e86b217a631488ababa6c793171189a09e107f1))
* run the packet script from both demos ([c85ff81](https://github.com/rosslight/Darp.CodeEditor/commit/c85ff815fb5ab0274ca3f3dcb8beb867fbff22a1))
* serve the C# script server on Darp.CodeEditor.Lsp ([51115db](https://github.com/rosslight/Darp.CodeEditor/commit/51115dbe1c5e76aacd6b2f4b9359a2e9d2c301fd))
* serve the C# script server on Darp.CodeEditor.Lsp ([8d30262](https://github.com/rosslight/Darp.CodeEditor/commit/8d30262bfded188881c0f030b19dd0be7d092557))
* start helper processes that end with the app ([8b04b74](https://github.com/rosslight/Darp.CodeEditor/commit/8b04b7497d8add6fbf0e8d34e46a738243dd453a))


### Bug Fixes

* answer requests for their snapshot instead of ContentModified ([974c23e](https://github.com/rosslight/Darp.CodeEditor/commit/974c23eccdca8675d523ab6973048583d5b4f0e0))
* ask for completion of a typed word as invoked ([28b916f](https://github.com/rosslight/Darp.CodeEditor/commit/28b916fe6614edead15de022203fdaae504e57db))
* bound handing a script to the runner by the compile timeout ([948cd08](https://github.com/rosslight/Darp.CodeEditor/commit/948cd0845328bcfa5c74083d30a680faae6b5d2d))
* bound the runner output the app keeps ([87f64f9](https://github.com/rosslight/Darp.CodeEditor/commit/87f64f9ae5134aaeb879fe9011f78adb393b5126))
* cancel signature help on edits and ask again with the current text ([d1ca2b6](https://github.com/rosslight/Darp.CodeEditor/commit/d1ca2b6267eeeec37117c5e40085ebff43df38ae))
* cancel whole-document token requests that a change made obsolete ([589b396](https://github.com/rosslight/Darp.CodeEditor/commit/589b396e2c8dedc6a1f953f6d5762dc01c44ad25))
* check that the runner loads the app's build of the script API ([ed8e886](https://github.com/rosslight/Darp.CodeEditor/commit/ed8e886b255346ec6be3c7cd7c5ed707f5c669d1))
* close quotes by the context where they are typed ([a86a711](https://github.com/rosslight/Darp.CodeEditor/commit/a86a711c80144af6a8cbb33a9b02abb34b23bbdd))
* close quotes only where the context is known ([31c1bb0](https://github.com/rosslight/Darp.CodeEditor/commit/31c1bb01896165dcb4f8a8ad0072ed2e1ae240a8))
* commit a completion item over its range, with late edits in the same undo step ([c8defe1](https://github.com/rosslight/Darp.CodeEditor/commit/c8defe1900f4fa7e9b7e77b874903f1e469a7b26))
* **completion:** join a late resolve only to the commit's own undo step ([2b13dc6](https://github.com/rosslight/Darp.CodeEditor/commit/2b13dc6c07be9435fda8f11227e72d3f0536e5ff))
* **completion:** restart the signature help pause on every edit ([d2ec865](https://github.com/rosslight/Darp.CodeEditor/commit/d2ec8651835fa3b6615104a84750cd4428edbb58))
* drop completion for closed documents, ignore stale versions, exit with the client ([0d5c74e](https://github.com/rosslight/Darp.CodeEditor/commit/0d5c74ea2c2312d83b161ee30d8c5a7279e2954b))
* **editor:** keep the tokens before a typed character as its context ([0b73247](https://github.com/rosslight/Darp.CodeEditor/commit/0b73247728fe34fbb8891416cbdb1c7650d4443f))
* **editor:** never pair a quote from cut-short or outdated tokens ([00d047f](https://github.com/rosslight/Darp.CodeEditor/commit/00d047f78ef9c30db30456c365a049ebdfeb3f60))
* end the connection when a write fails ([8aad61b](https://github.com/rosslight/Darp.CodeEditor/commit/8aad61b315fd834aba17335b7701b9690c5c0d33))
* filter completion by the text before the caret and allow empty filtered lists ([4999b6e](https://github.com/rosslight/Darp.CodeEditor/commit/4999b6ee7ce46b7c45bce8a0f18ea06be1a93afc))
* keep compiling and releasing out of the call deadline ([c6b47e4](https://github.com/rosslight/Darp.CodeEditor/commit/c6b47e43d53bffb5b2e2447d026fa06c5044f1e5))
* **lsp:** count initialize timeouts toward the hang escalation ([f2f89c7](https://github.com/rosslight/Darp.CodeEditor/commit/f2f89c75499cd4f2c6f2f28a3ac77da860a7a5fe))
* **lsp:** give cold and repeatedly hung servers more time ([d06189e](https://github.com/rosslight/Darp.CodeEditor/commit/d06189e006b9b6446db9b70406e01e8f6974e4e2))
* **lsp:** keep the wait for a hung server out of its healthy time ([5e75104](https://github.com/rosslight/Darp.CodeEditor/commit/5e751044b8a7d822c145980323da457a5c3464e6))
* **lsp:** say Restarting as soon as a server ends or hangs ([36cf568](https://github.com/rosslight/Darp.CodeEditor/commit/36cf5684460bac0d3961c412ae24393759c4086f))
* **lsp:** write timeouts in log messages in the invariant culture ([5aea715](https://github.com/rosslight/Darp.CodeEditor/commit/5aea71564bf7f2294d4736852e5a1ea6cbc49382))
* **luau:** keep backslashes in Unix file names in document URIs ([f513f47](https://github.com/rosslight/Darp.CodeEditor/commit/f513f471efd65aa89a463b417e4299f2b3a9581c))
* **luau:** make luau-lsp executable without a shell ([dae7395](https://github.com/rosslight/Darp.CodeEditor/commit/dae73957f08f4181abfacfa3bfb8830a814a9486))
* **luau:** ship luau-lsp only for the runtime identifiers it has builds for ([7cf1d37](https://github.com/rosslight/Darp.CodeEditor/commit/7cf1d37f940ce62d26d187484e439aeb047b7edd))
* make sending, cancellation, the lifecycle and input limits robust ([96230f0](https://github.com/rosslight/Darp.CodeEditor/commit/96230f0174ab55b88052af40d3936b4289670ed8))
* make the probe type only into the demo window it was given ([a57af37](https://github.com/rosslight/Darp.CodeEditor/commit/a57af37ceedbfbfbe6e388e26185c40a58c11420))
* remove the timing and culture races behind the flaky CI runs ([3432098](https://github.com/rosslight/Darp.CodeEditor/commit/34320989ad9d3bd90ae416af9e8a998ef83904a1))
* report runner exits and environment errors reliably ([424625b](https://github.com/rosslight/Darp.CodeEditor/commit/424625bffaee110587a433b41194326b3c8e6362))
* require the globals assembly identity, not only its name ([ca3d743](https://github.com/rosslight/Darp.CodeEditor/commit/ca3d743a05cefe77cf014ee191c846d4756e8cab))
* **runner:** kill a runner for a busy call only after the call timed out ([5284516](https://github.com/rosslight/Darp.CodeEditor/commit/528451622833b8205c2d1e4a59f93fdda4e44399))
* ship and load what marked script references need at run time ([56b9cd7](https://github.com/rosslight/Darp.CodeEditor/commit/56b9cd73e409873b712bf54cd0afba8f7009bb75))
* ship satellites and only what the runner lacks for NativeAOT scripts ([bc3688e](https://github.com/rosslight/Darp.CodeEditor/commit/bc3688e91dc9bd7e598b72c477d3fe87bc45d6a1))
* start no server for a document opened during disposal ([88c73a4](https://github.com/rosslight/Darp.CodeEditor/commit/88c73a4970142b3917c8420164f09779dcb91df4))
* start runners without the app's .NET startup variables ([56ade43](https://github.com/rosslight/Darp.CodeEditor/commit/56ade430ebbf81459497d80cc85a0203806d0137))
* start the environment only for the initialize the client received ([363c6a1](https://github.com/rosslight/Darp.CodeEditor/commit/363c6a1e7e1c522ba44e6cfc6f3151bbee93e105))
* stop injecting keys on Windows ([e3bb58e](https://github.com/rosslight/Darp.CodeEditor/commit/e3bb58e896cf694513a7b415704e977d39f8adee))
* tell results, cancellations, stale answers and errors apart in the benchmark ([f6e5a92](https://github.com/rosslight/Darp.CodeEditor/commit/f6e5a92f4a1cebd4d508de2d628d6bb29d5c350c))
* warm up the completion list so the first one does not stall ([2913b84](https://github.com/rosslight/Darp.CodeEditor/commit/2913b84d235a47c79ca8953110cd9bdadcf2654a))
* warm up the completion list with items and a description ([24b1add](https://github.com/rosslight/Darp.CodeEditor/commit/24b1add00316515a1057b96f8f6ce2efaf0483e9))
* watch the client process once initialize is answered ([1e5c3e9](https://github.com/rosslight/Darp.CodeEditor/commit/1e5c3e97f58bb30f2456d74f4ea15493365d6680))


### Performance Improvements

* filter long completion lists on the server ([aece13d](https://github.com/rosslight/Darp.CodeEditor/commit/aece13d4e84b3dcc30fa0eab8e09cc1407b41373))
* show a list asked again in the open completion window ([40dbb32](https://github.com/rosslight/Darp.CodeEditor/commit/40dbb32b5720318b99e4f4e82d3b746685533a4d))
* write completion lists directly and resolve complex edits lazily ([affe028](https://github.com/rosslight/Darp.CodeEditor/commit/affe02803b64758438a5f459fbae381efa725d7d))

## Changelog

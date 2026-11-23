# Changelog

## [0.2.0](https://github.com/rosslight/Darp.CodeEditor/compare/v0.1.0...v0.2.0) (2026-11-22)


### Features

* **sample:** add hotkey settings and show the YAML schema ([#56](https://github.com/rosslight/Darp.CodeEditor/issues/56)) ([31bb8df](https://github.com/rosslight/Darp.CodeEditor/commit/31bb8dfd90767f7ba7e5cf5e644a1742ec793328))
* ship an agent skill and rewrite the README ([#58](https://github.com/rosslight/Darp.CodeEditor/issues/58)) ([56f5f50](https://github.com/rosslight/Darp.CodeEditor/commit/56f5f501557e474562f9867eb12223f3f182de5d))

## 0.1.0 (2026-11-22)


### Features

* add the sample app ([#24](https://github.com/rosslight/Darp.CodeEditor/issues/24)) ([b6a5d83](https://github.com/rosslight/Darp.CodeEditor/commit/b6a5d831d5369ca95a19a4dfdba03db8ab415994))
* complete with codicons and show signature help from language servers ([#28](https://github.com/rosslight/Darp.CodeEditor/issues/28)) ([62d602a](https://github.com/rosslight/Darp.CodeEditor/commit/62d602ad269d461d9f7e5b9519b2f819824e0ba1))
* draft the public API of the v1 packages ([#15](https://github.com/rosslight/Darp.CodeEditor/issues/15)) ([7850f61](https://github.com/rosslight/Darp.CodeEditor/commit/7850f61a5e86455f7943714f6761ae5df1728815))
* edit as VS Code does, and search with limits ([#21](https://github.com/rosslight/Darp.CodeEditor/issues/21)) ([08193e8](https://github.com/rosslight/Darp.CodeEditor/commit/08193e8bb550cbaeeb58403fb8eb4726ef5caac1))
* edit by the language's rules, with undo by word, line commands and occurrences ([#45](https://github.com/rosslight/Darp.CodeEditor/issues/45)) ([d0a73ad](https://github.com/rosslight/Darp.CodeEditor/commit/d0a73ad7e995ce0d177871ec949e41ef133682ef))
* implement the editor control and background highlighting ([#20](https://github.com/rosslight/Darp.CodeEditor/issues/20)) ([a771d97](https://github.com/rosslight/Darp.CodeEditor/commit/a771d9717d83285acf8a2a95a9eb57b2cac61237))
* implement the language server client and diagnostics ([#25](https://github.com/rosslight/Darp.CodeEditor/issues/25)) ([9172688](https://github.com/rosslight/Darp.CodeEditor/commit/9172688edac50b3da3eab970dbe8811b8017a057))
* insert completion snippets with tab stops ([#30](https://github.com/rosslight/Darp.CodeEditor/issues/30)) ([33a2d3d](https://github.com/rosslight/Darp.CodeEditor/commit/33a2d3d613d2f98bc5486e61727b3be158a7090f))
* keep find to plain text and snippets to stops and placeholders ([#47](https://github.com/rosslight/Darp.CodeEditor/issues/47)) ([70011de](https://github.com/rosslight/Darp.CodeEditor/commit/70011dec1ab94726889c68d8d9d77ad8525c75df))
* rebuild completion and signature help as the editor's own ([#50](https://github.com/rosslight/Darp.CodeEditor/issues/50)) ([1650845](https://github.com/rosslight/Darp.CodeEditor/commit/16508456206107b4517b801b2666203660529444))
* rebuild the hover and step through problems with F8 ([#51](https://github.com/rosslight/Darp.CodeEditor/issues/51)) ([3d4a88d](https://github.com/rosslight/Darp.CodeEditor/commit/3d4a88d24ee1ec66c0ae4147f9180d7db5d9fb04))
* run the editor's keys as commands through Avalonia's key bindings ([#44](https://github.com/rosslight/Darp.CodeEditor/issues/44)) ([7fee51e](https://github.com/rosslight/Darp.CodeEditor/commit/7fee51e64dd68c014b9dca3b08f87304b19d9960))
* **sample:** connect the language servers that are installed ([#31](https://github.com/rosslight/Darp.CodeEditor/issues/31)) ([6774078](https://github.com/rosslight/Darp.CodeEditor/commit/6774078a72ad3d535aac2eb1aa6824885e23eea4))
* show completion, hover and signature help in the editor's own popups ([#48](https://github.com/rosslight/Darp.CodeEditor/issues/48)) ([fdf9677](https://github.com/rosslight/Darp.CodeEditor/commit/fdf967701e092a6bd9ff3ba7f5e268cb7262e1aa))
* show squiggles, hover and semantic colors from language servers ([#26](https://github.com/rosslight/Darp.CodeEditor/issues/26)) ([d1c9007](https://github.com/rosslight/Darp.CodeEditor/commit/d1c9007cb32f0440ffbc46e63229ddf88a8cc790))


### Bug Fixes

* compare benchmarks by process means, and skip cold-start allocations ([#19](https://github.com/rosslight/Darp.CodeEditor/issues/19)) ([18904c4](https://github.com/rosslight/Darp.CodeEditor/commit/18904c4e7754c56d4e4eea72c0bd839dd63e1f9c))
* keep an editor without a document quiet, and name the benchmark's real target ([#29](https://github.com/rosslight/Darp.CodeEditor/issues/29)) ([450eba0](https://github.com/rosslight/Darp.CodeEditor/commit/450eba00a08a5be698321258efcb25715cea0ea2))
* run CI to its end, keep tests off the wall clock, and color the first lines of a cold grammar ([#33](https://github.com/rosslight/Darp.CodeEditor/issues/33)) ([4d7527a](https://github.com/rosslight/Darp.CodeEditor/commit/4d7527a77abaecbc3176296c98cf9d0c5eebc6e2))
* skip coverage comments for forks and report uncompared benchmarks ([#18](https://github.com/rosslight/Darp.CodeEditor/issues/18)) ([fa4481b](https://github.com/rosslight/Darp.CodeEditor/commit/fa4481bfe3122cd552db2fb26816c85e2a7394cd))


### Performance Improvements

* lay out long lines once per keystroke, and keep the 3,000-character limit ([#22](https://github.com/rosslight/Darp.CodeEditor/issues/22)) ([52fdb03](https://github.com/rosslight/Darp.CodeEditor/commit/52fdb03e9459270624738098db565d4583d505fc))

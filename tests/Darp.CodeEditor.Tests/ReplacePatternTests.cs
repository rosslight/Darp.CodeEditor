using System.Text.RegularExpressions;
using Darp.CodeEditor.Search;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// What replaces a regular expression's match: VS Code's syntax, not .NET's, by the cases of VS Code's own tests.
/// </summary>
public sealed class ReplacePatternTests
{
    [Theory]
    // \t, \n and \\; a backslash before anything else stays.
    [InlineData("hi", "hi", @"\thello", "\thello")]
    [InlineData("hi", "hi", @"hello\n", "hello\n")]
    [InlineData("hi", "hi", @"\\thello", @"\thello")]
    [InlineData("hi", "hi", @"\\\thello", "\\\thello")]
    [InlineData("hi", "hi", @"hello\", @"hello\")]
    [InlineData("hi", "hi", @"hello\x", @"hello\x")]
    [InlineData("hi", "hi", @"hello\0", @"hello\0")]
    // The match; a group, of two digits only while the expression has that many; $$ for a dollar sign.
    [InlineData("hi", "hi", "hello$&", "hellohi")]
    [InlineData("hi", "hi", "hello$0", "hellohi")]
    [InlineData("hi", "hi", "hello$01", "hellohi1")]
    [InlineData("hi", "(hi)", "hello$10", "hellohi0")]
    [InlineData("hi", "(hi)()()()()()()()()()", "hello$10", "hello")]
    [InlineData("hi", "(hi)", "hello$100", "hellohi00")]
    [InlineData("hi", "(hi)", "hello$20", "hello$20")]
    [InlineData("hi", "hi", "hello$$", "hello$")]
    [InlineData("hi", "hi", "hello$$0", "hello$0")]
    [InlineData("abcd", "a(z)?", "a{$1}", "a{}")]
    [InlineData("this is a bla text", "(th)is(?=.*bla)", "$0$1", "thisth")]
    [InlineData(
        "let fs = require('fs')",
        @"let\s+(\w+)\s*=\s*require\s*\(\s*['""]([\w\.\-/]+)\s*['""]\s*\)\s*",
        "import * as $1 from '$2';",
        "import * as fs from 'fs';"
    )]
    // .NET's own syntax stays as it is.
    [InlineData("hi", "hi", "hello$`", "hello$`")]
    [InlineData("hi", "hi", "hello$'", "hello$'")]
    [InlineData("hi", "(?<x>hi)", "hello${x}", "hello${x}")]
    // \u and \l change the case of the next character of the group after them, \U and \L of all the rest.
    [InlineData("func privateFunc(", @"func (\w+)\(", @"func \U$1(", "func PRIVATEFUNC(")]
    [InlineData("func privateFunc(", @"func (\w+)\(", @"func \u$1(", "func PrivateFunc(")]
    [InlineData("func privateFunc(", @"func (\w+)\(", @"func \L$1(", "func privatefunc(")]
    [InlineData("func PrivateFunc(", @"func (\w+)\(", @"func \l$1(", "func privateFunc(")]
    [InlineData("hellogooDbye", @"hello(\w+)", @"hello\u\u\l\l\U$1", "helloGOodBYE")]
    public void A_replacement_is_built_as_in_VS_Code(
        string text,
        string pattern,
        string replacement,
        string expected
    ) => ReplacePattern.Parse(replacement).Build(Regex.Match(text, pattern)).ShouldBe(expected);
}

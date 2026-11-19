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
    public void A_replacement_takes_the_match_its_groups_escapes_and_case_changes(
        string text,
        string pattern,
        string replacement,
        string expected
    ) => ReplacePattern.Parse(replacement).Build(Regex.Match(text, pattern)).ShouldBe(expected);

    /// <summary>Preserve Case: a replacement without groups takes the case of the match, by VS Code's own tests.</summary>
    [Theory]
    [InlineData("abc", "Def", "def")]
    [InlineData("Abc", "Def", "Def")]
    [InlineData("ABC", "Def", "DEF")]
    [InlineData("aBc", "Def", "def")]
    [InlineData("AbC", "Def", "Def")]
    [InlineData("aBC", "Def", "def")]
    [InlineData("aBc", "DeF", "deF")]
    // Parts between hyphens or underscores, as many in both, each take the case of theirs.
    [InlineData("Foo-Bar", "newfoo-newbar", "Newfoo-Newbar")]
    [InlineData("Foo-Bar-Abc", "newfoo-newbar-newabc", "Newfoo-Newbar-Newabc")]
    [InlineData("Foo-Bar-abc", "newfoo-newbar", "Newfoo-newbar")]
    [InlineData("foo-Bar", "newfoo-newbar", "newfoo-Newbar")]
    [InlineData("foo-BAR", "newfoo-newbar", "newfoo-NEWBAR")]
    [InlineData("foO-BAR", "NewFoo-NewBar", "newFoo-NEWBAR")]
    [InlineData("Foo_Bar", "newfoo_newbar", "Newfoo_Newbar")]
    [InlineData("Foo_Bar_Abc", "newfoo_newbar_newabc", "Newfoo_Newbar_Newabc")]
    [InlineData("Foo_Bar_abc", "newfoo_newbar", "Newfoo_newbar")]
    [InlineData("Foo_Bar-abc", "newfoo_newbar-abc", "Newfoo_newbar-abc")]
    [InlineData("foo_Bar", "newfoo_newbar", "newfoo_Newbar")]
    [InlineData("foo_BAR", "newfoo_newbar", "newfoo_NEWBAR")]
    public void With_Preserve_Case_a_replacement_takes_the_case_of_the_match(
        string match,
        string replacement,
        string expected
    )
    {
        Match matched = Regex.Match(match, Regex.Escape(match));

        ReplacePattern.Parse(replacement).Build(matched, preserveCase: true).ShouldBe(expected);
        ReplacePattern.Static(replacement).Build(matched, preserveCase: true).ShouldBe(expected);
    }

    [Fact]
    public void With_Preserve_Case_a_replacement_with_a_group_keeps_its_case()
    {
        Match matched = Regex.Match("FOO", "(FOO)");

        ReplacePattern.Parse("bar$1").Build(matched, preserveCase: true).ShouldBe("barFOO");
    }
}

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
    // The groups that the expression declares, whose numbers may skip some.
    [InlineData("a", "(?<10>a)", "$10/$1", "a/$1")]
    // \u and \l change the case of the next character of the group after them, \U and \L of all the rest.
    [InlineData("func privateFunc(", @"func (\w+)\(", @"func \U$1(", "func PRIVATEFUNC(")]
    [InlineData("func privateFunc(", @"func (\w+)\(", @"func \u$1(", "func PrivateFunc(")]
    [InlineData("func privateFunc(", @"func (\w+)\(", @"func \L$1(", "func privatefunc(")]
    [InlineData("func PrivateFunc(", @"func (\w+)\(", @"func \l$1(", "func privateFunc(")]
    [InlineData("hellogooDbye", @"hello(\w+)", @"hello\u\u\l\l\U$1", "helloGOodBYE")]
    // By Unicode's full case mappings, as JavaScript's: a character's case may be longer than the character.
    [InlineData("straße", @"(\w+)", @"\U$1", "STRASSE")]
    [InlineData("ßa", @"(\w+)", @"\u$1", "SSa")]
    [InlineData("\u0130", "(.)", @"\L$1", "i\u0307")]
    public void A_replacement_takes_the_match_its_groups_escapes_and_case_changes(
        string text,
        string pattern,
        string replacement,
        string expected
    )
    {
        var regex = new Regex(pattern);

        ReplacePattern.Parse(replacement, regex).Build(regex.Match(text)).ShouldBe(expected);
    }

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
    // By Unicode's full case mappings.
    [InlineData("FOO", "straße", "STRASSE")]
    [InlineData("Foo", "ßar", "SSar")]
    // A capital sigma at the end of a word is ς in lower case.
    [InlineData("ab", "ΟΣ ΣΑ ΟΣ.", "ος σα ος.")]
    // After a character that is cased but not a letter, as a Roman numeral.
    [InlineData("ab", "\u2163\u03A3", "\u2173\u03C2")]
    public void With_Preserve_Case_a_replacement_takes_the_case_of_the_match(
        string match,
        string replacement,
        string expected
    )
    {
        var regex = new Regex(Regex.Escape(match));
        Match matched = regex.Match(match);

        ReplacePattern.Parse(replacement, regex).Build(matched, preserveCase: true).ShouldBe(expected);
        ReplacePattern.Static(replacement).Build(matched, preserveCase: true).ShouldBe(expected);
    }

    [Fact]
    public void With_Preserve_Case_a_replacement_with_a_group_keeps_its_case()
    {
        var regex = new Regex("(FOO)");

        ReplacePattern.Parse("bar$1", regex).Build(regex.Match("FOO"), preserveCase: true).ShouldBe("barFOO");
    }
}

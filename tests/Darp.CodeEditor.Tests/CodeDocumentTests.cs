namespace Darp.CodeEditor.Tests;

/// <summary>Editing a document on its UI thread: text, snapshots, undo and the unmodified point.</summary>
[Collection(UiFixture.Collection)]
public sealed class CodeDocumentTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task A_snapshot_keeps_its_text_after_edits() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "one\ntwo");
            TextSnapshot before = document.CurrentSnapshot;

            document.Replace(new TextRange(0, 3), "first");

            before.GetText().ShouldBe("one\ntwo");
            before.GetPosition(4).ShouldBe(new TextPosition(2, 1));
            document.CurrentSnapshot.GetText().ShouldBe("first\ntwo");
            document.CurrentSnapshot.Version.ShouldBeGreaterThan(before.Version);
        });

    [Theory]
    [InlineData(-4, 5, "xbc")]
    [InlineData(2, 99, "abx")]
    [InlineData(9, 0, "abcx")]
    [InlineData(1, -1, "axbc")]
    public Task Replace_clamps_the_range_to_the_text(int start, int length, string expected) =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "abc");

            document.Replace(new TextRange(start, length), "x");

            document.Text.ShouldBe(expected);
        });

    [Fact]
    public Task Each_replace_and_each_text_change_is_one_undo_step() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "abc");
            document.CanUndo.ShouldBeFalse();

            document.Replace(new TextRange(1, 1), "XY");
            document.Text = "new";
            document.Undo();

            document.Text.ShouldBe("aXYc");
            document.CanRedo.ShouldBeTrue();
            document.Undo();
            document.Text.ShouldBe("abc");
            document.CanUndo.ShouldBeFalse();
            document.Redo();
            document.Redo();
            document.Text.ShouldBe("new");
            document.CanRedo.ShouldBeFalse();
        });

    [Fact]
    public Task Undoing_back_to_the_unmodified_point_clears_IsModified() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "abc");
            document.IsModified.ShouldBeFalse();

            document.Replace(new TextRange(3, 0), "d");
            document.IsModified.ShouldBeTrue();
            document.MarkUnmodified(document.CurrentSnapshot).ShouldBeTrue();
            document.IsModified.ShouldBeFalse();

            document.Undo();
            document.IsModified.ShouldBeTrue();
            document.Redo();
            document.IsModified.ShouldBeFalse();

            // Typing the same text again is a change.
            document.Replace(new TextRange(3, 1), "");
            document.Replace(new TextRange(3, 0), "d");
            document.IsModified.ShouldBeTrue();
        });

    [Fact]
    public Task A_snapshot_saved_before_a_change_does_not_mark_the_document_unmodified() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "abc");
            document.Replace(new TextRange(3, 0), "d");
            TextSnapshot saved = document.CurrentSnapshot;
            document.Replace(new TextRange(4, 0), "e");

            document.MarkUnmodified(saved).ShouldBeFalse();

            document.IsModified.ShouldBeTrue();
        });

    [Fact]
    public Task Snapshots_of_another_document_are_refused() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "abc");
            TextSnapshot other = CodeDocument.Create(CodeLanguage.PlainText, "abc").CurrentSnapshot;
            TextSnapshot compiled = TextSnapshot.FromString("abc");

            Should.Throw<ArgumentException>(() => document.MarkUnmodified(other));
            Should.Throw<ArgumentException>(() => document.MarkUnmodified(compiled));
            Should.Throw<ArgumentException>(() => document.MapToCurrent(other, new TextRange(0, 1)));
            Should.Throw<ArgumentException>(() => document.MapToCurrent(compiled, new TextRange(0, 1)));
        });

    [Fact]
    public Task MapToCurrent_follows_the_edits_since_the_snapshot() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "let x = y;");
            TextSnapshot analyzed = document.CurrentSnapshot;
            var y = new TextRange(8, 1);

            document.Replace(new TextRange(0, 0), "// note\n");
            document.MapToCurrent(analyzed, y).ShouldBe(new TextRange(16, 1));

            // Typing at either edge stays outside the range.
            document.Replace(new TextRange(16, 0), "(");
            document.Replace(new TextRange(18, 0), ")");
            document.MapToCurrent(analyzed, y).ShouldBe(new TextRange(17, 1));
            document.Text.ShouldBe("// note\nlet x = (y);");

            // Deleting the range's text shrinks it to where it was.
            document.Replace(new TextRange(12, 7), "");
            document.MapToCurrent(analyzed, y).ShouldBe(new TextRange(12, 0));
        });

    [Fact]
    public Task TextChanged_comes_after_every_change_with_the_new_snapshot() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "a");
            var seen = new List<string>();
            document.TextChanged += (_, _) => seen.Add(document.CurrentSnapshot.GetText());

            document.Replace(new TextRange(1, 0), "b");
            document.Text = "c";
            document.Undo();
            document.Redo();

            seen.ShouldBe(["ab", "c", "ab", "c"]);
        });

    [Fact]
    public Task PropertyChanged_reports_the_undo_state_and_indentation() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "a");
            var changed = new List<string?>();
            document.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            document.Replace(new TextRange(1, 0), "b");
            changed.ShouldBe([nameof(CodeDocument.CanUndo), nameof(CodeDocument.IsModified)], ignoreOrder: true);

            changed.Clear();
            document.Undo();
            changed.ShouldBe(
                [nameof(CodeDocument.CanUndo), nameof(CodeDocument.CanRedo), nameof(CodeDocument.IsModified)],
                ignoreOrder: true
            );

            changed.Clear();
            document.Indentation = Indentation.Tabs();
            document.Indentation = Indentation.Tabs();
            changed.ShouldBe([nameof(CodeDocument.Indentation)]);
        });

    [Fact]
    public Task Indentation_starts_as_in_VS_Code() =>
        _ui.Run(() =>
        {
            CodeDocument.Create(CodeLanguage.Yaml).Indentation.ShouldBe(Indentation.Spaces(2));
            CodeDocument.Create(CodeLanguage.Json).Indentation.ShouldBe(Indentation.Spaces(4));
        });

    [Theory]
    [InlineData(0)]
    [InlineData(1_001)]
    public Task An_indentation_that_editors_cannot_show_is_rejected(int size) =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json);

            Should.Throw<ArgumentOutOfRangeException>(() => document.Indentation = Indentation.Spaces(size));
            document.Indentation.ShouldBe(Indentation.Spaces(4));
        });
}

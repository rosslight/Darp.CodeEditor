using Avalonia.Headless.XUnit;

namespace Darp.CodeEditor.Tests;

/// <summary>Editing a document on its UI thread: text, snapshots, undo and the unmodified point.</summary>
public sealed class CodeDocumentTests
{
    [AvaloniaFact]
    public void A_snapshot_keeps_its_text_after_edits()
    {
        CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "one\ntwo");
        TextSnapshot before = document.CurrentSnapshot;

        document.Replace(new TextRange(0, 3), "first");

        Assert.Equal("one\ntwo", before.GetText());
        Assert.Equal(new TextPosition(2, 1), before.GetPosition(4));
        Assert.Equal("first\ntwo", document.CurrentSnapshot.GetText());
        Assert.True(document.CurrentSnapshot.Version > before.Version);
    }

    [AvaloniaTheory]
    [InlineData(-4, 5, "xbc")]
    [InlineData(2, 99, "abx")]
    [InlineData(9, 0, "abcx")]
    [InlineData(1, -1, "axbc")]
    public void Replace_clamps_the_range_to_the_text(int start, int length, string expected)
    {
        CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "abc");

        document.Replace(new TextRange(start, length), "x");

        Assert.Equal(expected, document.Text);
    }

    [AvaloniaFact]
    public void Each_replace_and_each_text_change_is_one_undo_step()
    {
        CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "abc");
        Assert.False(document.CanUndo);

        document.Replace(new TextRange(1, 1), "XY");
        document.Text = "new";
        document.Undo();

        Assert.Equal("aXYc", document.Text);
        Assert.True(document.CanRedo);
        document.Undo();
        Assert.Equal("abc", document.Text);
        Assert.False(document.CanUndo);
        document.Redo();
        document.Redo();
        Assert.Equal("new", document.Text);
        Assert.False(document.CanRedo);
    }

    [AvaloniaFact]
    public void Undoing_back_to_the_unmodified_point_clears_IsModified()
    {
        CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "abc");
        Assert.False(document.IsModified);

        document.Replace(new TextRange(3, 0), "d");
        Assert.True(document.IsModified);
        Assert.True(document.MarkUnmodified(document.CurrentSnapshot));
        Assert.False(document.IsModified);

        document.Undo();
        Assert.True(document.IsModified);
        document.Redo();
        Assert.False(document.IsModified);

        // Typing the same text again is a change.
        document.Replace(new TextRange(3, 1), "");
        document.Replace(new TextRange(3, 0), "d");
        Assert.True(document.IsModified);
    }

    [AvaloniaFact]
    public void A_snapshot_saved_before_a_change_does_not_mark_the_document_unmodified()
    {
        CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "abc");
        document.Replace(new TextRange(3, 0), "d");
        TextSnapshot saved = document.CurrentSnapshot;
        document.Replace(new TextRange(4, 0), "e");

        Assert.False(document.MarkUnmodified(saved));

        Assert.True(document.IsModified);
    }

    [AvaloniaFact]
    public void Snapshots_of_another_document_are_refused()
    {
        CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "abc");
        TextSnapshot other = CodeDocument.Create(CodeLanguage.PlainText, "abc").CurrentSnapshot;
        TextSnapshot compiled = TextSnapshot.FromString("abc");

        Assert.ThrowsAny<ArgumentException>(() => document.MarkUnmodified(other));
        Assert.ThrowsAny<ArgumentException>(() => document.MarkUnmodified(compiled));
        Assert.ThrowsAny<ArgumentException>(() => document.MapToCurrent(other, new TextRange(0, 1)));
        Assert.ThrowsAny<ArgumentException>(() => document.MapToCurrent(compiled, new TextRange(0, 1)));
    }

    [AvaloniaFact]
    public void MapToCurrent_follows_the_edits_since_the_snapshot()
    {
        CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "let x = y;");
        TextSnapshot analyzed = document.CurrentSnapshot;
        var y = new TextRange(8, 1);

        document.Replace(new TextRange(0, 0), "// note\n");
        Assert.Equal(new TextRange(16, 1), document.MapToCurrent(analyzed, y));

        // Typing at either edge stays outside the range.
        document.Replace(new TextRange(16, 0), "(");
        document.Replace(new TextRange(18, 0), ")");
        Assert.Equal(new TextRange(17, 1), document.MapToCurrent(analyzed, y));
        Assert.Equal("// note\nlet x = (y);", document.Text);

        // Deleting the range's text shrinks it to where it was.
        document.Replace(new TextRange(12, 7), "");
        Assert.Equal(new TextRange(12, 0), document.MapToCurrent(analyzed, y));
    }

    [AvaloniaFact]
    public void TextChanged_comes_after_every_change_with_the_new_snapshot()
    {
        CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "a");
        var seen = new List<string>();
        document.TextChanged += (_, _) => seen.Add(document.CurrentSnapshot.GetText());

        document.Replace(new TextRange(1, 0), "b");
        document.Text = "c";
        document.Undo();
        document.Redo();

        Assert.Equal(["ab", "c", "ab", "c"], seen);
    }

    [AvaloniaFact]
    public void PropertyChanged_reports_the_undo_state_and_indentation()
    {
        CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "a");
        var changed = new List<string?>();
        document.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        document.Replace(new TextRange(1, 0), "b");
        // In any order: the names are compared sorted, as they are listed.
        Assert.Equal(
            [nameof(CodeDocument.CanUndo), nameof(CodeDocument.IsModified)],
            changed.Order(StringComparer.Ordinal)
        );

        changed.Clear();
        document.Undo();
        Assert.Equal(
            [nameof(CodeDocument.CanRedo), nameof(CodeDocument.CanUndo), nameof(CodeDocument.IsModified)],
            changed.Order(StringComparer.Ordinal)
        );

        changed.Clear();
        document.Indentation = Indentation.Tabs();
        document.Indentation = Indentation.Tabs();
        Assert.Equal([nameof(CodeDocument.Indentation)], changed);
    }

    [AvaloniaFact]
    public void Indentation_starts_as_in_VS_Code()
    {
        Assert.Equal(Indentation.Spaces(2), CodeDocument.Create(CodeLanguage.Yaml).Indentation);
        Assert.Equal(Indentation.Spaces(4), CodeDocument.Create(CodeLanguage.Json).Indentation);
    }
}

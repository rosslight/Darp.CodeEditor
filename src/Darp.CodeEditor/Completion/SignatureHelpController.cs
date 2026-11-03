using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Completion;

/// <summary>Shows the signatures of the call the caret is in, after typing '(' or ','.</summary>
internal sealed class SignatureHelpController
{
    private readonly CodeEditor _editor;
    private readonly LanguageFeatures _features;
    private CancellationTokenSource? _cancellation;
    private OverloadInsightWindow? _window;

    public SignatureHelpController(CodeEditor editor, LanguageFeatures features)
    {
        _editor = editor;
        _features = features;
        editor.TextArea.TextEntered += OnTextEntered;
    }

    public void Close()
    {
        _cancellation?.Cancel();
        _cancellation = null;
        _window?.Hide();
        _window = null;
    }

    private void OnTextEntered(object? sender, Avalonia.Input.TextInputEventArgs e)
    {
        if (_features.Service is not { } service || e.Text is not [char c])
            return;
        if (service.Capabilities.SignatureHelpTriggerCharacters.Contains(c))
            Request(
                service,
                new SignatureHelpTrigger(SignatureHelpTriggerKind.TriggerCharacter, c, _window is not null)
            );
        else if (c == ')')
            Close();
    }

    private async void Request(ILanguageService service, SignatureHelpTrigger trigger)
    {
        // The older request is no longer needed: cancelling it tells the server so.
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        (DocumentVersion version, ITextSourceVersion checkpoint) = _features.CreateSnapshot();
        int position = _editor.CaretOffset;
        long start = Stopwatch.GetTimestamp();
        SignatureHelp? help = await _features.RequestAsync(
            () => service.GetSignatureHelpAsync(version, position, trigger, cancellation.Token),
            cancellation.Token
        );
        if (cancellation.IsCancellationRequested)
            return;
        if (help is null || help.Signatures.Count == 0)
        {
            Close();
            return;
        }

        TextDocument document = _editor.Document;
        int rangeStart = checkpoint.MoveOffsetTo(
            document.Version,
            help.ApplicableRange.Start,
            AnchorMovementType.BeforeInsertion
        );
        int rangeEnd = checkpoint.MoveOffsetTo(
            document.Version,
            help.ApplicableRange.End,
            AnchorMovementType.AfterInsertion
        );
        _window?.Hide();
        _window = new OverloadInsightWindow(_editor.TextArea)
        {
            Provider = new OverloadProvider(help),
            StartOffset = rangeStart,
            EndOffset = Math.Max(rangeEnd, _editor.CaretOffset),
        };
        _window.Show();
        _editor.ReportTiming("signature-help", Stopwatch.GetElapsedTime(start), help.Signatures.Count);
    }

    private sealed class OverloadProvider(SignatureHelp help) : IOverloadProvider
    {
        private readonly SignatureHelp _help = help;
        private int _selectedIndex = Math.Clamp(help.ActiveSignature, 0, help.Signatures.Count - 1);

        public event PropertyChangedEventHandler? PropertyChanged;

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                _selectedIndex = Math.Clamp(value, 0, Count - 1);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }
        }

        public int Count => _help.Signatures.Count;

        public string CurrentIndexText => $"{_selectedIndex + 1} of {Count}";

        public object CurrentHeader =>
            CreateHeader(
                _help.Signatures[_selectedIndex],
                _help.Signatures[_selectedIndex].ActiveParameter ?? _help.ActiveParameter
            );

        public object CurrentContent => _help.Signatures[_selectedIndex].Documentation ?? "";

        private static TextBlock CreateHeader(SignatureInfo signature, int activeParameter)
        {
            var block = new TextBlock();
            InlineCollection inlines = block.Inlines ??= new InlineCollection();
            string label = signature.Label;
            int written = 0;
            int searchFrom = 0;
            for (int i = 0; i < signature.Parameters.Count; i++)
            {
                ParameterInfo parameter = signature.Parameters[i];
                // The service's offsets, or else the label's next occurrence.
                int index =
                    parameter.LabelRange?.Start ?? label.IndexOf(parameter.Label, searchFrom, StringComparison.Ordinal);
                int length = parameter.LabelRange?.Length ?? parameter.Label.Length;
                if (index < searchFrom || index + length > label.Length)
                    break;
                searchFrom = index + length;
                if (i != activeParameter)
                    continue;
                inlines.Add(new Run(label[..index]));
                inlines.Add(new Run(label[index..searchFrom]) { FontWeight = FontWeight.Bold });
                written = searchFrom;
                break;
            }
            inlines.Add(new Run(label[written..]));
            return block;
        }
    }
}

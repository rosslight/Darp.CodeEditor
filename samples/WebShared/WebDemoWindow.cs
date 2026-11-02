using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Darp.CodeEditor.Web;

namespace Darp.CodeEditor.WebDemo;

/// <param name="Create">Creates the tab's editor; called the first time the tab is shown.</param>
internal sealed record WebDemoTab(string Header, Func<WebCodeEditor> Create);

/// <summary>
/// The demo window: a toolbar with a text box (focus, Tab and clipboard checks) and a theme switch, a tab strip and the
/// editors.
/// </summary>
/// <remarks>
/// A web view is destroyed when it leaves the visual tree, which would reload the page on every tab switch. So each
/// editor is created on first use and then stays in the tree; switching tabs only toggles visibility.
/// </remarks>
internal sealed class WebDemoWindow : Window
{
    private readonly IReadOnlyList<WebDemoTab> _tabs;
    private readonly WebCodeEditor?[] _editors;
    private readonly Panel _editorPanel = new();
    private readonly TabStrip _tabStrip = new();
    private readonly TaskCompletionSource<WebCodeEditor> _firstEditor = new();
    private WebCodeEditor? _benchEditor;

    public WebDemoWindow(string title, IReadOnlyList<WebDemoTab> tabs, bool light)
    {
        Title = title;
        Width = 1200;
        Height = 800;
        _tabs = tabs;
        _editors = new WebCodeEditor?[tabs.Count];
        EditorTheme = light ? WebEditorTheme.Light : WebEditorTheme.Dark;

        AppTextBox = new TextBox { Width = 260, PlaceholderText = "App text box (focus and clipboard checks)" };
        var themeButton = new Button { Content = "Toggle theme" };
        themeButton.Click += (_, _) => ToggleTheme();
        Status = new TextBlock
        {
            Foreground = Brushes.White,
            Text = "Ctrl+S reaches the host · Ctrl+F searches in the editor",
        };

        _tabStrip.ItemsSource = tabs.Select(t => t.Header).ToArray();
        _tabStrip.SelectionChanged += (_, _) =>
        {
            if (_tabStrip.SelectedIndex >= 0)
                ShowTab(_tabStrip.SelectedIndex);
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(8, 4),
            Children = { AppTextBox, themeButton, _tabStrip },
        };
        var statusBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#007ACC")),
            Padding = new Thickness(8, 3),
            Child = Status,
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        Content = new DockPanel { Children = { toolbar, statusBar, _editorPanel } };

        KeyBindings.Add(
            new KeyBinding { Gesture = new KeyGesture(Key.S, KeyModifiers.Control), Command = new SaveCommand(this) }
        );
        ApplyTheme();
        // A web view attached before its window is mapped stays hidden on X11 until its visibility changes, so the
        // first editor is created once the window is open.
        Opened += (_, _) =>
        {
            _tabStrip.SelectedIndex = 0;
            _firstEditor.TrySetResult(EditorOf(0) ?? ShowTab(0));
        };
    }

    /// <summary>The first tab's editor, created when the window opened.</summary>
    public Task<WebCodeEditor> FirstEditor => _firstEditor.Task;

    /// <summary>Raised when an editor was created, before it is shown: subscribe to its page events here.</summary>
    public event Action<WebCodeEditor>? EditorCreated;

    public TextBox AppTextBox { get; }

    public TextBlock Status { get; }

    public WebEditorTheme EditorTheme { get; private set; }

    /// <summary>The editor of a tab, created when the tab was first shown.</summary>
    public WebCodeEditor? EditorOf(int index) => _editors[index];

    /// <summary>Shows a tab, creating its editor the first time.</summary>
    public WebCodeEditor ShowTab(int index)
    {
        if (_tabStrip.SelectedIndex != index)
            _tabStrip.SelectedIndex = index;
        WebCodeEditor editor = _editors[index] ??= Add(_tabs[index].Create());
        Show(editor);
        return editor;
    }

    /// <summary>Shows a separate editor for measurements, replacing the previous one.</summary>
    public void ShowBenchEditor(WebCodeEditor editor)
    {
        if (_benchEditor is not null)
            _editorPanel.Children.Remove(_benchEditor);
        _benchEditor = Add(editor);
        Show(editor);
    }

    public void ToggleTheme()
    {
        EditorTheme = EditorTheme == WebEditorTheme.Dark ? WebEditorTheme.Light : WebEditorTheme.Dark;
        ApplyTheme();
    }

    public void ReportSaved(string source) => Status.Text = $"Saved ({source}) at {DateTime.Now:HH:mm:ss.fff}";

    private WebCodeEditor Add(WebCodeEditor editor)
    {
        EditorCreated?.Invoke(editor);
        editor.EditorTheme = EditorTheme;
        editor.PageEvent += (_, e) =>
        {
            if (e.Type == "save")
                ReportSaved("Ctrl+S in the editor page");
        };
        _editorPanel.Children.Add(editor);
        return editor;
    }

    private void Show(WebCodeEditor editor)
    {
        foreach (Control child in _editorPanel.Children)
            child.IsVisible = ReferenceEquals(child, editor);
    }

    private void ApplyTheme()
    {
        RequestedThemeVariant = EditorTheme == WebEditorTheme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Background = new SolidColorBrush(Color.Parse(EditorTheme == WebEditorTheme.Dark ? "#1E1E1E" : "#FFFFFF"));
        foreach (Control child in _editorPanel.Children)
        {
            if (child is WebCodeEditor editor)
                editor.EditorTheme = EditorTheme;
        }
    }

    private sealed class SaveCommand(WebDemoWindow window) : System.Windows.Input.ICommand
    {
        private readonly WebDemoWindow _window = window;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _window.ReportSaved("Ctrl+S in the host");
    }
}

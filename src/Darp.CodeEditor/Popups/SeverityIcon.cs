// The shapes of VS Code's codicons for the severities of diagnostics: the path data of their SVG files, copied unchanged
// from the package @vscode/codicons 0.0.46-24 (https://github.com/microsoft/vscode-codicons, src/icons). Copyright (c)
// Microsoft Corporation, licensed under the Creative Commons Attribution 4.0 International license
// (https://creativecommons.org/licenses/by/4.0/). The package's THIRD-PARTY-NOTICES.md credits them.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The icon of a diagnostic's severity in a hover, as VS Code shows it: the codicon <c>error</c>, <c>warning</c> or
/// <c>info</c>, also for a hint, 16 by 16 pixels, in the theme's <c>problems*Icon.foreground</c>.
/// </summary>
internal sealed class SeverityIcon : Control
{
    private const string ErrorPath =
        "F1 M8 1C4.14 1 1 4.14 1 8C1 11.86 4.14 15 8 15C11.86 15 15 11.86 15 8C15 4.14 11.86 1 8 1ZM8 14C4.691 14 2 11.309 2 8C2 4.691 4.691 2 8 2C11.309 2 14 4.691 14 8C14 11.309 11.309 14 8 14ZM10.854 5.854L8.708 8L10.854 10.146C11.049 10.341 11.049 10.658 10.854 10.853C10.756 10.951 10.628 10.999 10.5 10.999C10.372 10.999 10.244 10.95 10.146 10.853L8 8.707L5.854 10.853C5.756 10.951 5.628 10.999 5.5 10.999C5.372 10.999 5.244 10.95 5.146 10.853C4.951 10.658 4.951 10.341 5.146 10.146L7.292 8L5.146 5.854C4.951 5.659 4.951 5.342 5.146 5.147C5.341 4.952 5.658 4.952 5.853 5.147L7.999 7.293L10.145 5.147C10.34 4.952 10.657 4.952 10.852 5.147C11.047 5.342 11.047 5.659 10.852 5.854H10.854Z";

    private const string WarningPath =
        "F1 M14.831 11.965L9.206 1.714C8.965 1.274 8.503 1 8 1C7.497 1 7.035 1.274 6.794 1.714L1.169 11.965C1.059 12.167 1 12.395 1 12.625C1 13.383 1.617 14 2.375 14H13.625C14.383 14 15 13.383 15 12.625C15 12.395 14.941 12.167 14.831 11.965ZM13.625 13H2.375C2.168 13 2 12.832 2 12.625C2 12.561 2.016 12.5 2.046 12.445L7.671 2.195C7.736 2.075 7.863 2 8 2C8.137 2 8.264 2.075 8.329 2.195L13.954 12.445C13.984 12.501 14 12.561 14 12.625C14 12.832 13.832 13 13.625 13ZM8.75 11.25C8.75 11.664 8.414 12 8 12C7.586 12 7.25 11.664 7.25 11.25C7.25 10.836 7.586 10.5 8 10.5C8.414 10.5 8.75 10.836 8.75 11.25ZM7.5 9V5.5C7.5 5.224 7.724 5 8 5C8.276 5 8.5 5.224 8.5 5.5V9C8.5 9.276 8.276 9.5 8 9.5C7.724 9.5 7.5 9.276 7.5 9Z";

    private const string InfoPath =
        "F1 M8.49902 7.49998C8.49902 7.22384 8.27517 6.99998 7.99902 6.99998C7.72288 6.99998 7.49902 7.22384 7.49902 7.49998V10.5C7.49902 10.7761 7.72288 11 7.99902 11C8.27517 11 8.49902 10.7761 8.49902 10.5V7.49998ZM8.74807 5.50001C8.74807 5.91369 8.41271 6.24905 7.99903 6.24905C7.58535 6.24905 7.25 5.91369 7.25 5.50001C7.25 5.08633 7.58535 4.75098 7.99903 4.75098C8.41271 4.75098 8.74807 5.08633 8.74807 5.50001ZM8 1C4.13401 1 1 4.13401 1 8C1 11.866 4.13401 15 8 15C11.866 15 15 11.866 15 8C15 4.13401 11.866 1 8 1ZM2 8C2 4.68629 4.68629 2 8 2C11.3137 2 14 4.68629 14 8C14 11.3137 11.3137 14 8 14C4.68629 14 2 11.3137 2 8Z";

    /// <summary>The shapes, parsed the first time they show, on the UI thread.</summary>
    private static Geometry? s_error;

    private static Geometry? s_warning;
    private static Geometry? s_info;

    private readonly Geometry _shape;
    private readonly IBrush _brush;

    public SeverityIcon(DiagnosticSeverity severity, PopupColors colors)
    {
        Severity = severity;
        Width = 16;
        Height = 16;
        (_shape, _brush) = severity switch
        {
            DiagnosticSeverity.Error => (s_error ??= Geometry.Parse(ErrorPath), colors.ErrorIcon),
            DiagnosticSeverity.Warning => (s_warning ??= Geometry.Parse(WarningPath), colors.WarningIcon),
            _ => (s_info ??= Geometry.Parse(InfoPath), colors.InfoIcon),
        };
    }

    /// <summary>The severity it shows, for tests.</summary>
    public DiagnosticSeverity Severity { get; }

    public override void Render(DrawingContext context) => context.DrawGeometry(_brush, null, _shape);
}

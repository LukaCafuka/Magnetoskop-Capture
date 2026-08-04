using System.Windows;
using System.Windows.Input;
using Magnetoskop.App.ViewModels;

namespace Magnetoskop.App.Views;

/// <summary>
/// Jog/shuttle dial input that maps cursor X to [-1, +1] while LMB is held.
/// Mouse wheel: scroll up = more reverse, scroll down = more forward (stays until
/// scrolled back to center, Stop, or a drag-release).
/// Avoids WPF <see cref="System.Windows.Controls.Slider"/> thumb-drag entirely —
/// that control keeps a stale drag offset and does not follow the cursor reliably.
/// </summary>
internal static class JogShuttleDial
{
    /// <summary>Dial deflection change per mouse-wheel notch (~10 notches to ±max).</summary>
    private const double WheelNotchStep = 0.1;

    public static void Attach(FrameworkElement surface, MainViewModel viewModel)
    {
        var dragging = false;

        surface.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            dragging = true;
            surface.CaptureMouse();
            Apply(surface, viewModel, e);
            e.Handled = true;
        };

        surface.PreviewMouseMove += (_, e) =>
        {
            if (!dragging || !surface.IsMouseCaptured) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag(surface, viewModel, ref dragging);
                return;
            }

            Apply(surface, viewModel, e);
            e.Handled = true;
        };

        surface.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            EndDrag(surface, viewModel, ref dragging);
            e.Handled = true;
        };

        surface.LostMouseCapture += (_, _) =>
        {
            // Capture lost externally (Alt-Tab, etc.). EndDrag already released on normal MouseUp.
            if (!dragging) return;
            dragging = false;
            _ = viewModel.ReleaseJogShuttleWheelAsync();
        };

        surface.PreviewMouseWheel += (_, e) =>
        {
            if (dragging) return;
            if (e.Delta == 0) return;

            // Scroll up (Delta > 0) → more rewind (−); scroll down → more forward (+).
            var notches = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
            var next = Math.Clamp(viewModel.WheelPosition - notches * WheelNotchStep, -1.0, 1.0);
            if (Math.Abs(next) < 0.02)
            {
                // Back to center → same clean stop as releasing a drag.
                _ = viewModel.ReleaseJogShuttleWheelAsync();
            }
            else
            {
                viewModel.WheelPosition = next;
            }

            e.Handled = true;
        };
    }

    private static void EndDrag(FrameworkElement surface, MainViewModel viewModel, ref bool dragging)
    {
        if (!dragging) return;
        dragging = false;
        if (surface.IsMouseCaptured)
        {
            surface.ReleaseMouseCapture();
        }

        _ = viewModel.ReleaseJogShuttleWheelAsync();
    }

    private static void Apply(FrameworkElement surface, MainViewModel viewModel, MouseEventArgs e)
    {
        var width = surface.ActualWidth;
        if (width < 1) return;

        var x = e.GetPosition(surface).X;
        var position = Math.Clamp(x / width * 2.0 - 1.0, -1.0, 1.0);
        viewModel.WheelPosition = position;
    }
}

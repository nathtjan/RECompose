// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace RECompose.App.Controls;

/// <summary>
/// A key that needs a deliberate press in Service lock (WF-11, LIVE-3): with <see cref="HoldRequired"/> it acts only after the
/// pointer, or Space or Enter, has been held for 0.6 s, with a bar filling along its foot while it waits. Without it, it is
/// an ordinary button. A tap that is too short does nothing.
/// </summary>
public class HoldButton : Button
{
    public static readonly StyledProperty<bool> HoldRequiredProperty =
        AvaloniaProperty.Register<HoldButton, bool>(nameof(HoldRequired));

    public static readonly StyledProperty<double> HoldProgressProperty =
        AvaloniaProperty.Register<HoldButton, double>(nameof(HoldProgress));

    public static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(600);

    private DispatcherTimer? _timer;
    private DateTime _started;
    private bool _completing;

    static HoldButton()
    {
        AffectsRender<HoldButton>(HoldProgressProperty);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public bool HoldRequired
    {
        get => GetValue(HoldRequiredProperty);
        set => SetValue(HoldRequiredProperty, value);
    }

    /// <summary>0…1 while the key is being held.</summary>
    public double HoldProgress
    {
        get => GetValue(HoldProgressProperty);
        private set => SetValue(HoldProgressProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (HoldRequired && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && IsEffectivelyEnabled)
        {
            e.Pointer.Capture(this);
            Begin();
            e.Handled = true;
            return;
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (HoldRequired)
        {
            // Released after the hold time without the timer having fired (a busy UI thread): it still counts.
            var held = _timer is not null && DateTime.UtcNow - _started >= HoldTime;
            Cancel();
            e.Pointer.Capture(null);
            e.Handled = true;
            if (held)
                Complete();
            return;
        }

        base.OnPointerReleased(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (HoldRequired)
            Cancel();
        base.OnPointerCaptureLost(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (HoldRequired && e.Key is Key.Space or Key.Enter)
        {
            if (_timer is null)
                Begin();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (HoldRequired && e.Key is Key.Space or Key.Enter)
        {
            Cancel();
            e.Handled = true;
            return;
        }

        base.OnKeyUp(e);
    }

    protected override void OnClick()
    {
        // In Service lock a plain click does nothing; only a completed hold acts.
        if (HoldRequired && !_completing)
            return;
        base.OnClick();
    }

    private void Begin()
    {
        Cancel();
        _started = DateTime.UtcNow;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) =>
        {
            var progress = (DateTime.UtcNow - _started) / HoldTime;
            if (progress < 1)
            {
                HoldProgress = progress;
                return;
            }

            Cancel();
            Complete();
        };
        _timer.Start();
    }

    private void Complete()
    {
        _completing = true;
        try
        {
            OnClick();
        }
        finally
        {
            _completing = false;
        }
    }

    private void Cancel()
    {
        _timer?.Stop();
        _timer = null;
        HoldProgress = 0;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (HoldProgress <= 0)
            return;

        var brush = this.TryFindResource("accent", ActualThemeVariant, out var accent) && accent is IBrush b ? b : Brushes.Gray;
        var width = Bounds.Width * HoldProgress;
        context.FillRectangle(brush, new Rect(0, Bounds.Height - 4, width, 4));
    }
}

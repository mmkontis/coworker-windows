using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Coworker.Controls;

/// The clickable, focusable surface of the model slider. It is a control of its own so it gets
/// keyboard focus and can expose the stop index to screen readers as a range value.
public sealed class StopSliderSurface : ContentControl
{
    public StopSliderSurface()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
    }

    /// Number of stops.
    public int Count { get; set; }

    /// Selected stop, -1 when the selected model is not one of the stops.
    public int Index { get; set; } = -1;

    /// Called by assistive technology to move to another stop.
    public Action<int>? IndexRequested { get; set; }

    public void NotifyIndexChanged(int oldIndex, int newIndex)
    {
        if (FrameworkElementAutomationPeer.FromElement(this) is StopSliderPeer peer)
            peer.RaiseValueChanged(Math.Max(oldIndex, 0), Math.Max(newIndex, 0));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StopSliderPeer(this);

    private sealed class StopSliderPeer(StopSliderSurface owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override object GetPatternCore(PatternInterface pattern) =>
            pattern == PatternInterface.RangeValue ? this : base.GetPatternCore(pattern);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        protected override string GetClassNameCore() => nameof(StopSliderSurface);

        public void RaiseValueChanged(double oldValue, double newValue) =>
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, newValue);

        public bool IsReadOnly => false;
        public double LargeChange => 1;
        public double SmallChange => 1;
        public double Minimum => 0;
        public double Maximum => Math.Max(owner.Count - 1, 0);
        public double Value => Math.Max(owner.Index, 0);

        public void SetValue(double value) => owner.IndexRequested?.Invoke((int)Math.Round(value));
    }
}

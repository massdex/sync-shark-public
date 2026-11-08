using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Control = System.Windows.Controls.Control;

namespace Massdex.SyncShark;

// Availability stays with the operation's existing predicate. This only exposes its explanation,
// including when a disabled control cannot receive focus or execute its handler.
internal static class ActionAvailability
{
    private sealed class AvailabilityState(Func<string> reason)
    {
        internal Func<string> Reason = reason;
        internal Action Refresh = () => { };
        internal Action<string> SetExplanation = _ => { };
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, AvailabilityState> Refreshers = new();

    internal static void Refresh(Control control)
    { if (Refreshers.TryGetValue(control, out var state)) state.Refresh(); }

    internal static void SetExplanation(Control control, string explanation)
    {
        if (Refreshers.TryGetValue(control, out var state)) state.SetExplanation(explanation);
        else
        {
            control.ToolTip = explanation;
            AutomationProperties.SetHelpText(control, explanation);
        }
    }

    internal static void Bind(Control control, Func<string> disabledReason)
    {
        if (Refreshers.TryGetValue(control, out var existing))
        {
            existing.Reason = disabledReason;
            existing.Refresh();
            return;
        }
        var state = new AvailabilityState(disabledReason);
        ToolTipService.SetShowOnDisabled(control, true);
        object? enabledTip = control.ToolTip;
        string? shownReason = null;
        void Refresh()
        {
            if (!control.IsEnabled)
            {
                if (shownReason is null) enabledTip = control.ToolTip;
                shownReason = state.Reason();
                control.ToolTip = shownReason;
                AutomationProperties.SetHelpText(control, shownReason);
            }
            else if (shownReason is not null)
            {
                if (Equals(control.ToolTip, shownReason)) control.ToolTip = enabledTip;
                shownReason = null;
                AutomationProperties.SetHelpText(control, control.ToolTip as string ?? "");
            }
        }
        control.IsEnabledChanged += (_, _) => Refresh();
        control.ToolTipOpening += (_, _) => Refresh();
        state.Refresh = Refresh;
        state.SetExplanation = explanation =>
        {
            enabledTip = explanation;
            if (control.IsEnabled)
            {
                control.ToolTip = explanation;
                AutomationProperties.SetHelpText(control, explanation);
            }
            else Refresh();
        };
        Refreshers.Add(control, state);
        Refresh();
    }
}

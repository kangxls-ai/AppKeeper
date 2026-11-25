using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AppKeeper;

// Intercept before TextBox consumes Enter as a newline in its default key handler.
internal sealed class PigeonComposer : TextBox
{
    protected override Type StyleKeyOverride=>typeof(TextBox);
    internal event Action? SendRequested;
    internal PigeonComposer(){AcceptsReturn=true;}
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.Key==Key.Enter)
        {
            if(this.GetVisualDescendants().OfType<TextPresenter>().Any(p=>!string.IsNullOrEmpty(p.PreeditText))){e.Handled=true;return;}
            if(e.KeyModifiers==KeyModifiers.None){e.Handled=true;SendRequested?.Invoke();return;}
        }
        // Ctrl+Enter follows TextBox's native newline/selection/undo handling.
        base.OnKeyDown(e);
    }
}

using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AppKeeper;

internal sealed partial class MacWindow
{
    private async Task ChatInputSmoke()
    {
        Navigate("飞鸽传书");var chosen=pigeon.Peers().First(p=>p.State=="paired");await ChoosePeer(chosen);await Task.Delay(80);
        var editor=(PigeonComposer)composer!;
        int Count()=>pigeon.History(chosen.Key).Count(item=>item.Outgoing&&item.Kind=="text");
        void Enter(KeyModifiers modifiers=KeyModifiers.None)=>editor.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.Enter,KeyModifiers=modifiers});
        var before=Count();editor.Text="回车直接发送";Enter();
        if(Count()!=before+1||!string.IsNullOrEmpty(editor.Text)||pigeon.History(chosen.Key).Last(item=>item.Outgoing).Value!="回车直接发送")throw new Exception("Enter 未发送消息或意外换行。");
        editor.Text="甲乙丙丁";editor.SelectionStart=1;editor.SelectionEnd=3;Enter(KeyModifiers.Control);
        if(Count()!=before+1||editor.Text!="甲"+editor.NewLine+"丁")throw new Exception("Ctrl+Enter 未按选区换行或误发消息。");
        Enter();if(Count()!=before+2||pigeon.History(chosen.Key).Last(item=>item.Outgoing).Value!="甲"+editor.NewLine+"丁")throw new Exception("多行消息发送失败。");
        Enter();editor.Text="  ";Enter();if(Count()!=before+2)throw new Exception("空消息被发送。");
        editor.Text="选词中的草稿";var presenter=editor.GetVisualDescendants().OfType<TextPresenter>().Single();presenter.PreeditText="zhong";Enter();
        if(Count()!=before+2||editor.Text!="选词中的草稿")throw new Exception("输入法组字时误发或改变草稿。");
        presenter.PreeditText=null;Enter();if(Count()!=before+3)throw new Exception("组字完成后无法发送。");
        Console.WriteLine("PASS: Mac chat routed Enter sends once, Ctrl+Enter replaces selection with newline, multiline queue, empty suppression and IME preedit protection");
    }
}

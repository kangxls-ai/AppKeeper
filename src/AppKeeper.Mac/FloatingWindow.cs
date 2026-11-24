using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace AppKeeper;

internal sealed class FloatingWindow : Window
{
    internal bool FinalClose;
    private readonly MacState state;
    private bool docking;
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(550)};
    internal FloatingWindow(MacState state,Action open)
    {
        this.state=state;Width=44;Height=44;SystemDecorations=SystemDecorations.None;CanResize=false;ShowInTaskbar=false;Topmost=true;
        TransparencyLevelHint=[WindowTransparencyLevel.Transparent];Background=Brushes.Transparent;
        Content=new Border{Background=new SolidColorBrush(Color.Parse("#477ec7")),CornerRadius=new CornerRadius(22),Child=new TextBlock{Text="▦",FontSize=24,Foreground=Brushes.White,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center,VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center}};
        PointerPressed+=(_,e)=>{if(e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)BeginMoveDrag(e);};PointerEntered+=(_,_)=>open();
        PositionChanged+=(_,_)=>{if(!docking){timer.Stop();timer.Start();}};timer.Tick+=(_,_)=>{timer.Stop();Dock();};
        Opened+=(_,_)=>{var area=(Screens.ScreenFromPoint(new PixelPoint(state.Settings.BallX,state.Settings.BallY))??Screens.Primary)?.WorkingArea;if(area is {} a){docking=true;Position=state.Settings.HasBallPosition?new PixelPoint(Math.Clamp(state.Settings.BallX,a.X-20,a.Right-24),Math.Clamp(state.Settings.BallY,a.Y,a.Bottom-44)):new PixelPoint(a.Right-24,a.Y+a.Height/2);docking=false;}};
        Closing+=(_,e)=>{if(!FinalClose&&e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)){e.Cancel=true;Hide();}};
    }
    internal void PrepareShutdown()
    {
        FinalClose=true;timer.Stop();
        if(IsVisible){state.Settings.BallX=Position.X;state.Settings.BallY=Position.Y;state.Settings.HasBallPosition=true;}
    }
    private void Dock()
    {
        var screen=Screens.ScreenFromPoint(Position)??Screens.Primary;if(screen==null)return;var a=screen.WorkingArea;var size=(int)(44*screen.Scaling);var half=size/2;
        var x=Position.X;var y=Position.Y;var distances=new[]{Math.Abs(x-a.X),Math.Abs(x+size-a.Right),Math.Abs(y-a.Y),Math.Abs(y+size-a.Bottom)};var edge=Array.IndexOf(distances,distances.Min());
        if(distances[edge]<size){if(edge==0)x=a.X-half;else if(edge==1)x=a.Right-half;else if(edge==2)y=a.Y-half;else y=a.Bottom-half;}
        docking=true;Position=new PixelPoint(x,y);docking=false;state.Settings.BallX=x;state.Settings.BallY=y;state.Settings.HasBallPosition=true;try{state.Save();}catch{/* Main preferences will report persistent storage errors. */}
    }
}

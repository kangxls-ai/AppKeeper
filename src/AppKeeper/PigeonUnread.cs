using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AppKeeper;

internal static class PigeonUnread
{
    internal static bool Incoming(PigeonItem item) => !item.Outgoing && !item.Archived && item.Kind is "text" or "file";
    internal static Dictionary<string, int> Counts(PigeonData data)
    {
        var paired = data.Peers.Where(peer => peer.State == "paired").Select(peer => peer.Key).ToHashSet();
        return data.Items.Where(item => item.Unread && Incoming(item) && paired.Contains(item.Peer))
            .GroupBy(item => item.Peer).ToDictionary(group => group.Key, group => group.Count());
    }
    internal static string Label(int count) => count > 99 ? "99+" : count.ToString();
    internal static string? ReadingPeer(string? selected, bool visible, bool active, bool minimized, bool choosing) =>
        visible && active && !minimized && !choosing ? selected : null;
}

internal sealed class PigeonUnreadBadge : Border
{
    private readonly TextBlock text = new()
    {
        FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };
    internal PigeonUnreadBadge()
    {
        Background = new SolidColorBrush(Color.FromRgb(250, 81, 81));
        CornerRadius = new(10); MinWidth = 20; Height = 20; Padding = new(5, 0, 5, 0);
        IsHitTestVisible = false; Child = text; Visibility = Visibility.Collapsed;
    }
    internal void Update(int count)
    {
        text.Text = PigeonUnread.Label(count);
        Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    internal string DisplayText => text.Text;
}

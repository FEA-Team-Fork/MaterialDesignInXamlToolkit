using System.Windows;

namespace MaterialDesignThemes.Wpf
{
    public class BannerMessageEventArgs : RoutedEventArgs
    {
        public BannerMessageEventArgs(BannerMessage message)
        {
            Message = message;
        }

        public BannerMessageEventArgs(RoutedEvent routedEvent, BannerMessage message) : base(routedEvent)
        {
            Message = message;
        }

        public BannerMessageEventArgs(RoutedEvent routedEvent, object source, BannerMessage message) : base(routedEvent, source)
        {
            Message = message;
        }

        public BannerMessage Message { get; }
    }
}
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MaterialDesignThemes.Wpf
{
    public enum BannerActionButtonPlacementMode
    {
        Auto,
        Inline,
        SeparateLine
    }

    /// <summary>
    /// Implements a <see cref="Banner"/> inspired by the Material Design specs (https://material.io/components/banners/).
    /// </summary>
    [ContentProperty(nameof(Message))]
    public class Banner : Control
    {
        private const string ActivateStoryboardName = "ActivateStoryboard";
        private const string DeactivateStoryboardName = "DeactivateStoryboard";

        private Action? _messageQueueRegistrationCleanUp = null;

        static Banner()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(Banner), new FrameworkPropertyMetadata(typeof(Banner)));
        }

        public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
            nameof(Message), typeof(BannerMessage), typeof(Banner), new PropertyMetadata(default(BannerMessage)));

        public BannerMessage? Message
        {
            get { return (BannerMessage?) GetValue(MessageProperty); }
            set { SetValue(MessageProperty, value); }
        }

        public static readonly DependencyProperty MessageQueueProperty = DependencyProperty.Register(
            nameof(MessageQueue), typeof(BannerMessageQueue), typeof(Banner), new PropertyMetadata(default(BannerMessageQueue), MessageQueuePropertyChangedCallback),
            MessageQueueValidateValueCallback);

        private static void MessageQueuePropertyChangedCallback(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs dependencyPropertyChangedEventArgs)
        {
            var banner = (Banner) dependencyObject;
            banner._messageQueueRegistrationCleanUp?.Invoke();
            var messageQueue = dependencyPropertyChangedEventArgs.NewValue as BannerMessageQueue;
            banner._messageQueueRegistrationCleanUp = messageQueue?.Pair(banner);
        }

		private static bool MessageQueueValidateValueCallback(object value)
        {
            if (value is null || ((BannerMessageQueue)value).Dispatcher == Dispatcher.CurrentDispatcher)
                return true;
            throw new ArgumentException("BannerMessageQueue must be created by the same thread.", nameof(value));
        }

		public BannerMessageQueue? MessageQueue
        {
            get => (BannerMessageQueue?) GetValue(MessageQueueProperty);
            set => SetValue(MessageQueueProperty, value);
        }

        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(Banner), new PropertyMetadata(default(bool), IsActivePropertyChangedCallback));

        public bool IsActive
        {
            get => (bool) GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public event RoutedPropertyChangedEventHandler<bool> IsActiveChanged
        {
            add => AddHandler(IsActiveChangedEvent, value);
            remove => RemoveHandler(IsActiveChangedEvent, value);
        }

        public static readonly RoutedEvent IsActiveChangedEvent = EventManager.RegisterRoutedEvent(
            nameof(IsActiveChanged), RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<bool>), typeof(Banner));

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var instance = d as Banner;
            var args = new RoutedPropertyChangedEventArgs<bool>((bool) e.OldValue, (bool) e.NewValue)
            {
                RoutedEvent = IsActiveChangedEvent
            };
            instance?.RaiseEvent(args);
        }

        public static readonly RoutedEvent DeactivateStoryboardCompletedEvent = EventManager.RegisterRoutedEvent(
            nameof(DeactivateStoryboardCompleted), RoutingStrategy.Bubble, typeof(BannerMessageEventArgs), typeof(Banner));

        public event RoutedPropertyChangedEventHandler<BannerMessage> DeactivateStoryboardCompleted
        {
            add => AddHandler(DeactivateStoryboardCompletedEvent, value);
            remove => RemoveHandler(DeactivateStoryboardCompletedEvent, value);
        }

        private static void OnDeactivateStoryboardCompleted(IInputElement banner, BannerMessage message)
        {
            var args = new BannerMessageEventArgs(DeactivateStoryboardCompletedEvent, message);
            banner.RaiseEvent(args);
        }

        public TimeSpan ActivateStoryboardDuration { get; private set; }

        public TimeSpan DeactivateStoryboardDuration { get; private set; }

        public static readonly DependencyProperty ActionButtonStyleProperty = DependencyProperty.Register(
            nameof(ActionButtonStyle), typeof(Style), typeof(Banner), new PropertyMetadata(default(Style)));

        public Style? ActionButtonStyle
        {
            get => (Style?) GetValue(ActionButtonStyleProperty);
            set => SetValue(ActionButtonStyleProperty, value);
        }

        public override void OnApplyTemplate()
        {
            //we regards to notification of deactivate storyboard finishing,
            //we either build a storyboard in code and subscribe to completed event, 
            //or take the not 100% proof of the storyboard duration from the storyboard itself
            //...HOWEVER...we can both methods result can work under the same public API so 
            //we can flip the implementation if this version does not pan out

            //(currently we have no even on the activate animation; don't 
            // need it just now, but it would mirror the deactivate)

            ActivateStoryboardDuration = GetStoryboardResourceDuration(ActivateStoryboardName);
            DeactivateStoryboardDuration = GetStoryboardResourceDuration(DeactivateStoryboardName);

            base.OnApplyTemplate();
        }

        private TimeSpan GetStoryboardResourceDuration(string resourceName)
        {
            var storyboard = Template.Resources.Contains(resourceName)
                ? (Storyboard) Template.Resources[resourceName]
                : null;

            return storyboard != null && storyboard.Duration.HasTimeSpan
                ? storyboard.Duration.TimeSpan
                : new Func<TimeSpan>(() =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Warning, no Duration was specified at root of storyboard '{resourceName}'.");
                    return TimeSpan.Zero;
                })();
        }

        private static void IsActivePropertyChangedCallback(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs dependencyPropertyChangedEventArgs)
        {
            OnIsActiveChanged(dependencyObject, dependencyPropertyChangedEventArgs);

            if ((bool) dependencyPropertyChangedEventArgs.NewValue) return;

            var banner = (Banner) dependencyObject;
            if (banner.Message is null) return;

            var dispatcherTimer = new DispatcherTimer
            {
                Tag = new Tuple<Banner, BannerMessage>(banner, banner.Message),
                Interval = banner.DeactivateStoryboardDuration
            };
            dispatcherTimer.Tick += DeactivateStoryboardDispatcherTimerOnTick;
            dispatcherTimer.Start();
        }

        private static void DeactivateStoryboardDispatcherTimerOnTick(object? sender, EventArgs eventArgs)
        {
            if (sender is DispatcherTimer dispatcherTimer)
            {
                dispatcherTimer.Stop();
                dispatcherTimer.Tick -= DeactivateStoryboardDispatcherTimerOnTick;
                var source = (Tuple<Banner, BannerMessage>)dispatcherTimer.Tag;
                OnDeactivateStoryboardCompleted(source.Item1, source.Item2);
            }
        }

        public static readonly DependencyProperty ActionButtonPlacementProperty = DependencyProperty.Register(
            nameof(ActionButtonPlacement), typeof(BannerActionButtonPlacementMode), typeof(Banner), new PropertyMetadata(BannerActionButtonPlacementMode.Auto));

        public BannerActionButtonPlacementMode ActionButtonPlacement
        {
            get => (BannerActionButtonPlacementMode) GetValue(ActionButtonPlacementProperty);
            set => SetValue(ActionButtonPlacementProperty, value);
        }
    }
}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MaterialDesignThemes.Wpf
{
    public class BannerMessageQueue : IBannerMessageQueue, IDisposable
    {
        private readonly TimeSpan _messageDuration;
        private readonly HashSet<Banner> _pairedBanners = new HashSet<Banner>();
        private readonly LinkedList<BannerMessageQueueItem> _bannerMessages = new LinkedList<BannerMessageQueueItem>();
        private readonly ManualResetEvent _disposedEvent = new ManualResetEvent(false);
        private readonly ManualResetEvent _pausedEvent = new ManualResetEvent(false);
        private readonly ManualResetEvent _messageWaitingEvent = new ManualResetEvent(false);
        private Tuple<BannerMessageQueueItem, DateTime> _latestShownItem;
        private int _pauseCounter;
        private bool _isDisposed;

        #region private class MouseNotOverManagedWaitHandle : IDisposable

        private class MouseNotOverManagedWaitHandle : IDisposable
        {
            private readonly ManualResetEvent _waitHandle;
            private readonly ManualResetEvent _disposedWaitHandle = new ManualResetEvent(false);
            private Action _cleanUp;
            private bool _isWaitHandleDisposed;
            private readonly object _waitHandleGate = new object();

            public MouseNotOverManagedWaitHandle(UIElement uiElement)
            {
                if (uiElement == null) throw new ArgumentNullException(nameof(uiElement));

                _waitHandle = new ManualResetEvent(!uiElement.IsMouseOver);
                uiElement.MouseEnter += UiElementOnMouseEnter;
                uiElement.MouseLeave += UiElementOnMouseLeave;

                _cleanUp = () =>
                {
                    uiElement.MouseEnter -= UiElementOnMouseEnter;
                    uiElement.MouseLeave -= UiElementOnMouseLeave;
                    lock (_waitHandleGate)
                    {
                        _waitHandle.Dispose();
                        _isWaitHandleDisposed = true;
                    }
                    _disposedWaitHandle.Set();
                    _disposedWaitHandle.Dispose();
                    _cleanUp = () => { };
                };
            }

            public WaitHandle WaitHandle => _waitHandle;

            private void UiElementOnMouseLeave(object sender, MouseEventArgs mouseEventArgs)
            {
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        _disposedWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                    }
                    catch (ObjectDisposedException)
                    {
                        /* we are we suppresing this? 
                         * as we have switched out wait onto another thread, so we don't block the UI thread, the
                         * _cleanUp/Dispose() action might also happen, and the _disposedWaitHandle might get disposed
                         * just before we WaitOne. We wond add a lock in the _cleanUp because it might block for 2 seconds.
                         * We could use a Monitor.TryEnter in _cleanUp and run clean up after but oh my gosh it's just getting
                         * too complicated for this use case, so for the rare times this happens, we can swallow safely                         
                         */
                    }

                }).ContinueWith(t =>
                {
                    if (((UIElement) sender).IsMouseOver) return;
                    lock (_waitHandleGate)
                    {
                        if (!_isWaitHandleDisposed)
                            _waitHandle.Set();
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }

            private void UiElementOnMouseEnter(object sender, MouseEventArgs mouseEventArgs)
            {
                _waitHandle.Reset();
            }

            public void Dispose()
            {
                _cleanUp();
            }
        }

        #endregion

        #region private class DurationMonitor

        private class DurationMonitor
        {
            private DateTime _completionTime;

            private DurationMonitor(
                TimeSpan minimumDuration,
                WaitHandle pausedWaitHandle,
                EventWaitHandle signalWhenDurationPassedWaitHandle,
                WaitHandle ceaseWaitHandle)
            {
                if (pausedWaitHandle == null) throw new ArgumentNullException(nameof(pausedWaitHandle));
                if (signalWhenDurationPassedWaitHandle == null)
                    throw new ArgumentNullException(nameof(signalWhenDurationPassedWaitHandle));
                if (ceaseWaitHandle == null) throw new ArgumentNullException(nameof(ceaseWaitHandle));

                _completionTime = DateTime.Now.Add(minimumDuration);

                //this keeps the event waiting simpler, rather that actually watching play -> pause -> play -> pause etc
                var granularity = TimeSpan.FromMilliseconds(200);

                Task.Factory.StartNew(() =>
                {
                    //keep upping the completion time in case it's paused...
                    while (DateTime.Now < _completionTime && !ceaseWaitHandle.WaitOne(granularity))
                    {
                        if (pausedWaitHandle.WaitOne(TimeSpan.Zero))
                        {
                            _completionTime = _completionTime.Add(granularity);
                        }
                    }

                    if (DateTime.Now >= _completionTime)
                        signalWhenDurationPassedWaitHandle.Set();
                });
            }

            public static DurationMonitor Start(TimeSpan minimumDuration,
                WaitHandle pausedWaitHandle,
                EventWaitHandle signalWhenDurationPassedWaitHandle,
                WaitHandle ceaseWaitHandle)
            {
                return new DurationMonitor(minimumDuration, pausedWaitHandle, signalWhenDurationPassedWaitHandle,
                    ceaseWaitHandle);
            }
        }

        #endregion

        public BannerMessageQueue() : this(TimeSpan.FromSeconds(30))
        {
        }

        public BannerMessageQueue(TimeSpan messageDuration)
        {
            _messageDuration = messageDuration;
            Task.Factory.StartNew(PumpAsync);
        }

        //oh if only I had Disposable.Create in this lib :)  tempted to copy it in like dragabalz, 
        //but this is an internal method so no one will know my direty Action disposer...
        internal Action Pair(Banner banner)
        {
            if (banner == null) throw new ArgumentNullException(nameof(banner));

            _pairedBanners.Add(banner);

            return () => _pairedBanners.Remove(banner);
        }

        internal Action Pause()
        {
            if (_isDisposed) return () => { };

            if (Interlocked.Increment(ref _pauseCounter) == 1)
                _pausedEvent.Set();

            return () =>
            {
                if (Interlocked.Decrement(ref _pauseCounter) == 0)
                    _pausedEvent.Reset();
            };
        }

        /// <summary>
        /// Gets or sets a value that indicates whether this message queue displays messages without discarding duplicates. 
        /// True to show every message even if there are duplicates.
        /// </summary>
        public bool IgnoreDuplicate { get; set; }

        public void Enqueue(object content)
        {
            Enqueue(content, false);
        }

        public void Enqueue(object content, bool neverConsiderToBeDuplicate)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));

            Enqueue(content, null, null, null, false, neverConsiderToBeDuplicate);
        }

        public void Enqueue(object content, object actionContent, Action actionHandler)
        {
            Enqueue(content, actionContent, actionHandler, false);
        }

        public void Enqueue(object content, object actionContent, Action actionHandler, bool promote)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (actionContent == null) throw new ArgumentNullException(nameof(actionContent));
            if (actionHandler == null) throw new ArgumentNullException(nameof(actionHandler));
            
            Enqueue(content, actionContent, _ => actionHandler(), promote, false, false);
        }

        public void Enqueue<TArgument>(object content, object actionContent, Action<TArgument> actionHandler,
            TArgument actionArgument)
        {
            Enqueue(content, actionContent, actionHandler, actionArgument, false, false);
        }

        public void Enqueue<TArgument>(object content, object actionContent, Action<TArgument> actionHandler,
            TArgument actionArgument, bool promote) =>
            Enqueue(content, actionContent, actionHandler, actionArgument, promote, promote);

        public void Enqueue<TArgument>(object content, object actionContent, Action<TArgument> actionHandler,
            TArgument actionArgument, bool promote, bool neverConsiderToBeDuplicate, TimeSpan? durationOverride = null)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));

            if (actionContent == null ^ actionHandler == null)
            {
                throw new ArgumentException("All action arguments must be provided if any are provided.",
                    actionContent != null ? nameof(actionContent) : nameof(actionHandler));
            }

            Action<object> handler = actionHandler != null
                ? new Action<object>(argument => actionHandler((TArgument)argument))
                : null;
            Enqueue(content, actionContent, handler, actionArgument, promote, neverConsiderToBeDuplicate);
        }

        public void Enqueue(object content, object actionContent, Action<object> actionHandler,
            object actionArgument, bool promote, bool neverConsiderToBeDuplicate, TimeSpan? durationOverride = null)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));

            if (actionContent == null ^ actionHandler == null)
            {
                throw new ArgumentException("All action arguments must be provided if any are provided.",
                    actionContent != null ? nameof(actionContent) : nameof(actionHandler));
            }

            var bannerMessageQueueItem = new BannerMessageQueueItem(content, durationOverride ?? _messageDuration,
                actionContent, actionHandler, actionArgument, promote, neverConsiderToBeDuplicate);
            if (promote)
                InsertAsLastNotPromotedNode(bannerMessageQueueItem);
            else
                _bannerMessages.AddLast(bannerMessageQueueItem);

            _messageWaitingEvent.Set();
        }

        private void InsertAsLastNotPromotedNode(BannerMessageQueueItem bannerMessageQueueItem)
        {
            var node = _bannerMessages.First;
            while (node != null)
            {
                if (!node.Value.IsPromoted)
                {
                    _bannerMessages.AddBefore(node, bannerMessageQueueItem);
                    return;
                }
                node = node.Next;
            }
            _bannerMessages.AddLast(bannerMessageQueueItem);
        }

        private async void PumpAsync()
        {
            while (!_isDisposed)
            {
                var eventId = WaitHandle.WaitAny(new WaitHandle[] { _disposedEvent, _messageWaitingEvent });
                if (eventId == 0) continue;
                var exemplar = _pairedBanners.FirstOrDefault();
                if (exemplar == null)
                {
                    Trace.TraceWarning(
                        "A banner message as waiting, but no Banner instances are assigned to the message queue.");
                    _disposedEvent.WaitOne(TimeSpan.FromSeconds(1));
                    continue;
                }

                //find a target
                var banner = await FindBanner(exemplar.Dispatcher);

                //show message
                if (banner != null)
                {
                    var message = _bannerMessages.First.Value;
                    _bannerMessages.RemoveFirst();
                    if (_latestShownItem == null
                        || IgnoreDuplicate
                        || message.IgnoreDuplicate
                        || !Equals(_latestShownItem.Item1.Content, message.Content)
                        || !Equals(_latestShownItem.Item1.ActionContent, message.ActionContent)
                        || _latestShownItem.Item2 <= DateTime.Now.Subtract(_latestShownItem.Item1.Duration))
                    {
                        await ShowAsync(banner, message);
                        _latestShownItem = new Tuple<BannerMessageQueueItem, DateTime>(message, DateTime.Now);
                    }
                }
                else
                {
                    //no banner could be found, take a break
                    _disposedEvent.WaitOne(TimeSpan.FromSeconds(1));
                }

                if (_bannerMessages.Count > 0)
                    _messageWaitingEvent.Set();
                else
                    _messageWaitingEvent.Reset();
            }
        }

        private DispatcherOperation<Banner> FindBanner(Dispatcher dispatcher)
        {
            return dispatcher.InvokeAsync(() =>
            {
                return _pairedBanners.FirstOrDefault(sb =>
                {
                    if (!sb.IsLoaded || sb.Visibility != Visibility.Visible) return false;
                    var window = Window.GetWindow(sb);
                    return window?.WindowState != WindowState.Minimized;
                });
            });
        }

        private async Task ShowAsync(Banner banner, BannerMessageQueueItem messageQueueItem)
        {
            await Task.Run(async () =>
                {
                    //create and show the message, setting up all the handles we need to wait on
                    var actionClickWaitHandle = new ManualResetEvent(false);
                    var mouseNotOverManagedWaitHandle =
                        await
                            banner.Dispatcher.InvokeAsync(
                                () => CreateAndShowMessage(banner, messageQueueItem, actionClickWaitHandle));
                    var durationPassedWaitHandle = new ManualResetEvent(false);
                    DurationMonitor.Start(messageQueueItem.Duration.Add(banner.ActivateStoryboardDuration),
                        _pausedEvent, durationPassedWaitHandle, _disposedEvent);

                    //wait until time span completed (including pauses and mouse overs), or the action is clicked
                    await WaitForCompletionAsync(mouseNotOverManagedWaitHandle, durationPassedWaitHandle, actionClickWaitHandle);

                    //close message on banner
                    await
                        banner.Dispatcher.InvokeAsync(
                            () => banner.SetCurrentValue(Banner.IsActiveProperty, false));

                    //we could wait for the animation event, but just doing 
                    //this for now...at least it is prevent extra call back hell
                    _disposedEvent.WaitOne(banner.DeactivateStoryboardDuration);

                    //remove message on banner
                    await banner.Dispatcher.InvokeAsync(
                        () => banner.SetCurrentValue(Banner.MessageProperty, null));

                    mouseNotOverManagedWaitHandle.Dispose();
                    durationPassedWaitHandle.Dispose();

                })
                .ContinueWith(t =>
                {
                    if (t.Exception == null) return;

                    var exc = t.Exception.InnerExceptions.FirstOrDefault() ?? t.Exception;
                    Trace.WriteLine("Error occured whilst showing Banner, exception will be rethrown.");
                    Trace.WriteLine($"{exc.Message} ({exc.GetType().FullName})");
                    Trace.WriteLine(exc.StackTrace);

                    throw t.Exception;
                });
        }

        private static MouseNotOverManagedWaitHandle CreateAndShowMessage(UIElement banner,
            BannerMessageQueueItem messageQueueItem, EventWaitHandle actionClickWaitHandle)
        {
            var clickCount = 0;
            var bannerMessage = Create(messageQueueItem);
            bannerMessage.ActionClick += (sender, args) =>
            {
                if (++clickCount == 1)
                    DoActionCallback(messageQueueItem);
                actionClickWaitHandle.Set();
            };
            banner.SetCurrentValue(Banner.MessageProperty, bannerMessage);
            banner.SetCurrentValue(Banner.IsActiveProperty, true);
            return new MouseNotOverManagedWaitHandle(banner);
        }

        private static async Task WaitForCompletionAsync(
            MouseNotOverManagedWaitHandle mouseNotOverManagedWaitHandle,
            WaitHandle durationPassedWaitHandle, WaitHandle actionClickWaitHandle)
        {
            await Task.WhenAny(
                Task.Factory.StartNew(() =>
                {
                    WaitHandle.WaitAll(new[]
                    {
                        mouseNotOverManagedWaitHandle.WaitHandle,
                        durationPassedWaitHandle
                    });
                }),
                Task.Factory.StartNew(actionClickWaitHandle.WaitOne));
        }

        private static void DoActionCallback(BannerMessageQueueItem messageQueueItem)
        {
            try
            {
                messageQueueItem.ActionHandler(messageQueueItem.ActionArgument);

            }
            catch (Exception exc)
            {
                Trace.WriteLine("Error during BannerMessageQueue message action callback, exception will be rethrown.");
                Trace.WriteLine($"{exc.Message} ({exc.GetType().FullName})");
                Trace.WriteLine(exc.StackTrace);

                throw;
            }
        }

        private static BannerMessage Create(BannerMessageQueueItem messageQueueItem)
        {
            return new BannerMessage {
                Content = messageQueueItem.Content,
                ActionContent = messageQueueItem.ActionContent
            };
        }

        public void Dispose()
        {
            _isDisposed = true;
            _disposedEvent.Set();
            _disposedEvent.Dispose();
            _pausedEvent.Dispose();
        }
    }
}
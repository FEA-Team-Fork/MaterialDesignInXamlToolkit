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
        private readonly Dispatcher _dispatcher;
        private readonly TimeSpan _messageDuration;
        private readonly HashSet<Banner> _pairedBanners = new HashSet<Banner>();
        private readonly LinkedList<BannerMessageQueueItem> _bannerMessages = new LinkedList<BannerMessageQueueItem>();
        private readonly object _bannerMessagesLock = new object();
        private readonly ManualResetEvent _disposedEvent = new ManualResetEvent(false);
        private readonly ManualResetEvent _pausedEvent = new ManualResetEvent(false);
        private readonly SemaphoreSlim _showMessageSemaphore = new SemaphoreSlim(1, 1);
        
        private int _pauseCounter;
        private bool _isDisposed;

		/// <summary>
        /// If set, the active banner will be closed.
        /// </summary>
        /// <remarks>
        /// Available only while the banner is displayed.
        /// Should be locked by <see cref="_bannerMessagesLock"/>.
        /// </remarks>
        private ManualResetEvent? _closeBannerEvent;

        /// <summary>
        /// Gets the <see cref="System.Windows.Threading.Dispatcher"/> this <see cref="BannerMessageQueue"/> is associated with.
        /// </summary>
        internal Dispatcher Dispatcher => _dispatcher;
        
		#region MouseNotOverManagedWaitHandle

        private class MouseNotOverManagedWaitHandle : IDisposable
        {
			private readonly UIElement _uiElement;
            private readonly ManualResetEvent _waitHandle;
            private readonly ManualResetEvent _disposedWaitHandle = new ManualResetEvent(false);
            private bool _isDisposed;
            private readonly object _waitHandleGate = new object();

            public MouseNotOverManagedWaitHandle(UIElement uiElement)
            {
                _uiElement = uiElement ?? throw new ArgumentNullException(nameof(uiElement));

                _waitHandle = new ManualResetEvent(!uiElement.IsMouseOver);
                uiElement.MouseEnter += UiElementOnMouseEnter;
                uiElement.MouseLeave += UiElementOnMouseLeave;
            }

            public EventWaitHandle WaitHandle => _waitHandle;
			
			private void UiElementOnMouseEnter(object sender, MouseEventArgs mouseEventArgs) => _waitHandle.Reset();

            private async void UiElementOnMouseLeave(object sender, MouseEventArgs mouseEventArgs)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        _disposedWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                    }
                    catch (ObjectDisposedException)
                    {
                        /* we are we suppressing this? 
                         * as we have switched out wait onto another thread, so we don't block the UI thread, the
                         * _cleanUp/Dispose() action might also happen, and the _disposedWaitHandle might get disposed
                         * just before we WaitOne. We won't add a lock in the _cleanUp because it might block for 2 seconds.
                         * We could use a Monitor.TryEnter in _cleanUp and run clean up after but oh my gosh it's just getting
                         * too complicated for this use case, so for the rare times this happens, we can swallow safely                         
                         */
                    }

                });
				if (((UIElement)sender).IsMouseOver) return;
                lock (_waitHandleGate)
                {
                    if (!_isDisposed)
                        _waitHandle.Set();
                }
            }

            public void Dispose()
            {
                if (_isDisposed)
                    return;

				_uiElement.MouseEnter -= UiElementOnMouseEnter;
                _uiElement.MouseLeave -= UiElementOnMouseLeave;
                lock (_waitHandleGate)
				{
                    _waitHandle.Dispose();
                    _isDisposed = true;
                }
                _disposedWaitHandle.Set();
                _disposedWaitHandle.Dispose();
            }
        }

        #endregion


        public BannerMessageQueue() : this(TimeSpan.FromSeconds(30))
        {
        }

        public BannerMessageQueue(TimeSpan messageDuration)
			: this(messageDuration, Dispatcher.FromThread(Thread.CurrentThread)
                          ?? throw new InvalidOperationException("BannerMessageQueue must be created in a dispatcher thread"))
        { }
        public BannerMessageQueue(TimeSpan messageDuration, Dispatcher dispatcher)
        {
            _messageDuration = messageDuration;
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        //oh if only I had Disposable.Create in this lib :)  tempted to copy it in like dragabalz,
        //but this is an internal method so no one will know my dirty Action disposer...
        internal Action Pair(Banner banner)
        {
            if (banner is null) throw new ArgumentNullException(nameof(banner));

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

        public void Enqueue(object content) => Enqueue(content, false);

        public void Enqueue(object content, bool neverConsiderToBeDuplicate)
        	=> Enqueue(content, null, null, null, false, neverConsiderToBeDuplicate);

        public void Enqueue(object content, object? actionContent, Action? actionHandler)
            => Enqueue(content, actionContent, actionHandler, false);

        public void Enqueue(object content, object? actionContent, Action? actionHandler, bool promote)
            => Enqueue(content, actionContent, _ => actionHandler?.Invoke(), promote, false, false);

        public void Enqueue<TArgument>(object content, object? actionContent, Action<TArgument?>? actionHandler,
            TArgument? actionArgument)
            => Enqueue(content, actionContent, actionHandler, actionArgument, false, false);

        public void Enqueue<TArgument>(object content, object? actionContent, Action<TArgument?>? actionHandler,
            TArgument? actionArgument, bool promote) =>
            Enqueue(content, actionContent, actionHandler, actionArgument, promote, promote);

        public void Enqueue<TArgument>(object content, object? actionContent, Action<TArgument?>? actionHandler,
            TArgument? actionArgument, bool promote, bool neverConsiderToBeDuplicate, TimeSpan? durationOverride = null)
        {
            if (content is null) throw new ArgumentNullException(nameof(content));

            if (actionContent is null ^ actionHandler is null)
            {
                throw new ArgumentException("All action arguments must be provided if any are provided.",
                    actionContent != null ? nameof(actionContent) : nameof(actionHandler));
            }

            Action<object?>? handler = actionHandler != null
                ? new Action<object?>(argument => actionHandler((TArgument?)argument))
                : null;
            Enqueue(content, actionContent, handler, actionArgument, promote, neverConsiderToBeDuplicate, durationOverride);
        }

        public void Enqueue(object content, object? actionContent, Action<object?>? actionHandler,
            object? actionArgument, bool promote, bool neverConsiderToBeDuplicate, TimeSpan? durationOverride = null)
        {
            if (content is null) throw new ArgumentNullException(nameof(content));

            if (actionContent is null ^ actionHandler is null)
            {
                throw new ArgumentException("All action arguments must be provided if any are provided.",
                    actionContent != null ? nameof(actionContent) : nameof(actionHandler));
            }

            var bannerMessageQueueItem = new BannerMessageQueueItem(content, durationOverride ?? _messageDuration,
                actionContent, actionHandler, actionArgument, promote, neverConsiderToBeDuplicate);
            InsertItem(bannerMessageQueueItem);
        }

        private void InsertItem(BannerMessageQueueItem item)
        {
            lock (_bannerMessagesLock)
            {
                var added = false;
                var node = _bannerMessages.First;
                while (node != null)
                {
                    if (!IgnoreDuplicate && item.IsDuplicate(node.Value))
                        return;

                    if (item.IsPromoted && !node.Value.IsPromoted)
                    {
                        _bannerMessages.AddBefore(node, item);
                        added = true;
                        break;
                    }
                    node = node.Next;
                }
                if (!added)
                    _bannerMessages.AddLast(item);

            }

            _dispatcher.InvokeAsync(ShowNextAsync);
        }
		/// <summary>
        /// Clear the message queue and close the active banner.
        /// This method can be called from any thread.
        /// </summary>
        public void Clear()
        {
            lock (_bannerMessagesLock)
            {
                _bannerMessages.Clear();
                _closeBannerEvent?.Set();
            }
        }
            

        private void StartDuration(TimeSpan minimumDuration, EventWaitHandle durationPassedWaitHandle)
        {
            if (durationPassedWaitHandle is null) throw new ArgumentNullException(nameof(durationPassedWaitHandle));

            var completionTime = DateTime.Now.Add(minimumDuration);

            //this keeps the event waiting simpler, rather that actually watching play -> pause -> play -> pause etc
            var granularity = TimeSpan.FromMilliseconds(200);

            Task.Run(() =>
            {
                while (true)
				{
                    if (DateTime.Now >= completionTime) // time is over
                    {
                        durationPassedWaitHandle.Set();
                        break;
                    }

                    if (_disposedEvent.WaitOne(granularity)) // queue is disposed
                        break;

                	if (durationPassedWaitHandle.WaitOne(TimeSpan.Zero)) // manual exit (like message action click)
                        break;

                    if (_pausedEvent.WaitOne(TimeSpan.Zero)) // on pause completion time is extended
                        completionTime = completionTime.Add(granularity);
                }
            });
        }



private async Task ShowNextAsync()
        {
            await _showMessageSemaphore.WaitAsync()
                .ConfigureAwait(true);
            try
            {
                Banner banner;
                while (true)
                {
                    if (_isDisposed || _dispatcher.HasShutdownStarted)
                        return;

                    banner = FindBanner();
                    if (banner != null)
                        break;

                    Trace.TraceWarning("A banner message is waiting, but no banner instances are assigned to the message queue.");
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
                }

                LinkedListNode<BannerMessageQueueItem>? messageNode;
                lock (_bannerMessagesLock)
                {
                    messageNode = _bannerMessages.First;
                    if (messageNode is null)
                        return;
                    _closeBannerEvent = new ManualResetEvent(false);
                }

                await ShowAsync(banner, messageNode.Value, _closeBannerEvent)
                    .ConfigureAwait(false);

                lock (_bannerMessagesLock)
                {
                    if (messageNode.List == _bannerMessages)    // Check if it has not been cleared.
                        _bannerMessages.Remove(messageNode);
                    _closeBannerEvent.Dispose();
                    _closeBannerEvent = null;
                }
            }
            finally
            {
                _showMessageSemaphore.Release();
            }

            Banner FindBanner() => _pairedBanners.FirstOrDefault(sb =>
            {
                if (!sb.IsLoaded || sb.Visibility != Visibility.Visible) return false;
                var window = Window.GetWindow(sb);
                return window?.WindowState != WindowState.Minimized;
            });
        }

        private async Task ShowAsync(Banner banner, BannerMessageQueueItem messageQueueItem, ManualResetEvent actionClickWaitHandle)
        {
            //create and show the message, setting up all the handles we need to wait on
            var tuple = CreateAndShowMessage(banner, messageQueueItem, actionClickWaitHandle);
            var bannerMessage = tuple.Item1;
            var mouseNotOverManagedWaitHandle = tuple.Item2;

            var durationPassedWaitHandle = new ManualResetEvent(false);
            StartDuration(messageQueueItem.Duration.Add(banner.ActivateStoryboardDuration), durationPassedWaitHandle);

            //wait until time span completed (including pauses and mouse overs), or the action is clicked
            await WaitForCompletionAsync(mouseNotOverManagedWaitHandle, durationPassedWaitHandle, actionClickWaitHandle);

            //close message on banner
            banner.SetCurrentValue(Banner.IsActiveProperty, false);

            //we could wait for the animation event, but just doing
            //this for now...at least it is prevent extra call back hell
            await Task.Delay(banner.DeactivateStoryboardDuration);

            //this prevents missing resource warnings after the message is removed from the Banner
            //see https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/issues/2040
            bannerMessage.Resources = BannerMessage.defaultResources;

            //remove message on banner
            banner.SetCurrentValue(Banner.MessageProperty, null);

            mouseNotOverManagedWaitHandle.Dispose();
            durationPassedWaitHandle.Dispose();
        }

        private static Tuple<BannerMessage, MouseNotOverManagedWaitHandle> CreateAndShowMessage(UIElement banner,
            BannerMessageQueueItem messageQueueItem, EventWaitHandle actionClickWaitHandle)
        {
            var clickCount = 0;
            var bannerMessage = new BannerMessage
            {
                Content = messageQueueItem.Content,
                ActionContent = messageQueueItem.ActionContent
            };
            bannerMessage.ActionClick += (sender, args) =>
            {
                if (++clickCount == 1)
                    DoActionCallback(messageQueueItem);
                actionClickWaitHandle.Set();
            };
            banner.SetCurrentValue(Banner.MessageProperty, bannerMessage);
            banner.SetCurrentValue(Banner.IsActiveProperty, true);
            return Tuple.Create(bannerMessage, new MouseNotOverManagedWaitHandle(banner));
        }

        private static async Task WaitForCompletionAsync(
            MouseNotOverManagedWaitHandle mouseNotOverManagedWaitHandle,
            EventWaitHandle durationPassedWaitHandle,
            EventWaitHandle actionClickWaitHandle)
        {
            var durationTask = Task.Run(() =>
            {
                WaitHandle.WaitAll(new WaitHandle[]
                {
                    mouseNotOverManagedWaitHandle.WaitHandle,
                    durationPassedWaitHandle
                });
            });
            var actionClickTask = Task.Run(actionClickWaitHandle.WaitOne);
            await Task.WhenAny(durationTask, actionClickTask);

            mouseNotOverManagedWaitHandle.WaitHandle.Set();
            durationPassedWaitHandle.Set();
            actionClickWaitHandle.Set();

            await Task.WhenAll(durationTask, actionClickTask);
        }

        private static void DoActionCallback(BannerMessageQueueItem messageQueueItem)
        {
            try
            {
                messageQueueItem.ActionHandler?.Invoke(messageQueueItem.ActionArgument);
            }
            catch (Exception exc)
            {
                Trace.WriteLine("Error during BannerMessageQueue message action callback, exception will be rethrown.");
                Trace.WriteLine($"{exc.Message} ({exc.GetType().FullName})");
                Trace.WriteLine(exc.StackTrace);

                throw;
            }
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
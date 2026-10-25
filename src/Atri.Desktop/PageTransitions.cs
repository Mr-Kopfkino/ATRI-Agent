using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Atri.Desktop;

/// <summary>One transition owns page opacity at a time; a newer request cancels and settles the old one.</summary>
internal sealed class PageTransitions : IDisposable
{
    private readonly IReadOnlyDictionary<string, Grid> pages;
    private readonly Border black;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? active;
    private bool disposed;
    internal string CurrentPage { get; private set; } = "title";
    internal bool IsTransitioning { get; private set; }
    internal int CompletedCount { get; private set; }
    internal int BlackTransitionCount { get; private set; }
    internal double LastBlackPeak { get; private set; }
    internal event Action? BlackFrameReached;

    internal PageTransitions(IReadOnlyDictionary<string, Grid> pages, Border black)
    {
        this.pages = pages;
        this.black = black;
        Settle();
    }

    internal Task NavigateAsync(string destination, Action<string> changed, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!pages.ContainsKey(destination)) throw new ArgumentException("未知页面。", nameof(destination));
        active?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        active = request;
        return RunAsync(destination, changed, request);
    }

    private async Task RunAsync(string destination, Action<string> changed, CancellationTokenSource request)
    {
        var token = request.Token;
        bool entered = false;
        try
        {
            await gate.WaitAsync(token);
            entered = true;
            token.ThrowIfCancellationRequested();
            if (destination == CurrentPage) return;
            IsTransitioning = true;
            var source = pages[CurrentPage];
            var target = pages[destination];
            if (CurrentPage == "title" && destination is "chat" or "system")
            {
                BlackTransitionCount++;
                black.Visibility = Visibility.Visible;
                black.IsHitTestVisible = true;
                await FadeAsync(black, 0, 1, 180, token);
                LastBlackPeak = black.Opacity;
                source.Visibility = Visibility.Collapsed;
                target.Visibility = Visibility.Visible;
                target.Opacity = 1;
                CurrentPage = destination;
                changed(destination);
                // Commit only after an actual opaque black frame has been presented.
                await black.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                BlackFrameReached?.Invoke();
                await Task.Delay(35, token);
                await FadeAsync(black, 1, 0, 220, token);
            }
            else
            {
                target.Visibility = Visibility.Visible;
                target.Opacity = 0;
                CurrentPage = destination;
                changed(destination);
                await Task.WhenAll(FadeAsync(source, 1, 0, 240, token), FadeAsync(target, 0, 1, 240, token));
            }
            token.ThrowIfCancellationRequested();
            CompletedCount++;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (entered) { Settle(); IsTransitioning = false; gate.Release(); }
            if (ReferenceEquals(active, request)) active = null;
            request.Dispose();
        }
    }

    private void Settle()
    {
        foreach (var page in pages)
        {
            page.Value.BeginAnimation(UIElement.OpacityProperty, null);
            page.Value.Opacity = 1;
            page.Value.Visibility = page.Key == CurrentPage ? Visibility.Visible : Visibility.Collapsed;
        }
        black.BeginAnimation(UIElement.OpacityProperty, null);
        black.Opacity = 0;
        black.Visibility = Visibility.Collapsed;
        black.IsHitTestVisible = false;
    }

    private static async Task FadeAsync(UIElement element, double from, double to, int milliseconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        EventHandler ended = (_, _) => completed.TrySetResult();
        animation.Completed += ended;
        using var cancellation = token.Register(() => completed.TrySetCanceled(token));
        element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        try { await completed.Task; }
        finally { animation.Completed -= ended; element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = to; }
    }

    public void Dispose() { if (disposed) return; disposed = true; active?.Cancel(); }
}

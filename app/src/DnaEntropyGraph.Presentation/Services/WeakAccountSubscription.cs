using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Subscribes a page's ViewModel to <see cref="IGcpAccount.AccountChanged"/> without keeping it alive (issue #112). The account service
/// is a singleton and the page ViewModels are transient (one per page open), so a plain <c>+=</c> would keep every page ever opened
/// and run its handler for ever. The handler unsubscribes itself the first time it fires after the owner has been collected.
/// </summary>
internal static class WeakAccountSubscription
{
    /// <summary>Returns the action that ends the subscription now (a page's Detach); the weak handler still covers a page that is dropped without one.</summary>
    public static Action Attach<T>(IGcpAccount account, T owner, Action<T> onChanged)
        where T : class
    {
        var weak = new WeakReference<T>(owner);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (weak.TryGetTarget(out var target))
            {
                onChanged(target);
            }
            else
            {
                account.AccountChanged -= handler;
            }
        };
        account.AccountChanged += handler;
        return () => account.AccountChanged -= handler;
    }
}

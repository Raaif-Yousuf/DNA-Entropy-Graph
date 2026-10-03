using Google.Apis.Util.Store;

namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>Keeps nothing. The sign-in flow runs against this because the account's key (its sub) is only known after the tokens arrive; they are then written to the real store under that key.</summary>
internal sealed class DiscardingDataStore : IDataStore
{
    public Task StoreAsync<T>(string key, T value) => Task.CompletedTask;

    public Task DeleteAsync<T>(string key) => Task.CompletedTask;

    public Task<T> GetAsync<T>(string key) => Task.FromResult(default(T)!);

    public Task ClearAsync() => Task.CompletedTask;
}

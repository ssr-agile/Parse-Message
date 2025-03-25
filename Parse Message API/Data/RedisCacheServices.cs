using StackExchange.Redis;

public class RedisCacheServices
{
    private readonly IDatabase _database;

    public RedisCacheServices(IConnectionMultiplexer redis)
    {
        _database = redis.GetDatabase();
    }

    public async Task SetCacheAsync(string key, string value, TimeSpan? expiry = null)
    {
        await _database.StringSetAsync(key, value, expiry);
    }

    public async Task<string?> GetCacheAsync(string key)
    {
        return await _database.StringGetAsync(key);
    }

    public async Task RemoveCacheAsync(string key)
    {
        await _database.KeyDeleteAsync(key);
    }
}

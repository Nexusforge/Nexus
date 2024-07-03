// MIT License
// Copyright (c) [2024] [nexus-main]

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Nexus.Core;
using Nexus.Utilities;

namespace Nexus.Services;

internal interface IPipelineService
{
    Task<Guid> PutAsync(
        string userId,
        Pipeline pipeline);

    bool TryGet(
        string userId,
        Guid pipelineId,
        [NotNullWhen(true)] out Pipeline? token);

    Task DeleteAsync(string userId, Guid pipelineId);

    Task<List<(string, IReadOnlyDictionary<Guid, Pipeline>)>> GetAllAsync();

    Task<IReadOnlyDictionary<Guid, Pipeline>> GetAllForUserAsync(string userId);
}

internal class PipelineService(IDatabaseService databaseService)
    : IPipelineService
{
    private readonly SemaphoreSlim _semaphoreSlim = new(1, 1);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Pipeline>> _cache = new();

    private readonly IDatabaseService _databaseService = databaseService;

    public Task<Guid> PutAsync(
        string userId,
        Pipeline pipeline)
    {
        return InteractWithPipelineMapAsync(userId, pipelineMap =>
        {
            var id = Guid.NewGuid();

            pipelineMap.AddOrUpdate(
                id,
                pipeline,
                (key, _) => pipeline
            );

            return id;
        }, saveChanges: true);
    }

    public bool TryGet(
        string userId,
        Guid pipelineId,
        [NotNullWhen(true)] out Pipeline? pipeline)
    {
        var pipelineMap = GetPipelineMap(userId);

        return pipelineMap.TryGetValue(pipelineId, out pipeline);
    }

    public Task DeleteAsync(string userId, Guid pipelineId)
    {
        return InteractWithPipelineMapAsync<object?>(userId, pipelineMap =>
        {
            var pipelineEntry = pipelineMap
                .FirstOrDefault(entry => entry.Key == pipelineId);

            pipelineMap.TryRemove(pipelineEntry.Key, out _);
            return default;
        }, saveChanges: true);
    }

    public async Task<List<(string, IReadOnlyDictionary<Guid, Pipeline>)>> GetAllAsync()
    {
        var result = new List<(string, IReadOnlyDictionary<Guid, Pipeline>)>();

        foreach (var userId in _databaseService.EnumerateUsers())
        {
            var pipeline = await GetAllForUserAsync(userId);
            result.Add((userId, pipeline));
        }

        return result;
    }

    public Task<IReadOnlyDictionary<Guid, Pipeline>> GetAllForUserAsync(
        string userId)
    {
        return InteractWithPipelineMapAsync(
            userId,
            pipelineMap => (IReadOnlyDictionary<Guid, Pipeline>)pipelineMap,
            saveChanges: false);
    }

    private ConcurrentDictionary<Guid, Pipeline> GetPipelineMap(
        string userId)
    {
        return _cache.GetOrAdd(
            userId,
            key =>
            {
                if (_databaseService.TryReadPipelineMap(userId, out var jsonString))
                {
                    return JsonSerializer.Deserialize<ConcurrentDictionary<Guid, Pipeline>>(jsonString)
                        ?? throw new Exception("pipelineMap is null");
                }

                else
                {
                    return new ConcurrentDictionary<Guid, Pipeline>();
                }
            });
    }

    private async Task<T> InteractWithPipelineMapAsync<T>(
        string userId,
        Func<ConcurrentDictionary<Guid, Pipeline>, T> func,
        bool saveChanges)
    {
        await _semaphoreSlim.WaitAsync().ConfigureAwait(false);

        try
        {
            var pipelineMap = GetPipelineMap(userId);
            var result = func(pipelineMap);

            if (saveChanges)
            {
                using var stream = _databaseService.WritePipelineMap(userId);
                JsonSerializerHelper.SerializeIndented(stream, pipelineMap);
            }

            return result;
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }
}

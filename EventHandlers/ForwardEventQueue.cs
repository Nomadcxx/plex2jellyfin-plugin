namespace Plex2Jellyfin.Plugin.EventHandlers;

internal sealed record ForwardedEvent(string EventType, object Payload, string? ItemId, string? ItemPath);

// Item events share a lane by ID, keeping each item's add/remove/update order while other items run concurrently.
internal sealed class ForwardEventQueue : IDisposable
{
    internal const int WorkerCount = 4;
    internal const string ItemAddedEvent = "ItemAdded";
    internal const string ItemRemovedEvent = "ItemRemoved";
    internal const string ItemUpdatedEvent = "ItemUpdated";

    private sealed class Lane
    {
        internal readonly LinkedList<ForwardedEvent> Events = new();
        internal readonly SemaphoreSlim Available = new(0);
    }

    // ponytail: one short lock keeps coalescing and queue order atomic; shard it if enqueue contention appears.
    private readonly object _gate = new();
    private readonly Lane[] _lanes = Enumerable.Range(0, WorkerCount).Select(_ => new Lane()).ToArray();
    private readonly Dictionary<string, LinkedListNode<ForwardedEvent>> _pendingUpdates = new(StringComparer.Ordinal);
    private uint _nextLane;
    private bool _completed;
    private bool _disposed;

    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _lanes.Sum(lane => lane.Events.Count);
            }
        }
    }

    internal bool Enqueue(ForwardedEvent item)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return false;
            }

            var hasItemId = !string.IsNullOrWhiteSpace(item.ItemId);
            if (item.EventType == ItemUpdatedEvent && hasItemId && _pendingUpdates.TryGetValue(item.ItemId!, out var pending))
            {
                pending.Value = item;
                return true;
            }

            if (hasItemId && item.EventType is ItemAddedEvent or ItemRemovedEvent)
            {
                _pendingUpdates.Remove(item.ItemId!);
            }

            var lane = _lanes[ChooseLane(item.ItemId, hasItemId)];
            var node = lane.Events.AddLast(item);
            if (item.EventType == ItemUpdatedEvent && hasItemId)
            {
                _pendingUpdates[item.ItemId!] = node;
            }
            lane.Available.Release();
            return true;
        }
    }

    internal async ValueTask<ForwardedEvent?> ReadAsync(int workerIndex, CancellationToken cancellationToken)
    {
        var lane = _lanes[workerIndex];
        while (true)
        {
            await lane.Available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (lane.Events.First is { } node)
                {
                    lane.Events.RemoveFirst();
                    if (node.Value.EventType == ItemUpdatedEvent &&
                        !string.IsNullOrWhiteSpace(node.Value.ItemId) &&
                        _pendingUpdates.TryGetValue(node.Value.ItemId, out var pending) &&
                        ReferenceEquals(node, pending))
                    {
                        _pendingUpdates.Remove(node.Value.ItemId);
                    }
                    return node.Value;
                }

                if (_completed)
                {
                    return null;
                }
            }
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            foreach (var lane in _lanes)
            {
                lane.Available.Release();
            }
        }
    }

    internal static int WorkerIndexForItem(string itemId) =>
        (int)((uint)StringComparer.Ordinal.GetHashCode(itemId) % WorkerCount);

    public void Dispose()
    {
        Complete();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var lane in _lanes)
            {
                lane.Available.Dispose();
            }
        }
    }

    private int ChooseLane(string? itemId, bool hasItemId)
    {
        if (hasItemId)
        {
            return WorkerIndexForItem(itemId!);
        }

        return (int)(_nextLane++ % WorkerCount);
    }
}

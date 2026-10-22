using Plex2Jellyfin.Plugin.EventHandlers;

if (ForwardEventQueue.WorkerCount != 4)
{
    throw new InvalidOperationException($"worker count was {ForwardEventQueue.WorkerCount}, want 4");
}

const string itemId = "item-1";
using var queue = new ForwardEventQueue();
queue.Enqueue(new ForwardedEvent(ForwardEventQueue.ItemUpdatedEvent, "old", itemId, "/media/item.mkv"));
queue.Enqueue(new ForwardedEvent(ForwardEventQueue.ItemUpdatedEvent, "latest", itemId, "/media/item.mkv"));
queue.Enqueue(new ForwardedEvent(ForwardEventQueue.ItemAddedEvent, "added", itemId, "/media/item.mkv"));
queue.Enqueue(new ForwardedEvent(ForwardEventQueue.ItemRemovedEvent, "removed", itemId, "/media/item.mkv"));
queue.Enqueue(new ForwardedEvent(ForwardEventQueue.ItemUpdatedEvent, "after-remove", itemId, "/media/item.mkv"));

if (queue.PendingCount != 4)
{
    throw new InvalidOperationException($"queue had {queue.PendingCount} items, want 4 after coalescing");
}

queue.Complete();
var results = new List<string>();
var worker = ForwardEventQueue.WorkerIndexForItem(itemId);
while (await queue.ReadAsync(worker, CancellationToken.None) is { } item)
{
    results.Add($"{item.EventType}:{item.Payload}");
}

var expected = new[]
{
    "ItemUpdated:latest",
    "ItemAdded:added",
    "ItemRemoved:removed",
    "ItemUpdated:after-remove"
};
if (!results.SequenceEqual(expected))
{
    throw new InvalidOperationException($"queue order was [{string.Join(", ", results)}]");
}

Console.WriteLine("event queue self-check passed");

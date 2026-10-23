using System.Text;
using System.Text.Json;
using Plex2Jellyfin.Plugin.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Plex2Jellyfin.Plugin.EventHandlers;

public class EventForwarder : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly ITaskManager _taskManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EventForwarder> _logger;

    private static DateTime _lastProgressEventSent = DateTime.MinValue;
    private volatile ForwardEventQueue? _eventQueue;
    private CancellationTokenSource? _workerCancellation;
    private Task[] _workers = [];
    private bool _disposed;

    public EventForwarder(
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        ITaskManager taskManager,
        IMediaSourceManager mediaSourceManager,
        IHttpClientFactory httpClientFactory,
        ILogger<EventForwarder> logger)
    {
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _taskManager = taskManager;
        _mediaSourceManager = mediaSourceManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var config = Plex2JellyfinPlugin.Instance?.Configuration;
        if (config?.EnableEventForwarding != true)
        {
            _logger.LogInformation("Event forwarding is disabled");
        }

        // ponytail: Keep subscriptions and four workers alive while disabled; handlers check current config. Tie worker lifetime to config changes if four idle tasks become costly.
        var queue = new ForwardEventQueue();
        var workerCancellation = new CancellationTokenSource();
        _eventQueue = queue;
        _workerCancellation = workerCancellation;
        _workers = Enumerable.Range(0, ForwardEventQueue.WorkerCount)
            .Select(worker => ForwardWorkerAsync(queue, worker, workerCancellation.Token))
            .ToArray();

        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;

        _taskManager.TaskCompleted += OnTaskCompleted;

        _logger.LogInformation("EventForwarder started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _taskManager.TaskCompleted -= OnTaskCompleted;

        var queue = _eventQueue;
        if (queue == null)
        {
            return;
        }

        queue.Complete();
        try
        {
            await Task.WhenAll(_workers).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _workerCancellation?.Cancel();
            await Task.WhenAll(_workers);
            var pending = queue.PendingCount;
            if (pending > 0)
            {
                _logger.LogWarning(
                    "Stopped event forwarding with {PendingCount} events still queued",
                    pending);
            }
        }
        finally
        {
            _eventQueue = null;
            queue.Dispose();
            _workerCancellation?.Dispose();
            _workerCancellation = null;
            _workers = [];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (!ShouldForwardEvent(ForwardEventQueue.ItemAddedEvent)) return;
        EnqueueEvent(ForwardEventQueue.ItemAddedEvent, BuildItemPayload(e.Item), e.Item);
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (!ShouldForwardEvent(ForwardEventQueue.ItemRemovedEvent)) return;
        EnqueueEvent(ForwardEventQueue.ItemRemovedEvent, BuildItemPayload(e.Item), e.Item);
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (!ShouldForwardEvent(ForwardEventQueue.ItemUpdatedEvent)) return;
        EnqueueEvent(ForwardEventQueue.ItemUpdatedEvent, BuildItemPayload(e.Item), e.Item);
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (!ShouldForwardEvent("PlaybackStart")) return;
        EnqueueEvent("PlaybackStart", BuildPlaybackPayload(e), e.Item);
    }

    private void OnPlaybackStopped(object? sender, PlaybackProgressEventArgs e)
    {
        if (!ShouldForwardEvent("PlaybackStopped")) return;
        EnqueueEvent("PlaybackStopped", BuildPlaybackPayload(e), e.Item);
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (!ShouldForwardEvent("PlaybackProgress")) return;
        if ((DateTime.UtcNow - _lastProgressEventSent).TotalSeconds < 30) return;
        _lastProgressEventSent = DateTime.UtcNow;
        EnqueueEvent("PlaybackProgress", BuildPlaybackPayload(e), e.Item);
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (!ShouldForwardEvent("TaskCompleted")) return;
        EnqueueEvent("TaskCompleted", BuildTaskCompletedPayload(e));
    }

    private void EnqueueEvent(string eventType, object payload, BaseItem? item = null)
    {
        if (!ShouldForwardEvent(eventType)) return;

        var queue = _eventQueue;
        if (queue == null) return;

        var forwarded = new ForwardedEvent(eventType, payload, item?.Id.ToString(), item?.Path);
        if (!queue.Enqueue(forwarded))
        {
            _logger.LogWarning(
                "Dropping {EventType} event while event forwarding is stopping for item {ItemId} at {ItemPath}",
                eventType, forwarded.ItemId, forwarded.ItemPath);
        }
    }

    private static bool ShouldForwardEvent(string eventType)
    {
        return ShouldForwardEvent(Plex2JellyfinPlugin.Instance?.Configuration, eventType);
    }

    private static bool ShouldForwardEvent(PluginConfiguration? config, string eventType)
    {
        if (config?.EnableEventForwarding != true)
        {
            return false;
        }

        return eventType switch
        {
            ForwardEventQueue.ItemAddedEvent or ForwardEventQueue.ItemRemovedEvent or ForwardEventQueue.ItemUpdatedEvent => config.ForwardLibraryEvents,
            "PlaybackStart" or "PlaybackStopped" or "PlaybackProgress" => config.ForwardPlaybackEvents,
            "TaskCompleted" => true,
            _ => false
        };
    }

    private object BuildItemPayload(BaseItem item)
    {
        var hasSubtitles = false;
        try
        {
            var streams = _mediaSourceManager.GetMediaStreams(item.Id);
            hasSubtitles = streams.Any(s => s.Type == MediaStreamType.Subtitle);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not retrieve media streams for {ItemName}", item.Name);
        }

        return new
        {
            EventType = "ItemChanged",
            Timestamp = DateTime.UtcNow.ToString("O"),
            Item = new
            {
                Id = item.Id.ToString(),
                Name = item.Name,
                Path = item.Path,
                Type = item.GetType().Name,
                ProviderIds = item.ProviderIds,
                IsIdentified = item.ProviderIds.Count > 0,
                LibraryName = item.GetParent()?.Name,
                ParentId = item.ParentId.ToString(),
                HasSubtitles = hasSubtitles,
                PrimaryImagePath = item.GetImagePath(ImageType.Primary),
                DateCreated = item.DateCreated.ToString("O"),
                DateModified = item.DateModified.ToString("O")
            }
        };
    }

    private static object BuildPlaybackPayload(PlaybackProgressEventArgs e)
    {
        var item = e.Item;

        return new
        {
            EventType = "Playback",
            Timestamp = DateTime.UtcNow.ToString("O"),
            Session = new
            {
                Id = e.Session.Id,
                DeviceId = e.Session.DeviceId,
                DeviceName = e.Session.DeviceName,
                Client = e.Session.Client,
                UserId = e.Session.UserId.ToString(),
                UserName = e.Session.UserName
            },
            Item = item != null ? new
            {
                Id = item.Id.ToString(),
                Name = item.Name,
                Path = item.Path,
                Type = item.GetType().Name
            } : (object?)null,
            Playback = new
            {
                PositionTicks = e.PlaybackPositionTicks,
                DurationTicks = item?.RunTimeTicks,
                IsPaused = e.IsPaused
            }
        };
    }

    private static object BuildTaskCompletedPayload(TaskCompletionEventArgs e)
    {
        return new
        {
            EventType = "TaskCompleted",
            Timestamp = DateTime.UtcNow.ToString("O"),
            Task = new
            {
                Id = e.Task.Id.ToString(),
                Name = e.Task.Name,
                Category = e.Task.Category
            },
            Result = new
            {
                Status = e.Result.Status.ToString(),
                StartTimeUtc = e.Result.StartTimeUtc.ToString("O"),
                EndTimeUtc = e.Result.EndTimeUtc.ToString("O"),
                ErrorMessage = e.Result.ErrorMessage,
                LongErrorMessage = e.Result.LongErrorMessage
            }
        };
    }

    private async Task ForwardWorkerAsync(ForwardEventQueue queue, int workerIndex, CancellationToken cancellationToken)
    {
        try
        {
            while (await queue.ReadAsync(workerIndex, cancellationToken) is { } item)
            {
                try
                {
                    await ForwardEvent(item, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Unhandled error forwarding {EventType} for item {ItemId} at {ItemPath}",
                        item.EventType, item.ItemId, item.ItemPath);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ForwardEvent(ForwardedEvent forwarded, CancellationToken cancellationToken)
    {
        var config = Plex2JellyfinPlugin.Instance?.Configuration;
        if (config == null) return;

        if (string.IsNullOrWhiteSpace(config.SharedSecret) ||
            string.IsNullOrWhiteSpace(config.Plex2JellyfinUrl))
        {
            _logger.LogDebug(
                "Skipping {EventType}: plugin is not configured yet (missing daemon URL or shared secret)",
                forwarded.EventType);
            return;
        }

        var url = $"{config.Plex2JellyfinUrl.TrimEnd('/')}/api/v1/webhooks/jellyfin";
        var maxRetries = Math.Max(1, config.RetryCount);
        var timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds);

        var requestPayload = new
        {
            EventType = forwarded.EventType,
            Timestamp = DateTime.UtcNow.ToString("O"),
            Payload = forwarded.Payload
        };

        try
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient();
                    client.Timeout = timeout;

                    var json = JsonSerializer.Serialize(requestPayload, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });

                    using var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };

                    request.Headers.Add("X-Plex2Jellyfin-Webhook-Secret", config.SharedSecret);
                    request.Headers.Add("X-Jellyfin-Event", forwarded.EventType);

                    using var response = await client.SendAsync(request, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogDebug("Forwarded {EventType} event to Plex2Jellyfin", forwarded.EventType);
                        return;
                    }

                    _logger.LogWarning("Failed to forward {EventType} event: {StatusCode}",
                        forwarded.EventType, response.StatusCode);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error forwarding {EventType} event (attempt {Attempt}/{Max})",
                        forwarded.EventType, attempt + 1, maxRetries);
                }

                if (attempt < maxRetries - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Cancelled {EventType} forwarding for item {ItemId} at {ItemPath}",
                forwarded.EventType, forwarded.ItemId, forwarded.ItemPath);
            throw;
        }

        _logger.LogError(
            "Failed to forward {EventType} event after {MaxRetries} attempts for item {ItemId} at {ItemPath}",
            forwarded.EventType, maxRetries, forwarded.ItemId, forwarded.ItemPath);
    }
}

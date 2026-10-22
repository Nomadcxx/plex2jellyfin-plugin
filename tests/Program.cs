using System.Text.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Plex2Jellyfin.Plugin;
using Plex2Jellyfin.Plugin.Api;
using Plex2Jellyfin.Plugin.Configuration;
using Plex2Jellyfin.Plugin.EventHandlers;

var eventPolicy = typeof(EventForwarder).GetMethod(
    "ShouldForwardEvent",
    BindingFlags.Static | BindingFlags.NonPublic,
    binder: null,
    types: [typeof(PluginConfiguration), typeof(string)],
    modifiers: null)
    ?? throw new InvalidOperationException("EventForwarder should expose its event-specific forwarding policy");
var shouldForward = new Func<PluginConfiguration, string, bool>(
    (configuration, eventType) => (bool)eventPolicy.Invoke(null, [configuration, eventType])!);
var policyConfiguration = new PluginConfiguration
{
    EnableEventForwarding = true,
    ForwardLibraryEvents = false,
    ForwardPlaybackEvents = true
};
if (shouldForward(policyConfiguration, "ItemAdded") || !shouldForward(policyConfiguration, "PlaybackStart") ||
    !shouldForward(policyConfiguration, "TaskCompleted"))
{
    throw new InvalidOperationException("Forwarding should honor the current category settings");
}
policyConfiguration.ForwardLibraryEvents = true;
policyConfiguration.ForwardPlaybackEvents = false;
if (!shouldForward(policyConfiguration, "ItemAdded") || shouldForward(policyConfiguration, "PlaybackStart"))
{
    throw new InvalidOperationException("Changing category settings should take effect without restarting");
}
policyConfiguration.EnableEventForwarding = false;
if (shouldForward(policyConfiguration, "TaskCompleted"))
{
    throw new InvalidOperationException("The master setting should disable every event category");
}

var previousPlugin = Plex2JellyfinPlugin.Instance;
var plugin = (Plex2JellyfinPlugin)RuntimeHelpers.GetUninitializedObject(typeof(Plex2JellyfinPlugin));
typeof(Plex2JellyfinPlugin).GetProperty(nameof(Plex2JellyfinPlugin.Instance))!.SetValue(null, plugin);
typeof(BasePlugin<PluginConfiguration>)
    .GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!
    .SetValue(plugin, new PluginConfiguration
    {
        EnableEventForwarding = true,
        ForwardLibraryEvents = false,
        ForwardPlaybackEvents = false
    });

var eventLibraryManager = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
var eventLibraryProxy = (LibraryManagerProxy)(object)eventLibraryManager;
var eventSessionManager = DispatchProxy.Create<ISessionManager, SessionManagerProxy>();
var eventSessionProxy = (SessionManagerProxy)(object)eventSessionManager;
var eventForwarder = new EventForwarder(
    eventLibraryManager,
    eventSessionManager,
    DispatchProxy.Create<ITaskManager, TaskManagerProxy>(),
    DispatchProxy.Create<IMediaSourceManager, EmptyProxy>(),
    DispatchProxy.Create<IHttpClientFactory, EmptyProxy>(),
    NullLogger<EventForwarder>.Instance);
await eventForwarder.StartAsync(CancellationToken.None);
if (eventLibraryProxy.ItemAddedHandler is null || eventLibraryProxy.ItemRemovedHandler is null ||
    eventLibraryProxy.ItemUpdatedHandler is null || eventSessionProxy.PlaybackStartHandler is null ||
    eventSessionProxy.PlaybackStoppedHandler is null || eventSessionProxy.PlaybackProgressHandler is null)
{
    throw new InvalidOperationException("Event categories should remain subscribed so settings can change at runtime");
}
await eventForwarder.StopAsync(CancellationToken.None);
typeof(Plex2JellyfinPlugin).GetProperty(nameof(Plex2JellyfinPlugin.Instance))!.SetValue(null, previousPlugin);

var taskManager = DispatchProxy.Create<ITaskManager, TaskManagerProxy>();
((TaskManagerProxy)(object)taskManager).Tasks =
[
    CreateWorker("d7ec11ec-128a-41c0-a178-64a5f5c61a4e", "RefreshLibrary", "Refresh Media Library", TaskState.Running, 80),
    CreateWorker("b2d9cbad-6ef4-42c3-92d9-4f70cb481b99", "RefreshLibrary", "Refresh Media Library", TaskState.Idle, 0),
    CreateWorker("f5aec2c7-9b45-468b-8d44-04a933c18d62", "GenerateThumbnails", "Generate Thumbnails", TaskState.Running, 25)
];

var controller = new Plex2JellyfinController(null!, null!, null!, taskManager, null!, null!);
var result = (OkObjectResult)controller.GetActiveScans();
using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
var count = payload.RootElement.TryGetProperty("ActiveScanCount", out var pascalCaseCount)
    ? pascalCaseCount.GetInt32()
    : payload.RootElement.GetProperty("activeScanCount").GetInt32();

if (count != 1)
{
    throw new InvalidOperationException($"Expected one running refresh task, got ActiveScanCount={count}");
}

var scans = payload.RootElement.GetProperty("Scans");
if (scans.GetArrayLength() != 1 || scans[0].GetProperty("TaskId").GetString() != "d7ec11ec-128a-41c0-a178-64a5f5c61a4e" ||
    scans[0].GetProperty("ProgressPercentage").GetDouble() != 80)
{
    throw new InvalidOperationException("Active scan did not return its task identity and progress");
}

var libraryManager = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
var libraryManagerProxy = (LibraryManagerProxy)(object)libraryManager;
libraryManagerProxy.Folders =
[
    new VirtualFolderInfo { Name = "Movies", Locations = ["/movies", "/films"], ItemId = "movies-id" }
];
libraryManagerProxy.Root.Children =
[
    new Movie { Id = Guid.Parse("af147cdc-8ed9-4c3e-b7b1-c776c875264b"), Name = "In library", Path = "/movies/In library.mkv" },
    new Movie { Id = Guid.Parse("3300f506-cb7c-4660-8d03-4d504c5bcbd6"), Name = "Sibling", Path = "/movies-old/Sibling.mkv" }
];
var libraryMonitor = DispatchProxy.Create<ILibraryMonitor, LibraryMonitorProxy>();
var libraryMonitorProxy = (LibraryMonitorProxy)(object)libraryMonitor;

var refreshController = new Plex2JellyfinController(libraryMonitor, libraryManager, null!, taskManager, null!, NullLogger<Plex2JellyfinController>.Instance);
var refreshResult = refreshController.RefreshPath(new RefreshPathRequest { Path = "/movies/Film (2026)/Film.mkv" });
if (refreshResult is not AcceptedResult || libraryManagerProxy.ValidatedWholeLibrary ||
    !libraryMonitorProxy.ChangedPaths.SequenceEqual(["/movies/Film (2026)/Film.mkv"]))
{
    throw new InvalidOperationException("RefreshPath should queue its path without validating the whole library");
}

var pathScanResult = refreshController.ScanLibrary(new ScanRequest { Path = "/movies/Film (2026)" });
if (pathScanResult is not AcceptedResult || libraryManagerProxy.ValidatedWholeLibrary ||
    !libraryMonitorProxy.ChangedPaths.SequenceEqual(["/movies/Film (2026)/Film.mkv", "/movies/Film (2026)"]))
{
    throw new InvalidOperationException("A path-scoped scan should queue only the requested path");
}

var scanResult = refreshController.ScanLibrary(new ScanRequest { LibraryId = "movies-id" });
if (scanResult is not AcceptedResult || libraryManagerProxy.ValidatedWholeLibrary ||
    !libraryMonitorProxy.ChangedPaths.SequenceEqual(["/movies/Film (2026)/Film.mkv", "/movies/Film (2026)", "/movies", "/films"]))
{
    throw new InvalidOperationException("ScanLibrary should queue only the requested library's roots");
}

if (refreshController.GetLibraryByPath("/movies/Film.mkv") is not OkObjectResult ||
    refreshController.GetLibraryByPath("/movies-old/Film.mkv") is not NotFoundObjectResult)
{
    throw new InvalidOperationException("Library path matching should require a directory boundary");
}

if (refreshController.GetUnidentifiableItems("stale-library-id") is not NotFoundObjectResult)
{
    throw new InvalidOperationException("An unknown library ID should return not found");
}

var unidentifiableResult = refreshController.GetUnidentifiableItems("movies-id") as OkObjectResult
    ?? throw new InvalidOperationException("A known library should return its unidentifiable items");
using var unidentifiablePayload = JsonDocument.Parse(JsonSerializer.Serialize(unidentifiableResult.Value));
if (unidentifiablePayload.RootElement.GetProperty("TotalCount").GetInt32() != 1 ||
    unidentifiablePayload.RootElement.GetProperty("Items").GetArrayLength() != 1 ||
    unidentifiablePayload.RootElement.GetProperty("Items")[0].GetProperty("Path").GetString() != "/movies/In library.mkv")
{
    throw new InvalidOperationException("Unidentifiable items should be limited to the selected library root");
}

libraryManagerProxy.Root.Children = Enumerable.Range(0, 105)
    .Select(i => new Movie { Id = Guid.NewGuid(), Name = $"Unidentified {i}", Path = $"/movies/{i}.mkv" })
    .ToArray();
var unidentifiablePage = refreshController.GetUnidentifiableItems("movies-id") as OkObjectResult
    ?? throw new InvalidOperationException("A known library should return its unidentifiable items");
using var unidentifiablePagePayload = JsonDocument.Parse(JsonSerializer.Serialize(unidentifiablePage.Value));
if (unidentifiablePagePayload.RootElement.GetProperty("TotalCount").GetInt32() != 105 ||
    unidentifiablePagePayload.RootElement.GetProperty("Items").GetArrayLength() != 100)
{
    throw new InvalidOperationException("Unidentifiable results should report the total and visible limit");
}

static IScheduledTaskWorker CreateWorker(string id, string key, string name, TaskState state, double progress)
{
    var task = DispatchProxy.Create<IScheduledTask, ScheduledTaskProxy>();
    ((ScheduledTaskProxy)(object)task).Key = key;

    var worker = DispatchProxy.Create<IScheduledTaskWorker, TaskWorkerProxy>();
    ((TaskWorkerProxy)(object)worker).Task = task;
    ((TaskWorkerProxy)(object)worker).TaskId = id;
    ((TaskWorkerProxy)(object)worker).TaskName = name;
    ((TaskWorkerProxy)(object)worker).TaskState = state;
    ((TaskWorkerProxy)(object)worker).Progress = progress;
    return worker;
}

public class TaskManagerProxy : DispatchProxy
{
    public IReadOnlyList<IScheduledTaskWorker> Tasks { get; set; } = Array.Empty<IScheduledTaskWorker>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "get_ScheduledTasks")
        {
            return Tasks;
        }

        return targetMethod?.ReturnType == typeof(void)
            ? null
            : targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
    }
}

public class LibraryManagerProxy : DispatchProxy
{
    public List<VirtualFolderInfo> Folders { get; set; } = [];
    public AggregateFolder Root { get; } = new();
    public bool ValidatedWholeLibrary { get; private set; }
    public Delegate? ItemAddedHandler { get; private set; }
    public Delegate? ItemRemovedHandler { get; private set; }
    public Delegate? ItemUpdatedHandler { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "add_ItemAdded") ItemAddedHandler = Delegate.Combine(ItemAddedHandler, (Delegate)args![0]!);
        if (targetMethod?.Name == "remove_ItemAdded") ItemAddedHandler = Delegate.Remove(ItemAddedHandler, (Delegate)args![0]!);
        if (targetMethod?.Name == "add_ItemRemoved") ItemRemovedHandler = Delegate.Combine(ItemRemovedHandler, (Delegate)args![0]!);
        if (targetMethod?.Name == "remove_ItemRemoved") ItemRemovedHandler = Delegate.Remove(ItemRemovedHandler, (Delegate)args![0]!);
        if (targetMethod?.Name == "add_ItemUpdated") ItemUpdatedHandler = Delegate.Combine(ItemUpdatedHandler, (Delegate)args![0]!);
        if (targetMethod?.Name == "remove_ItemUpdated") ItemUpdatedHandler = Delegate.Remove(ItemUpdatedHandler, (Delegate)args![0]!);

        if (targetMethod?.Name == "GetVirtualFolders")
        {
            return Folders;
        }

        if (targetMethod?.Name == "get_RootFolder")
        {
            return Root;
        }

        if (targetMethod?.Name == "ValidateMediaLibrary")
        {
            ValidatedWholeLibrary = true;
            return Task.CompletedTask;
        }

        return targetMethod?.ReturnType == typeof(void)
            ? null
            : targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
    }
}

public class SessionManagerProxy : DispatchProxy
{
    public Delegate? PlaybackStartHandler { get; private set; }
    public Delegate? PlaybackStoppedHandler { get; private set; }
    public Delegate? PlaybackProgressHandler { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "add_PlaybackStart") PlaybackStartHandler = Delegate.Combine(PlaybackStartHandler, (Delegate)args![0]!);
        if (targetMethod?.Name == "remove_PlaybackStart") PlaybackStartHandler = Delegate.Remove(PlaybackStartHandler, (Delegate)args![0]!);
        if (targetMethod?.Name == "add_PlaybackStopped") PlaybackStoppedHandler = Delegate.Combine(PlaybackStoppedHandler, (Delegate)args![0]!);
        if (targetMethod?.Name == "remove_PlaybackStopped") PlaybackStoppedHandler = Delegate.Remove(PlaybackStoppedHandler, (Delegate)args![0]!);
        if (targetMethod?.Name == "add_PlaybackProgress") PlaybackProgressHandler = Delegate.Combine(PlaybackProgressHandler, (Delegate)args![0]!);
        if (targetMethod?.Name == "remove_PlaybackProgress") PlaybackProgressHandler = Delegate.Remove(PlaybackProgressHandler, (Delegate)args![0]!);

        return targetMethod?.ReturnType == typeof(void)
            ? null
            : targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
    }
}

public class EmptyProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return targetMethod?.ReturnType == typeof(void)
            ? null
            : targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
    }
}

public class LibraryMonitorProxy : DispatchProxy
{
    public List<string> ChangedPaths { get; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "ReportFileSystemChanged")
        {
            ChangedPaths.Add((string)args![0]!);
        }

        return null;
    }
}

public class ScheduledTaskProxy : DispatchProxy
{
    public string Key { get; set; } = "";

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return targetMethod?.Name == "get_Key" ? Key : null;
    }
}

public class TaskWorkerProxy : DispatchProxy
{
    public IScheduledTask Task { get; set; } = null!;
    public string TaskId { get; set; } = "";
    public string TaskName { get; set; } = "";
    public TaskState TaskState { get; set; }
    public double Progress { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return targetMethod?.Name switch
        {
            "get_ScheduledTask" => Task,
            "get_Id" => TaskId,
            "get_Name" => TaskName,
            "get_State" => TaskState,
            "get_CurrentProgress" => Progress,
            _ => null
        };
    }
}

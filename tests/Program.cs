using System.Text.Json;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Plex2Jellyfin.Plugin.Api;

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
    public bool ValidatedWholeLibrary { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "GetVirtualFolders")
        {
            return Folders;
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

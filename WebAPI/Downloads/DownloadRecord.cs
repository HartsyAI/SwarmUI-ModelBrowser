using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.Downloads;

/// <summary>Lifecycle state of a download. `Paused` (cancelled) and `Errored` (failed) both resume from the saved byte offset.</summary>
public enum DownloadState { Downloading, Paused, Errored, Completed }

/// <summary>Persisted state for one model download, resumable across Cancel/Retry and server restarts.
/// API keys are never stored here; they are read from the session on each Start/Resume.</summary>
public class DownloadRecord
{
    public string Id;
    public string UserId;
    public string Url;
    public string Type;
    public string Name;
    public string Extension;
    public string OutPath;
    public string PartPath;
    public string Metadata;
    public string Image;
    public long TotalBytes;
    public long DownloadedBytes;
    public string ETag;
    public string LastModified;
    public DownloadState State;
    public string ErrorMessage;
    public long CreatedAtMs;
    public long UpdatedAtMs;

    /// <summary>Bytes per second over the last ~1 s. Not persisted.</summary>
    [JsonIgnore] public double PerSecond;
    /// <summary>Note for the current attempt only, eg that the server could not resume and the download restarted. Not persisted.</summary>
    [JsonIgnore] public string TransientNote;
    /// <summary>Signals the running pump task to stop. Null unless a download is currently in flight.</summary>
    [JsonIgnore] public CancellationTokenSource Canceller;
    /// <summary>The in-flight pump task, if any. Cancel() awaits this (bounded) so its response reflects the settled state.</summary>
    [JsonIgnore] public Task PumpTask;

    /// <summary>Shape sent to the frontend: omits filesystem paths and internal fields the UI has no use for.</summary>
    public JObject ToNetObject()
    {
        return new JObject()
        {
            ["id"] = Id,
            ["url"] = Url,
            ["type"] = Type,
            ["name"] = Name,
            ["extension"] = Extension,
            ["image"] = Image,
            ["totalBytes"] = TotalBytes,
            ["downloadedBytes"] = DownloadedBytes,
            ["perSecond"] = PerSecond,
            ["state"] = State.ToString().ToLowerInvariant(),
            ["error"] = ErrorMessage,
            ["note"] = TransientNote,
            ["createdAtMs"] = CreatedAtMs,
            ["updatedAtMs"] = UpdatedAtMs
        };
    }

    /// <summary>Shape written to persistent storage: everything needed to resume or redraw a card, minus the ephemeral fields above.</summary>
    public JObject ToManifestObject()
    {
        return new JObject()
        {
            ["id"] = Id,
            ["userId"] = UserId,
            ["url"] = Url,
            ["type"] = Type,
            ["name"] = Name,
            ["extension"] = Extension,
            ["outPath"] = OutPath,
            ["partPath"] = PartPath,
            ["metadata"] = Metadata,
            ["image"] = Image,
            ["totalBytes"] = TotalBytes,
            ["downloadedBytes"] = DownloadedBytes,
            ["etag"] = ETag,
            ["lastModified"] = LastModified,
            ["state"] = State.ToString(),
            ["errorMessage"] = ErrorMessage,
            ["createdAtMs"] = CreatedAtMs,
            ["updatedAtMs"] = UpdatedAtMs
        };
    }

    public static DownloadRecord FromManifestObject(JObject obj)
    {
        return new DownloadRecord()
        {
            Id = $"{obj["id"]}",
            UserId = $"{obj["userId"]}",
            Url = $"{obj["url"]}",
            Type = $"{obj["type"]}",
            Name = $"{obj["name"]}",
            Extension = $"{obj["extension"]}",
            OutPath = $"{obj["outPath"]}",
            PartPath = $"{obj["partPath"]}",
            Metadata = obj["metadata"]?.ToString(),
            Image = obj["image"]?.ToString(),
            TotalBytes = (long)(obj["totalBytes"] ?? 0),
            DownloadedBytes = (long)(obj["downloadedBytes"] ?? 0),
            ETag = obj["etag"]?.ToString(),
            LastModified = obj["lastModified"]?.ToString(),
            State = Enum.TryParse(($"{obj["state"]}"), out DownloadState state) ? state : DownloadState.Errored,
            ErrorMessage = obj["errorMessage"]?.ToString(),
            CreatedAtMs = (long)(obj["createdAtMs"] ?? 0),
            UpdatedAtMs = (long)(obj["updatedAtMs"] ?? 0)
        };
    }
}

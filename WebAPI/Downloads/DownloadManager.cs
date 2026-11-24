using FreneticUtilities.FreneticToolkit;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Hartsy.Extensions.Downloads;

/// <summary>Owns every model download: start, pause (Cancel keeps the partial file), resume via HTTP Range, and clear.
/// Downloads run detached from any request, and their records live in the user database, so they survive a page reload
/// or a server restart. Core's `Utilities.DownloadFile` is not reused because it deletes the partial file on cancel or failure.</summary>
public static class DownloadManager
{
    /// <summary>All known downloads by ID; <see cref="SaveRecord"/> mirrors each change to the database.</summary>
    private static readonly ConcurrentDictionary<string, DownloadRecord> Records = new();

    /// <summary>Generic-data name the download records are filed under in each user's store.</summary>
    private const string GenericDataName = "model_browser_download";

    /// <summary>Completed downloads older than this are dropped from the database at startup.</summary>
    private static readonly long CompletedRetentionMs = TimeSpan.FromDays(7).Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>If a read produces no data for this long, the download is treated as stalled and errors out (resumable).</summary>
    private static readonly TimeSpan IdleReadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Minimum spacing between user-DB writes of in-progress byte counts. Kept long: the UI reads in-memory records and Init trusts the part file.</summary>
    private const long ProgressPersistIntervalMs = 30_000;

    private const int ReadBufferSize = 256 * 1024;

    /// <summary>Held while Start or Resume checks a download's state and claims it, so two requests never start two transfers into one file.</summary>
    private static readonly object StartLock = new();

    /// <summary>Redirects one download may take before it is refused.</summary>
    private const int MaxDownloadRedirects = 10;

    /// <summary>The Hartsy site. Its downloads take the user's Hartsy key, and only from this origin.</summary>
    private const string HartsyOrigin = "https://hartsy.ai/";

    /// <summary>Headers that identify the caller. They travel only to the origin they were set for.</summary>
    private static readonly HashSet<string> CredentialHeaders = new(StringComparer.OrdinalIgnoreCase) { "X-API-Key", "Authorization" };

    /// <summary>Client for the transfer itself. It does not follow redirects on its own: HttpClient would copy every request
    /// header to the host it redirects to, which sends a Hartsy key to the signed storage URL Hartsy answers with.</summary>
    private static readonly HttpClient RedirectClient = CreateRedirectClient();

    private static HttpClient CreateRedirectClient()
    {
        HttpClient client = new(new SocketsHttpHandler() { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = 1000 });
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SwarmUI/{Utilities.Version}");
        client.Timeout = TimeSpan.FromMinutes(120);
        return client;
    }

    /// <summary>Sends a download request and follows its redirects itself. A redirect to another origin drops the credential
    /// headers, as HttpClient drops Authorization. Range and If-Range are kept, so a resumed download still asks for the bytes
    /// it is missing.</summary>
    private static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(HttpRequestMessage request, CancellationToken token)
    {
        for (int hop = 0; ; hop++)
        {
            HttpResponseMessage response = await RedirectClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            bool isRedirect = response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            if (!isRedirect || response.Headers.Location is null)
            {
                return response;
            }
            if (hop == MaxDownloadRedirects)
            {
                response.Dispose();
                throw new SwarmReadableErrorException("Failed to download: too many redirects.");
            }
            Uri next = new(request.RequestUri, response.Headers.Location);
            response.Dispose();
            bool sameOrigin = next.Scheme == request.RequestUri.Scheme && next.Authority == request.RequestUri.Authority;
            HttpRequestMessage nextRequest = new(HttpMethod.Get, next);
            foreach ((string key, IEnumerable<string> values) in request.Headers)
            {
                if (sameOrigin || !CredentialHeaders.Contains(key))
                {
                    nextRequest.Headers.TryAddWithoutValidation(key, values);
                }
            }
            request = nextRequest;
        }
    }

    /// <summary>Query-string-safe token characters, mirroring core's own sanitization for the same API keys.</summary>
    private static string SanitizeKey(string key) => ModelsAPI.TokenTextLimiter.TrimToMatches(key);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Loads every stored download record at extension init. No user has logged in yet, so this scans the
    /// generic-data store directly. A record still marked `Downloading` is demoted to `Paused`; its partial file stays resumable.</summary>
    public static void Init()
    {
        string marker = $"///${GenericDataName}///";
        List<SessionHandler.GenericDataStore> entries;
        lock (Program.Sessions.DBLock)
        {
            entries = [.. Program.Sessions.GenericData.Find(d => d.ID.Contains(marker))];
        }
        long nowMs = NowMs();
        foreach (SessionHandler.GenericDataStore entry in entries)
        {
            JObject obj;
            try
            {
                obj = JObject.Parse(entry.Data);
            }
            catch (Exception ex)
            {
                Logs.Warning($"[ModelBrowser] failed to parse stored download record '{entry.ID}', skipping: {ex.ReadableString()}");
                continue;
            }
            DownloadRecord rec = DownloadRecord.FromManifestObject(obj);
            if (string.IsNullOrEmpty(rec.Id) || string.IsNullOrEmpty(rec.UserId))
            {
                continue;
            }
            if (rec.State == DownloadState.Completed)
            {
                if (nowMs - rec.UpdatedAtMs <= CompletedRetentionMs)
                {
                    Records[rec.Id] = rec;
                }
                else
                {
                    lock (Program.Sessions.DBLock)
                    {
                        Program.Sessions.GenericData.Delete(entry.ID);
                    }
                }
                continue;
            }
            if (rec.State == DownloadState.Downloading)
            {
                rec.State = DownloadState.Paused;
            }
            // The stored count is saved only every ProgressPersistIntervalMs, so the part file decides where Append resumes.
            rec.DownloadedBytes = File.Exists(rec.PartPath) ? new FileInfo(rec.PartPath).Length : 0;
            Records[rec.Id] = rec;
            SaveRecord(rec);
        }
    }

    /// <summary>Upserts one download record into its owning user's generic-data store.</summary>
    private static void SaveRecord(DownloadRecord rec)
    {
        User user = Program.Sessions.GetUser(rec.UserId, makeNew: false);
        if (user is null)
        {
            Logs.Warning($"[ModelBrowser] could not persist download '{rec.Id}' - user '{rec.UserId}' not found.");
            return;
        }
        user.SaveGenericData(GenericDataName, rec.Id, rec.ToManifestObject().ToString());
    }

    /// <summary>Removes one download record from its owning user's generic-data store.</summary>
    private static void RemoveRecord(string userId, string id)
    {
        Program.Sessions.GetUser(userId, makeNew: false)?.DeleteGenericData(GenericDataName, id);
    }

    public static JObject ListForUser(Session session)
    {
        JArray arr = [.. Records.Values.Where(r => r.UserId == session.User.UserID).OrderByDescending(r => r.UpdatedAtMs).Select(r => r.ToNetObject())];
        return new JObject() { ["success"] = true, ["downloads"] = arr };
    }

    private static bool TryGetOwned(Session session, string id, out DownloadRecord rec, out JObject error)
    {
        if (string.IsNullOrEmpty(id) || !Records.TryGetValue(id, out rec) || rec.UserId != session.User.UserID)
        {
            rec = null;
            error = new JObject() { ["error"] = "Download not found." };
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>Mirrors core `DoModelDownloadWS`'s extension/folder resolution exactly, so files land in the same place core would put them.</summary>
    private static (string Extension, string Folder) ResolveExtensionAndFolder(T2IModelHandler handler, string type, string url)
    {
        string extension = "safetensors";
        string folder = handler.DownloadFolderPath;
        if (url.EndsWith(".gguf"))
        {
            extension = "gguf";
            if (type == "Stable-Diffusion")
            {
                folder += "/../diffusion_models";
            }
        }
        return (extension, folder);
    }

    /// <summary>Applies per-host auth (Civitai token, Hugging Face bearer, Hartsy key) and returns the hint shown if the host
    /// refuses. Keys come from the session each time so they never reach the stored record.</summary>
    private static (string RequestUrl, Dictionary<string, string> Headers, string AuthHint) ResolveRequest(Session session, string originalUrl)
    {
        string url = originalUrl.Split('#')[0];
        Dictionary<string, string> headers = [];
        string authHint = Utilities.ApplyDownloadAPIKey(ref url, headers, session);
        if (url.StartsWith(HartsyOrigin))
        {
            string key = session.User.GetGenericData("hartsy_api", "key");
            if (!string.IsNullOrEmpty(key))
            {
                headers["X-API-Key"] = SanitizeKey(key);
                authHint = "Hartsy refused this download with your API key. Make sure the Hartsy API key in the User Settings page is valid and holds the models:download permission, and that your subscription tier covers this model.";
            }
            else
            {
                authHint = "Hartsy refused this download, and you do not have a Hartsy API key set. If this model requires a subscription, set your Hartsy API key in the User Settings page to download it.";
            }
        }
        return (url, headers, authHint);
    }

    /// <summary>Starts a new download, or returns `{ error }` for a bad URL, refused name, unknown type or occupied destination.</summary>
    public static JObject Start(Session session, string url, string type, string name, string metadata, string image)
    {
        if (string.IsNullOrWhiteSpace(url) || (!url.StartsWith("http://") && !url.StartsWith("https://")))
        {
            return new JObject() { ["error"] = "Invalid URL." };
        }
        name = Utilities.StrictFilenameClean(name.Replace(' ', '_'));
        if (ModelsAPI.TryGetRefusalForModel(session, name, out JObject refusal))
        {
            return refusal;
        }
        if (!Program.T2IModelSets.TryGetValue(type, out T2IModelHandler handler))
        {
            return new JObject() { ["error"] = "Invalid type." };
        }
        (string extension, string folder) = ResolveExtensionAndFolder(handler, type, url);
        string outPath = $"{folder}/{name}.{extension}";
        if (File.Exists(outPath))
        {
            return new JObject() { ["error"] = $"A model already exists at '{outPath}' - rename it or choose a different destination." };
        }
        // SwarmUI shows a sidecar thumbnail only as a data URI and ignores a plain URL, so only a data URI is written. A URL
        // would also replace the thumbnail the sidecar already carries.
        if (image is not null && image.StartsWith("data:image/") && !string.IsNullOrWhiteSpace(metadata))
        {
            try
            {
                JObject metaObj = JObject.Parse(metadata);
                metaObj["modelspec.thumbnail"] = image;
                metadata = metaObj.ToString();
            }
            catch (Exception ex)
            {
                Logs.Debug($"[ModelBrowser] failed to inject thumbnail into metadata for '{name}': {ex.ReadableString()}");
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        long nowMs = NowMs();
        string id = Guid.NewGuid().ToString("N");
        DownloadRecord rec = new()
        {
            Id = id,
            UserId = session.User.UserID,
            Url = url,
            Type = type,
            Name = name,
            Extension = extension,
            OutPath = outPath,
            // Each download writes its own part file, so two users fetching the same model never share one.
            PartPath = $"{outPath}.{id}.edl.part",
            Metadata = metadata,
            Image = image,
            State = DownloadState.Downloading,
            CreatedAtMs = nowMs,
            UpdatedAtMs = nowMs
        };
        lock (StartLock)
        {
            // A finished download whose file is gone from disk must not block a new one. A finished file still on disk is refused above.
            if (Records.Values.Any(r => r.UserId == session.User.UserID && r.State != DownloadState.Completed && string.Equals(r.OutPath, outPath, StringComparison.OrdinalIgnoreCase)))
            {
                return new JObject() { ["error"] = $"A download to '{outPath}' already exists - resume or clear it from the Downloads panel first." };
            }
            Records[rec.Id] = rec;
        }
        SaveRecord(rec);
        Kick(rec, session);
        return new JObject() { ["success"] = true, ["download"] = rec.ToNetObject() };
    }

    /// <summary>Resumes a paused or errored download from its saved byte offset. A no-op if it is already downloading.</summary>
    public static JObject Resume(Session session, string id)
    {
        if (!TryGetOwned(session, id, out DownloadRecord rec, out JObject error))
        {
            return error;
        }
        if (File.Exists(rec.OutPath) && rec.State != DownloadState.Completed)
        {
            return new JObject() { ["error"] = $"A model already exists at '{rec.OutPath}' - move it, or clear this download." };
        }
        lock (StartLock)
        {
            if (rec.State == DownloadState.Downloading)
            {
                return new JObject() { ["success"] = true, ["download"] = rec.ToNetObject() };
            }
            if (rec.State == DownloadState.Completed)
            {
                return new JObject() { ["error"] = "This download already completed." };
            }
            rec.State = DownloadState.Downloading;
        }
        rec.ErrorMessage = null;
        rec.TransientNote = null;
        rec.UpdatedAtMs = NowMs();
        SaveRecord(rec);
        Kick(rec, session);
        return new JObject() { ["success"] = true, ["download"] = rec.ToNetObject() };
    }

    private static void Kick(DownloadRecord rec, Session session)
    {
        (string requestUrl, Dictionary<string, string> headers, string authHint) = ResolveRequest(session, rec.Url);
        rec.Canceller = new CancellationTokenSource();
        rec.PumpTask = RunDownloadAsync(rec, requestUrl, headers, authHint);
    }

    /// <summary>Cancels an in-progress download, keeping the partial file for a later Resume. Waits briefly for the
    /// pump to settle so the returned state is current.</summary>
    public static async Task<JObject> CancelAsync(Session session, string id)
    {
        if (!TryGetOwned(session, id, out DownloadRecord rec, out JObject error))
        {
            return error;
        }
        if (rec.State != DownloadState.Downloading)
        {
            return new JObject() { ["success"] = true, ["download"] = rec.ToNetObject() };
        }
        rec.Canceller?.Cancel();
        Task pump = rec.PumpTask;
        if (pump is not null)
        {
            await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        return new JObject() { ["success"] = true, ["download"] = rec.ToNetObject() };
    }

    /// <summary>Deletes the partial file and forgets the download. Refused while downloading, so a delete never races a write.
    /// A completed download is only removed from the list; its model file is never touched.</summary>
    public static JObject Clear(Session session, string id)
    {
        if (!TryGetOwned(session, id, out DownloadRecord rec, out JObject error))
        {
            return error;
        }
        if (rec.State == DownloadState.Downloading)
        {
            return new JObject() { ["error"] = "Cancel the download before clearing it." };
        }
        // A completed download's part file was moved into place. An older record's PartPath can be shared with a newer
        // download to the same destination, so only an unfinished download owns its part file.
        try
        {
            if (rec.State != DownloadState.Completed && File.Exists(rec.PartPath))
            {
                File.Delete(rec.PartPath);
            }
        }
        catch (Exception ex)
        {
            return new JObject() { ["error"] = $"Failed to delete partial file: {ex.Message}" };
        }
        Records.TryRemove(rec.Id, out _);
        RemoveRecord(rec.UserId, rec.Id);
        return new JObject() { ["success"] = true };
    }

    /// <summary>Sends the transfer request. Hartsy refuses a key without models:download even for a free model, which needs
    /// no key, so a refused Hartsy request is sent once more without it. A gated model is refused again and says why.</summary>
    private static async Task<HttpResponseMessage> SendDownloadAsync(string requestUrl, Dictionary<string, string> headers, DownloadRecord rec, bool isResume, CancellationToken token)
    {
        HttpResponseMessage response = await SendFollowingRedirectsAsync(BuildDownloadRequest(requestUrl, headers, rec, isResume), token);
        bool keyRefused = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        if (!keyRefused || !requestUrl.StartsWith(HartsyOrigin) || !headers.ContainsKey("X-API-Key"))
        {
            return response;
        }
        response.Dispose();
        Dictionary<string, string> anonymous = new(headers);
        anonymous.Remove("X-API-Key");
        return await SendFollowingRedirectsAsync(BuildDownloadRequest(requestUrl, anonymous, rec, isResume), token);
    }

    /// <summary>Builds one transfer request. A resumed download asks only for the bytes it is missing, with If-Range so a
    /// changed file restarts instead of splicing.</summary>
    private static HttpRequestMessage BuildDownloadRequest(string requestUrl, Dictionary<string, string> headers, DownloadRecord rec, bool isResume)
    {
        HttpRequestMessage request = new(HttpMethod.Get, requestUrl);
        foreach ((string key, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }
        if (isResume)
        {
            request.Headers.Range = new RangeHeaderValue(rec.DownloadedBytes, null);
            string ifRange = string.IsNullOrEmpty(rec.ETag) ? rec.LastModified : rec.ETag;
            if (!string.IsNullOrEmpty(ifRange))
            {
                request.Headers.TryAddWithoutValidation("If-Range", ifRange);
            }
        }
        return request;
    }

    /// <summary>Throws a readable error for a transfer answer other than 200 or 206, with the refusal reason and key hint for a 401 or 403.</summary>
    private static async Task EnsureTransferResponseAsync(HttpResponseMessage response, string authHint)
    {
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
        {
            return;
        }
        string message = $"Failed to download: got response code {(int)response.StatusCode} {response.StatusCode}";
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            string reason = await Utilities.GetResponseErrorReason(response);
            if (reason is not null)
            {
                message += $" (\"{reason}\")";
            }
            if (authHint is not null)
            {
                message += $". {authHint}";
            }
        }
        throw new SwarmReadableErrorException(message);
    }

    /// <summary>Whether a transfer answer's body starts at the given byte: a 206 for that offset, or a 200 when it is zero.</summary>
    private static bool StartsAt(HttpResponseMessage response, long offset)
    {
        return response.StatusCode == HttpStatusCode.PartialContent ? response.Content.Headers.ContentRange?.From == offset : offset == 0;
    }

    /// <summary>Runs one transfer, resuming when the part file already holds bytes. Detached from any request; only
    /// `rec.Canceller` or shutdown stops it.</summary>
    private static async Task RunDownloadAsync(DownloadRecord rec, string requestUrl, Dictionary<string, string> headers, string authHint)
    {
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(Program.GlobalProgramCancel, rec.Canceller.Token);
        HttpResponseMessage response = null;
        try
        {
            // Append continues at the end of the part file, which a cancelled write can leave past the counted bytes.
            rec.DownloadedBytes = rec.DownloadedBytes > 0 && File.Exists(rec.PartPath) ? new FileInfo(rec.PartPath).Length : 0;
            bool isResume = rec.DownloadedBytes > 0;
            if (isResume && rec.DownloadedBytes == rec.TotalBytes)
            {
                // Every byte is already here, and a Range request past the end would only be refused with 416.
                CompleteDownload(rec);
                return;
            }
            response = await SendDownloadAsync(requestUrl, headers, rec, isResume, linked.Token);
            if (isResume && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && response.Content.Headers.ContentRange?.Length == rec.DownloadedBytes)
            {
                CompleteDownload(rec);
                return;
            }
            await EnsureTransferResponseAsync(response, authHint);
            if (isResume && !StartsAt(response, rec.DownloadedBytes))
            {
                // The server ignored the Range or answered a different one; appending would corrupt the file, so restart and say why.
                bool wrongRange = response.StatusCode == HttpStatusCode.PartialContent;
                string reason = wrongRange ? "returned a different byte range than requested" : "did not support resuming";
                rec.TransientNote = $"The server {reason}, so this download restarted from the beginning.";
                Logs.Info($"[ModelBrowser] download '{rec.Name}' {reason}; restarting from scratch.");
                isResume = false;
                rec.DownloadedBytes = 0;
                if (wrongRange)
                {
                    // That body is only part of the file, so ask for all of it.
                    response.Dispose();
                    response = await SendDownloadAsync(requestUrl, headers, rec, false, linked.Token);
                    await EnsureTransferResponseAsync(response, authHint);
                }
            }
            if (!StartsAt(response, rec.DownloadedBytes))
            {
                throw new SwarmReadableErrorException("Failed to download: the server sent a different part of the file than requested.");
            }
            rec.ETag = response.Headers.ETag?.Tag ?? rec.ETag;
            rec.LastModified = response.Content.Headers.LastModified?.ToString("R") ?? rec.LastModified;
            long? reportedTotal = response.StatusCode == HttpStatusCode.PartialContent ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength;
            if (reportedTotal.HasValue && reportedTotal.Value > 0)
            {
                rec.TotalBytes = reportedTotal.Value;
            }
            // Persist the resume validators now, so a crash before the first throttled save still resumes with If-Range.
            rec.UpdatedAtMs = NowMs();
            SaveRecord(rec);
            long lastPersistMs = Environment.TickCount64;
            long lastSpeedSampleMs = Environment.TickCount64;
            long bytesAtLastSample = rec.DownloadedBytes;
            using (FileStream file = new(rec.PartPath, isResume ? FileMode.Append : FileMode.Create, FileAccess.Write))
            {
                if (!isResume)
                {
                    rec.DownloadedBytes = 0;
                }
                using Stream stream = await response.Content.ReadAsStreamAsync(linked.Token);
                byte[] buffer = new byte[ReadBufferSize];
                while (true)
                {
                    int read;
                    using (CancellationTokenSource idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
                    {
                        idleTimeout.CancelAfter(IdleReadTimeout);
                        try
                        {
                            read = await stream.ReadAsync(buffer, idleTimeout.Token);
                        }
                        catch (OperationCanceledException) when (!linked.Token.IsCancellationRequested)
                        {
                            throw new SwarmReadableErrorException($"Download stalled: no data received for {IdleReadTimeout.TotalMinutes:0} minutes.");
                        }
                    }
                    if (read <= 0)
                    {
                        break;
                    }
                    await file.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                    rec.DownloadedBytes += read;
                    long nowTicks = Environment.TickCount64;
                    if (nowTicks - lastSpeedSampleMs >= 1000)
                    {
                        rec.PerSecond = (rec.DownloadedBytes - bytesAtLastSample) * 1000.0 / (nowTicks - lastSpeedSampleMs);
                        bytesAtLastSample = rec.DownloadedBytes;
                        lastSpeedSampleMs = nowTicks;
                    }
                    if (nowTicks - lastPersistMs >= ProgressPersistIntervalMs)
                    {
                        rec.UpdatedAtMs = NowMs();
                        SaveRecord(rec);
                        lastPersistMs = nowTicks;
                    }
                }
                await file.FlushAsync(linked.Token);
            }
            if (rec.TotalBytes > 0 && rec.DownloadedBytes != rec.TotalBytes)
            {
                throw new SwarmReadableErrorException($"Download incomplete: expected {rec.TotalBytes} bytes but got {rec.DownloadedBytes} bytes.");
            }
            CompleteDownload(rec);
        }
        catch (OperationCanceledException)
        {
            rec.State = DownloadState.Paused;
            rec.PerSecond = 0;
            rec.UpdatedAtMs = NowMs();
            SaveRecord(rec);
        }
        catch (Exception ex)
        {
            rec.State = DownloadState.Errored;
            rec.ErrorMessage = ex is SwarmReadableErrorException ? ex.Message : "Failed to download the model due to an internal exception.";
            rec.PerSecond = 0;
            rec.UpdatedAtMs = NowMs();
            SaveRecord(rec);
            if (ex is not SwarmReadableErrorException)
            {
                Logs.Warning($"[ModelBrowser] download '{rec.Name}' failed with internal exception: {ex.ReadableString()}");
            }
        }
        finally
        {
            response?.Dispose();
            linked.Dispose();
        }
    }

    /// <summary>Moves the finished part file into place and marks the download Completed, then runs <see cref="FinalizeDownload"/>.
    /// The model is saved by then, so a failure there is logged and noted instead of failing the download.</summary>
    private static void CompleteDownload(DownloadRecord rec)
    {
        if (File.Exists(rec.OutPath))
        {
            throw new SwarmReadableErrorException($"Download finished, but a model already exists at '{rec.OutPath}'. Move that file, then Retry.");
        }
        File.Move(rec.PartPath, rec.OutPath, overwrite: false);
        rec.State = DownloadState.Completed;
        rec.PerSecond = 0;
        rec.UpdatedAtMs = NowMs();
        SaveRecord(rec);
        try
        {
            FinalizeDownload(rec);
        }
        catch (Exception ex)
        {
            rec.TransientNote = "The model was saved, but writing its metadata or refreshing the model list failed. See the server log.";
            Logs.Warning($"[ModelBrowser] download '{rec.Name}' was saved, but finishing its setup failed: {ex.ReadableString()}");
        }
    }

    /// <summary>Repeats core's post-download steps (metadata sidecar, handler refresh, optional resave), so the model matches
    /// one fetched by core's downloader.</summary>
    private static void FinalizeDownload(DownloadRecord rec)
    {
        if (!string.IsNullOrWhiteSpace(rec.Metadata))
        {
            File.WriteAllText(Path.ChangeExtension(rec.OutPath, "swarm.json"), rec.Metadata);
        }
        if (!Program.T2IModelSets.TryGetValue(rec.Type, out T2IModelHandler handler))
        {
            Logs.Warning($"[ModelBrowser] model type '{rec.Type}' is no longer registered; skipping refresh for completed download '{rec.Name}'.");
            return;
        }
        using (ManyReadOneWriteLock.WriteClaim claim = Program.RefreshLock.LockWrite())
        {
            handler.Refresh();
        }
        if (Program.ServerSettings.Paths.DownloaderAlwaysResave && rec.Extension == "safetensors")
        {
            string modelKey = $"{rec.Name}.safetensors";
            if (handler.Models.TryGetValue(modelKey, out T2IModel model))
            {
                model.ResaveModel();
            }
            else
            {
                Logs.Warning($"[ModelBrowser] could not resave model '{modelKey}' as it has not shown up in the backing handler.");
            }
        }
    }
}

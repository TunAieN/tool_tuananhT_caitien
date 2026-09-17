using System.Text.Json;

namespace ToolTikTokV11.Services;

public sealed record TikTokVideoFileAttachResult(
    bool Success, string FailureReason, bool SelectButtonFound,
    bool ChooserArmed, bool ChooserEvent, string Source);

public sealed partial class ChromeController
{
    internal async Task EnableVideoPostNetworkObservationAsync(CancellationToken ct = default)
        => await Cdp.CallAsync("Network.enable", ct: ct);

    internal IDisposable SubscribeToVideoPostNetworkEvents(Action<string, JsonElement> observer)
        => Cdp.SubscribeToEvents(observer);

    internal async Task<string> ReadVideoPostResponseBodyAsync(string requestId, CancellationToken ct = default)
    {
        var result = await Cdp.CallAsync("Network.getResponseBody", new { requestId }, ct);
        return result.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
            ? body.GetString() ?? ""
            : "";
    }

    public async Task<TikTokVideoFileAttachResult> SelectAndAttachVideoThroughUiAsync(
        string selectedVideoPath,
        CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(selectedVideoPath);
        const string buttonProbe = """
(() => {
  const norm=s=>String(s||'').replace(/\s+/g,' ').trim().toLowerCase();
  const visible=el=>{if(!el)return false;const r=el.getBoundingClientRect(),cs=getComputedStyle(el);return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'};
  const allowed=new Set(['chọn video','select video','select file','upload video']);
  const target=[...document.querySelectorAll('button,[role="button"]')].find(el=>{
    if(!visible(el)||el.disabled||el.getAttribute('aria-disabled')==='true')return false;
    const text=norm(el.innerText||el.textContent||''),aria=norm(el.getAttribute('aria-label')||''),child=norm(el.querySelector('span,p,strong')?.innerText||'');
    return allowed.has(text)||allowed.has(aria)||allowed.has(child);
  })||null;
  if(target)window.__ttVideoSelectButton=target;
  return target?{found:true,tag:target.tagName||'',role:target.getAttribute('role')||'',text:String(target.innerText||target.textContent||'').replace(/\s+/g,' ').trim(),disabled:!!target.disabled,ariaDisabled:target.getAttribute('aria-disabled')||'',dataE2e:target.getAttribute('data-e2e')||'',outerHtml:String(target.outerHTML||'').replace(/\s+/g,' ').slice(0,420)}:{found:false};
})()
""";
        JsonElement buttonMetadata = default;
        JsonElement metadata = default;
        var buttonDeadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            ct.ThrowIfCancellationRequested();
            buttonMetadata = await EvalAsync(buttonProbe, ct: ct);
            metadata = buttonMetadata.TryGetProperty("value", out var candidateValue)
                && candidateValue.ValueKind == JsonValueKind.Object ? candidateValue : default;
            if (Flag(metadata, "found")) break;
            await Task.Delay(250, ct);
        } while (DateTime.UtcNow < buttonDeadline);
        var buttonFound = Flag(metadata, "found");
        _log.Info(
            $"[VIDEO][SELECT_VIDEO_BUTTON] found={buttonFound.ToString().ToLowerInvariant()} " +
            $"tag={Property(metadata, "tag")} role={Property(metadata, "role")} text={Property(metadata, "text")} " +
            $"disabled={Property(metadata, "disabled")} ariaDisabled={Property(metadata, "ariaDisabled")} " +
            $"dataE2E={Property(metadata, "dataE2e")} outerHtml={Property(metadata, "outerHtml")}");
        if (!buttonFound)
            return new(false, "SELECT_VIDEO_BUTTON_NOT_FOUND", false, false, false, "none");

        var chooserCompletion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveChooser(string eventName, JsonElement parameters)
        {
            if (eventName == "Page.fileChooserOpened") chooserCompletion.TrySetResult(parameters.Clone());
        }

        var chooserArmed = false;
        using var subscription = Cdp.SubscribeToEvents(ObserveChooser);
        try
        {
            await Cdp.CallAsync("Page.enable", ct: ct);
            await Cdp.CallAsync("Page.setInterceptFileChooserDialog", new { enabled = true }, ct);
            chooserArmed = true;
            _log.Info("[VIDEO][FILE_CHOOSER_ARMED] result=true");
        }
        catch (Exception ex)
        {
            _log.Warn($"[VIDEO][FILE_CHOOSER_ARMED] result=false error={ex.GetType().Name}");
            return new(false, "FILE_CHOOSER_ARM_FAILED", true, false, false, "none");
        }

        try
        {
            var clickResult = await EvalAsync("""
(() => {
  const norm=s=>String(s||'').replace(/\s+/g,' ').trim().toLowerCase();
  const visible=el=>{if(!el)return false;const r=el.getBoundingClientRect(),cs=getComputedStyle(el);return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'};
  const allowed=new Set(['chọn video','select video','select file','upload video']);
  let target=window.__ttVideoSelectButton;
  if(!target?.isConnected||!visible(target)||target.disabled||target.getAttribute('aria-disabled')==='true')target=[...document.querySelectorAll('button,[role="button"]')].find(el=>{if(!visible(el)||el.disabled||el.getAttribute('aria-disabled')==='true')return false;const text=norm(el.innerText||el.textContent||''),aria=norm(el.getAttribute('aria-label')||''),child=norm(el.querySelector('span,p,strong')?.innerText||'');return allowed.has(text)||allowed.has(aria)||allowed.has(child)})||null;
  if(!target)return false;target.click();return true;
})()
""", ct: ct);
            var clicked = clickResult.TryGetProperty("value", out var clickValue) && clickValue.ValueKind == JsonValueKind.True;
            if (!clicked)
                return new(false, "SELECT_VIDEO_BUTTON_NOT_FOUND", true, chooserArmed, false, "none");
            _log.Info("[VIDEO][SELECT_VIDEO_CLICKED]");

            JsonElement chooserEvent = default;
            var chooserOpened = false;
            try
            {
                chooserEvent = await chooserCompletion.Task.WaitAsync(TimeSpan.FromSeconds(4), ct);
                chooserOpened = true;
            }
            catch (TimeoutException) { }

            var mode = chooserOpened && chooserEvent.TryGetProperty("mode", out var modeElement) ? modeElement.GetString() ?? "" : "NONE";
            var backendNodeId = chooserOpened && chooserEvent.TryGetProperty("backendNodeId", out var backendElement)
                && backendElement.TryGetInt32(out var backendId) ? backendId : 0;
            _log.Info($"[VIDEO][FILE_CHOOSER_OPENED] chooserEvent={chooserOpened.ToString().ToLowerInvariant()} mode={mode} backendNodeIdPresent={(backendNodeId > 0).ToString().ToLowerInvariant()}");

            if (chooserOpened && backendNodeId > 0)
            {
                try
                {
                    await Cdp.CallAsync("DOM.setFileInputFiles", new { files = new[] { fullPath }, backendNodeId }, ct);
                    _log.Info($"[VIDEO][FILE_CHOOSER_FILE_SET] result=OK filename={Path.GetFileName(fullPath)}");
                    return new(true, "", true, chooserArmed, true, "file-chooser");
                }
                catch (Exception ex)
                {
                    _log.Warn($"[VIDEO][FILE_CHOOSER_FILE_SET] result=FAIL filename={Path.GetFileName(fullPath)} error={ex.GetType().Name}");
                }
            }

            var fallback = await TrySetVideoFileInputFallbackAsync(fullPath, ct);
            if (fallback.Success)
                return new(true, "", true, chooserArmed, chooserOpened, fallback.Source);

            if (!chooserOpened)
                _log.Warn("[VIDEO][FILE_CHOOSER_NATIVE_DIALOG_UNHANDLED] chooserEvent=false fileInputFound=false");
            var reason = fallback.InputFound ? "FILE_ATTACH_FAILED" : chooserOpened ? "FILE_INPUT_NOT_FOUND" : "FILE_CHOOSER_NOT_TRIGGERED";
            return new(false, reason, true, chooserArmed, chooserOpened, "none");
        }
        finally
        {
            try { await Cdp.CallAsync("Page.setInterceptFileChooserDialog", new { enabled = false }, CancellationToken.None); }
            catch { }
        }
    }

    async Task<(bool Success, bool InputFound, string Source)> TrySetVideoFileInputFallbackAsync(string fullPath, CancellationToken ct)
    {
        const string pickInput = """
(() => [...document.querySelectorAll('input[type="file"]')].sort((a,b)=>(String(b.accept||'').toLowerCase().includes('video')?1:0)-(String(a.accept||'').toLowerCase().includes('video')?1:0))[0]||null)()
""";
        var metadataResult = await EvalAsync("""
(() => {const el=[...document.querySelectorAll('input[type="file"]')].sort((a,b)=>(String(b.accept||'').toLowerCase().includes('video')?1:0)-(String(a.accept||'').toLowerCase().includes('video')?1:0))[0]||null;if(!el)return {found:false};const r=el.getBoundingClientRect(),cs=getComputedStyle(el);return {found:true,visible:r.width>0&&r.height>0&&cs.display!=='none'&&cs.visibility!=='hidden',enabled:!el.disabled,accept:el.accept||''}})()
""", ct: ct);
        var meta = metadataResult.TryGetProperty("value", out var metaValue) && metaValue.ValueKind == JsonValueKind.Object ? metaValue : default;
        var found = Flag(meta, "found");
        if (found)
        {
            try
            {
                var evaluated = await Cdp.CallAsync("Runtime.evaluate", new { expression = pickInput, awaitPromise = false, returnByValue = false, userGesture = true }, ct);
                var objectId = evaluated.TryGetProperty("result", out var result) && result.TryGetProperty("objectId", out var objectIdElement) ? objectIdElement.GetString() ?? "" : "";
                if (objectId.Length > 0)
                {
                    await Cdp.CallAsync("DOM.setFileInputFiles", new { files = new[] { fullPath }, objectId }, ct);
                    _log.Info($"[VIDEO][FILE_INPUT_FALLBACK] found=true visible={Property(meta, "visible")} enabled={Property(meta, "enabled")} accept={Property(meta, "accept")} result=OK");
                    return (true, true, "main-document-input");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO][FILE_INPUT_FALLBACK] found=true visible={Property(meta, "visible")} enabled={Property(meta, "enabled")} accept={Property(meta, "accept")} result=FAIL error={ex.GetType().Name}");
                return (false, true, "main-document-input");
            }
        }

        try
        {
            var document = await Cdp.CallAsync("DOM.getDocument", new { depth = -1, pierce = true }, ct);
            var rootNodeId = document.TryGetProperty("root", out var root) && root.TryGetProperty("nodeId", out var rootIdElement)
                && rootIdElement.TryGetInt32(out var rootId) ? rootId : 0;
            if (rootNodeId > 0)
            {
                var query = await Cdp.CallAsync("DOM.querySelectorAll", new { nodeId = rootNodeId, selector = "input[type=file]" }, ct);
                if (query.TryGetProperty("nodeIds", out var nodeIds) && nodeIds.ValueKind == JsonValueKind.Array)
                {
                    foreach (var nodeIdElement in nodeIds.EnumerateArray())
                    {
                        if (!nodeIdElement.TryGetInt32(out var nodeId)) continue;
                        var description = await Cdp.CallAsync("DOM.describeNode", new { nodeId }, ct);
                        var backendId = description.TryGetProperty("node", out var node) && node.TryGetProperty("backendNodeId", out var backendIdElement)
                            && backendIdElement.TryGetInt32(out var value) ? value : 0;
                        if (backendId <= 0) continue;
                        await Cdp.CallAsync("DOM.setFileInputFiles", new { files = new[] { fullPath }, backendNodeId = backendId }, ct);
                        _log.Info("[VIDEO][FILE_INPUT_FALLBACK] found=true visible=unknown enabled=unknown accept=unknown result=OK source=flattened-frame-dom");
                        return (true, true, "flattened-frame-dom");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"[VIDEO][FILE_INPUT_FALLBACK] found=false visible=unknown enabled=unknown accept=unknown result=FAIL source=flattened-frame-dom error={ex.GetType().Name}");
        }
        _log.Warn("[VIDEO][FILE_INPUT_FALLBACK] found=false visible=unknown enabled=unknown accept=unknown result=FAIL");
        return (false, false, "none");
    }

    static string Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) ? item.ToString() : "";
    static bool Flag(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
}

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountManager;

internal sealed record CodexAccountDisplay(string? ModelAccountLabel, string ChatGptEmail)
{
    internal string? ModelAccountTypeLabel { get; init; }
}

internal enum NativeFastPatchWaitOutcome
{
    Patched,
    SkippedWithoutReload,
    TimedOutWithoutReload,
    TimedOutAfterReloadAttempt
}

/// <summary>
/// Applies a fail-closed, in-memory response substitution to the official Codex renderer so
/// the built-in Standard/Fast picker is available for PAT and API-key authentication.
/// The signed MSIX/ASAR files are never modified.
/// </summary>
internal static class CodexNativeFastBridge
{
    internal const string ProcessArgument = "--codex-native-fast-bridge";
    internal const string PortArgument = "--cdp-port";
    internal const string BrowserIdArgument = "--cdp-browser-id";
    internal const string OwnerPidArgument = "--cdp-owner-pid";
    internal const string OwnerStartTicksArgument = "--cdp-owner-start-ticks";
    internal const string OwnerRootArgument = "--cdp-owner-root";
    internal const string AllowRendererReloadArgument = "--allow-renderer-reload";

    private const int MinimumPort = 1024;
    private const int MaximumPort = 65535;
    private const int MaximumEndpointResponseBytes = 512 * 1024;
    private const int MaximumRendererSourceBytes = 32 * 1024 * 1024;
    private const int MaximumFetchResponseHeaders = 128;
    private const int MaximumFetchResponseHeaderNameLength = 128;
    private const int MaximumFetchResponseHeaderValueLength = 16 * 1024;
    private const int CompatibilityAuthCaptureOccurrences = 6;
    private const uint ErrorInsufficientBuffer = 122;
    private const int AddressFamilyInterNetwork = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const string LegacyRendererBundleUrl =
        "app://codex/assets/app-initial-DWsVN4CS.js";
    private const string PreviousRendererBundleUrl =
        "app://-/assets/app-initial-DWsVN4CS.js";
    private const string CurrentRendererBundleUrl =
        "app://-/assets/app-initial-C_Tkoze_.js";
    private const string LatestRendererBundleUrl =
        "app://-/assets/app-initial-izy3qYQi.js";
    private const string Current5229RendererBundleUrl =
        "app://-/assets/app-initial-BhpTek7p.js";
    private const string PreviousRendererSourceSha256 =
        "4C22397E9DAF90C13978C011AE08142ADC0D7BA49FA4109D946CB840774274D8";
    private const string CurrentRendererSourceSha256 =
        "B09A7C92CEE07E25F383A8495DD4C0A9754512E7184E845E13A81BAF7DCAF89A";
    private const string LatestRendererSourceSha256 =
        "F09FC19171315B858E31481FCE919366387D67CB04CE0BD7322FDD2D68983B26";
    private const string Current5229RendererSourceSha256 =
        "7359EEFF35A798A68C0E610CA65F3B12946E08FDAC68CD988A903F061CFCE7A0";
    private const string Current5229RendererPatchedSha256 =
        "479E9B7F2419B7DC90874D863016A2AE513E4F2BE2E1E70B0FE5B8C3166F8ADB";
    private const string FetchNavigationReadinessKey = "fetch-navigation";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RendererPreflightTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownCommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EndpointPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly Regex BrowserIdentityPattern = new(
        "^[A-Za-z0-9._-]{1,200}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ModernRendererBundlePattern = new(
        "\\Aapp://-/assets/app-initial-[A-Za-z0-9_-]{6,80}\\.js\\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly ConcurrentDictionary<string, RendererPatchProfile>
        CompatibleRendererProfilesByFingerprint = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly HashSet<string> RewrittenFetchResponseHeaders = new(
        [
            "content-length",
            "content-encoding",
            "transfer-encoding",
            "content-md5",
            "content-range",
            "etag",
            "last-modified"
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly string[] CredentialEnvironmentVariableNames =
    [
        "OPENAI_API_KEY",
        "OPENAI_ACCESS_TOKEN",
        "OPENAI_AUTH_TOKEN",
        "OPENAI_TOKEN",
        "CODEX_API_KEY",
        "CODEX_ACCESS_TOKEN",
        "CODEX_AUTH_TOKEN",
        "AZURE_OPENAI_API_KEY",
        "PERSONAL_ACCESS_TOKEN",
        "ACCESS_TOKEN"
    ];

    // These are deliberately complete, version-shaped function bodies. Updating Codex may
    // rename minified symbols or alter either flow. In that case no source is edited until a
    // new, reviewed pair is shipped.
    private const string VisibilityGateOriginal =
        "function Ois(e){let t=(0,kis.c)(6),n=Y(Vk),r=e?.hostId??n,i=PA(r),a=i?.authMethod===`chatgpt`,o=i?.authMethod??null,s;t[0]!==r||t[1]!==o?(s={authMethod:o,hostId:r},t[0]=r,t[1]=o,t[2]=s):s=t[2];let{data:c,isPending:l}=_s(Wb,s),u=!!i?.isLoading||a&&l,d=a&&!u&&c!=null&&c?.requirements?.featureRequirements?.fast_mode!==!1,f;return t[3]!==u||t[4]!==d?(f={isServiceTierAllowed:d,isLoading:u},t[3]=u,t[4]=d,t[5]=f):f=t[5],f}";

    private const string VisibilityGatePatched =
        "function Ois(e){let t=(0,kis.c)(6),n=Y(Vk),r=e?.hostId??n,i=PA(r),a=i?.authMethod===`chatgpt`,o=i?.authMethod??null,s;t[0]!==r||t[1]!==o?(s={authMethod:o,hostId:r},t[0]=r,t[1]=o,t[2]=s):s=t[2];let{data:c,isPending:l}=_s(Wb,s),u=!!i?.isLoading||a&&l,d=!u&&(a?c!=null&&c?.requirements?.featureRequirements?.fast_mode!==!1:o===`personalAccessToken`||o===`apikey`),f;return t[3]!==u||t[4]!==d?(f={isServiceTierAllowed:d,isLoading:u},t[3]=u,t[4]=d,t[5]=f):f=t[5],f}";

    private const string ConfigReadGateOriginal =
        "async function bri(e,t){let n=await _ri(e,t);if(n!==`chatgpt`)return!1;let r=await w0t(e,t,{priority:`critical`});return e.query.setData(Wb,{authMethod:n,hostId:t},r),r.requirements?.featureRequirements?.fast_mode!==!1}";

    private const string ConfigReadGatePatched =
        "async function bri(e,t){let n=await _ri(e,t);if(n===`personalAccessToken`||n===`apikey`)return!0;if(n!==`chatgpt`)return!1;let r=await w0t(e,t,{priority:`critical`});return e.query.setData(Wb,{authMethod:n,hostId:t},r),r.requirements?.featureRequirements?.fast_mode!==!1}";

    // PAT/API models served by a compatible endpoint do not always return serviceTiers in
    // model/list. The official renderer otherwise hides Fast even after the two account gates
    // above are open. Capture the already-resolved auth method and use Codex's own reviewed
    // Standard/Fast(priority) fallback only for those two auth modes. The selected-tier fallback
    // keeps the Fast row visibly selected while still writing and sending `priority`.
    private const string ServiceTierAuthCaptureOriginal =
        "u=_s(Ek,e),d=_s(Bos,e),f=PA(o.hostId)?.authMethod??null,p;";

    private const string ServiceTierAuthCapturePatched =
        // Keep the capture inside the existing `let` declaration. The renderer bundle runs in
        // strict mode, so declare `_camAuth` before assigning it instead of relying on an
        // implicit global that would throw before the picker can render.
        "u=_s(Ek,e),d=_s(Bos,e),f=PA(o.hostId)?.authMethod??null,p,_camAuth;_camAuth=f;";

    private const string ServiceTierOptionsOriginal =
        "T=p,E=o.hostId,w=Gwr(s),";

    private const string ServiceTierOptionsPatched =
        "T=p,E=o.hostId,w=(()=>{let e=Gwr(s);return(_camAuth===`personalAccessToken`||_camAuth===`apikey`)&&!e.some(e=>e.value===tTr)?[...e,...oTr.filter(e=>e.value===tTr)]:e})(),";

    private const string ServiceTierSelectionOriginal =
        "S=e!=null&&(u?.serviceTier!==void 0||d!==void 0)?y?k:null:Zwr(s,k,y),x=S==null?null:Xwr(s,S);";

    private const string ServiceTierSelectionPatched =
        "S=e!=null&&(u?.serviceTier!==void 0||d!==void 0)?y?k:null:Zwr(s,k,y),x=S==null?null:Xwr(s,S)??((_camAuth===`personalAccessToken`||_camAuth===`apikey`)&&S===tTr?tTr:null);";

    private static readonly (string Name, string Original, string Patched)[] PreviousRendererPatchContract =
    [
        ("visibility", VisibilityGateOriginal, VisibilityGatePatched),
        ("config", ConfigReadGateOriginal, ConfigReadGatePatched),
        ("auth-capture", ServiceTierAuthCaptureOriginal, ServiceTierAuthCapturePatched),
        ("options", ServiceTierOptionsOriginal, ServiceTierOptionsPatched),
        ("selection", ServiceTierSelectionOriginal, ServiceTierSelectionPatched)
    ];

    // These downstream anchors are intentionally not edited. They ensure that Codex still maps
    // the Fast row to priority, persists the same service_tier key, and carries the resolved tier
    // into the local-composer request before any account gate is relaxed.
    private static readonly (string Name, string Value)[] PreviousRendererSemanticAnchors =
    [
        ("fast-is-priority", "tTr=`priority`,nTr=`fast`,rTr=`ultrafast`,iTr=`default`"),
        ("config-key", "function Fos(e){return e==null?`service_tier`:`profiles.${e}.service_tier`}"),
        ("request-tier", "serviceTierForRequest:S")
    ];

    // OpenAI.Codex 26.818.2872.0 uses a new reviewed renderer bundle. Keep its complete
    // minified bodies separate from the preceding version so an update can never combine
    // anchors or replacements from two different builds.
    private const string CurrentVisibilityGateOriginal =
        "function Cas(e){let t=(0,was.c)(6),n=Y(Tk),r=e?.hostId??n,i=vA(r),a=i?.authMethod===`chatgpt`,o=i?.authMethod??null,s;t[0]!==r||t[1]!==o?(s={authMethod:o,hostId:r},t[0]=r,t[1]=o,t[2]=s):s=t[2];let{data:c,isPending:l}=gs(Bb,s),u=!!i?.isLoading||a&&l,d=a&&!u&&c!=null&&c?.requirements?.featureRequirements?.fast_mode!==!1,f;return t[3]!==u||t[4]!==d?(f={isServiceTierAllowed:d,isLoading:u},t[3]=u,t[4]=d,t[5]=f):f=t[5],f}";

    private const string CurrentVisibilityGatePatched =
        "function Cas(e){let t=(0,was.c)(6),n=Y(Tk),r=e?.hostId??n,i=vA(r),a=i?.authMethod===`chatgpt`,o=i?.authMethod??null,s;t[0]!==r||t[1]!==o?(s={authMethod:o,hostId:r},t[0]=r,t[1]=o,t[2]=s):s=t[2];let{data:c,isPending:l}=gs(Bb,s),u=!!i?.isLoading||a&&l,d=!u&&(a?c!=null&&c?.requirements?.featureRequirements?.fast_mode!==!1:o===`personalAccessToken`||o===`apikey`),f;return t[3]!==u||t[4]!==d?(f={isServiceTierAllowed:d,isLoading:u},t[3]=u,t[4]=d,t[5]=f):f=t[5],f}";

    private const string CurrentConfigReadGateOriginal =
        "async function Fri(e,t){let n=await Mri(e,t);if(n!==`chatgpt`)return!1;let r=await O0t(e,t,{priority:`critical`});return e.query.setData(Bb,{authMethod:n,hostId:t},r),r.requirements?.featureRequirements?.fast_mode!==!1}";

    private const string CurrentConfigReadGatePatched =
        "async function Fri(e,t){let n=await Mri(e,t);if(n===`personalAccessToken`||n===`apikey`)return!0;if(n!==`chatgpt`)return!1;let r=await O0t(e,t,{priority:`critical`});return e.query.setData(Bb,{authMethod:n,hostId:t},r),r.requirements?.featureRequirements?.fast_mode!==!1}";

    private const string CurrentServiceTierAuthCaptureOriginal =
        "u=gs(uk,e),d=gs(Fss,e),f=vA(o.hostId)?.authMethod??null,p;";

    private const string CurrentServiceTierAuthCapturePatched =
        "u=gs(uk,e),d=gs(Fss,e),f=vA(o.hostId)?.authMethod??null,p,_camAuth;_camAuth=f;";

    private const string CurrentServiceTierOptionsOriginal =
        "T=p,E=o.hostId,w=sTr(s),";

    private const string CurrentServiceTierOptionsPatched =
        "T=p,E=o.hostId,w=(()=>{let e=sTr(s);return(_camAuth===`personalAccessToken`||_camAuth===`apikey`)&&!e.some(e=>e.value===_Tr)?[...e,...STr.filter(e=>e.value===_Tr)]:e})(),";

    private const string CurrentServiceTierSelectionOriginal =
        "S=e!=null&&(u?.serviceTier!==void 0||d!==void 0)?y?k:null:pTr(s,k,y),x=S==null?null:fTr(s,S);";

    private const string CurrentServiceTierSelectionPatched =
        "S=e!=null&&(u?.serviceTier!==void 0||d!==void 0)?y?k:null:pTr(s,k,y),x=S==null?null:fTr(s,S)??((_camAuth===`personalAccessToken`||_camAuth===`apikey`)&&S===_Tr?_Tr:null);";

    private static readonly (string Name, string Original, string Patched)[] CurrentRendererPatchContract =
    [
        ("visibility", CurrentVisibilityGateOriginal, CurrentVisibilityGatePatched),
        ("config", CurrentConfigReadGateOriginal, CurrentConfigReadGatePatched),
        ("auth-capture", CurrentServiceTierAuthCaptureOriginal, CurrentServiceTierAuthCapturePatched),
        ("options", CurrentServiceTierOptionsOriginal, CurrentServiceTierOptionsPatched),
        ("selection", CurrentServiceTierSelectionOriginal, CurrentServiceTierSelectionPatched)
    ];

    private static readonly (string Name, string Value)[] CurrentRendererSemanticAnchors =
    [
        ("fast-is-priority", "_Tr=`priority`,vTr=`fast`,yTr=`ultrafast`,bTr=`default`"),
        ("fast-fallback", "STr=[xTr,{description:UO.fastDescription,iconKind:`fast`,label:UO.fastLabel,tier:null,value:_Tr}]"),
        ("config-key", "function Ass(e){return e==null?`service_tier`:`profiles.${e}.service_tier`}"),
        ("request-tier", "serviceTierForRequest:S")
    ];

    // OpenAI.Codex 26.818.3698.0 uses another reviewed renderer bundle. Keep its minified
    // bodies and semantic anchors isolated from every prior version so a renderer update can
    // never satisfy a contract by mixing symbols from different builds.
    private const string LatestVisibilityGateOriginal =
        "function jas(e){let t=(0,Mas.c)(6),n=Y(Mk),r=e?.hostId??n,i=TA(r),a=i?.authMethod===`chatgpt`,o=i?.authMethod??null,s;t[0]!==r||t[1]!==o?(s={authMethod:o,hostId:r},t[0]=r,t[1]=o,t[2]=s):s=t[2];let{data:c,isPending:l}=hs(Ub,s),u=!!i?.isLoading||a&&l,d=a&&!u&&c!=null&&c?.requirements?.featureRequirements?.fast_mode!==!1,f;return t[3]!==u||t[4]!==d?(f={isServiceTierAllowed:d,isLoading:u},t[3]=u,t[4]=d,t[5]=f):f=t[5],f}";

    private const string LatestVisibilityGatePatched =
        "function jas(e){let t=(0,Mas.c)(6),n=Y(Mk),r=e?.hostId??n,i=TA(r),a=i?.authMethod===`chatgpt`,o=i?.authMethod??null,s;t[0]!==r||t[1]!==o?(s={authMethod:o,hostId:r},t[0]=r,t[1]=o,t[2]=s):s=t[2];let{data:c,isPending:l}=hs(Ub,s),u=!!i?.isLoading||a&&l,d=!u&&(a?c!=null&&c?.requirements?.featureRequirements?.fast_mode!==!1:o===`personalAccessToken`||o===`apikey`),f;return t[3]!==u||t[4]!==d?(f={isServiceTierAllowed:d,isLoading:u},t[3]=u,t[4]=d,t[5]=f):f=t[5],f}";

    private const string LatestConfigReadGateOriginal =
        "async function Mri(e,t){let n=await kri(e,t);if(n!==`chatgpt`)return!1;let r=await T0t(e,t,{priority:`critical`});return e.query.setData(Ub,{authMethod:n,hostId:t},r),r.requirements?.featureRequirements?.fast_mode!==!1}";

    private const string LatestConfigReadGatePatched =
        "async function Mri(e,t){let n=await kri(e,t);if(n===`personalAccessToken`||n===`apikey`)return!0;if(n!==`chatgpt`)return!1;let r=await T0t(e,t,{priority:`critical`});return e.query.setData(Ub,{authMethod:n,hostId:t},r),r.requirements?.featureRequirements?.fast_mode!==!1}";

    private const string LatestServiceTierAuthCaptureOriginal =
        "u=hs(_k,e),d=hs(Uss,e),f=TA(o.hostId)?.authMethod??null,p;";

    private const string LatestServiceTierAuthCapturePatched =
        "u=hs(_k,e),d=hs(Uss,e),f=TA(o.hostId)?.authMethod??null,p,_camAuth;_camAuth=f;";

    private const string LatestServiceTierOptionsOriginal =
        "T=p,E=o.hostId,w=eTr(s),";

    private const string LatestServiceTierOptionsPatched =
        "T=p,E=o.hostId,w=(()=>{let e=eTr(s);return(_camAuth===`personalAccessToken`||_camAuth===`apikey`)&&!e.some(e=>e.value===uTr)?[...e,...hTr.filter(e=>e.value===uTr)]:e})(),";

    private const string LatestServiceTierSelectionOriginal =
        "S=e!=null&&(u?.serviceTier!==void 0||d!==void 0)?y?k:null:oTr(s,k,y),x=S==null?null:aTr(s,S);";

    private const string LatestServiceTierSelectionPatched =
        "S=e!=null&&(u?.serviceTier!==void 0||d!==void 0)?y?k:null:oTr(s,k,y),x=S==null?null:aTr(s,S)??((_camAuth===`personalAccessToken`||_camAuth===`apikey`)&&S===uTr?uTr:null);";

    private static readonly (string Name, string Original, string Patched)[] LatestRendererPatchContract =
    [
        ("visibility", LatestVisibilityGateOriginal, LatestVisibilityGatePatched),
        ("config", LatestConfigReadGateOriginal, LatestConfigReadGatePatched),
        ("auth-capture", LatestServiceTierAuthCaptureOriginal, LatestServiceTierAuthCapturePatched),
        ("options", LatestServiceTierOptionsOriginal, LatestServiceTierOptionsPatched),
        ("selection", LatestServiceTierSelectionOriginal, LatestServiceTierSelectionPatched)
    ];

    private static readonly (string Name, string Value)[] LatestRendererSemanticAnchors =
    [
        ("fast-is-priority", "uTr=`priority`,dTr=`fast`,fTr=`ultrafast`,pTr=`default`"),
        ("fast-fallback", "hTr=[mTr,{description:XO.fastDescription,iconKind:`fast`,label:XO.fastLabel,tier:null,value:uTr}]"),
        ("config-key", "function Rss(e){return e==null?`service_tier`:`profiles.${e}.service_tier`}"),
        ("request-tier", "serviceTierForRequest:S")
    ];

    // OpenAI.Codex 26.818.5229.0 keeps the same reviewed behavior but renames its minified
    // symbols again. Bind all five edits and all four downstream semantics to this exact build.
    private const string Current5229VisibilityGateOriginal =
        "function $os(e){let t=(0,ess.c)(6),n=Y(Hk),r=e?.hostId??n,i=FA(r),a=i?.authMethod===`chatgpt`,o=i?.authMethod??null,s;t[0]!==r||t[1]!==o?(s={authMethod:o,hostId:r},t[0]=r,t[1]=o,t[2]=s):s=t[2];let{data:c,isPending:l}=hs(Lb,s),u=!!i?.isLoading||a&&l,d=a&&!u&&c!=null&&c?.requirements?.featureRequirements?.fast_mode!==!1,f;return t[3]!==u||t[4]!==d?(f={isServiceTierAllowed:d,isLoading:u},t[3]=u,t[4]=d,t[5]=f):f=t[5],f}";

    private const string Current5229VisibilityGatePatched =
        "function $os(e){let t=(0,ess.c)(6),n=Y(Hk),r=e?.hostId??n,i=FA(r),a=i?.authMethod===`chatgpt`,o=i?.authMethod??null,s;t[0]!==r||t[1]!==o?(s={authMethod:o,hostId:r},t[0]=r,t[1]=o,t[2]=s):s=t[2];let{data:c,isPending:l}=hs(Lb,s),u=!!i?.isLoading||a&&l,d=!u&&(a?c!=null&&c?.requirements?.featureRequirements?.fast_mode!==!1:o===`personalAccessToken`||o===`apikey`),f;return t[3]!==u||t[4]!==d?(f={isServiceTierAllowed:d,isLoading:u},t[3]=u,t[4]=d,t[5]=f):f=t[5],f}";

    private const string Current5229ConfigReadGateOriginal =
        "async function Yii(e,t){let n=await Kii(e,t);if(n!==`chatgpt`)return!1;let r=await F0t(e,t,{priority:`critical`});return e.query.setData(Lb,{authMethod:n,hostId:t},r),r.requirements?.featureRequirements?.fast_mode!==!1}";

    private const string Current5229ConfigReadGatePatched =
        "async function Yii(e,t){let n=await Kii(e,t);if(n===`personalAccessToken`||n===`apikey`)return!0;if(n!==`chatgpt`)return!1;let r=await F0t(e,t,{priority:`critical`});return e.query.setData(Lb,{authMethod:n,hostId:t},r),r.requirements?.featureRequirements?.fast_mode!==!1}";

    private const string Current5229ServiceTierAuthCaptureOriginal =
        "u=hs(Dk,e),d=hs(dls,e),f=FA(o.hostId)?.authMethod??null,p;";

    private const string Current5229ServiceTierAuthCapturePatched =
        "u=hs(Dk,e),d=hs(dls,e),f=FA(o.hostId)?.authMethod??null,p,_camAuth;_camAuth=f;";

    private const string Current5229ServiceTierOptionsOriginal =
        "T=p,E=o.hostId,w=gEr(s),";

    private const string Current5229ServiceTierOptionsPatched =
        "T=p,E=o.hostId,w=(()=>{let e=gEr(s);return(_camAuth===`personalAccessToken`||_camAuth===`apikey`)&&!e.some(e=>e.value===EEr)?[...e,...jEr.filter(e=>e.value===EEr)]:e})(),";

    private const string Current5229ServiceTierSelectionOriginal =
        "S=e!=null&&(u?.serviceTier!==void 0||d!==void 0)?y?k:null:SEr(s,k,y),x=S==null?null:xEr(s,S);";

    private const string Current5229ServiceTierSelectionPatched =
        "S=e!=null&&(u?.serviceTier!==void 0||d!==void 0)?y?k:null:SEr(s,k,y),x=S==null?null:xEr(s,S)??((_camAuth===`personalAccessToken`||_camAuth===`apikey`)&&S===EEr?EEr:null);";

    private static readonly (string Name, string Original, string Patched)[] Current5229RendererPatchContract =
    [
        ("visibility", Current5229VisibilityGateOriginal, Current5229VisibilityGatePatched),
        ("config", Current5229ConfigReadGateOriginal, Current5229ConfigReadGatePatched),
        ("auth-capture", Current5229ServiceTierAuthCaptureOriginal, Current5229ServiceTierAuthCapturePatched),
        ("options", Current5229ServiceTierOptionsOriginal, Current5229ServiceTierOptionsPatched),
        ("selection", Current5229ServiceTierSelectionOriginal, Current5229ServiceTierSelectionPatched)
    ];

    private static readonly (string Name, string Value)[] Current5229RendererSemanticAnchors =
    [
        ("fast-is-priority", "EEr=`priority`,DEr=`fast`,OEr=`ultrafast`,kEr=`default`"),
        ("fast-fallback", "jEr=[AEr,{description:ok.fastDescription,iconKind:`fast`,label:ok.fastLabel,tier:null,value:EEr}]"),
        ("config-key", "function ols(e){return e==null?`service_tier`:`profiles.${e}.service_tier`}"),
        ("request-tier", "serviceTierForRequest:S")
    ];

    private sealed record RendererPatchProfile(
        string Name,
        string BundleUrl,
        string SourceSha256,
        (string Name, string Original, string Patched)[] PatchContract,
        (string Name, string Value)[] SemanticAnchors,
        string? PatchedSha256 = null,
        bool IsCompatibilityProfile = false);

    private static readonly RendererPatchProfile[] RendererPatchProfiles =
    [
        new(
            "legacy-2026-08-20",
            LegacyRendererBundleUrl,
            PreviousRendererSourceSha256,
            PreviousRendererPatchContract,
            PreviousRendererSemanticAnchors),
        new(
            "current-2026-08-20",
            PreviousRendererBundleUrl,
            PreviousRendererSourceSha256,
            PreviousRendererPatchContract,
            PreviousRendererSemanticAnchors),
        new(
            "current-2026-08-21",
            CurrentRendererBundleUrl,
            CurrentRendererSourceSha256,
            CurrentRendererPatchContract,
            CurrentRendererSemanticAnchors),
        new(
            "current-2026-08-21-3698",
            LatestRendererBundleUrl,
            LatestRendererSourceSha256,
            LatestRendererPatchContract,
            LatestRendererSemanticAnchors),
        new(
            "current-2026-08-23-5229",
            Current5229RendererBundleUrl,
            Current5229RendererSourceSha256,
            Current5229RendererPatchContract,
            Current5229RendererSemanticAnchors,
            Current5229RendererPatchedSha256)
    ];

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexAccountManager",
        "native-fast-bridge.log");

    internal static int RunProcess(string[] args)
    {
        try
        {
            var port = ReadRequiredPort(args);
            var expectedBrowserId = ReadOptionalArgument(args, BrowserIdArgument);
            if (string.IsNullOrWhiteSpace(expectedBrowserId) ||
                !BrowserIdentityPattern.IsMatch(expectedBrowserId))
            {
                throw new ArgumentException(
                    "A verified Codex CDP browser identity is required before the native Fast bridge starts.");
            }
            var ownerPid = ReadRequiredPositiveInt(args, OwnerPidArgument);
            var ownerStartTicks = ReadRequiredPositiveLong(args, OwnerStartTicksArgument);
            var ownerRoot = ReadRequiredDirectory(args, OwnerRootArgument);
            var allowRendererReload = args.Contains(
                AllowRendererReloadArgument,
                StringComparer.OrdinalIgnoreCase);
            if (!IsExpectedCdpOwner(port, ownerPid, ownerStartTicks, ownerRoot))
            {
                throw new InvalidOperationException(
                    "The Codex CDP port is not owned by the verified Codex process.");
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            using var singleton = new Mutex(
                initiallyOwned: false,
                name: BuildSingletonMutexName(
                    port,
                    expectedBrowserId,
                    ownerPid,
                    ownerStartTicks));
            using var rendererReady = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.ManualReset,
                name: BuildRendererReadyEventName(
                    port,
                    expectedBrowserId,
                    ownerPid,
                    ownerStartTicks));
            using var rendererPatchSkipped = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.ManualReset,
                name: BuildRendererPatchSkippedEventName(
                    port,
                    expectedBrowserId,
                    ownerPid,
                    ownerStartTicks));
            using var rendererReloadAttempted = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.ManualReset,
                name: BuildRendererReloadAttemptedEventName(
                    port,
                    expectedBrowserId,
                    ownerPid,
                    ownerStartTicks));
            var ownsSingleton = false;
            try
            {
                try
                {
                    ownsSingleton = singleton.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    ownsSingleton = true;
                }
                if (!ownsSingleton)
                {
                    Log("bridge_already_running", $"port={port}; browser={expectedBrowserId}");
                    return 0;
                }

                // Only the process that acquired the singleton may clear a previous state.
                // A duplicate launcher must not erase readiness published by the live owner.
                rendererReady.Reset();
                rendererPatchSkipped.Reset();
                rendererReloadAttempted.Reset();
                return RunWatchAsync(
                        port,
                        expectedBrowserId,
                        ownerPid,
                        ownerStartTicks,
                        ownerRoot,
                        allowRendererReload,
                        rendererReady,
                        rendererPatchSkipped,
                        rendererReloadAttempted,
                        cancellation.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                if (ownsSingleton)
                {
                    singleton.ReleaseMutex();
                }
            }
        }
        catch (Exception ex)
        {
            Log("bridge_failed", ex.Message);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal static Process StartDetached(
        int port,
        string expectedBrowserId,
        int ownerPid,
        long ownerStartTicks,
        string ownerRoot,
        bool allowRendererReload)
    {
        ValidatePort(port);
        if (string.IsNullOrWhiteSpace(expectedBrowserId) ||
            !BrowserIdentityPattern.IsMatch(expectedBrowserId))
        {
            throw new ArgumentException("The supplied Codex CDP browser identity is invalid.", nameof(expectedBrowserId));
        }
        if (ownerPid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerPid));
        }
        if (ownerStartTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerStartTicks));
        }
        ownerRoot = ValidateOwnerRoot(ownerRoot);

        var startInfo = BuildStartInfo(
            port,
            expectedBrowserId,
            ownerPid,
            ownerStartTicks,
            ownerRoot,
            allowRendererReload);
        return Process.Start(startInfo) ??
               throw new InvalidOperationException("Codex native Fast bridge did not return a process handle.");
    }

    internal static bool WaitForRendererPatch(
        int port,
        string expectedBrowserId,
        TimeSpan timeout)
    {
        return WaitForRendererPatch(port, expectedBrowserId, 0, 0, timeout);
    }

    internal static bool WaitForRendererPatch(
        int port,
        string expectedBrowserId,
        int ownerPid,
        long ownerStartTicks,
        TimeSpan timeout)
    {
        ValidatePort(port);
        if (string.IsNullOrWhiteSpace(expectedBrowserId) ||
            !BrowserIdentityPattern.IsMatch(expectedBrowserId))
        {
            throw new ArgumentException("The supplied Codex CDP browser identity is invalid.", nameof(expectedBrowserId));
        }
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        if ((ownerPid == 0) != (ownerStartTicks == 0) ||
            ownerPid < 0 || ownerStartTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerPid));
        }

        using var rendererReady = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: BuildRendererReadyEventName(
                port,
                expectedBrowserId,
                ownerPid,
                ownerStartTicks));
        return rendererReady.WaitOne(timeout);
    }

    internal static NativeFastPatchWaitOutcome WaitForRendererPatchOutcome(
        int port,
        string expectedBrowserId,
        int ownerPid,
        long ownerStartTicks,
        TimeSpan timeout)
    {
        ValidatePort(port);
        if (string.IsNullOrWhiteSpace(expectedBrowserId) ||
            !BrowserIdentityPattern.IsMatch(expectedBrowserId))
        {
            throw new ArgumentException("The supplied Codex CDP browser identity is invalid.", nameof(expectedBrowserId));
        }
        if (ownerPid <= 0 || ownerStartTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerPid));
        }
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var rendererReady = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: BuildRendererReadyEventName(
                port,
                expectedBrowserId,
                ownerPid,
                ownerStartTicks));
        using var rendererPatchSkipped = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: BuildRendererPatchSkippedEventName(
                port,
                expectedBrowserId,
                ownerPid,
                ownerStartTicks));
        using var rendererReloadAttempted = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: BuildRendererReloadAttemptedEventName(
                port,
                expectedBrowserId,
                ownerPid,
                ownerStartTicks));

        var deadline = DateTime.UtcNow + timeout;
        var reloadAttemptObserved = false;
        while (true)
        {
            if (rendererReady.WaitOne(TimeSpan.Zero))
            {
                return NativeFastPatchWaitOutcome.Patched;
            }
            if (!reloadAttemptObserved &&
                rendererReloadAttempted.WaitOne(TimeSpan.Zero))
            {
                reloadAttemptObserved = true;
            }
            if (!reloadAttemptObserved &&
                rendererPatchSkipped.WaitOne(TimeSpan.Zero))
            {
                // Manual-reset events can become signaled together at a target boundary.
                // Re-sample the higher-priority facts before accepting the terminal no-reload
                // outcome so a real Page.reload can never be hidden by another target's skip.
                if (rendererReady.WaitOne(TimeSpan.Zero))
                {
                    return NativeFastPatchWaitOutcome.Patched;
                }
                if (rendererReloadAttempted.WaitOne(TimeSpan.Zero))
                {
                    reloadAttemptObserved = true;
                }
                else
                {
                    return NativeFastPatchWaitOutcome.SkippedWithoutReload;
                }
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                // One final ordered snapshot closes the timeout-boundary race. Once reload is
                // observed it is sticky and no WithoutReload outcome is permitted.
                if (rendererReady.WaitOne(TimeSpan.Zero))
                {
                    return NativeFastPatchWaitOutcome.Patched;
                }
                if (!reloadAttemptObserved &&
                    rendererReloadAttempted.WaitOne(TimeSpan.Zero))
                {
                    reloadAttemptObserved = true;
                }
                if (!reloadAttemptObserved &&
                    rendererPatchSkipped.WaitOne(TimeSpan.Zero))
                {
                    if (rendererReady.WaitOne(TimeSpan.Zero))
                    {
                        return NativeFastPatchWaitOutcome.Patched;
                    }
                    if (rendererReloadAttempted.WaitOne(TimeSpan.Zero))
                    {
                        reloadAttemptObserved = true;
                    }
                    else
                    {
                        return NativeFastPatchWaitOutcome.SkippedWithoutReload;
                    }
                }
                return reloadAttemptObserved
                    ? NativeFastPatchWaitOutcome.TimedOutAfterReloadAttempt
                    : NativeFastPatchWaitOutcome.TimedOutWithoutReload;
            }

            if (reloadAttemptObserved)
            {
                _ = rendererReady.WaitOne(remaining);
                continue;
            }

            _ = WaitHandle.WaitAny(
                [rendererReady, rendererReloadAttempted, rendererPatchSkipped],
                remaining);
            if (rendererReloadAttempted.WaitOne(TimeSpan.Zero))
            {
                reloadAttemptObserved = true;
            }
        }
    }

    internal static async Task<bool> TryApplyAccountDisplayAsync(
        int port,
        string expectedBrowserId,
        int ownerPid,
        long ownerStartTicks,
        string ownerRoot,
        CodexAccountDisplay display,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(display);
        ValidatePort(port);
        if (string.IsNullOrWhiteSpace(expectedBrowserId) ||
            !BrowserIdentityPattern.IsMatch(expectedBrowserId))
        {
            throw new ArgumentException("The supplied Codex CDP browser identity is invalid.", nameof(expectedBrowserId));
        }
        if (ownerPid <= 0 || ownerStartTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerPid));
        }
        ownerRoot = ValidateOwnerRoot(ownerRoot);
        if (!IsExpectedCdpOwner(port, ownerPid, ownerStartTicks, ownerRoot))
        {
            return false;
        }

        using var httpClient = CreateLoopbackClient();
        var version = await ReadBrowserVersionAsync(httpClient, port, cancellationToken);
        if (!version.BrowserId.Equals(expectedBrowserId, StringComparison.Ordinal))
        {
            return false;
        }
        var primaryTargets = (await ReadAppTargetsAsync(httpClient, port, cancellationToken))
            .Where(target => IsPrimaryOfficialCodexPageUrl(target.PageUrl))
            .ToArray();
        if (primaryTargets.Length != 1)
        {
            Log("account_display_skipped", $"port={port}; primary_targets={primaryTargets.Length}");
            return false;
        }

        await using var connection = await CdpConnection.ConnectAsync(
            primaryTargets[0].WebSocketUrl,
            cancellationToken);
        await connection.SendAsync(
            "Runtime.enable",
            new { },
            CommandTimeout,
            cancellationToken);
        var versionBeforeSend = await ReadBrowserVersionAsync(httpClient, port, cancellationToken);
        var targetsBeforeSend = (await ReadAppTargetsAsync(httpClient, port, cancellationToken))
            .Where(target => IsPrimaryOfficialCodexPageUrl(target.PageUrl))
            .ToArray();
        if (!versionBeforeSend.BrowserId.Equals(expectedBrowserId, StringComparison.Ordinal) ||
            targetsBeforeSend.Length != 1 ||
            !targetsBeforeSend[0].Id.Equals(primaryTargets[0].Id, StringComparison.Ordinal) ||
            !targetsBeforeSend[0].WebSocketUrl.AbsoluteUri.Equals(
                primaryTargets[0].WebSocketUrl.AbsoluteUri,
                StringComparison.Ordinal) ||
            !IsExpectedCdpOwner(port, ownerPid, ownerStartTicks, ownerRoot))
        {
            return false;
        }
        var frameTreeBeforeSend = await connection.SendAsync(
            "Page.getFrameTree",
            new { },
            CommandTimeout,
            cancellationToken);
        if (!IsExpectedPrimaryOfficialCodexFrameTree(
                frameTreeBeforeSend,
                primaryTargets[0].PageUrl) ||
            !IsExpectedCdpOwner(port, ownerPid, ownerStartTicks, ownerRoot))
        {
            return false;
        }

        // Build and send the PII-bearing expression only after the connected target, browser
        // identity and immutable listener owner have all been revalidated.
        var source = BuildAccountDisplayScript(display);
        var evaluation = await connection.SendAsync(
            "Runtime.evaluate",
            new
            {
                expression = source,
                returnByValue = true,
                awaitPromise = false,
                userGesture = false
            },
            CommandTimeout,
            cancellationToken);
        if (!IsSuccessfulAccountDisplayEvaluation(evaluation) ||
            !IsExpectedCdpOwner(port, ownerPid, ownerStartTicks, ownerRoot))
        {
            return false;
        }

        Log(
            "account_display_observer_installed",
            $"port={port}; target={primaryTargets[0].Id}; dual_login={display.ModelAccountLabel != null}");
        return true;
    }

    private static ProcessStartInfo BuildStartInfo(
        int port,
        string expectedBrowserId,
        int? ownerPid = null,
        long? ownerStartTicks = null,
        string? ownerRoot = null,
        bool allowRendererReload = false)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("The current executable path could not be resolved.");
        }

        var startInfo = new ProcessStartInfo(processPath)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var variableName in CredentialEnvironmentVariableNames)
        {
            startInfo.Environment.Remove(variableName);
        }
        if (Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = typeof(CodexNativeFastBridge).Assembly.GetName().Name;
            var assemblyPath = string.IsNullOrWhiteSpace(assemblyName)
                ? null
                : Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new InvalidOperationException("The bridge assembly path could not be resolved.");
            }
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add(ProcessArgument);
        startInfo.ArgumentList.Add(PortArgument);
        startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(BrowserIdArgument);
        startInfo.ArgumentList.Add(expectedBrowserId);
        if (ownerPid.HasValue || ownerStartTicks.HasValue || !string.IsNullOrWhiteSpace(ownerRoot))
        {
            if (!ownerPid.HasValue || !ownerStartTicks.HasValue || string.IsNullOrWhiteSpace(ownerRoot))
            {
                throw new ArgumentException("The bridge owner identity must be complete.");
            }
            startInfo.ArgumentList.Add(OwnerPidArgument);
            startInfo.ArgumentList.Add(ownerPid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(OwnerStartTicksArgument);
            startInfo.ArgumentList.Add(ownerStartTicks.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(OwnerRootArgument);
            startInfo.ArgumentList.Add(ownerRoot);
        }
        if (allowRendererReload)
        {
            startInfo.ArgumentList.Add(AllowRendererReloadArgument);
        }
        return startInfo;
    }

    private static string BuildSingletonMutexName(
        int port,
        string browserId,
        int ownerPid = 0,
        long ownerStartTicks = 0)
    {
        return $"Local\\CodexAccountManager.NativeFast.{port}.{BuildBrowserIdentityHash(browserId, ownerPid, ownerStartTicks)}";
    }

    private static string BuildRendererReadyEventName(
        int port,
        string browserId,
        int ownerPid = 0,
        long ownerStartTicks = 0)
    {
        return $"Local\\CodexAccountManager.NativeFast.Ready.{port}.{BuildBrowserIdentityHash(browserId, ownerPid, ownerStartTicks)}";
    }

    private static string BuildRendererPatchSkippedEventName(
        int port,
        string browserId,
        int ownerPid = 0,
        long ownerStartTicks = 0)
    {
        return $"Local\\CodexAccountManager.NativeFast.Skipped.{port}.{BuildBrowserIdentityHash(browserId, ownerPid, ownerStartTicks)}";
    }

    private static string BuildRendererReloadAttemptedEventName(
        int port,
        string browserId,
        int ownerPid = 0,
        long ownerStartTicks = 0)
    {
        return $"Local\\CodexAccountManager.NativeFast.ReloadAttempted.{port}.{BuildBrowserIdentityHash(browserId, ownerPid, ownerStartTicks)}";
    }

    private static string BuildBrowserIdentityHash(
        string browserId,
        int ownerPid = 0,
        long ownerStartTicks = 0)
    {
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                browserId + ":" + ownerPid.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ":" + ownerStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture))))[..32];
    }

    private static async Task<int> RunWatchAsync(
        int port,
        string? expectedBrowserId,
        int ownerPid,
        long ownerStartTicks,
        string ownerRoot,
        bool allowRendererReload,
        EventWaitHandle rendererReady,
        EventWaitHandle rendererPatchSkipped,
        EventWaitHandle rendererReloadAttempted,
        CancellationToken cancellationToken)
    {
        ValidatePort(port);
        using var httpClient = CreateLoopbackClient();
        var version = await WaitForBrowserVersionAsync(
            httpClient,
            port,
            expectedBrowserId,
            cancellationToken);
        expectedBrowserId = version.BrowserId;

        Log("bridge_attached", $"port={port}; browser={expectedBrowserId}");
        var workers = new Dictionary<string, RendererWorker>(StringComparer.Ordinal);
        var reloadGates = new Dictionary<string, ControlledReloadGate>(StringComparer.Ordinal);
        var readiness = new RendererReadinessState(rendererReady);
        var endpointFailures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!IsExpectedCdpOwner(port, ownerPid, ownerStartTicks, ownerRoot))
                {
                    Log("cdp_owner_changed", $"port={port}; pid={ownerPid}");
                    return 4;
                }
                IReadOnlyList<CdpTarget> targets;
                try
                {
                    var currentVersion = await ReadBrowserVersionAsync(httpClient, port, cancellationToken);
                    if (!currentVersion.BrowserId.Equals(expectedBrowserId, StringComparison.Ordinal))
                    {
                        Log(
                            "browser_identity_changed",
                            $"expected={expectedBrowserId}; actual={currentVersion.BrowserId}");
                        return 3;
                    }
                    targets = await ReadAppTargetsAsync(httpClient, port, cancellationToken);
                    endpointFailures = 0;
                }
                catch (Exception ex) when (
                    ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
                {
                    readiness.Suspend();
                    endpointFailures++;
                    if (endpointFailures >= 12)
                    {
                        Log("endpoint_closed", $"port={port}; error={ex.Message}");
                        return 0;
                    }
                    await Task.Delay(EndpointPollInterval, cancellationToken);
                    continue;
                }

                var activeTargetIds = targets.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
                readiness.Suspend();
                readiness.SynchronizeTargets(activeTargetIds);
                foreach (var staleId in workers.Keys.Where(id => !activeTargetIds.Contains(id)).ToArray())
                {
                    await workers[staleId].DisposeAsync();
                    workers.Remove(staleId);
                }

                foreach (var target in targets)
                {
                    if (workers.TryGetValue(target.Id, out var existing) &&
                        !existing.IsClosed &&
                        !existing.RequiresReconnect)
                    {
                        continue;
                    }

                    var generation = readiness.BeginConnection(target.Id);
                    if (existing != null)
                    {
                        if (existing.RequiresReconnect)
                        {
                            Log("target_reconnect_scheduled", $"target={target.Id}");
                        }
                        await existing.DisposeAsync();
                        workers.Remove(target.Id);
                    }

                    RendererWorker? worker = null;
                    try
                    {
                        worker = await RendererWorker.ConnectAsync(
                            target,
                            port,
                            () => readiness.MarkPending(target.Id, generation),
                            () => readiness.MarkReady(target.Id, generation),
                            cancellationToken);
                        workers.Add(target.Id, worker);
                        worker.ActivateReadiness();
                        if (!reloadGates.TryGetValue(target.Id, out var reloadGate))
                        {
                            reloadGate = new ControlledReloadGate();
                            reloadGates.Add(target.Id, reloadGate);
                        }
                    }
                    catch (Exception ex)
                    {
                        readiness.MarkPending(target.Id, generation);
                        if (worker != null)
                        {
                            workers.Remove(target.Id);
                            await worker.DisposeAsync();
                        }
                        Log("target_attach_failed", $"target={target.Id}; error={ex.Message}");
                    }
                }

                // Arm every current page before any controlled reload. This prevents one
                // renderer from fetching the reviewed bundle while another target is not yet
                // protected by the exact Fetch response contract.
                var allTargetsAttached = targets.All(target =>
                    workers.TryGetValue(target.Id, out var worker) &&
                    !worker.IsClosed &&
                    !worker.RequiresReconnect &&
                    worker.IsPreflightAllowed);
                if (allTargetsAttached)
                {
                    foreach (var target in targets)
                    {
                        await workers[target.Id].StartControlledReloadAsync(
                            reloadGates[target.Id],
                            allowRendererReload,
                            () => rendererReloadAttempted.Set(),
                            cancellationToken);
                    }
                }
                else if (allowRendererReload &&
                         targets.Count != 0 &&
                         targets.All(target => workers.TryGetValue(target.Id, out var worker) &&
                                               !worker.IsClosed))
                {
                    // Every current target completed preflight, but at least one renderer was
                    // rejected.  No Page.reload command can run in this state. Publish that
                    // terminal fact and exit so the parent preserves the already-ready client
                    // instead of mistaking a started helper process for a started reload.
                    if (rendererReloadAttempted.WaitOne(TimeSpan.Zero))
                    {
                        Log(
                            "renderer_patch_skip_suppressed",
                            $"target_count={targets.Count}; reload_attempted=true; reason=preflight-rejected-after-reload");
                    }
                    else
                    {
                        rendererPatchSkipped.Set();
                        Log(
                            "renderer_patch_skipped",
                            $"target_count={targets.Count}; reload_attempted=false; reason=preflight-rejected");
                        return 0;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                readiness.Resume();
                await Task.Delay(EndpointPollInterval, cancellationToken);
            }
        }
        finally
        {
            readiness.Suspend();
            readiness.SynchronizeTargets(Array.Empty<string>());
            foreach (var worker in workers.Values)
            {
                await worker.DisposeAsync();
            }
        }

        return 0;
    }

    private static HttpClient CreateLoopbackClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    private static async Task<CdpBrowserVersion> WaitForBrowserVersionAsync(
        HttpClient client,
        int port,
        string? expectedBrowserId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + StartupTimeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var version = await ReadBrowserVersionAsync(client, port, cancellationToken);
                if (expectedBrowserId == null ||
                    version.BrowserId.Equals(expectedBrowserId, StringComparison.Ordinal))
                {
                    return version;
                }
                throw new InvalidOperationException(
                    $"Codex CDP browser identity mismatch: expected {expectedBrowserId}, got {version.BrowserId}.");
            }
            catch (Exception ex) when (
                ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
            {
                lastError = ex;
            }
            await Task.Delay(EndpointPollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"No Codex CDP endpoint appeared on 127.0.0.1:{port}: {lastError?.Message ?? "timed out"}");
    }

    private static async Task<CdpBrowserVersion> ReadBrowserVersionAsync(
        HttpClient client,
        int port,
        CancellationToken cancellationToken)
    {
        using var document = await ReadJsonAsync(client, port, "/json/version", cancellationToken);
        var webSocketUrl = document.RootElement.TryGetProperty("webSocketDebuggerUrl", out var value)
            ? value.GetString()
            : null;
        var validated = ValidateWebSocketUrl(webSocketUrl, port, "browser", expectedTargetId: null);
        var browserId = validated.AbsolutePath[(validated.AbsolutePath.LastIndexOf('/') + 1)..];
        if (!BrowserIdentityPattern.IsMatch(browserId))
        {
            throw new InvalidOperationException("Codex returned an invalid CDP browser identity.");
        }
        return new CdpBrowserVersion(browserId, validated);
    }

    private static async Task<IReadOnlyList<CdpTarget>> ReadAppTargetsAsync(
        HttpClient client,
        int port,
        CancellationToken cancellationToken)
    {
        using var document = await ReadJsonAsync(client, port, "/json/list", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Codex CDP target list was not an array.");
        }

        var targets = new List<CdpTarget>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var id = element.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            var type = element.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            var pageUrl = element.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
            var webSocketUrl = element.TryGetProperty("webSocketDebuggerUrl", out var wsValue)
                ? wsValue.GetString()
                : null;
            if (!string.Equals(type, "page", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(id) ||
                !BrowserIdentityPattern.IsMatch(id) ||
                !IsReviewedOfficialCodexPageUrl(pageUrl))
            {
                continue;
            }

            var validated = ValidateWebSocketUrl(webSocketUrl, port, "page", id);
            targets.Add(new CdpTarget(id, pageUrl!, validated));
        }
        return targets;
    }

    internal static bool IsReviewedOfficialCodexPageUrl(string? value)
    {
        // Electron reports these exact URLs for the reviewed official Codex renderers.
        // Keep this as a literal allow-list: accepting a structurally similar app:// URL
        // could attach the bridge to an unrelated privileged page.
        return string.Equals(value, "app://codex/", StringComparison.Ordinal) ||
               string.Equals(value, "app://-/index.html", StringComparison.Ordinal) ||
               string.Equals(
                   value,
                   "app://-/index.html?initialRoute=%2Favatar-overlay",
                   StringComparison.Ordinal);
    }

    private static bool IsPrimaryOfficialCodexPageUrl(string? value) =>
        string.Equals(value, "app://codex/", StringComparison.Ordinal) ||
        string.Equals(value, "app://-/index.html", StringComparison.Ordinal);

    private static bool IsExpectedPrimaryOfficialCodexFrameTree(
        JsonElement result,
        string expectedPageUrl)
    {
        if (!IsPrimaryOfficialCodexPageUrl(expectedPageUrl) ||
            !result.TryGetProperty("frameTree", out var frameTree) ||
            frameTree.ValueKind != JsonValueKind.Object ||
            !frameTree.TryGetProperty("frame", out var frame) ||
            frame.ValueKind != JsonValueKind.Object ||
            !frame.TryGetProperty("url", out var url) ||
            url.ValueKind != JsonValueKind.String ||
            !string.Equals(url.GetString(), expectedPageUrl, StringComparison.Ordinal))
        {
            return false;
        }

        return !frame.TryGetProperty("parentId", out var parentId) ||
               parentId.ValueKind == JsonValueKind.Null ||
               parentId.ValueKind == JsonValueKind.String &&
               string.IsNullOrWhiteSpace(parentId.GetString());
    }

    internal static string BuildAccountDisplayScript(CodexAccountDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        var modelAccountLabel = NormalizeAccountDisplayText(
            display.ModelAccountLabel,
            maximumLength: 180,
            required: false);
        var modelAccountTypeLabel = modelAccountLabel == null
            ? null
            : NormalizeAccountDisplayText(
                display.ModelAccountTypeLabel ?? "模型账号（本地）",
                maximumLength: 40,
                required: true);
        var chatGptEmail = NormalizeAccountDisplayText(
            display.ChatGptEmail,
            maximumLength: 320,
            required: true)!;
        try
        {
            var parsed = new System.Net.Mail.MailAddress(chatGptEmail);
            if (!parsed.Address.Equals(chatGptEmail, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The ChatGPT account email is not canonical.", nameof(display));
            }
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The ChatGPT account email is invalid.", nameof(display), ex);
        }

        var payloadJson = JsonSerializer.Serialize(new
        {
            modelAccountLabel,
            modelAccountTypeLabel,
            chatGptEmail
        });
        return $$"""
            (() => {
              const STATE_KEY = "__CODEX_ACCOUNT_MANAGER_ACCOUNT_DISPLAY_V1__";
              const ATTRIBUTE = "data-cam-account-display";
              const VERSION = "1.2.0";
              const payload = {{payloadJson}};
              const previous = window[STATE_KEY];
              if (previous && typeof previous.cleanup === "function") previous.cleanup();

              let scheduled = null;
              const removeAll = () => {
                document.querySelectorAll(`[${ATTRIBUTE}]`).forEach((node) => node.remove());
              };
              const ensure = () => {
                const shell = document.querySelector("aside.app-shell-left-panel");
                if (!shell) {
                  removeAll();
                  return { shellReady: false, triggerReady: false, displayMounted: false };
                }
                const triggers = [...shell.querySelectorAll('button.sidebar-item[aria-haspopup="menu"]')]
                  .filter((button) => {
                    const avatar = [...button.querySelectorAll("span")]
                      .some((node) => node.classList.contains("rounded-full"));
                    const name = [...button.querySelectorAll("span")]
                      .some((node) => node.classList.contains("truncate") &&
                        node.classList.contains("flex-1") && node.classList.contains("min-w-0"));
                    return avatar && name && Boolean(button.id);
                  });
                if (triggers.length !== 1) {
                  removeAll();
                  return { shellReady: true, triggerReady: false, displayMounted: false };
                }

                const trigger = triggers[0];
                const menus = [...document.querySelectorAll('[role="menu"][aria-labelledby]')]
                  .filter((menu) => menu.getAttribute("aria-labelledby") === trigger.id &&
                    menu.getAttribute("data-state") === "open");
                if (menus.length !== 1) {
                  removeAll();
                  return { shellReady: true, triggerReady: true, displayMounted: false };
                }
                const menu = menus[0];
                const items = [...menu.querySelectorAll('[role="menuitem"]')]
                  .filter((item) => item.closest('[role="menu"]') === menu);
                if (items.length < 1) {
                  removeAll();
                  return { shellReady: true, triggerReady: true, displayMounted: false };
                }
                const host = items[0];
                const contentRows = [...host.children]
                  .filter((node) => !node.hasAttribute(ATTRIBUTE));
                if (contentRows.length !== 1) {
                  removeAll();
                  return { shellReady: true, triggerReady: true, displayMounted: false };
                }
                const profileRow = contentRows[0];
                const profileNames = [...contentRows[0].querySelectorAll("span")]
                  .filter((node) => node.classList.contains("truncate") &&
                    node.classList.contains("flex-1") && node.classList.contains("min-w-0"));
                const hasReviewedAvatar = Boolean(profileRow.querySelector("img")) ||
                  [...profileRow.querySelectorAll("span")]
                    .some((node) => node.classList.contains("rounded-full"));
                if (profileNames.length !== 1 || !hasReviewedAvatar) {
                  removeAll();
                  return { shellReady: true, triggerReady: true, displayMounted: false };
                }

                let block = [...host.children]
                  .find((node) => node.getAttribute(ATTRIBUTE) === VERSION);
                if (!block) {
                  [...host.querySelectorAll(`[${ATTRIBUTE}]`)].forEach((node) => node.remove());
                  block = document.createElement("div");
                  block.setAttribute(ATTRIBUTE, VERSION);
                  block.setAttribute("role", "note");
                  block.style.cssText =
                    "display:grid;gap:2px;min-width:0;margin-top:4px;padding-left:26px;" +
                    "font-size:11px;line-height:1.35;opacity:.78;pointer-events:none;";
                  host.appendChild(block);
                }

                const entries = payload.modelAccountLabel
                  ? [[payload.modelAccountTypeLabel, payload.modelAccountLabel], ["ChatGPT", payload.chatGptEmail]]
                  : [["ChatGPT", payload.chatGptEmail]];
                const signature = JSON.stringify(entries);
                if (block.dataset.signature !== signature) {
                  block.replaceChildren();
                  block.dataset.signature = signature;
                  for (const [labelText, valueText] of entries) {
                    const line = document.createElement("div");
                    line.style.cssText = "display:flex;gap:5px;min-width:0;white-space:nowrap;";
                    line.title = `${labelText}：${valueText}`;
                    const label = document.createElement("span");
                    label.textContent = `${labelText}：`;
                    label.style.flex = "0 0 auto";
                    const value = document.createElement("span");
                    value.textContent = valueText;
                    value.style.cssText =
                      "min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;";
                    line.append(label, value);
                    block.appendChild(line);
                  }
                  block.setAttribute(
                    "aria-label",
                    entries.map(([label, value]) => `${label}：${value}`).join("；"));
                }
                return { shellReady: true, triggerReady: true, displayMounted: true };
              };
              const schedule = () => {
                if (scheduled !== null) return;
                scheduled = setTimeout(() => {
                  scheduled = null;
                  ensure();
                }, 60);
              };
              const observer = new MutationObserver(schedule);
              observer.observe(document.documentElement, {
                childList: true,
                subtree: true,
                attributes: true,
                attributeFilter: ["data-state", "aria-labelledby"]
              });
              const cleanup = () => {
                observer.disconnect();
                if (scheduled !== null) clearTimeout(scheduled);
                scheduled = null;
                removeAll();
                if (window[STATE_KEY]?.cleanup === cleanup) delete window[STATE_KEY];
              };
              window[STATE_KEY] = { cleanup, ensure, observer, version: VERSION };
              const initial = ensure();
              return {
                observerInstalled: true,
                shellReady: initial.shellReady,
                triggerReady: initial.triggerReady,
                displayMounted: initial.displayMounted,
                version: VERSION
              };
            })()
            """;
    }

    private static string? NormalizeAccountDisplayText(
        string? value,
        int maximumLength,
        bool required)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            if (required)
            {
                throw new ArgumentException("A required account display value is missing.", nameof(value));
            }
            return null;
        }
        if (normalized.Length > maximumLength ||
            normalized.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
        {
            throw new ArgumentException("An account display value is unsafe or too long.", nameof(value));
        }
        return normalized;
    }

    private static bool IsSuccessfulAccountDisplayEvaluation(JsonElement evaluation)
    {
        if (evaluation.TryGetProperty("exceptionDetails", out _ ) ||
            !evaluation.TryGetProperty("result", out var remoteObject) ||
            !remoteObject.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("observerInstalled", out var observerInstalled) ||
            observerInstalled.ValueKind != JsonValueKind.True ||
            !value.TryGetProperty("shellReady", out var shellReady) ||
            shellReady.ValueKind != JsonValueKind.True ||
            !value.TryGetProperty("triggerReady", out var triggerReady) ||
            triggerReady.ValueKind != JsonValueKind.True ||
            !value.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        return version.GetString()?.Equals("1.2.0", StringComparison.Ordinal) == true;
    }

    private static bool IsReviewedModernCodexPageUrl(string? value)
    {
        return string.Equals(value, "app://-/index.html", StringComparison.Ordinal) ||
               string.Equals(
                   value,
                   "app://-/index.html?initialRoute=%2Favatar-overlay",
                   StringComparison.Ordinal);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpClient client,
        int port,
        string resource,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"http://127.0.0.1:{port}{resource}", UriKind.Absolute);
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        var readToken = readTimeout.Token;
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, readToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumEndpointResponseBytes)
        {
            throw new IOException("Codex CDP endpoint response exceeded the bridge safety limit.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(readToken);
        using var bounded = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, readToken);
            if (read == 0)
            {
                break;
            }
            if (bounded.Length + read > MaximumEndpointResponseBytes)
            {
                throw new IOException("Codex CDP endpoint response exceeded the bridge safety limit.");
            }
            bounded.Write(buffer, 0, read);
        }
        bounded.Position = 0;
        return await JsonDocument.ParseAsync(bounded, cancellationToken: readToken);
    }

    private static Uri ValidateWebSocketUrl(
        string? value,
        int port,
        string targetKind,
        string? expectedTargetId)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != port ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("Codex returned a CDP WebSocket URL outside the expected loopback endpoint.");
        }

        var prefix = "/devtools/" + targetKind + "/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex returned a CDP WebSocket URL with an invalid target kind.");
        }
        var id = uri.AbsolutePath[prefix.Length..];
        if (!BrowserIdentityPattern.IsMatch(id) ||
            (expectedTargetId != null && !id.Equals(expectedTargetId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Codex returned a CDP WebSocket URL with an invalid target identity.");
        }
        return uri;
    }

    internal static RendererPatchResult PatchRendererSource(string source, out string patchedSource)
    {
        return PatchRendererSource(RendererPatchProfiles[1], source, out patchedSource);
    }

    private static RendererPatchResult PatchRendererSource(
        RendererPatchProfile profile,
        string source,
        out string patchedSource)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(source);
        patchedSource = source;
        if (Encoding.UTF8.GetByteCount(source) > MaximumRendererSourceBytes)
        {
            return RendererPatchResult.Rejected("renderer bundle exceeds the 32 MB safety limit");
        }
        var invalidAnchor = profile.SemanticAnchors.FirstOrDefault(anchor =>
            CountOccurrences(source, anchor.Value) != 1);
        if (!string.IsNullOrEmpty(invalidAnchor.Name))
        {
            return RendererPatchResult.Rejected(
                $"renderer semantic anchor {invalidAnchor.Name} did not match exactly once");
        }

        var counts = profile.PatchContract
            .Select(patch => new
            {
                patch.Name,
                Original = CountOccurrences(source, patch.Original),
                Patched = CountOccurrences(source, patch.Patched)
            })
            .ToArray();

        if (counts.All(count => count.Original == 0 && count.Patched == 1))
        {
            if (profile.PatchedSha256 != null &&
                !SourceFingerprint(source).Equals(
                    profile.PatchedSha256,
                    StringComparison.Ordinal))
            {
                return RendererPatchResult.Rejected(
                    "patched renderer fingerprint did not match the reviewed contract");
            }
            return RendererPatchResult.AlreadyPatched();
        }
        if (counts.Any(count => count.Original != 1 || count.Patched != 0))
        {
            return RendererPatchResult.Rejected(
                "renderer version did not match every reviewed Fast-mode contract exactly (" +
                string.Join(
                    ", ",
                    counts.Select(count => $"{count.Name}={count.Original}/{count.Patched}")) +
                ")");
        }

        foreach (var patch in profile.PatchContract)
        {
            patchedSource = patchedSource.Replace(
                patch.Original,
                patch.Patched,
                StringComparison.Ordinal);
        }
        var verifiedPatchedSource = patchedSource;
        if (profile.PatchContract.Any(patch =>
                CountOccurrences(verifiedPatchedSource, patch.Original) != 0 ||
                CountOccurrences(verifiedPatchedSource, patch.Patched) != 1))
        {
            patchedSource = source;
            return RendererPatchResult.Rejected("post-patch contract verification failed");
        }
        if (profile.PatchedSha256 != null &&
            !SourceFingerprint(verifiedPatchedSource).Equals(
                profile.PatchedSha256,
                StringComparison.Ordinal))
        {
            patchedSource = source;
            return RendererPatchResult.Rejected(
                "post-patch renderer fingerprint did not match the reviewed contract");
        }
        return RendererPatchResult.Patched();
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static bool TryCreateCompatibleRendererProfile(
        string bundleUrl,
        string source,
        out RendererPatchProfile profile,
        out string detail)
    {
        var authCaptureCount = CountOccurrences(source, "_camAuth");
        if (authCaptureCount == 0)
        {
            return TryCreateCompatibleRendererProfileFromOriginal(
                bundleUrl,
                source,
                out profile,
                out detail);
        }
        if (authCaptureCount == CompatibilityAuthCaptureOccurrences)
        {
            return TryCreateCompatibleRendererProfileFromPatched(
                bundleUrl,
                source,
                out profile,
                out detail);
        }
        profile = null!;
        detail =
            "renderer contained a partial or ambiguous bridge-local auth capture contract";
        return false;
    }

    private static bool TryCreateCompatibleRendererProfileFromOriginal(
        string bundleUrl,
        string source,
        out RendererPatchProfile profile,
        out string detail)
    {
        profile = null!;
        detail = string.Empty;
        if (!ModernRendererBundlePattern.IsMatch(bundleUrl))
        {
            detail = "renderer URL was not an exact modern app-initial bundle candidate";
            return false;
        }
        if (StrictUtf8.GetByteCount(source) > MaximumRendererSourceBytes)
        {
            detail = "renderer bundle exceeds the 32 MB safety limit";
            return false;
        }
        if (CountOccurrences(source, "_camAuth") != 0)
        {
            detail = "renderer already contains the bridge-local auth capture identifier";
            return false;
        }

        const string identifier = "[A-Za-z_$][A-Za-z0-9_$]*";
        var priorityPattern =
            "(?<priority>" + identifier + ")=`priority`," +
            "(?<fast>" + identifier + ")=`fast`," +
            "(?<ultrafast>" + identifier + ")=`ultrafast`," +
            "(?<standard>" + identifier + ")=`default`";
        if (!TryGetUniqueStructuralMatch(
                source,
                "fast-is-priority",
                priorityPattern,
                out var priorityMatch,
                out detail))
        {
            return false;
        }
        var priority = priorityMatch.Groups["priority"].Value;
        var tierNames = new[]
        {
            priority,
            priorityMatch.Groups["fast"].Value,
            priorityMatch.Groups["ultrafast"].Value,
            priorityMatch.Groups["standard"].Value
        };
        if (tierNames.Distinct(StringComparer.Ordinal).Count() != tierNames.Length)
        {
            detail = "renderer service-tier identifiers were not distinct";
            return false;
        }

        var fallbackPattern =
            "(?<fallback>" + identifier + ")=\\[(?<standardOption>" + identifier +
            "),\\{description:(?<labels>" + identifier +
            ")\\.fastDescription,iconKind:`fast`,label:(?<labelsAgain>" + identifier +
            ")\\.fastLabel,tier:null,value:" + Regex.Escape(priority) + "\\}\\]";
        if (!TryGetUniqueStructuralMatch(
                source,
                "fast-fallback",
                fallbackPattern,
                out var fallbackMatch,
                out detail))
        {
            return false;
        }
        if (!string.Equals(
                fallbackMatch.Groups["labels"].Value,
                fallbackMatch.Groups["labelsAgain"].Value,
                StringComparison.Ordinal))
        {
            detail = "renderer Fast fallback did not use one label source";
            return false;
        }
        var fallback = fallbackMatch.Groups["fallback"].Value;
        var declarationPattern =
            "var " + priority + "," + priorityMatch.Groups["fast"].Value + "," +
            priorityMatch.Groups["ultrafast"].Value + "," +
            priorityMatch.Groups["standard"].Value + "," +
            fallbackMatch.Groups["labels"].Value + "," +
            fallbackMatch.Groups["standardOption"].Value + "," + fallback + ",";
        if (!TryGetUniqueStructuralMatch(
                source,
                "service-tier-declarations",
                Regex.Escape(declarationPattern),
                out var declarationMatch,
                out detail))
        {
            return false;
        }

        var visibilityPattern =
            "function (?<visibilityFunction>" + identifier +
            @")\(e\)\{let t=\(0,(?<memo>" + identifier +
            @")\.c\)\(6\),n=Y\((?<hostAtom>" + identifier +
            @")\),r=e\?\.hostId\?\?n,i=(?<authLookup>" + identifier +
            @")\(r\),a=i\?\.authMethod===`chatgpt`,o=i\?\.authMethod\?\?null,s;t\[0\]!==r\|\|t\[1\]!==o\?\(s=\{authMethod:o,hostId:r\},t\[0\]=r,t\[1\]=o,t\[2\]=s\):s=t\[2\];let\{data:c,isPending:l\}=(?<stateHook>" + identifier +
            @")\((?<queryKey>" + identifier +
            @"),s\),u=!!i\?\.isLoading\|\|a&&l,d=a&&!u&&c!=null&&c\?\.requirements\?\.featureRequirements\?\.fast_mode!==!1,f;return t\[3\]!==u\|\|t\[4\]!==d\?\(f=\{isServiceTierAllowed:d,isLoading:u\},t\[3\]=u,t\[4\]=d,t\[5\]=f\):f=t\[5\],f\}";
        if (!TryGetUniqueStructuralMatch(
                source,
                "visibility",
                visibilityPattern,
                out var visibilityMatch,
                out detail))
        {
            return false;
        }
        var authLookup = visibilityMatch.Groups["authLookup"].Value;
        var stateHook = visibilityMatch.Groups["stateHook"].Value;
        var queryKey = visibilityMatch.Groups["queryKey"].Value;

        var configPattern =
            "async function (?<configFunction>" + identifier +
            @")\(e,t\)\{let n=await (?<authReader>" + identifier +
            @")\(e,t\);if\(n!==`chatgpt`\)return!1;let r=await (?<requirementsReader>" +
            identifier + @")\(e,t,\{priority:`critical`\}\);return e\.query\.setData\(" +
            Regex.Escape(queryKey) +
            @",\{authMethod:n,hostId:t\},r\),r\.requirements\?\.featureRequirements\?\.fast_mode!==!1\}";
        if (!TryGetUniqueStructuralMatch(
                source,
                "config",
                configPattern,
                out var configMatch,
                out detail))
        {
            return false;
        }

        var authCapturePattern =
            @"let\{data:(?<modelsData>" + identifier +
            @"),isLoading:(?<modelsLoading>" + identifier +
            @")\}=(?<modelsHook>" + identifier + @")\(s\),u=" +
            Regex.Escape(stateHook) + "\\((?<threadAtom>" + identifier +
            "),e\\),d=" + Regex.Escape(stateHook) + "\\((?<profileAtom>" + identifier +
            "),e\\),f=" + Regex.Escape(authLookup) +
            @"\(o\.hostId\)\?\.authMethod\?\?null,p;";
        if (!TryGetUniqueStructuralMatch(
                source,
                "auth-capture",
                authCapturePattern,
                out var authCaptureMatch,
                out detail))
        {
            return false;
        }

        var optionsPattern =
            @"T=p,E=o\.hostId,w=(?<optionsFunction>" + identifier + @")\(s\),";
        if (!TryGetUniqueStructuralMatch(
                source,
                "options",
                optionsPattern,
                out var optionsMatch,
                out detail))
        {
            return false;
        }
        var optionsFunction = optionsMatch.Groups["optionsFunction"].Value;

        var selectionPattern =
            @"S=e!=null&&\(u\?\.serviceTier!==void 0\|\|d!==void 0\)\?y\?k:null:(?<normalizeTier>" +
            identifier + @")\(s,k,y\),x=S==null\?null:(?<resolveTier>" + identifier +
            @")\(s,S\);";
        if (!TryGetUniqueStructuralMatch(
                source,
                "selection",
                selectionPattern,
                out var selectionMatch,
                out detail))
        {
            return false;
        }
        var configKeyPattern =
            "function (?<configKeyFunction>" + identifier +
            @")\(e\)\{return e==null\?`service_tier`:`profiles\.\$\{e\}\.service_tier`\}";
        if (!TryGetUniqueStructuralMatch(
                source,
                "config-key",
                configKeyPattern,
                out var configKeyMatch,
                out detail) ||
            !TryGetUniqueStructuralMatch(
                source,
                "request-tier",
                "serviceTierForRequest:S",
                out var requestTierMatch,
                out detail))
        {
            return false;
        }

        var settingsFunctionPattern =
            "function (?<settingsFunction>" + identifier +
            @")\(e,t,n,r\)\{(?=let i=\(0,(?<settingsMemo>" + identifier +
            @")\.c\)\((?<cacheSlots>[1-9][0-9]{0,2})\),)";
        if (!TryFindUniqueServiceTierFunction(
                source,
                settingsFunctionPattern,
                configKeyMatch,
                authCaptureMatch,
                selectionMatch,
                optionsMatch,
                requestTierMatch,
                out var settingsFunctionMatch,
                out var settingsFunctionEnd,
                out detail))
        {
            return false;
        }
        if (MatchesOverlap(visibilityMatch, configMatch) ||
            MatchesOverlap(authCaptureMatch, selectionMatch) ||
            MatchesOverlap(authCaptureMatch, optionsMatch) ||
            MatchesOverlap(selectionMatch, optionsMatch))
        {
            detail =
                "renderer service-tier structures were not ordered inside one complete bounded function";
            return false;
        }
        if (CountOccurrences(
                source[settingsFunctionMatch.Index..(settingsFunctionEnd + 1)],
                fallback) != 0)
        {
            detail =
                "renderer service-tier function shadowed the reviewed Fast fallback identifier";
            return false;
        }
        var codeMatches = new[]
        {
            declarationMatch,
            priorityMatch,
            fallbackMatch,
            visibilityMatch,
            configMatch,
            configKeyMatch,
            settingsFunctionMatch,
            authCaptureMatch,
            selectionMatch,
            optionsMatch,
            requestTierMatch
        };
        var settingsOpeningBrace =
            settingsFunctionMatch.Index + settingsFunctionMatch.Length - 1;
        if (!TryGetJavaScriptBracePaths(
                source,
                codeMatches.Select(match => match.Index),
                out var bracePaths) ||
            !bracePaths[declarationMatch.Index].SequenceEqual(
                bracePaths[settingsFunctionMatch.Index]) ||
            !bracePaths[priorityMatch.Index].SequenceEqual(
                bracePaths[fallbackMatch.Index]) ||
            !IsBracePathPrefix(
                bracePaths[declarationMatch.Index],
                bracePaths[priorityMatch.Index],
                requireChild: true) ||
            !IsBracePathPrefix(
                bracePaths[authCaptureMatch.Index],
                bracePaths[selectionMatch.Index],
                requireChild: false) ||
            !IsBracePathPrefix(
                bracePaths[authCaptureMatch.Index],
                bracePaths[optionsMatch.Index],
                requireChild: false) ||
            !bracePaths[selectionMatch.Index].SequenceEqual(
                bracePaths[optionsMatch.Index]) ||
            !IsBracePathPrefix(
                bracePaths[authCaptureMatch.Index],
                bracePaths[requestTierMatch.Index],
                requireChild: false) ||
            !codeMatches
                .Where(match =>
                    match == authCaptureMatch ||
                    match == selectionMatch ||
                    match == optionsMatch ||
                    match == requestTierMatch)
                .All(match => bracePaths[match.Index].Contains(settingsOpeningBrace)))
        {
            detail =
                "renderer structural matches were not executable code in one verified lexical scope";
            return false;
        }

        const string visibilityGateOriginal =
            "d=a&&!u&&c!=null&&c?.requirements?.featureRequirements?.fast_mode!==!1";
        const string visibilityGatePatched =
            "d=!u&&(a?c!=null&&c?.requirements?.featureRequirements?.fast_mode!==!1:o===`personalAccessToken`||o===`apikey`)";
        const string configGateOriginal = "if(n!==`chatgpt`)return!1;";
        const string configGatePatched =
            "if(n===`personalAccessToken`||n===`apikey`)return!0;if(n!==`chatgpt`)return!1;";
        if (!TryReplaceExactlyOnce(
                visibilityMatch.Value,
                visibilityGateOriginal,
                visibilityGatePatched,
                out var visibilityPatched) ||
            !TryReplaceExactlyOnce(
                configMatch.Value,
                configGateOriginal,
                configGatePatched,
                out var configPatched) ||
            !TryReplaceExactlyOnce(
                authCaptureMatch.Value,
                "p;",
                "p,_camAuth;_camAuth=f;",
                out var authCapturePatched))
        {
            detail = "renderer structural match could not produce exact gate replacements";
            return false;
        }

        var optionsPatched =
            "T=p,E=o.hostId,w=(()=>{let e=" + optionsFunction +
            "(s);return(_camAuth===`personalAccessToken`||_camAuth===`apikey`)&&!e.some(e=>e.value===" +
            "`priority`)?[...e,..." + fallback +
            ".filter(e=>e.value===`priority`)]:e})(),";
        var selectionPatched =
            selectionMatch.Value[..^1] +
            "??((_camAuth===`personalAccessToken`||_camAuth===`apikey`)&&S===" +
            "`priority`?`priority`:null);";
        var patchContract = new (string Name, string Original, string Patched)[]
        {
            ("visibility", visibilityMatch.Value, visibilityPatched),
            ("config", configMatch.Value, configPatched),
            ("auth-capture", authCaptureMatch.Value, authCapturePatched),
            ("options", optionsMatch.Value, optionsPatched),
            ("selection", selectionMatch.Value, selectionPatched)
        };
        var semanticAnchors = new (string Name, string Value)[]
        {
            ("fast-is-priority", priorityMatch.Value),
            ("fast-fallback", fallbackMatch.Value),
            ("config-key", configKeyMatch.Value),
            ("request-tier", requestTierMatch.Value)
        };
        var originalSha256 = SourceFingerprint(source);
        var provisional = new RendererPatchProfile(
            "compatible-" + originalSha256[..12].ToLowerInvariant(),
            bundleUrl,
            originalSha256,
            patchContract,
            semanticAnchors,
            PatchedSha256: null,
            IsCompatibilityProfile: true);
        var patchResult = PatchRendererSource(provisional, source, out var patchedSource);
        if (patchResult.Status != RendererPatchStatus.Patched ||
            CountOccurrences(patchedSource, "_camAuth") !=
            CompatibilityAuthCaptureOccurrences)
        {
            detail = "renderer compatibility profile failed full patch verification: " +
                     patchResult.Detail +
                     $"; auth_capture_count={CountOccurrences(patchedSource, "_camAuth")}";
            return false;
        }
        var patchedSha256 = SourceFingerprint(patchedSource);
        profile = provisional with { PatchedSha256 = patchedSha256 };
        var verified = PatchRendererSource(profile, source, out var verifiedSource);
        if (verified.Status != RendererPatchStatus.Patched ||
            !string.Equals(verifiedSource, patchedSource, StringComparison.Ordinal) ||
            !IsReviewedPatchedRendererSource(profile, patchedSource))
        {
            profile = null!;
            detail = "renderer compatibility profile failed fingerprint-bound verification";
            return false;
        }

        detail = "strict structural compatibility profile verified";
        return true;
    }

    private static bool TryCreateCompatibleRendererProfileFromPatched(
        string bundleUrl,
        string source,
        out RendererPatchProfile profile,
        out string detail)
    {
        profile = null!;
        detail = string.Empty;
        if (!ModernRendererBundlePattern.IsMatch(bundleUrl) ||
            StrictUtf8.GetByteCount(source) > MaximumRendererSourceBytes ||
            CountOccurrences(source, "_camAuth") != CompatibilityAuthCaptureOccurrences)
        {
            detail = "patched renderer did not satisfy the bounded compatibility preconditions";
            return false;
        }

        const string identifier = "[A-Za-z_$][A-Za-z0-9_$]*";
        var priorityPattern =
            "(?<priority>" + identifier + ")=`priority`," +
            "(?<fast>" + identifier + ")=`fast`," +
            "(?<ultrafast>" + identifier + ")=`ultrafast`," +
            "(?<standard>" + identifier + ")=`default`";
        if (!TryGetUniqueStructuralMatch(
                source,
                "patched-fast-is-priority",
                priorityPattern,
                out var priorityMatch,
                out detail))
        {
            return false;
        }
        var priority = priorityMatch.Groups["priority"].Value;
        var tierNames = new[]
        {
            priority,
            priorityMatch.Groups["fast"].Value,
            priorityMatch.Groups["ultrafast"].Value,
            priorityMatch.Groups["standard"].Value
        };
        if (tierNames.Distinct(StringComparer.Ordinal).Count() != tierNames.Length)
        {
            detail = "patched renderer service-tier identifiers were not distinct";
            return false;
        }

        var fallbackPattern =
            "(?<fallback>" + identifier + ")=\\[(?<standardOption>" + identifier +
            "),\\{description:(?<labels>" + identifier +
            ")\\.fastDescription,iconKind:`fast`,label:(?<labelsAgain>" + identifier +
            ")\\.fastLabel,tier:null,value:" + Regex.Escape(priority) + "\\}\\]";
        if (!TryGetUniqueStructuralMatch(
                source,
                "patched-fast-fallback",
                fallbackPattern,
                out var fallbackMatch,
                out detail) ||
            !string.Equals(
                fallbackMatch.Groups["labels"].Value,
                fallbackMatch.Groups["labelsAgain"].Value,
                StringComparison.Ordinal))
        {
            detail = string.IsNullOrEmpty(detail)
                ? "patched renderer Fast fallback did not use one label source"
                : detail;
            return false;
        }
        var fallback = fallbackMatch.Groups["fallback"].Value;

        var visibilityPattern =
            "function (?<visibilityFunction>" + identifier +
            @")\(e\)\{let t=\(0,(?<memo>" + identifier +
            @")\.c\)\(6\),n=Y\((?<hostAtom>" + identifier +
            @")\),r=e\?\.hostId\?\?n,i=(?<authLookup>" + identifier +
            @")\(r\),a=i\?\.authMethod===`chatgpt`,o=i\?\.authMethod\?\?null,s;t\[0\]!==r\|\|t\[1\]!==o\?\(s=\{authMethod:o,hostId:r\},t\[0\]=r,t\[1\]=o,t\[2\]=s\):s=t\[2\];let\{data:c,isPending:l\}=(?<stateHook>" + identifier +
            @")\((?<queryKey>" + identifier +
            @"),s\),u=!!i\?\.isLoading\|\|a&&l,d=!u&&\(a\?c!=null&&c\?\.requirements\?\.featureRequirements\?\.fast_mode!==!1:o===`personalAccessToken`\|\|o===`apikey`\),f;return t\[3\]!==u\|\|t\[4\]!==d\?\(f=\{isServiceTierAllowed:d,isLoading:u\},t\[3\]=u,t\[4\]=d,t\[5\]=f\):f=t\[5\],f\}";
        if (!TryGetUniqueStructuralMatch(
                source,
                "patched-visibility",
                visibilityPattern,
                out var visibilityMatch,
                out detail))
        {
            return false;
        }
        var authLookup = visibilityMatch.Groups["authLookup"].Value;
        var stateHook = visibilityMatch.Groups["stateHook"].Value;
        var queryKey = visibilityMatch.Groups["queryKey"].Value;

        var configPattern =
            "async function (?<configFunction>" + identifier +
            @")\(e,t\)\{let n=await (?<authReader>" + identifier +
            @")\(e,t\);if\(n===`personalAccessToken`\|\|n===`apikey`\)return!0;if\(n!==`chatgpt`\)return!1;let r=await (?<requirementsReader>" +
            identifier + @")\(e,t,\{priority:`critical`\}\);return e\.query\.setData\(" +
            Regex.Escape(queryKey) +
            @",\{authMethod:n,hostId:t\},r\),r\.requirements\?\.featureRequirements\?\.fast_mode!==!1\}";
        if (!TryGetUniqueStructuralMatch(
                source,
                "patched-config",
                configPattern,
                out var configMatch,
                out detail))
        {
            return false;
        }

        var authCapturePattern =
            @"let\{data:(?<modelsData>" + identifier +
            @"),isLoading:(?<modelsLoading>" + identifier +
            @")\}=(?<modelsHook>" + identifier + @")\(s\),u=" +
            Regex.Escape(stateHook) + "\\((?<threadAtom>" + identifier +
            "),e\\),d=" + Regex.Escape(stateHook) + "\\((?<profileAtom>" + identifier +
            "),e\\),f=" + Regex.Escape(authLookup) +
            @"\(o\.hostId\)\?\.authMethod\?\?null,p,_camAuth;_camAuth=f;";
        if (!TryGetUniqueStructuralMatch(
                source,
                "patched-auth-capture",
                authCapturePattern,
                out var authCaptureMatch,
                out detail))
        {
            return false;
        }

        var optionsPattern =
            @"T=p,E=o\.hostId,w=\(\(\)=>\{let e=(?<optionsFunction>" + identifier +
            @")\(s\);return\(_camAuth===`personalAccessToken`\|\|_camAuth===`apikey`\)&&!e\.some\(e=>e\.value===" +
            @"`priority`\)\?\[\.\.\.e,\.\.\." + Regex.Escape(fallback) +
            @"\.filter\(e=>e\.value===`priority`\)\]:e\}\)\(\),";
        if (!TryGetUniqueStructuralMatch(
                source,
                "patched-options",
                optionsPattern,
                out var optionsMatch,
                out detail))
        {
            return false;
        }

        var selectionPattern =
            @"S=e!=null&&\(u\?\.serviceTier!==void 0\|\|d!==void 0\)\?y\?k:null:(?<normalizeTier>" +
            identifier + @")\(s,k,y\),x=S==null\?null:(?<resolveTier>" + identifier +
            @")\(s,S\)\?\?\(\(_camAuth===`personalAccessToken`\|\|_camAuth===`apikey`\)&&S===" +
            @"`priority`\?`priority`:null\);";
        if (!TryGetUniqueStructuralMatch(
                source,
                "patched-selection",
                selectionPattern,
                out var selectionMatch,
                out detail))
        {
            return false;
        }

        const string visibilityGatePatched =
            "d=!u&&(a?c!=null&&c?.requirements?.featureRequirements?.fast_mode!==!1:o===`personalAccessToken`||o===`apikey`)";
        const string visibilityGateOriginal =
            "d=a&&!u&&c!=null&&c?.requirements?.featureRequirements?.fast_mode!==!1";
        const string configGatePatched =
            "if(n===`personalAccessToken`||n===`apikey`)return!0;if(n!==`chatgpt`)return!1;";
        const string configGateOriginal = "if(n!==`chatgpt`)return!1;";
        if (!TryReplaceExactlyOnce(
                visibilityMatch.Value,
                visibilityGatePatched,
                visibilityGateOriginal,
                out var visibilityOriginal) ||
            !TryReplaceExactlyOnce(
                configMatch.Value,
                configGatePatched,
                configGateOriginal,
                out var configOriginal) ||
            !TryReplaceExactlyOnce(
                authCaptureMatch.Value,
                "p,_camAuth;_camAuth=f;",
                "p;",
                out var authCaptureOriginal))
        {
            detail = "patched renderer could not be deterministically restored";
            return false;
        }
        var optionsOriginal =
            "T=p,E=o.hostId,w=" + optionsMatch.Groups["optionsFunction"].Value + "(s),";
        var selectionOriginal =
            "S=e!=null&&(u?.serviceTier!==void 0||d!==void 0)?y?k:null:" +
            selectionMatch.Groups["normalizeTier"].Value +
            "(s,k,y),x=S==null?null:" + selectionMatch.Groups["resolveTier"].Value + "(s,S);";

        var restoredSource = source;
        var reverseContracts = new[]
        {
            (Patched: visibilityMatch.Value, Original: visibilityOriginal),
            (Patched: configMatch.Value, Original: configOriginal),
            (Patched: authCaptureMatch.Value, Original: authCaptureOriginal),
            (Patched: optionsMatch.Value, Original: optionsOriginal),
            (Patched: selectionMatch.Value, Original: selectionOriginal)
        };
        foreach (var contract in reverseContracts)
        {
            if (!TryReplaceExactlyOnce(
                    restoredSource,
                    contract.Patched,
                    contract.Original,
                    out restoredSource))
            {
                detail = "patched renderer reverse contract was incomplete or ambiguous";
                return false;
            }
        }
        if (CountOccurrences(restoredSource, "_camAuth") != 0 ||
            !TryCreateCompatibleRendererProfileFromOriginal(
                bundleUrl,
                restoredSource,
                out profile,
                out detail) ||
            profile.PatchedSha256 == null ||
            !profile.PatchedSha256.Equals(SourceFingerprint(source), StringComparison.Ordinal))
        {
            profile = null!;
            detail = string.IsNullOrEmpty(detail)
                ? "restored renderer did not regenerate the same patched fingerprint"
                : detail;
            return false;
        }
        var verification = PatchRendererSource(profile, source, out var verifiedSource);
        if (verification.Status != RendererPatchStatus.AlreadyPatched ||
            !string.Equals(verifiedSource, source, StringComparison.Ordinal) ||
            !IsReviewedPatchedRendererSource(profile, source))
        {
            profile = null!;
            detail = "patched renderer failed the regenerated compatibility contract";
            return false;
        }

        detail = "strict already-patched structural compatibility profile verified";
        return true;
    }

    private static bool TryGetUniqueStructuralMatch(
        string source,
        string name,
        string pattern,
        out Match match,
        out string detail)
    {
        try
        {
            var expression = new Regex(
                pattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(5));
            match = expression.Match(source);
            if (!match.Success || match.NextMatch().Success)
            {
                detail = $"renderer structural contract {name} did not match exactly once";
                return false;
            }
            detail = string.Empty;
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            match = Match.Empty;
            detail = $"renderer structural contract {name} exceeded its matching deadline";
            return false;
        }
    }

    private static bool TryFindUniqueServiceTierFunction(
        string source,
        string pattern,
        Match configKeyMatch,
        Match authCaptureMatch,
        Match selectionMatch,
        Match optionsMatch,
        Match requestTierMatch,
        out Match settingsFunctionMatch,
        out int settingsFunctionEnd,
        out string detail)
    {
        settingsFunctionMatch = Match.Empty;
        settingsFunctionEnd = -1;
        try
        {
            var expression = new Regex(
                pattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(5));
            var candidateCount = 0;
            foreach (Match candidate in expression.Matches(source))
            {
                if (!int.TryParse(candidate.Groups["cacheSlots"].Value, out var cacheSlots) ||
                    cacheSlots is < 20 or > 200)
                {
                    continue;
                }
                var openingBrace = candidate.Index + candidate.Length - 1;
                if (!TryFindJavaScriptBlockEnd(source, openingBrace, out var candidateEnd) ||
                    configKeyMatch.Index >= candidate.Index ||
                    candidate.Index - (configKeyMatch.Index + configKeyMatch.Length) > 4 * 1024 ||
                    authCaptureMatch.Index <= openingBrace ||
                    selectionMatch.Index <= authCaptureMatch.Index + authCaptureMatch.Length ||
                    optionsMatch.Index <= selectionMatch.Index + selectionMatch.Length ||
                    requestTierMatch.Index <= optionsMatch.Index + optionsMatch.Length ||
                    requestTierMatch.Index + requestTierMatch.Length >= candidateEnd ||
                    candidateEnd - candidate.Index > 32 * 1024)
                {
                    continue;
                }
                candidateCount++;
                settingsFunctionMatch = candidate;
                settingsFunctionEnd = candidateEnd;
                if (candidateCount > 1)
                {
                    break;
                }
            }
            if (candidateCount == 1)
            {
                detail = string.Empty;
                return true;
            }
            settingsFunctionMatch = Match.Empty;
            settingsFunctionEnd = -1;
            detail =
                "renderer service-tier structures did not resolve to exactly one complete function";
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            detail = "renderer service-tier function matching exceeded its deadline";
            return false;
        }
    }

    private static bool TryGetJavaScriptBracePaths(
        string source,
        IEnumerable<int> offsets,
        out Dictionary<int, int[]> bracePaths)
    {
        bracePaths = new Dictionary<int, int[]>();
        var targets = offsets.Distinct().Order().ToArray();
        if (targets.Length == 0 || targets[0] < 0 || targets[^1] >= source.Length)
        {
            return false;
        }
        var targetSet = targets.ToHashSet();
        var braces = new Stack<int>();
        var templateExpressionDepths = new Stack<int>();
        var inSingleQuotedString = false;
        var inDoubleQuotedString = false;
        var inTemplateText = false;
        var inLineComment = false;
        var inBlockComment = false;
        var canStartRegularExpression = true;
        var limit = targets[^1] + 1;

        for (var index = 0; index < limit; index++)
        {
            if (targetSet.Contains(index))
            {
                if (inSingleQuotedString ||
                    inDoubleQuotedString ||
                    inTemplateText ||
                    inLineComment ||
                    inBlockComment)
                {
                    bracePaths.Clear();
                    return false;
                }
                bracePaths[index] = braces.Reverse().ToArray();
                if (bracePaths.Count == targetSet.Count)
                {
                    return true;
                }
            }

            var current = source[index];
            if (inSingleQuotedString || inDoubleQuotedString)
            {
                if (current == '\\')
                {
                    index++;
                    continue;
                }
                if (inSingleQuotedString && current == '\'' ||
                    inDoubleQuotedString && current == '"')
                {
                    inSingleQuotedString = false;
                    inDoubleQuotedString = false;
                    canStartRegularExpression = false;
                }
                continue;
            }
            if (inTemplateText)
            {
                if (current == '\\')
                {
                    index++;
                    continue;
                }
                if (current == '`')
                {
                    inTemplateText = false;
                    canStartRegularExpression = false;
                    continue;
                }
                if (current == '$' &&
                    index + 1 < limit &&
                    source[index + 1] == '{')
                {
                    braces.Push(index + 1);
                    templateExpressionDepths.Push(braces.Count);
                    inTemplateText = false;
                    canStartRegularExpression = true;
                    index++;
                }
                continue;
            }
            if (inLineComment)
            {
                if (current is '\r' or '\n')
                {
                    inLineComment = false;
                }
                continue;
            }
            if (inBlockComment)
            {
                if (current == '*' && index + 1 < limit && source[index + 1] == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }

            if (current == '\'')
            {
                inSingleQuotedString = true;
            }
            else if (current == '"')
            {
                inDoubleQuotedString = true;
            }
            else if (current == '`')
            {
                inTemplateText = true;
            }
            else if (current == '/' && index + 1 < limit && source[index + 1] == '/')
            {
                inLineComment = true;
                index++;
            }
            else if (current == '/' && index + 1 < limit && source[index + 1] == '*')
            {
                inBlockComment = true;
                index++;
            }
            else if (current == '/')
            {
                if (!canStartRegularExpression)
                {
                    canStartRegularExpression = true;
                    if (index + 1 < limit && source[index + 1] == '=')
                    {
                        index++;
                    }
                    continue;
                }
                if (!TrySkipJavaScriptRegularExpression(source, ref index, limit))
                {
                    bracePaths.Clear();
                    return false;
                }
                canStartRegularExpression = false;
            }
            else if (current == '{')
            {
                braces.Push(index);
                canStartRegularExpression = true;
            }
            else if (current == '}')
            {
                if (braces.Count == 0)
                {
                    bracePaths.Clear();
                    return false;
                }
                var closingTemplateExpression =
                    templateExpressionDepths.Count > 0 &&
                    templateExpressionDepths.Peek() == braces.Count;
                braces.Pop();
                if (closingTemplateExpression)
                {
                    templateExpressionDepths.Pop();
                    inTemplateText = true;
                }
                else
                {
                    canStartRegularExpression = false;
                }
            }
            else if (IsJavaScriptIdentifierStart(current))
            {
                var tokenStart = index;
                while (index + 1 < limit &&
                       IsJavaScriptIdentifierPart(source[index + 1]))
                {
                    index++;
                }
                canStartRegularExpression = IsRegularExpressionPrefixKeyword(
                    source.AsSpan(tokenStart, index - tokenStart + 1));
            }
            else if (char.IsDigit(current) ||
                     current == '.' && index + 1 < limit && char.IsDigit(source[index + 1]))
            {
                while (index + 1 < limit &&
                       (char.IsLetterOrDigit(source[index + 1]) ||
                        source[index + 1] is '_' or '.'))
                {
                    index++;
                }
                canStartRegularExpression = false;
            }
            else if (!char.IsWhiteSpace(current))
            {
                canStartRegularExpression = current switch
                {
                    ')' or ']' => false,
                    '.' => false,
                    '+' or '-' when index + 1 < limit && source[index + 1] == current => false,
                    _ => true
                };
                if ((current is '+' or '-' or '&' or '|' or '=' or '!' or '<' or '>') &&
                    index + 1 < limit &&
                    source[index + 1] == current)
                {
                    index++;
                }
            }
        }

        bracePaths.Clear();
        return false;
    }

    private static bool IsBracePathPrefix(
        IReadOnlyList<int> parent,
        IReadOnlyList<int> child,
        bool requireChild)
    {
        if (parent.Count > child.Count || requireChild && parent.Count == child.Count)
        {
            return false;
        }
        for (var index = 0; index < parent.Count; index++)
        {
            if (parent[index] != child[index])
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryReplaceExactlyOnce(
        string source,
        string original,
        string replacement,
        out string result)
    {
        result = source;
        if (CountOccurrences(source, original) != 1)
        {
            return false;
        }
        var offset = source.IndexOf(original, StringComparison.Ordinal);
        result = string.Concat(
            source.AsSpan(0, offset),
            replacement,
            source.AsSpan(offset + original.Length));
        return result.Length == source.Length - original.Length + replacement.Length &&
               result.AsSpan(offset, replacement.Length).SequenceEqual(replacement);
    }

    private static bool MatchesOverlap(Match left, Match right)
    {
        return left.Index < right.Index + right.Length &&
               right.Index < left.Index + left.Length;
    }

    private static bool TryFindJavaScriptBlockEnd(
        string source,
        int openingBrace,
        out int closingBrace)
    {
        closingBrace = -1;
        if (openingBrace < 0 ||
            openingBrace >= source.Length ||
            source[openingBrace] != '{')
        {
            return false;
        }

        var depth = 0;
        var templateExpressionDepths = new Stack<int>();
        var inSingleQuotedString = false;
        var inDoubleQuotedString = false;
        var inTemplateText = false;
        var inLineComment = false;
        var inBlockComment = false;
        var canStartRegularExpression = true;
        var limit = Math.Min(source.Length, openingBrace + 32 * 1024 + 1);
        for (var index = openingBrace; index < limit; index++)
        {
            var current = source[index];
            if (inSingleQuotedString || inDoubleQuotedString)
            {
                if (current == '\\')
                {
                    index++;
                    continue;
                }
                if (inSingleQuotedString && current == '\'' ||
                    inDoubleQuotedString && current == '"')
                {
                    inSingleQuotedString = false;
                    inDoubleQuotedString = false;
                    canStartRegularExpression = false;
                }
                continue;
            }
            if (inTemplateText)
            {
                if (current == '\\')
                {
                    index++;
                    continue;
                }
                if (current == '`')
                {
                    inTemplateText = false;
                    canStartRegularExpression = false;
                    continue;
                }
                if (current == '$' &&
                    index + 1 < limit &&
                    source[index + 1] == '{')
                {
                    depth++;
                    templateExpressionDepths.Push(depth);
                    inTemplateText = false;
                    canStartRegularExpression = true;
                    index++;
                }
                continue;
            }
            if (inLineComment)
            {
                if (current is '\r' or '\n')
                {
                    inLineComment = false;
                }
                continue;
            }
            if (inBlockComment)
            {
                if (current == '*' && index + 1 < limit && source[index + 1] == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }

            if (current == '\'')
            {
                inSingleQuotedString = true;
            }
            else if (current == '"')
            {
                inDoubleQuotedString = true;
            }
            else if (current == '`')
            {
                inTemplateText = true;
            }
            else if (current == '/' && index + 1 < limit && source[index + 1] == '/')
            {
                inLineComment = true;
                index++;
            }
            else if (current == '/' && index + 1 < limit && source[index + 1] == '*')
            {
                inBlockComment = true;
                index++;
            }
            else if (current == '/')
            {
                if (!canStartRegularExpression)
                {
                    canStartRegularExpression = true;
                    if (index + 1 < limit && source[index + 1] == '=')
                    {
                        index++;
                    }
                    continue;
                }
                if (!TrySkipJavaScriptRegularExpression(source, ref index, limit))
                {
                    return false;
                }
                canStartRegularExpression = false;
            }
            else if (current == '{')
            {
                depth++;
                canStartRegularExpression = true;
            }
            else if (current == '}')
            {
                if (depth <= 0)
                {
                    return false;
                }
                var closingTemplateExpression =
                    templateExpressionDepths.Count > 0 &&
                    templateExpressionDepths.Peek() == depth;
                depth--;
                if (closingTemplateExpression)
                {
                    templateExpressionDepths.Pop();
                    inTemplateText = true;
                    continue;
                }
                if (depth == 0)
                {
                    closingBrace = index;
                    return templateExpressionDepths.Count == 0;
                }
                canStartRegularExpression = false;
            }
            else if (IsJavaScriptIdentifierStart(current))
            {
                var tokenStart = index;
                while (index + 1 < limit &&
                       IsJavaScriptIdentifierPart(source[index + 1]))
                {
                    index++;
                }
                var token = source.AsSpan(tokenStart, index - tokenStart + 1);
                canStartRegularExpression = IsRegularExpressionPrefixKeyword(token);
            }
            else if (char.IsDigit(current) ||
                     current == '.' && index + 1 < limit && char.IsDigit(source[index + 1]))
            {
                while (index + 1 < limit &&
                       (char.IsLetterOrDigit(source[index + 1]) ||
                        source[index + 1] is '_' or '.'))
                {
                    index++;
                }
                canStartRegularExpression = false;
            }
            else if (!char.IsWhiteSpace(current))
            {
                canStartRegularExpression = current switch
                {
                    ')' or ']' => false,
                    '.' => false,
                    '+' or '-' when index + 1 < limit && source[index + 1] == current => false,
                    _ => true
                };
                if ((current is '+' or '-' or '&' or '|' or '=' or '!' or '<' or '>') &&
                    index + 1 < limit &&
                    source[index + 1] == current)
                {
                    index++;
                }
            }
        }
        return false;
    }

    private static bool TrySkipJavaScriptRegularExpression(
        string source,
        ref int slashIndex,
        int limit)
    {
        var inCharacterClass = false;
        for (var index = slashIndex + 1; index < limit; index++)
        {
            var current = source[index];
            if (current == '\\')
            {
                index++;
                continue;
            }
            if (current is '\r' or '\n')
            {
                return false;
            }
            if (current == '[')
            {
                inCharacterClass = true;
            }
            else if (current == ']' && inCharacterClass)
            {
                inCharacterClass = false;
            }
            else if (current == '/' && !inCharacterClass)
            {
                while (index + 1 < limit && IsJavaScriptIdentifierPart(source[index + 1]))
                {
                    index++;
                }
                slashIndex = index;
                return true;
            }
        }
        return false;
    }

    private static bool IsJavaScriptIdentifierStart(char value)
    {
        return value is '_' or '$' || char.IsLetter(value) || value >= 0x80;
    }

    private static bool IsJavaScriptIdentifierPart(char value)
    {
        return IsJavaScriptIdentifierStart(value) || char.IsDigit(value);
    }

    private static bool IsRegularExpressionPrefixKeyword(ReadOnlySpan<char> token)
    {
        return token.SequenceEqual("return") ||
               token.SequenceEqual("throw") ||
               token.SequenceEqual("case") ||
               token.SequenceEqual("delete") ||
               token.SequenceEqual("void") ||
               token.SequenceEqual("typeof") ||
               token.SequenceEqual("new") ||
               token.SequenceEqual("in") ||
               token.SequenceEqual("of") ||
               token.SequenceEqual("yield") ||
               token.SequenceEqual("await") ||
               token.SequenceEqual("else") ||
               token.SequenceEqual("do") ||
               token.SequenceEqual("instanceof");
    }

    private static bool IsReviewedPatchedRendererSource(
        RendererPatchProfile profile,
        string patchedSource)
    {
        if (profile.PatchedSha256 != null &&
            !SourceFingerprint(patchedSource).Equals(
                profile.PatchedSha256,
                StringComparison.Ordinal))
        {
            return false;
        }
        var restoredSource = patchedSource;
        foreach (var patch in profile.PatchContract)
        {
            restoredSource = restoredSource.Replace(
                patch.Patched,
                patch.Original,
                StringComparison.Ordinal);
        }
        return SourceFingerprint(restoredSource).Equals(
            profile.SourceSha256,
            StringComparison.Ordinal);
    }

    private static bool RendererSourceMatchesProfile(
        RendererPatchProfile profile,
        string source,
        string fingerprint,
        out RendererPatchResult patch,
        out bool alreadyPatched)
    {
        patch = PatchRendererSource(profile, source, out _);
        alreadyPatched = patch.Status == RendererPatchStatus.AlreadyPatched;
        return (patch.Status == RendererPatchStatus.Patched &&
                fingerprint.Equals(profile.SourceSha256, StringComparison.Ordinal)) ||
               (alreadyPatched && IsReviewedPatchedRendererSource(profile, source));
    }

    private static string CompatibilityProfileCacheKey(string bundleUrl, string fingerprint)
    {
        return bundleUrl + "\n" + fingerprint;
    }

    private static void CacheCompatibleRendererProfile(RendererPatchProfile profile)
    {
        if (!profile.IsCompatibilityProfile || profile.PatchedSha256 == null)
        {
            throw new InvalidOperationException(
                "Only a fingerprint-bound compatibility profile can enter the runtime cache.");
        }
        CompatibleRendererProfilesByFingerprint[
            CompatibilityProfileCacheKey(profile.BundleUrl, profile.SourceSha256)] = profile;
        CompatibleRendererProfilesByFingerprint[
            CompatibilityProfileCacheKey(profile.BundleUrl, profile.PatchedSha256)] = profile;
    }

    private static bool TryGetCachedCompatibleRendererProfile(
        string bundleUrl,
        string fingerprint,
        out RendererPatchProfile profile)
    {
        return CompatibleRendererProfilesByFingerprint.TryGetValue(
                   CompatibilityProfileCacheKey(bundleUrl, fingerprint),
                   out profile!) &&
               profile.IsCompatibilityProfile &&
               string.Equals(profile.BundleUrl, bundleUrl, StringComparison.Ordinal);
    }

    private static bool IsRendererBundleUrl(string? value)
    {
        // Never normalize through Uri: escaped or dot-segment paths could otherwise impersonate
        // a renderer. Modern hash-named candidates are observed only on an exact reviewed page;
        // their source must still satisfy the strict structural contract before interception.
        return value != null &&
               (RendererPatchProfiles.Any(profile =>
                    string.Equals(value, profile.BundleUrl, StringComparison.Ordinal)) ||
                ModernRendererBundlePattern.IsMatch(value));
    }

    private static bool IsRendererBundleUrlForPage(string? pageUrl, string? bundleUrl)
    {
        if (string.Equals(pageUrl, "app://codex/", StringComparison.Ordinal))
        {
            return string.Equals(bundleUrl, LegacyRendererBundleUrl, StringComparison.Ordinal);
        }
        return IsReviewedModernCodexPageUrl(pageUrl) &&
               bundleUrl != null &&
               ModernRendererBundlePattern.IsMatch(bundleUrl);
    }

    private static IReadOnlyList<RendererPatchProfile> ReviewedRendererProfilesForPage(
        string? pageUrl)
    {
        if (string.Equals(pageUrl, "app://codex/", StringComparison.Ordinal))
        {
            return RendererPatchProfiles
                .Where(profile => string.Equals(
                    profile.BundleUrl,
                    LegacyRendererBundleUrl,
                    StringComparison.Ordinal))
                .ToArray();
        }
        if (string.Equals(pageUrl, "app://-/index.html", StringComparison.Ordinal) ||
            string.Equals(
                pageUrl,
                "app://-/index.html?initialRoute=%2Favatar-overlay",
                StringComparison.Ordinal))
        {
            return RendererPatchProfiles
                .Where(profile =>
                    string.Equals(
                        profile.BundleUrl,
                        PreviousRendererBundleUrl,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        profile.BundleUrl,
                        CurrentRendererBundleUrl,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        profile.BundleUrl,
                        LatestRendererBundleUrl,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        profile.BundleUrl,
                        Current5229RendererBundleUrl,
                        StringComparison.Ordinal))
                .ToArray();
        }
        return Array.Empty<RendererPatchProfile>();
    }

    private static RendererResponseBody ReadRendererResponseBody(JsonElement response)
    {
        if (!response.TryGetProperty("body", out var bodyValue) ||
            bodyValue.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Codex did not return the renderer response body.");
        }
        var encodedBody = bodyValue.GetString() ?? string.Empty;
        var base64Encoded = response.TryGetProperty("base64Encoded", out var encodedValue) &&
                            encodedValue.ValueKind == JsonValueKind.True;
        byte[] bytes;
        try
        {
            bytes = base64Encoded
                ? Convert.FromBase64String(encodedBody)
                : StrictUtf8.GetBytes(encodedBody);
        }
        catch (Exception ex) when (ex is FormatException or EncoderFallbackException)
        {
            throw new InvalidDataException("Codex returned an invalid renderer response encoding.", ex);
        }
        if (bytes.Length == 0 || bytes.Length > MaximumRendererSourceBytes)
        {
            throw new IOException("Codex renderer response exceeded the bridge safety limit.");
        }
        try
        {
            return new RendererResponseBody(StrictUtf8.GetString(bytes), bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Codex renderer response was not strict UTF-8.", ex);
        }
    }

    private static IReadOnlyList<CdpResponseHeader> BuildFulfilledResponseHeaders(
        JsonElement pausedResponse,
        int contentLength)
    {
        if (contentLength <= 0 || contentLength > MaximumRendererSourceBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength));
        }
        if (!pausedResponse.TryGetProperty("responseHeaders", out var headerValues) ||
            headerValues.ValueKind != JsonValueKind.Array ||
            headerValues.GetArrayLength() > MaximumFetchResponseHeaders)
        {
            throw new InvalidOperationException("Codex returned invalid renderer response headers.");
        }

        string? contentType = null;
        foreach (var header in headerValues.EnumerateArray())
        {
            var name = header.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString()
                : null;
            var value = header.TryGetProperty("value", out var valueElement)
                ? valueElement.GetString()
                : null;
            if (!IsSafeFetchHeaderName(name) || !IsSafeFetchHeaderValue(value))
            {
                throw new InvalidOperationException("Codex returned an unsafe renderer response header.");
            }
            if (!RewrittenFetchResponseHeaders.Contains(name!) &&
                name!.Equals("content-type", StringComparison.OrdinalIgnoreCase))
            {
                if (contentType != null)
                {
                    throw new InvalidOperationException("Codex returned duplicate renderer content types.");
                }
                contentType = value;
            }
        }

        var mediaType = contentType?.Split(';', 2)[0].Trim();
        if (mediaType == null ||
            !(mediaType.Equals("text/javascript", StringComparison.OrdinalIgnoreCase) ||
              mediaType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase) ||
              mediaType.Equals("text/ecmascript", StringComparison.OrdinalIgnoreCase) ||
              mediaType.Equals("application/ecmascript", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Codex renderer response was not JavaScript.");
        }

        return
        [
            new CdpResponseHeader("Content-Type", "text/javascript; charset=utf-8"),
            new CdpResponseHeader("Cache-Control", "no-store"),
            new CdpResponseHeader(
                "Content-Length",
                contentLength.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ];
    }

    private static bool IsSafeFetchHeaderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumFetchResponseHeaderNameLength)
        {
            return false;
        }
        const string tokenPunctuation = "!#$%&'*+-.^_`|~";
        return value.All(character =>
            character <= 0x7f &&
            (char.IsAsciiLetterOrDigit(character) || tokenPunctuation.Contains(character)));
    }

    private static bool IsSafeFetchHeaderValue(string? value)
    {
        return value != null &&
               value.Length <= MaximumFetchResponseHeaderValueLength &&
               value.IndexOfAny(['\r', '\n', '\0']) < 0;
    }

    private static string SourceFingerprint(string source)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private static int ReadRequiredPort(string[] args)
    {
        var value = ReadOptionalArgument(args, PortArgument);
        if (!int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var port))
        {
            throw new ArgumentException($"{PortArgument} requires a numeric TCP port.");
        }
        ValidatePort(port);
        return port;
    }

    private static int ReadRequiredPositiveInt(string[] args, string name)
    {
        var value = ReadOptionalArgument(args, name);
        if (!int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed <= 0)
        {
            throw new ArgumentException($"{name} requires a positive integer.");
        }
        return parsed;
    }

    private static long ReadRequiredPositiveLong(string[] args, string name)
    {
        var value = ReadOptionalArgument(args, name);
        if (!long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed <= 0)
        {
            throw new ArgumentException($"{name} requires a positive integer.");
        }
        return parsed;
    }

    private static string ReadRequiredDirectory(string[] args, string name)
    {
        var value = ReadOptionalArgument(args, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} requires a Codex application directory.");
        }
        return ValidateOwnerRoot(value);
    }

    private static string ValidateOwnerRoot(string value)
    {
        var fullPath = Path.GetFullPath(value.Trim());
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("The verified Codex application directory does not exist.");
        }
        var appDirectory = new DirectoryInfo(fullPath);
        var packageDirectory = appDirectory.Parent;
        if (!appDirectory.Name.Equals("app", StringComparison.OrdinalIgnoreCase) ||
            packageDirectory == null ||
            !packageDirectory.Name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(packageDirectory.FullName, "AppxManifest.xml")))
        {
            throw new InvalidOperationException(
                "The bridge owner directory is not a registered OpenAI.Codex package.");
        }
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsExpectedCdpOwner(
        int port,
        int expectedPid,
        long expectedStartTicks,
        string expectedRoot)
    {
        if (expectedPid <= 0 || expectedStartTicks <= 0 || string.IsNullOrWhiteSpace(expectedRoot))
        {
            return false;
        }

        try
        {
            if (!TryGetLoopbackListenerOwner(port, out var listenerPid) ||
                listenerPid != expectedPid)
            {
                return false;
            }

            using var process = Process.GetProcessById(expectedPid);
            if (process.HasExited ||
                process.StartTime.ToUniversalTime().Ticks != expectedStartTicks)
            {
                return false;
            }

            var fileName = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            var fullFileName = Path.GetFullPath(fileName);
            var root = ValidateOwnerRoot(expectedRoot);
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (!fullFileName.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Path.GetFileName(fullFileName).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
                   Path.GetFileName(fullFileName).Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) ||
                   Path.GetFileName(fullFileName).Equals("codex.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or Win32Exception or
            UnauthorizedAccessException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static bool TryGetLoopbackListenerOwner(int port, out int processId)
    {
        processId = 0;
        var bufferSize = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferSize,
            order: false,
            AddressFamilyInterNetwork,
            TcpTableOwnerPidListener,
            reserved: 0);
        if (result != ErrorInsufficientBuffer || bufferSize <= sizeof(uint))
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref bufferSize,
                order: false,
                AddressFamilyInterNetwork,
                TcpTableOwnerPidListener,
                reserved: 0);
            if (result != 0)
            {
                return false;
            }

            var rowCount = Marshal.ReadInt32(buffer);
            if (rowCount is < 0 or > 65535)
            {
                return false;
            }

            var rowSize = Marshal.SizeOf<NativeTcpRowOwnerPid>();
            var rowPointer = IntPtr.Add(buffer, sizeof(uint));
            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<NativeTcpRowOwnerPid>(rowPointer);
                rowPointer = IntPtr.Add(rowPointer, rowSize);
                var localPort = unchecked((ushort)IPAddress.NetworkToHostOrder(
                    unchecked((short)row.LocalPort)));
                if (localPort != port ||
                    !new IPAddress(BitConverter.GetBytes(row.LocalAddress)).Equals(IPAddress.Loopback) ||
                    row.OwningProcessId is 0 or > int.MaxValue)
                {
                    continue;
                }

                processId = (int)row.OwningProcessId;
                return true;
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadOptionalArgument(string[] args, string name)
    {
        var index = Array.FindIndex(
            args,
            argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void ValidatePort(int port)
    {
        if (port is < MinimumPort or > MaximumPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "CDP port must be between 1024 and 65535.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int outputBufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int ipVersion,
        int tableClass,
        uint reserved);

    private static void Log(string eventName, string detail)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var safeDetail = (detail ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            if (safeDetail.Length > 1000)
            {
                safeDetail = safeDetail[..1000];
            }
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.UtcNow:O}\t{eventName}\t{safeDetail}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Diagnostics must never affect Codex startup or the existing gateway.
        }
    }

    private static string BuildReviewedRendererSource(RendererPatchProfile profile)
    {
        return "prefix;" +
               string.Join(";middle;", profile.PatchContract.Select(patch => patch.Original)) +
               ";anchors;" +
               string.Join(";anchor;", profile.SemanticAnchors.Select(anchor => anchor.Value)) +
               ";suffix";
    }

    private static string BuildCompatibleRendererSource(RendererPatchProfile profile)
    {
        var patches = profile.PatchContract.ToDictionary(
            patch => patch.Name,
            patch => patch.Original,
            StringComparer.Ordinal);
        var anchors = profile.SemanticAnchors.ToDictionary(
            anchor => anchor.Name,
            anchor => anchor.Value,
            StringComparer.Ordinal);
        var tiers = Regex.Matches(
                anchors["fast-is-priority"],
                "([A-Za-z_$][A-Za-z0-9_$]*)=`(?:priority|fast|ultrafast|default)`",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .ToArray();
        var fallbackShape = Regex.Match(
            anchors["fast-fallback"],
            "(?<fallback>[A-Za-z_$][A-Za-z0-9_$]*)=\\[(?<standardOption>[A-Za-z_$][A-Za-z0-9_$]*)," +
            "\\{description:(?<labels>[A-Za-z_$][A-Za-z0-9_$]*)\\.fastDescription",
            RegexOptions.CultureInvariant);
        if (tiers.Length != 4 || !fallbackShape.Success)
        {
            throw new InvalidOperationException(
                "Compatibility renderer fixture requires the four modern semantic anchors.");
        }
        var declaration =
            "var " + string.Join(",", tiers) + "," +
            fallbackShape.Groups["labels"].Value + "," +
            fallbackShape.Groups["standardOption"].Value + "," +
            fallbackShape.Groups["fallback"].Value + ",";
        return "prefix;" +
               declaration +
               "CompatibilityMetadata,CompatibilityInitializer=CompatibilityLoader((()=>{" +
               anchors["fast-is-priority"] + ";" +
               anchors["fast-fallback"] + "}));" +
               patches["visibility"] + ";" +
               patches["config"] + ";" +
               anchors["config-key"] +
               "function CompatibilitySettings(e,t,n,r){let i=(0,CompatibilityMemo.c)(41)," +
               "compatibilityState=0;let{data:compatibilityModels," +
               "isLoading:compatibilityModelsLoading}=CompatibilityModelsHook(s)," +
               patches["auth-capture"] +
               "let compatibleSelection=0;" + patches["selection"] +
               "let compatibleOptions=0;" + patches["options"] +
               "let compatibleResult={" + anchors["request-tier"] +
               "};return compatibleResult};suffix";
    }

    private static void ValidateRendererPatchProfile(RendererPatchProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) ||
            string.IsNullOrWhiteSpace(profile.BundleUrl) ||
            profile.SourceSha256.Length != 64 ||
            profile.SourceSha256.Any(value => !Uri.IsHexDigit(value)) ||
            profile.PatchedSha256 != null &&
            (profile.PatchedSha256.Length != 64 ||
             profile.PatchedSha256.Any(value => !Uri.IsHexDigit(value))) ||
            profile.IsCompatibilityProfile ||
            profile.PatchContract.Length == 0 ||
            profile.SemanticAnchors.Length == 0)
        {
            throw new InvalidOperationException("Native Fast bridge contains an invalid renderer profile.");
        }

        var source = BuildReviewedRendererSource(profile);
        var syntheticProfile = profile with
        {
            SourceSha256 = SourceFingerprint(source),
            PatchedSha256 = null
        };
        var result = PatchRendererSource(syntheticProfile, source, out var patched);
        if (result.Status != RendererPatchStatus.Patched ||
            profile.PatchContract.Any(contract =>
                CountOccurrences(patched, contract.Original) != 0 ||
                CountOccurrences(patched, contract.Patched) != 1))
        {
            throw new InvalidOperationException(
                $"Native Fast bridge did not patch renderer profile {profile.Name} completely.");
        }

        var secondPass = PatchRendererSource(syntheticProfile, patched, out var secondSource);
        if (secondPass.Status != RendererPatchStatus.AlreadyPatched ||
            secondSource != patched ||
            !IsReviewedPatchedRendererSource(syntheticProfile, patched) ||
            IsReviewedPatchedRendererSource(syntheticProfile, patched + ";tampered"))
        {
            throw new InvalidOperationException(
                $"Native Fast bridge renderer profile {profile.Name} is not safely idempotent.");
        }

        var rejectedSources = new List<string>
        {
            source + profile.PatchContract[0].Original
        };
        foreach (var contract in profile.PatchContract)
        {
            rejectedSources.Add(source.Replace(contract.Original, string.Empty, StringComparison.Ordinal));
            rejectedSources.Add(source.Replace(contract.Original, contract.Patched, StringComparison.Ordinal));
        }
        foreach (var rejected in rejectedSources)
        {
            if (PatchRendererSource(syntheticProfile, rejected, out var untouched).Status != RendererPatchStatus.Rejected ||
                untouched != rejected)
            {
                throw new InvalidOperationException(
                    $"Native Fast bridge accepted an incomplete or ambiguous {profile.Name} renderer.");
            }
        }
    }

    internal static void ValidatePatchContract()
    {
        if (RendererPatchProfiles.Length != 5 ||
            RendererPatchProfiles.Select(profile => profile.BundleUrl).Distinct(StringComparer.Ordinal).Count() !=
            RendererPatchProfiles.Length)
        {
            throw new InvalidOperationException("Native Fast bridge renderer profiles are incomplete or ambiguous.");
        }

        foreach (var profile in RendererPatchProfiles)
        {
            ValidateRendererPatchProfile(profile);
        }

        var versionProfiles = RendererPatchProfiles
            .DistinctBy(profile => profile.SourceSha256, StringComparer.Ordinal)
            .ToArray();
        foreach (var sourceProfile in versionProfiles)
        {
            var source = BuildReviewedRendererSource(sourceProfile);
            foreach (var otherProfile in versionProfiles.Where(profile =>
                         !ReferenceEquals(profile.PatchContract, sourceProfile.PatchContract)))
            {
                if (PatchRendererSource(otherProfile, source, out var untouched).Status !=
                        RendererPatchStatus.Rejected ||
                    untouched != source)
                {
                    throw new InvalidOperationException(
                        "Native Fast bridge mixed contracts from different renderer profiles.");
                }
            }
        }

        if (!IsRendererBundleUrl("app://codex/assets/app-initial-DWsVN4CS.js") ||
            !IsRendererBundleUrl("app://-/assets/app-initial-DWsVN4CS.js") ||
            !IsRendererBundleUrl("app://-/assets/app-initial-C_Tkoze_.js") ||
            !IsRendererBundleUrl("app://-/assets/app-initial-izy3qYQi.js") ||
            !IsRendererBundleUrl("app://-/assets/app-initial-BhpTek7p.js") ||
            !IsRendererBundleUrl("app://-/assets/app-initial-Future_123.js") ||
            IsRendererBundleUrl(null) ||
            IsRendererBundleUrl("") ||
            IsRendererBundleUrl(" app://-/assets/app-initial-DWsVN4CS.js") ||
            IsRendererBundleUrl("APP://-/assets/app-initial-DWsVN4CS.js") ||
            IsRendererBundleUrl("APP://-/assets/app-initial-C_Tkoze_.js") ||
            IsRendererBundleUrl("APP://-/assets/app-initial-izy3qYQi.js") ||
            IsRendererBundleUrl("APP://-/assets/app-initial-BhpTek7p.js") ||
            IsRendererBundleUrl("https://example.com/assets/app-initial-DWsVN4CS.js") ||
            IsRendererBundleUrl("file://-/assets/app-initial-DWsVN4CS.js") ||
            IsRendererBundleUrl("app://other/assets/app-initial-DWsVN4CS.js") ||
            IsRendererBundleUrl("app://user@-/assets/app-initial-DWsVN4CS.js") ||
            IsRendererBundleUrl("app://-:19335/assets/app-initial-DWsVN4CS.js") ||
            IsRendererBundleUrl("app://codex/assets/other.js") ||
            IsRendererBundleUrl("app://-/assets/app-initial-Other.js") ||
            IsRendererBundleUrl("app://-/Assets/app-initial-DWsVN4CS.js") ||
            IsRendererBundleUrl("app://-/assets/app-initial-DWsVN4CS.js/") ||
            IsRendererBundleUrl("app://-/assets/./app-initial-DWsVN4CS.js") ||
            IsRendererBundleUrl("app://-/assets/foo/../app-initial-DWsVN4CS.js") ||
            IsRendererBundleUrl("app://-/%61ssets/app-initial-DWsVN4CS.js") ||
            IsRendererBundleUrl("app://-/assets/%61pp-initial-DWsVN4CS.js") ||
            IsRendererBundleUrl("app://-/assets/app-initial-DWsVN4CS%2Ejs") ||
            IsRendererBundleUrl("app://-/assets/app-initial-DWsVN4CS.js#fragment") ||
            IsRendererBundleUrl("app://codex/assets/app-initial-DWsVN4CS.js?changed=1") ||
            IsRendererBundleUrl("app://-/assets/app-initial-DWsVN4CS.js?changed=1") ||
            IsRendererBundleUrl("app://-/assets/app-initial-C_Tkoze_.js?changed=1") ||
            IsRendererBundleUrl("app://-/assets/app-initial-izy3qYQi.js?changed=1") ||
            IsRendererBundleUrl("app://-/assets/app-initial-BhpTek7p.js?changed=1") ||
            IsRendererBundleUrl(
                "app://-/assets/app-initial-" + new string('a', 81) + ".js") ||
            IsRendererBundleUrl("app://-/assets/app-initial-DWsVN4CS.js\n"))
        {
            throw new InvalidOperationException("Native Fast bridge renderer URL validation failed.");
        }

        var legacyProfiles = ReviewedRendererProfilesForPage("app://codex/");
        var currentProfiles = ReviewedRendererProfilesForPage("app://-/index.html");
        var overlayProfiles = ReviewedRendererProfilesForPage(
            "app://-/index.html?initialRoute=%2Favatar-overlay");
        if (legacyProfiles.Count != 1 ||
            !string.Equals(
                legacyProfiles[0].BundleUrl,
                LegacyRendererBundleUrl,
                StringComparison.Ordinal) ||
            currentProfiles.Count != 4 ||
            !currentProfiles.Any(profile => string.Equals(
                profile.BundleUrl,
                PreviousRendererBundleUrl,
                StringComparison.Ordinal)) ||
            !currentProfiles.Any(profile => string.Equals(
                profile.BundleUrl,
                CurrentRendererBundleUrl,
                StringComparison.Ordinal)) ||
            !currentProfiles.Any(profile => string.Equals(
                profile.BundleUrl,
                LatestRendererBundleUrl,
                StringComparison.Ordinal)) ||
            !currentProfiles.Any(profile => string.Equals(
                profile.BundleUrl,
                Current5229RendererBundleUrl,
                StringComparison.Ordinal)) ||
            overlayProfiles.Count != currentProfiles.Count ||
            ReviewedRendererProfilesForPage("app://-/unreviewed.html").Count != 0)
        {
            throw new InvalidOperationException("Native Fast bridge page-to-renderer profile mapping failed.");
        }

        if (!IsRendererBundleUrlForPage(
                "app://-/index.html",
                "app://-/assets/app-initial-Future_123.js") ||
            !IsRendererBundleUrlForPage(
                "app://-/index.html?initialRoute=%2Favatar-overlay",
                Current5229RendererBundleUrl) ||
            IsRendererBundleUrlForPage(
                "app://codex/",
                "app://-/assets/app-initial-Future_123.js") ||
            IsRendererBundleUrlForPage(
                "app://-/unreviewed.html",
                "app://-/assets/app-initial-Future_123.js"))
        {
            throw new InvalidOperationException(
                "Native Fast bridge compatibility candidates escaped their reviewed pages.");
        }

        if (!IsReviewedOfficialCodexPageUrl("app://codex/") ||
            !IsReviewedOfficialCodexPageUrl("app://-/index.html") ||
            !IsReviewedOfficialCodexPageUrl(
                "app://-/index.html?initialRoute=%2Favatar-overlay") ||
            IsReviewedOfficialCodexPageUrl(null) ||
            IsReviewedOfficialCodexPageUrl("") ||
            IsReviewedOfficialCodexPageUrl(" app://-/index.html") ||
            IsReviewedOfficialCodexPageUrl("APP://-/index.html") ||
            IsReviewedOfficialCodexPageUrl("app://fs/") ||
            IsReviewedOfficialCodexPageUrl("app://codex/settings") ||
            IsReviewedOfficialCodexPageUrl("app://codex/?changed=1") ||
            IsReviewedOfficialCodexPageUrl("app://user@codex/") ||
            IsReviewedOfficialCodexPageUrl("app://codex/#fragment") ||
            IsReviewedOfficialCodexPageUrl("app://codex/%2e") ||
            IsReviewedOfficialCodexPageUrl("app://user@-/index.html") ||
            IsReviewedOfficialCodexPageUrl("app://-:19335/index.html") ||
            IsReviewedOfficialCodexPageUrl("app://-/index.html/") ||
            IsReviewedOfficialCodexPageUrl("app://-/index.html/extra") ||
            IsReviewedOfficialCodexPageUrl("app://-/./index.html") ||
            IsReviewedOfficialCodexPageUrl("app://-/foo/../index.html") ||
            IsReviewedOfficialCodexPageUrl("app://-/%69ndex.html") ||
            IsReviewedOfficialCodexPageUrl("app://-/index%2Ehtml") ||
            IsReviewedOfficialCodexPageUrl("app://-:0/index.html") ||
            IsReviewedOfficialCodexPageUrl("app://-/index.html#fragment") ||
            IsReviewedOfficialCodexPageUrl("app://-/index.html?changed=1") ||
            IsReviewedOfficialCodexPageUrl(
                "app://-/index.html?initialRoute=/avatar-overlay") ||
            IsReviewedOfficialCodexPageUrl("app://-/index.html?initialRoute=%2Fsettings") ||
            IsReviewedOfficialCodexPageUrl(
                "app://-/index.html?initialRoute=%2favatar-overlay") ||
            IsReviewedOfficialCodexPageUrl(
                "app://-/index.html?initialRoute=%2Favatar-overlay&changed=1") ||
            IsReviewedOfficialCodexPageUrl("http://-/index.html") ||
            IsReviewedOfficialCodexPageUrl("https://codex/"))
        {
            throw new InvalidOperationException("Native Fast bridge page URL validation failed.");
        }

        var lexerFixture =
            "function Scanner(){const quotient=(value/2)/3;" +
            "const pattern=/[{}\\/]+/gi;" +
            "const nested=`outer ${format({value:`inner ${count / 2}`})}`;" +
            "// } ignored\n/* { ignored } */return {quotient,pattern,nested}}";
        var lexerOpeningBrace = lexerFixture.IndexOf('{');
        var lexerReturn = lexerFixture.IndexOf("return", StringComparison.Ordinal);
        var lexerTemplateText = lexerFixture.IndexOf("outer", StringComparison.Ordinal);
        if (!TryFindJavaScriptBlockEnd(
                lexerFixture,
                lexerOpeningBrace,
                out var lexerClosingBrace) ||
            lexerClosingBrace != lexerFixture.Length - 1 ||
            !TryGetJavaScriptBracePaths(
                lexerFixture,
                new[] { 0, lexerReturn },
                out var lexerPaths) ||
            lexerPaths[0].Length != 0 ||
            !lexerPaths[lexerReturn].Contains(lexerOpeningBrace) ||
            TryGetJavaScriptBracePaths(
                lexerFixture,
                new[] { lexerTemplateText },
                out _) ||
            TryFindJavaScriptBlockEnd(
                lexerFixture[..^1],
                lexerOpeningBrace,
                out _) ||
            TryFindJavaScriptBlockEnd(
                "function Broken(){const value='unterminated}",
                "function Broken()".Length,
                out _) ||
            TryFindJavaScriptBlockEnd(
                "function Broken(){const value=`unterminated ${value}`;",
                "function Broken()".Length,
                out _) ||
            TryFindJavaScriptBlockEnd(
                "function Broken(){const value=/unterminated\n}",
                "function Broken()".Length,
                out _))
        {
            throw new InvalidOperationException(
                "Native Fast JavaScript lexical boundary validation failed.");
        }

        var compatibilitySourceProfiles = new[]
        {
            RendererPatchProfiles[2],
            RendererPatchProfiles[3],
            RendererPatchProfiles[4]
        };
        for (var index = 0; index < compatibilitySourceProfiles.Length; index++)
        {
            var sourceProfile = compatibilitySourceProfiles[index];
            var candidateUrl =
                $"app://-/assets/app-initial-Future_{index:D2}_compat.js";
            var compatibleSource = BuildCompatibleRendererSource(sourceProfile);
            if (!TryCreateCompatibleRendererProfile(
                    candidateUrl,
                    compatibleSource,
                    out var compatibilityProfile,
                    out var compatibilityDetail) ||
                !compatibilityProfile.IsCompatibilityProfile ||
                compatibilityProfile.PatchedSha256 == null ||
                !compatibilityProfile.SourceSha256.Equals(
                    SourceFingerprint(compatibleSource),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Native Fast structural compatibility rejected {sourceProfile.Name}: " +
                    compatibilityDetail);
            }

            var compatibilityPatch = PatchRendererSource(
                compatibilityProfile,
                compatibleSource,
                out var compatiblePatchedSource);
            var compatibilitySecondPass = PatchRendererSource(
                compatibilityProfile,
                compatiblePatchedSource,
                out var compatibleSecondSource);
            CacheCompatibleRendererProfile(compatibilityProfile);
            if (compatibilityPatch.Status != RendererPatchStatus.Patched ||
                compatibilitySecondPass.Status != RendererPatchStatus.AlreadyPatched ||
                !string.Equals(
                    compatiblePatchedSource,
                    compatibleSecondSource,
                    StringComparison.Ordinal) ||
                !IsReviewedPatchedRendererSource(
                    compatibilityProfile,
                    compatiblePatchedSource) ||
                !TryGetCachedCompatibleRendererProfile(
                    candidateUrl,
                    compatibilityProfile.SourceSha256,
                    out var cachedOriginalProfile) ||
                !ReferenceEquals(cachedOriginalProfile, compatibilityProfile) ||
                !TryGetCachedCompatibleRendererProfile(
                    candidateUrl,
                    compatibilityProfile.PatchedSha256,
                    out var cachedPatchedProfile) ||
                !ReferenceEquals(cachedPatchedProfile, compatibilityProfile))
            {
                throw new InvalidOperationException(
                    $"Native Fast structural compatibility was not fingerprint-bound for {sourceProfile.Name}.");
            }
        }

        var negativeProfile = RendererPatchProfiles[4];
        var negativeSource = BuildCompatibleRendererSource(negativeProfile);
        var negativePatches = negativeProfile.PatchContract.ToDictionary(
            patch => patch.Name,
            patch => patch.Original,
            StringComparer.Ordinal);
        const string selectionSentinel = "__native_fast_selection_sentinel__";
        var reorderedSource = negativeSource
            .Replace(negativePatches["selection"], selectionSentinel, StringComparison.Ordinal)
            .Replace(
                negativePatches["options"],
                negativePatches["selection"],
                StringComparison.Ordinal)
            .Replace(
                selectionSentinel,
                negativePatches["options"],
                StringComparison.Ordinal);
        var selectionOutsideFunction =
            negativeSource.Replace(
                negativePatches["selection"],
                string.Empty,
                StringComparison.Ordinal) + negativePatches["selection"];
        const string compatibilityAuthDeclarationPrefix =
            "let{data:compatibilityModels,isLoading:compatibilityModelsLoading}=" +
            "CompatibilityModelsHook(s),";
        var authCaptureInChildBlock = negativeSource.Replace(
            compatibilityAuthDeclarationPrefix + negativePatches["auth-capture"],
            "{" + compatibilityAuthDeclarationPrefix +
            negativePatches["auth-capture"] + "}",
            StringComparison.Ordinal);
        var mismatchedFallback = negativeSource.Replace(
            Current5229RendererSemanticAnchors.Single(anchor =>
                anchor.Name.Equals("fast-fallback", StringComparison.Ordinal)).Value,
            "jEr=[AEr,{description:ok.fastDescription,iconKind:`fast`," +
            "label:ok.fastLabel,tier:null,value:DEr}]",
            StringComparison.Ordinal);
        var rejectedCompatibilitySources = new[]
        {
            negativeSource + negativePatches["visibility"],
            negativeSource.Replace(
                negativePatches["config"],
                string.Empty,
                StringComparison.Ordinal),
            negativeSource + "_camAuth",
            reorderedSource,
            selectionOutsideFunction,
            authCaptureInChildBlock,
            mismatchedFallback
        };
        foreach (var rejectedSource in rejectedCompatibilitySources)
        {
            if (TryCreateCompatibleRendererProfile(
                    "app://-/assets/app-initial-Future_negative.js",
                    rejectedSource,
                    out _,
                    out _))
            {
                throw new InvalidOperationException(
                    "Native Fast structural compatibility accepted an ambiguous or disconnected renderer.");
            }
        }

        if (!TryCreateCompatibleRendererProfile(
                "app://-/assets/app-initial-Future_restart.js",
                negativeSource,
                out var restartProfile,
                out _) ||
            PatchRendererSource(
                restartProfile,
                negativeSource,
                out var restartPatchedSource).Status != RendererPatchStatus.Patched ||
            !TryCreateCompatibleRendererProfile(
                "app://-/assets/app-initial-Future_uncached.js",
                restartPatchedSource,
                out var uncachedPatchedProfile,
                out _) ||
            !uncachedPatchedProfile.SourceSha256.Equals(
                restartProfile.SourceSha256,
                StringComparison.Ordinal) ||
            !uncachedPatchedProfile.PatchedSha256!.Equals(
                SourceFingerprint(restartPatchedSource),
                StringComparison.Ordinal) ||
            PatchRendererSource(
                uncachedPatchedProfile,
                restartPatchedSource,
                out _).Status != RendererPatchStatus.AlreadyPatched)
        {
            throw new InvalidOperationException(
                "Native Fast did not safely recover an uncached already-patched future renderer.");
        }
        for (var partialCount = 1;
             partialCount < CompatibilityAuthCaptureOccurrences;
             partialCount++)
        {
            if (TryCreateCompatibleRendererProfile(
                    "app://-/assets/app-initial-Future_partial.js",
                    negativeSource + string.Concat(
                        Enumerable.Repeat("_camAuth", partialCount)),
                    out _,
                    out _))
            {
                throw new InvalidOperationException(
                    "Native Fast accepted a partial compatibility auth-capture contract.");
            }
        }
        if (TryCreateCompatibleRendererProfile(
                "app://-/assets/app-initial-Future_extra.js",
                restartPatchedSource + "_camAuth",
                out _,
                out _))
        {
            throw new InvalidOperationException(
                "Native Fast accepted an over-complete compatibility auth-capture contract.");
        }

        var startInfo = BuildStartInfo(19335, "test-browser");
        if (!startInfo.ArgumentList.Contains(ProcessArgument) ||
            !startInfo.ArgumentList.Contains(PortArgument) ||
            !startInfo.ArgumentList.Contains("19335") ||
            !startInfo.ArgumentList.Contains(BrowserIdArgument) ||
            !startInfo.ArgumentList.Contains("test-browser") ||
            startInfo.UseShellExecute ||
            !startInfo.CreateNoWindow ||
            startInfo.WindowStyle != ProcessWindowStyle.Hidden)
        {
            throw new InvalidOperationException("Native Fast bridge child-process arguments were not preserved.");
        }

        var readyEventName = BuildRendererReadyEventName(19335, "test-browser");
        if (readyEventName.Equals(
                BuildRendererReadyEventName(19335, "other-browser"),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Native Fast bridge readiness was not scoped to the browser identity.");
        }
        using var readyEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: readyEventName);
        readyEvent.Reset();
        var readiness = new RendererReadinessState(readyEvent);
        readiness.SynchronizeTargets(["renderer-a", "renderer-b"]);
        var firstGeneration = readiness.BeginConnection("renderer-a");
        var secondGeneration = readiness.BeginConnection("renderer-b");
        readiness.Resume();
        readiness.MarkReady("renderer-a", firstGeneration);
        if (readyEvent.WaitOne(0))
        {
            throw new InvalidOperationException(
                "Native Fast bridge readiness accepted only one of multiple current renderers.");
        }
        readiness.MarkReady("renderer-b", secondGeneration);
        if (!readyEvent.WaitOne(0))
        {
            throw new InvalidOperationException(
                "Native Fast bridge readiness did not accept every current renderer.");
        }
        readyEvent.Reset();
        readiness.Suspend();
        readiness.SynchronizeTargets(["renderer-a", "renderer-b"]);
        if (readyEvent.WaitOne(0))
        {
            throw new InvalidOperationException(
                "Native Fast bridge published readiness before renderer reconciliation completed.");
        }
        readiness.Resume();
        if (!readyEvent.WaitOne(0))
        {
            throw new InvalidOperationException(
                "Native Fast bridge readiness was not republished by the live owner.");
        }
        readiness.Suspend();
        readiness.SynchronizeTargets(["renderer-a", "renderer-b"]);
        var replacementGeneration = readiness.BeginConnection("renderer-a");
        readiness.MarkReady("renderer-a", firstGeneration);
        if (readyEvent.WaitOne(0))
        {
            throw new InvalidOperationException(
                "Native Fast bridge readiness accepted a stale renderer connection.");
        }
        readiness.Resume();
        if (readyEvent.WaitOne(0))
        {
            throw new InvalidOperationException(
                "Native Fast bridge restored readiness before a replacement renderer completed.");
        }
        readiness.MarkReady("renderer-a", replacementGeneration);
        if (!readyEvent.WaitOne(0))
        {
            throw new InvalidOperationException(
                "Native Fast bridge did not restore readiness after the replacement renderer completed.");
        }
        readiness.MarkPending("renderer-b", secondGeneration);
        if (readyEvent.WaitOne(0))
        {
            throw new InvalidOperationException(
                "Native Fast bridge readiness ignored a pending current renderer.");
        }
        readiness.Suspend();
        readiness.SynchronizeTargets(["renderer-a"]);
        if (readyEvent.WaitOne(0))
        {
            throw new InvalidOperationException(
                "Native Fast bridge published readiness before a removed renderer was reconciled.");
        }
        readiness.Resume();
        if (!readyEvent.WaitOne(0))
        {
            throw new InvalidOperationException(
                "Native Fast bridge readiness retained a renderer that no longer exists.");
        }
        readiness.Suspend();
        readiness.SynchronizeTargets(Array.Empty<string>());

        const string testPageUrl = "app://-/index.html";
        var bufferedWorkerReadiness = new RendererWorkerReadinessState();
        bufferedWorkerReadiness.Arm("buffered-frame", "buffered-loader", testPageUrl);
        bufferedWorkerReadiness.MarkPreflightVerified(alreadyPatched: false);
        if (!bufferedWorkerReadiness.TryBeginFetch(
                "buffered-frame",
                testPageUrl,
                out var bufferedFetch) ||
            !bufferedWorkerReadiness.IsCurrent(bufferedFetch))
        {
            throw new InvalidOperationException(
                "Native Fast bridge did not arm the buffered renderer response contract.");
        }
        bufferedWorkerReadiness.MarkFulfilled(bufferedFetch);
        if (!bufferedWorkerReadiness.MarkScriptVerified(
                bufferedFetch.Epoch,
                bufferedFetch.LoaderId) ||
            !bufferedWorkerReadiness.LifecycleEvent(
                bufferedFetch.FrameId,
                bufferedFetch.LoaderId,
                "load") ||
            bufferedWorkerReadiness.IsReady)
        {
            throw new InvalidOperationException(
                "Native Fast bridge published buffered renderer readiness before attachment completed.");
        }
        bufferedWorkerReadiness.Activate();
        if (!bufferedWorkerReadiness.IsReady)
        {
            throw new InvalidOperationException(
                "Native Fast bridge did not publish buffered same-loader evidence after attachment.");
        }

        var guardedWorkerReadiness = new RendererWorkerReadinessState();
        guardedWorkerReadiness.Arm("guarded-frame", "guarded-loader", testPageUrl);
        guardedWorkerReadiness.MarkPreflightVerified(alreadyPatched: true);
        guardedWorkerReadiness.Activate();
        if (guardedWorkerReadiness.IsReady)
        {
            throw new InvalidOperationException(
                "Native Fast bridge treated preflight admission as fulfilled renderer evidence.");
        }
        if (!guardedWorkerReadiness.TryBeginFetch(
                "guarded-frame",
                testPageUrl,
                out var guardedFetch))
        {
            throw new InvalidOperationException(
                "Native Fast bridge did not accept the reviewed main-frame response.");
        }
        guardedWorkerReadiness.MarkFulfilled(guardedFetch);
        if (!guardedWorkerReadiness.MarkScriptVerified(
                guardedFetch.Epoch,
                guardedFetch.LoaderId) ||
            guardedWorkerReadiness.IsReady)
        {
            throw new InvalidOperationException(
                "Native Fast bridge became ready before the fulfilled loader completed.");
        }
        if (guardedWorkerReadiness.LifecycleEvent(
                guardedFetch.FrameId,
                "stale-loader",
                "load") ||
            guardedWorkerReadiness.IsReady)
        {
            throw new InvalidOperationException(
                "Native Fast bridge accepted a lifecycle event from a stale loader.");
        }
        if (!guardedWorkerReadiness.LifecycleEvent(
                guardedFetch.FrameId,
                guardedFetch.LoaderId,
                "load") ||
            !guardedWorkerReadiness.IsReady)
        {
            throw new InvalidOperationException(
                "Native Fast bridge did not require fulfill, read-back, and same-loader load.");
        }
        if (!guardedWorkerReadiness.BeginFrameLoading(guardedFetch.FrameId) ||
            guardedWorkerReadiness.IsReady)
        {
            throw new InvalidOperationException(
                "Native Fast bridge did not clear readiness when a new navigation began.");
        }
        if (!guardedWorkerReadiness.FrameNavigated(
                guardedFetch.FrameId,
                "replacement-loader",
                testPageUrl,
                pageIsReviewed: true) ||
            guardedWorkerReadiness.IsReady ||
            guardedWorkerReadiness.IsCurrent(guardedFetch))
        {
            throw new InvalidOperationException(
                "Native Fast bridge retained evidence from a replaced loader.");
        }
        guardedWorkerReadiness.MarkResponseFailed(guardedFetch);
        guardedWorkerReadiness.MarkVerificationFailed(
            guardedFetch.Epoch,
            guardedFetch.LoaderId);
        if (!guardedWorkerReadiness.TryBeginFetch(
                guardedFetch.FrameId,
                testPageUrl,
                out var rejectedFetch))
        {
            throw new InvalidOperationException(
                "Native Fast bridge did not arm the replacement loader response.");
        }
        guardedWorkerReadiness.MarkResponseFailed(rejectedFetch);
        if (guardedWorkerReadiness.IsReady)
        {
            throw new InvalidOperationException(
                "Native Fast bridge published readiness after a response rejection.");
        }
        if (!guardedWorkerReadiness.TryBeginFetch(
                guardedFetch.FrameId,
                testPageUrl,
                out var replacementFetch))
        {
            throw new InvalidOperationException(
                "Native Fast bridge did not permit a later reviewed replacement response.");
        }
        guardedWorkerReadiness.MarkFulfilled(replacementFetch);
        if (!guardedWorkerReadiness.MarkScriptVerified(
                replacementFetch.Epoch,
                replacementFetch.LoaderId) ||
            !guardedWorkerReadiness.LifecycleEvent(
                replacementFetch.FrameId,
                replacementFetch.LoaderId,
                "load") ||
            !guardedWorkerReadiness.IsReady)
        {
            throw new InvalidOperationException(
                "Native Fast bridge did not recover after a fully verified replacement response.");
        }
        guardedWorkerReadiness.MarkVerificationFailed(
            replacementFetch.Epoch,
            replacementFetch.LoaderId);
        if (guardedWorkerReadiness.IsReady)
        {
            throw new InvalidOperationException(
                "Native Fast bridge ignored a current-loader read-back failure.");
        }
        guardedWorkerReadiness.Fault();
        if (guardedWorkerReadiness.IsReady)
        {
            throw new InvalidOperationException(
                "Native Fast bridge retained readiness after its CDP connection faulted.");
        }

        var reloadWorkerReadiness = new RendererWorkerReadinessState();
        reloadWorkerReadiness.Arm("reload-frame", "reload-loader", testPageUrl);
        reloadWorkerReadiness.MarkPreflightVerified(alreadyPatched: false);
        reloadWorkerReadiness.Activate();
        if (!reloadWorkerReadiness.TryBeginControlledReload(out _) ||
            reloadWorkerReadiness.IsReady ||
            !reloadWorkerReadiness.FrameNavigated(
                "reload-frame",
                "reloaded-loader",
                testPageUrl,
                pageIsReviewed: true))
        {
            throw new InvalidOperationException(
                "Native Fast bridge did not bind a controlled reload to a fresh navigation.");
        }
        reloadWorkerReadiness.ExecutionContextsCleared();

        var firstTargetReloadGate = new ControlledReloadGate();
        if (!firstTargetReloadGate.TryConsume() || firstTargetReloadGate.TryConsume())
        {
            throw new InvalidOperationException(
                "Native Fast bridge allowed more than one controlled reload for a target.");
        }
        var secondTargetReloadGate = new ControlledReloadGate();
        if (!secondTargetReloadGate.TryConsume())
        {
            throw new InvalidOperationException(
                "Native Fast bridge shared a controlled reload gate across different targets.");
        }

        var accountDisplayScript = BuildAccountDisplayScript(
            new CodexAccountDisplay("model\"${notCode}@example.test", "chatgpt@example.com")
            {
                ModelAccountTypeLabel = "PAT 模型账号"
            });
        using var accountDisplayResult = JsonDocument.Parse(
            """{"result":{"type":"object","value":{"observerInstalled":true,"shellReady":true,"triggerReady":true,"displayMounted":false,"version":"1.2.0"}}}""");
        using var accountDisplayFrameTree = JsonDocument.Parse(
            """{"frameTree":{"frame":{"id":"frame-1","url":"app://-/index.html"}}}""");
        using var rejectedAccountDisplayFrameTree = JsonDocument.Parse(
            """{"frameTree":{"frame":{"id":"frame-1","url":"app://-/index.html?initialRoute=%2Favatar-overlay"}}}""");
        if (!accountDisplayScript.Contains("data-cam-account-display", StringComparison.Ordinal) ||
            !accountDisplayScript.Contains("MutationObserver", StringComparison.Ordinal) ||
            !accountDisplayScript.Contains("textContent", StringComparison.Ordinal) ||
            accountDisplayScript.Contains("innerHTML", StringComparison.Ordinal) ||
            !accountDisplayScript.Contains(
                JsonSerializer.Serialize("PAT 模型账号"),
                StringComparison.Ordinal) ||
            !accountDisplayScript.Contains("chatgpt@example.com", StringComparison.Ordinal) ||
            !IsSuccessfulAccountDisplayEvaluation(accountDisplayResult.RootElement) ||
            !IsExpectedPrimaryOfficialCodexFrameTree(
                accountDisplayFrameTree.RootElement,
                "app://-/index.html") ||
            IsExpectedPrimaryOfficialCodexFrameTree(
                rejectedAccountDisplayFrameTree.RootElement,
                "app://-/index.html"))
        {
            throw new InvalidOperationException(
                "Codex account identity display injection is not bounded, idempotent, and text-only.");
        }
        var invalidAccountDisplayRejected = false;
        try
        {
            _ = BuildAccountDisplayScript(
                new CodexAccountDisplay("model", "Display <chatgpt@example.com>"));
        }
        catch (ArgumentException)
        {
            invalidAccountDisplayRejected = true;
        }
        if (!invalidAccountDisplayRejected)
        {
            throw new InvalidOperationException(
                "Codex account identity display accepted a non-canonical email address.");
        }

        if (WaitForRendererPatch(19335, "test-browser", TimeSpan.Zero))
        {
            throw new InvalidOperationException("Native Fast bridge readiness started in a stale signaled state.");
        }
        readyEvent.Set();
        if (!WaitForRendererPatch(19335, "test-browser", TimeSpan.Zero))
        {
            throw new InvalidOperationException("Native Fast bridge readiness handshake did not cross process handles.");
        }

        const int outcomePort = 19366;
        const string outcomeBrowser = "test-outcome-browser";
        const int outcomeOwnerPid = 424242;
        const long outcomeOwnerStartTicks = 434343;
        var outcomeReadyName = BuildRendererReadyEventName(
            outcomePort,
            outcomeBrowser,
            outcomeOwnerPid,
            outcomeOwnerStartTicks);
        var outcomeSkippedName = BuildRendererPatchSkippedEventName(
            outcomePort,
            outcomeBrowser,
            outcomeOwnerPid,
            outcomeOwnerStartTicks);
        var outcomeReloadName = BuildRendererReloadAttemptedEventName(
            outcomePort,
            outcomeBrowser,
            outcomeOwnerPid,
            outcomeOwnerStartTicks);
        if (new[] { outcomeReadyName, outcomeSkippedName, outcomeReloadName }
                .Distinct(StringComparer.Ordinal)
                .Count() != 3 ||
            outcomeReloadName.Equals(
                BuildRendererReloadAttemptedEventName(
                    outcomePort,
                    outcomeBrowser,
                    outcomeOwnerPid + 1,
                    outcomeOwnerStartTicks),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Native Fast patch outcomes were not independently scoped to one immutable renderer owner.");
        }
        using var outcomeReady = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: outcomeReadyName);
        using var outcomeSkipped = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: outcomeSkippedName);
        using var outcomeReload = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: outcomeReloadName);
        outcomeReady.Reset();
        outcomeSkipped.Reset();
        outcomeReload.Reset();
        if (WaitForRendererPatchOutcome(
                outcomePort,
                outcomeBrowser,
                outcomeOwnerPid,
                outcomeOwnerStartTicks,
                TimeSpan.Zero) != NativeFastPatchWaitOutcome.TimedOutWithoutReload)
        {
            throw new InvalidOperationException(
                "Native Fast patch outcome inferred a reload without an explicit owner-scoped signal.");
        }
        outcomeSkipped.Set();
        if (WaitForRendererPatchOutcome(
                outcomePort,
                outcomeBrowser,
                outcomeOwnerPid,
                outcomeOwnerStartTicks,
                TimeSpan.Zero) != NativeFastPatchWaitOutcome.SkippedWithoutReload)
        {
            throw new InvalidOperationException(
                "Native Fast preflight rejection did not terminate as a no-reload outcome.");
        }
        outcomeSkipped.Reset();
        outcomeReload.Set();
        if (WaitForRendererPatchOutcome(
                outcomePort,
                outcomeBrowser,
                outcomeOwnerPid,
                outcomeOwnerStartTicks,
                TimeSpan.Zero) != NativeFastPatchWaitOutcome.TimedOutAfterReloadAttempt)
        {
            throw new InvalidOperationException(
                "Native Fast reload recovery was enabled without its explicit reload-attempt signal.");
        }
        outcomeSkipped.Set();
        if (WaitForRendererPatchOutcome(
                outcomePort,
                outcomeBrowser,
                outcomeOwnerPid,
                outcomeOwnerStartTicks,
                TimeSpan.Zero) != NativeFastPatchWaitOutcome.TimedOutAfterReloadAttempt)
        {
            throw new InvalidOperationException(
                "Native Fast skipped state overrode an owner-scoped reload-attempt signal.");
        }
        outcomeReady.Set();
        if (WaitForRendererPatchOutcome(
                outcomePort,
                outcomeBrowser,
                outcomeOwnerPid,
                outcomeOwnerStartTicks,
                TimeSpan.Zero) != NativeFastPatchWaitOutcome.Patched)
        {
            throw new InvalidOperationException(
                "Native Fast patched readiness did not take precedence over simultaneous reload and skipped state.");
        }
    }

    private sealed class RendererReadinessState
    {
        private readonly EventWaitHandle _rendererReady;
        private readonly object _syncRoot = new();
        private readonly Dictionary<string, long> _targetGenerations = new(StringComparer.Ordinal);
        private readonly HashSet<string> _readyTargets = new(StringComparer.Ordinal);
        private bool _suspended = true;

        internal RendererReadinessState(EventWaitHandle rendererReady)
        {
            _rendererReady = rendererReady;
        }

        internal void SynchronizeTargets(IEnumerable<string> targetIds)
        {
            var currentTargets = targetIds.ToHashSet(StringComparer.Ordinal);
            lock (_syncRoot)
            {
                foreach (var staleTarget in _targetGenerations.Keys
                             .Where(targetId => !currentTargets.Contains(targetId))
                             .ToArray())
                {
                    _targetGenerations.Remove(staleTarget);
                    _readyTargets.Remove(staleTarget);
                }
                foreach (var targetId in currentTargets)
                {
                    _targetGenerations.TryAdd(targetId, 0);
                }
                UpdateReadyEvent();
            }
        }

        internal void Resume()
        {
            lock (_syncRoot)
            {
                _suspended = false;
                UpdateReadyEvent();
            }
        }

        internal void Suspend()
        {
            lock (_syncRoot)
            {
                _suspended = true;
                UpdateReadyEvent();
            }
        }

        internal long BeginConnection(string targetId)
        {
            lock (_syncRoot)
            {
                if (!_targetGenerations.TryGetValue(targetId, out var generation))
                {
                    throw new InvalidOperationException("Cannot connect an inactive Codex renderer target.");
                }
                var nextGeneration = checked(generation + 1);
                _targetGenerations[targetId] = nextGeneration;
                _readyTargets.Remove(targetId);
                UpdateReadyEvent();
                return nextGeneration;
            }
        }

        internal void MarkPending(string targetId, long generation)
        {
            lock (_syncRoot)
            {
                if (_targetGenerations.TryGetValue(targetId, out var currentGeneration) &&
                    currentGeneration == generation)
                {
                    _readyTargets.Remove(targetId);
                    UpdateReadyEvent();
                }
            }
        }

        internal void MarkReady(string targetId, long generation)
        {
            lock (_syncRoot)
            {
                if (_targetGenerations.TryGetValue(targetId, out var currentGeneration) &&
                    currentGeneration == generation)
                {
                    _readyTargets.Add(targetId);
                    UpdateReadyEvent();
                }
            }
        }

        private void UpdateReadyEvent()
        {
            if (!_suspended &&
                _targetGenerations.Count > 0 &&
                _targetGenerations.Keys.All(_readyTargets.Contains))
            {
                _rendererReady.Set();
            }
            else
            {
                _rendererReady.Reset();
            }
        }
    }

    private sealed class RendererWorker : IAsyncDisposable
    {
        private readonly CdpConnection _connection;
        private readonly Action _notifyRendererPending;
        private readonly Action _notifyRendererReady;
        private readonly string _targetId;
        private readonly string _pageUrl;
        private readonly IReadOnlyDictionary<string, RendererPatchProfile> _reviewedProfiles;
        private readonly TaskCompletionSource<ObservedRendererScript> _preflightScript = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, byte> _seenScripts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _seenFetchRequests = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, PausedFetchResolutionState> _pausedFetchRequests =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, FulfilledRendererResponse> _fulfilledResponses =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long, long> _executionContextEpochs = new();
        private readonly SemaphoreSlim _responsePatchLock = new(1, 1);
        private readonly object _stateSync = new();
        private readonly RendererWorkerReadinessState _readinessState = new();
        private volatile bool _preflightFinished;
        private volatile bool _preflightAllowed;
        private volatile bool _disposed;
        private volatile bool _requiresReconnect;
        private RendererPatchProfile? _selectedProfile;

        private RendererWorker(
            CdpConnection connection,
            CdpTarget target,
            IReadOnlyList<RendererPatchProfile> reviewedProfiles,
            Action notifyRendererPending,
            Action notifyRendererReady)
        {
            _connection = connection;
            _targetId = target.Id;
            _pageUrl = target.PageUrl;
            _reviewedProfiles = reviewedProfiles.ToDictionary(
                profile => profile.BundleUrl,
                StringComparer.Ordinal);
            _notifyRendererPending = notifyRendererPending;
            _notifyRendererReady = notifyRendererReady;
            _connection.EventReceived += OnEventReceived;
            _connection.Closed += OnConnectionClosed;
        }

        internal bool IsClosed => _connection.IsClosed;
        internal bool RequiresReconnect => _requiresReconnect;
        internal bool IsPreflightAllowed => _preflightAllowed;

        internal void ActivateReadiness()
        {
            lock (_stateSync)
            {
                if (_disposed)
                {
                    return;
                }
                _readinessState.Activate();
                PublishReadiness();
            }
        }

        internal async Task StartControlledReloadAsync(
            ControlledReloadGate reloadGate,
            bool allowRendererReload,
            Action notifyReloadAttempted,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(notifyReloadAttempted);
            if (!allowRendererReload || !_preflightAllowed || !reloadGate.TryConsume())
            {
                return;
            }

            RendererFrameSnapshot snapshot;
            lock (_stateSync)
            {
                if (_disposed || !_readinessState.TryBeginControlledReload(out snapshot))
                {
                    return;
                }
                PublishReadiness();
            }

            try
            {
                // Signal before issuing Page.reload. If the CDP command begins navigation and
                // its acknowledgement is then lost, the parent must conservatively treat the
                // renderer as having reloaded and retain the clean-recovery path.
                notifyReloadAttempted();
                await _connection.SendAsync(
                    "Page.reload",
                    new
                    {
                        ignoreCache = true,
                        loaderId = snapshot.LoaderId
                    },
                    CommandTimeout,
                    cancellationToken);
                Log(
                    "renderer_controlled_reload_started",
                    $"target={_targetId}; loader={snapshot.LoaderId}");
            }
            catch (Exception ex)
            {
                lock (_stateSync)
                {
                    _readinessState.Fault();
                    PublishReadiness();
                }
                Log(
                    "renderer_controlled_reload_failed",
                    $"target={_targetId}; error={ex.Message}");
            }
        }

        private void ArmFromFrameTree(JsonElement result)
        {
            if (!result.TryGetProperty("frameTree", out var frameTree) ||
                !frameTree.TryGetProperty("frame", out var frame))
            {
                throw new InvalidOperationException("Codex did not return its main renderer frame.");
            }
            var frameId = frame.TryGetProperty("id", out var frameIdValue)
                ? frameIdValue.GetString()
                : null;
            var loaderId = frame.TryGetProperty("loaderId", out var loaderIdValue)
                ? loaderIdValue.GetString()
                : null;
            var pageUrl = frame.TryGetProperty("url", out var urlValue)
                ? urlValue.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(frameId) ||
                string.IsNullOrWhiteSpace(loaderId) ||
                !string.Equals(pageUrl, _pageUrl, StringComparison.Ordinal) ||
                !IsReviewedOfficialCodexPageUrl(pageUrl))
            {
                throw new InvalidOperationException("Codex returned an unreviewed main renderer frame.");
            }

            lock (_stateSync)
            {
                _readinessState.Arm(frameId, loaderId, pageUrl!);
                PublishReadiness();
            }
        }

        private async Task CompletePreflightAsync(CancellationToken cancellationToken)
        {
            ObservedRendererScript observed;
            using (var preflightTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                preflightTimeout.CancelAfter(RendererPreflightTimeout);
                try
                {
                    observed = await _preflightScript.Task.WaitAsync(preflightTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    RejectPreflight(
                        "the reviewed renderer script was not observed before the cold-start deadline",
                        requiresReconnect: true);
                    return;
                }
            }

            var sourceResult = await _connection.SendAsync(
                "Debugger.getScriptSource",
                new { scriptId = observed.ScriptId },
                CommandTimeout,
                cancellationToken);
            var source = sourceResult.TryGetProperty("scriptSource", out var sourceValue)
                ? sourceValue.GetString()
                : null;
            if (source == null || StrictUtf8.GetByteCount(source) > MaximumRendererSourceBytes)
            {
                RejectPreflight("the renderer source was missing or oversized");
                return;
            }

            var fingerprint = SourceFingerprint(source);
            RendererPatchProfile? profile = null;
            RendererPatchResult? patch = null;
            var alreadyPatched = false;
            if (_reviewedProfiles.TryGetValue(observed.Url, out var reviewedProfile))
            {
                if (RendererSourceMatchesProfile(
                        reviewedProfile,
                        source,
                        fingerprint,
                        out var reviewedPatch,
                        out alreadyPatched))
                {
                    profile = reviewedProfile;
                    patch = reviewedPatch;
                }
                else
                {
                    RejectPreflight(
                        $"sha256={fingerprint}; exact_profile={reviewedProfile.Name}; " +
                        $"contract={reviewedPatch.Status}; detail={reviewedPatch.Detail}; " +
                        "structural fallback is disabled for a known bundle URL");
                    return;
                }
            }

            if (profile == null &&
                TryGetCachedCompatibleRendererProfile(
                    observed.Url,
                    fingerprint,
                    out var cachedProfile) &&
                RendererSourceMatchesProfile(
                    cachedProfile,
                    source,
                    fingerprint,
                    out var cachedPatch,
                    out alreadyPatched))
            {
                profile = cachedProfile;
                patch = cachedPatch;
            }

            if (profile == null)
            {
                if (!TryCreateCompatibleRendererProfile(
                        observed.Url,
                        source,
                        out var compatibleProfile,
                        out var compatibilityDetail))
                {
                    RejectPreflight(
                        $"sha256={fingerprint}; no_exact_profile; " +
                        $"compatibility={compatibilityDetail}");
                    return;
                }
                CacheCompatibleRendererProfile(compatibleProfile);
                if (!RendererSourceMatchesProfile(
                        compatibleProfile,
                        source,
                        fingerprint,
                        out var compatiblePatch,
                        out alreadyPatched))
                {
                    RejectPreflight(
                        $"sha256={fingerprint}; generated compatibility profile was not stable; " +
                        $"contract={compatiblePatch.Status}; detail={compatiblePatch.Detail}");
                    return;
                }
                profile = compatibleProfile;
                patch = compatiblePatch;
                Log(
                    "renderer_compatibility_profile_verified",
                    $"target={_targetId}; profile={profile.Name}; url={observed.Url}; " +
                    $"sha256={profile.SourceSha256}; patched_sha256={profile.PatchedSha256}");
            }

            if (patch == null)
            {
                RejectPreflight("renderer preflight did not resolve a verified patch contract");
                return;
            }

            _selectedProfile = profile;
            _preflightAllowed = true;
            _preflightFinished = true;
            lock (_stateSync)
            {
                _readinessState.MarkPreflightVerified(alreadyPatched);
                PublishReadiness();
            }
            Log(
                alreadyPatched ? "renderer_preflight_already_patched" : "renderer_preflight_verified",
                $"target={_targetId}; profile={profile.Name}; url={observed.Url}; sha256={fingerprint}");
        }

        private void RejectPreflight(string detail, bool requiresReconnect = false)
        {
            _preflightFinished = true;
            _preflightAllowed = false;
            lock (_stateSync)
            {
                _readinessState.Fault();
                _requiresReconnect = requiresReconnect;
                PublishReadiness();
            }
            Log("renderer_version_rejected", $"target={_targetId}; {detail}");
        }

        internal static async Task<RendererWorker> ConnectAsync(
            CdpTarget target,
            int port,
            Action notifyRendererPending,
            Action notifyRendererReady,
            CancellationToken cancellationToken)
        {
            _ = ValidateWebSocketUrl(target.WebSocketUrl.AbsoluteUri, port, "page", target.Id);
            var reviewedProfiles = ReviewedRendererProfilesForPage(target.PageUrl);
            if (reviewedProfiles.Count == 0)
            {
                throw new InvalidOperationException(
                    "The Codex renderer page does not map to a reviewed bundle profile.");
            }
            var connection = await CdpConnection.ConnectAsync(target.WebSocketUrl, cancellationToken);
            var worker = new RendererWorker(
                connection,
                target,
                reviewedProfiles,
                notifyRendererPending,
                notifyRendererReady);
            try
            {
                await connection.SendAsync(
                    "Page.enable",
                    new { },
                    CommandTimeout,
                    cancellationToken);
                await connection.SendAsync(
                    "Page.setLifecycleEventsEnabled",
                    new { enabled = true },
                    CommandTimeout,
                    cancellationToken);
                var frameTree = await connection.SendAsync(
                    "Page.getFrameTree",
                    new { },
                    CommandTimeout,
                    cancellationToken);
                worker.ArmFromFrameTree(frameTree);
                await connection.SendAsync(
                    "Runtime.enable",
                    new { },
                    CommandTimeout,
                    cancellationToken);
                await connection.SendAsync(
                    "Debugger.enable",
                    new { maxScriptsCacheSize = MaximumRendererSourceBytes * 2L },
                    CommandTimeout,
                    cancellationToken);
                await worker.CompletePreflightAsync(cancellationToken);
                if (!worker._preflightAllowed)
                {
                    return worker;
                }
                var selectedProfile = worker._selectedProfile ??
                                      throw new InvalidOperationException(
                                          "Renderer preflight did not select a reviewed profile.");
                await connection.SendAsync(
                    "Fetch.enable",
                    new
                    {
                        patterns = new[]
                        {
                            new
                            {
                                urlPattern = selectedProfile.BundleUrl,
                                resourceType = "Script",
                                requestStage = "Response"
                            }
                        },
                        handleAuthRequests = false
                    },
                    CommandTimeout,
                    cancellationToken);
                return worker;
            }
            catch
            {
                await worker.DisposeAsync();
                throw;
            }
        }

        private void OnEventReceived(string method, JsonElement parameters)
        {
            if (_disposed)
            {
                return;
            }

            if (method.Equals("Fetch.requestPaused", StringComparison.Ordinal))
            {
                _ = HandleFetchRequestPausedAsync(parameters);
                return;
            }
            if (method.Equals("Page.frameStartedLoading", StringComparison.Ordinal))
            {
                var frameId = parameters.TryGetProperty("frameId", out var frameValue)
                    ? frameValue.GetString()
                    : null;
                lock (_stateSync)
                {
                    if (!_disposed && _readinessState.BeginFrameLoading(frameId))
                    {
                        PublishReadiness();
                    }
                }
                return;
            }
            if (method.Equals("Page.frameNavigated", StringComparison.Ordinal))
            {
                HandleFrameNavigated(parameters);
                return;
            }
            if (method.Equals("Page.lifecycleEvent", StringComparison.Ordinal))
            {
                HandleLifecycleEvent(parameters);
                return;
            }
            if (method.Equals("Runtime.executionContextsCleared", StringComparison.Ordinal))
            {
                _executionContextEpochs.Clear();
                lock (_stateSync)
                {
                    if (!_disposed)
                    {
                        _readinessState.ExecutionContextsCleared();
                        PublishReadiness();
                    }
                }
                return;
            }
            if (method.Equals("Runtime.executionContextCreated", StringComparison.Ordinal))
            {
                HandleExecutionContextCreated(parameters);
                return;
            }
            if (!method.Equals("Debugger.scriptParsed", StringComparison.Ordinal))
            {
                return;
            }

            var scriptId = parameters.TryGetProperty("scriptId", out var idValue)
                ? idValue.GetString()
                : null;
            var url = parameters.TryGetProperty("url", out var urlValue)
                ? urlValue.GetString()
                : null;
            var executionContextId = parameters.TryGetProperty(
                    "executionContextId",
                    out var contextValue) &&
                contextValue.TryGetInt64(out var parsedContextId)
                    ? parsedContextId
                    : 0;
            if (string.IsNullOrWhiteSpace(scriptId) ||
                url == null ||
                !IsRendererBundleUrlForPage(_pageUrl, url) ||
                !_seenScripts.TryAdd(scriptId, 0))
            {
                return;
            }
            if (executionContextId <= 0 ||
                !_executionContextEpochs.TryGetValue(executionContextId, out var contextEpoch))
            {
                return;
            }
            lock (_stateSync)
            {
                if (_disposed || !_readinessState.IsCurrentEpoch(contextEpoch))
                {
                    return;
                }
            }

            if (!_preflightFinished)
            {
                _preflightScript.TrySetResult(new ObservedRendererScript(scriptId, url!));
                return;
            }
            if (!_preflightAllowed ||
                !string.Equals(
                    url,
                    _selectedProfile?.BundleUrl,
                    StringComparison.Ordinal) ||
                !_fulfilledResponses.TryGetValue(url!, out var fulfilled) ||
                fulfilled.Epoch != contextEpoch)
            {
                return;
            }

            _ = VerifyFulfilledScriptAsync(scriptId, url!, fulfilled);
        }

        private void HandleFrameNavigated(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("frame", out var frame))
            {
                return;
            }
            var frameId = frame.TryGetProperty("id", out var frameIdValue)
                ? frameIdValue.GetString()
                : null;
            var loaderId = frame.TryGetProperty("loaderId", out var loaderIdValue)
                ? loaderIdValue.GetString()
                : null;
            var pageUrl = frame.TryGetProperty("url", out var pageUrlValue)
                ? pageUrlValue.GetString()
                : null;
            lock (_stateSync)
            {
                if (!_disposed && _readinessState.FrameNavigated(
                        frameId,
                        loaderId,
                        pageUrl,
                        string.Equals(pageUrl, _pageUrl, StringComparison.Ordinal) &&
                        IsReviewedOfficialCodexPageUrl(pageUrl)))
                {
                    PublishReadiness();
                }
            }
        }

        private void HandleLifecycleEvent(JsonElement parameters)
        {
            var frameId = parameters.TryGetProperty("frameId", out var frameValue)
                ? frameValue.GetString()
                : null;
            var loaderId = parameters.TryGetProperty("loaderId", out var loaderValue)
                ? loaderValue.GetString()
                : null;
            var name = parameters.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString()
                : null;
            lock (_stateSync)
            {
                if (!_disposed && _readinessState.LifecycleEvent(frameId, loaderId, name))
                {
                    PublishReadiness();
                }
            }
        }

        private void HandleExecutionContextCreated(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("context", out var context) ||
                !context.TryGetProperty("id", out var idValue) ||
                !idValue.TryGetInt64(out var contextId) ||
                contextId <= 0 ||
                !context.TryGetProperty("auxData", out var auxiliary) ||
                auxiliary.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            var frameId = auxiliary.TryGetProperty("frameId", out var frameValue)
                ? frameValue.GetString()
                : null;
            var isDefault = !auxiliary.TryGetProperty("isDefault", out var defaultValue) ||
                            defaultValue.ValueKind == JsonValueKind.True;
            if (!isDefault)
            {
                return;
            }
            lock (_stateSync)
            {
                if (!_disposed && _readinessState.TryGetCurrentEpoch(frameId, out var epoch))
                {
                    _executionContextEpochs[contextId] = epoch;
                }
            }
        }

        private async Task HandleFetchRequestPausedAsync(JsonElement parameters)
        {
            var requestId = parameters.TryGetProperty("requestId", out var requestIdValue)
                ? requestIdValue.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(requestId) ||
                !_seenFetchRequests.TryAdd(requestId, 0) ||
                !_pausedFetchRequests.TryAdd(requestId, PausedFetchResolutionState.Pending))
            {
                return;
            }

            RendererNavigationSnapshot snapshot = default;
            string? requestUrl = null;
            await _responsePatchLock.WaitAsync();
            try
            {
                if (_disposed || _connection.IsClosed || !_preflightAllowed)
                {
                    throw new InvalidOperationException("Renderer response patching is not armed.");
                }
                var profile = _selectedProfile ??
                              throw new InvalidOperationException(
                                  "Renderer response patching has no selected profile.");
                if (!parameters.TryGetProperty("request", out var request) ||
                    request.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("Codex returned an invalid paused renderer request.");
                }
                requestUrl = request.TryGetProperty("url", out var requestUrlValue)
                    ? requestUrlValue.GetString()
                    : null;
                var method = request.TryGetProperty("method", out var methodValue)
                    ? methodValue.GetString()
                    : null;
                var resourceType = parameters.TryGetProperty("resourceType", out var resourceValue)
                    ? resourceValue.GetString()
                    : null;
                var frameId = parameters.TryGetProperty("frameId", out var frameValue)
                    ? frameValue.GetString()
                    : null;
                var responseStatusCode = 0;
                var hasResponseStatus = parameters.TryGetProperty(
                        "responseStatusCode",
                        out var statusValue) &&
                    statusValue.TryGetInt32(out responseStatusCode);
                var hasResponseError = parameters.TryGetProperty(
                        "responseErrorReason",
                        out var responseErrorValue) &&
                    responseErrorValue.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(responseErrorValue.GetString());
                if (!string.Equals(requestUrl, profile.BundleUrl, StringComparison.Ordinal) ||
                    !string.Equals(method, "GET", StringComparison.Ordinal) ||
                    !string.Equals(resourceType, "Script", StringComparison.Ordinal) ||
                    !hasResponseStatus || responseStatusCode != 200 ||
                    hasResponseError ||
                    parameters.TryGetProperty("redirectedRequestId", out _))
                {
                    throw new InvalidOperationException("Codex returned an unreviewed renderer response.");
                }

                lock (_stateSync)
                {
                    if (_disposed ||
                        !_readinessState.TryBeginFetch(frameId, _pageUrl, out snapshot))
                    {
                        throw new InvalidOperationException(
                            "The renderer response did not belong to the current main-frame loader.");
                    }
                    PublishReadiness();
                }

                var bodyResult = await _connection.SendAsync(
                    "Fetch.getResponseBody",
                    new { requestId },
                    CommandTimeout,
                    CancellationToken.None);
                var body = ReadRendererResponseBody(bodyResult);
                var originalFingerprint = SourceFingerprint(body.Source);
                if (!originalFingerprint.Equals(profile.SourceSha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Codex renderer response did not match the reviewed source fingerprint.");
                }
                var patch = PatchRendererSource(profile, body.Source, out var patchedSource);
                if (patch.Status != RendererPatchStatus.Patched)
                {
                    throw new InvalidOperationException(
                        "Codex renderer response failed the reviewed patch contract: " + patch.Detail);
                }
                var patchedBytes = StrictUtf8.GetBytes(patchedSource);
                if (patchedBytes.Length == 0 || patchedBytes.Length > MaximumRendererSourceBytes)
                {
                    throw new IOException("Patched renderer response exceeded the bridge safety limit.");
                }
                var patchedFingerprint = SourceFingerprint(patchedSource);
                var responseHeaders = BuildFulfilledResponseHeaders(parameters, patchedBytes.Length);
                lock (_stateSync)
                {
                    if (_disposed || !_readinessState.IsCurrent(snapshot))
                    {
                        throw new InvalidOperationException(
                            "The renderer navigated before its response could be fulfilled.");
                    }
                }

                var fulfilled = new FulfilledRendererResponse(
                    requestId,
                    snapshot.Epoch,
                    snapshot.LoaderId,
                    patchedFingerprint);
                if (!_pausedFetchRequests.TryUpdate(
                        requestId,
                        PausedFetchResolutionState.Fulfilling,
                        PausedFetchResolutionState.Pending))
                {
                    throw new InvalidOperationException(
                        "The paused renderer response was already claimed by another resolver.");
                }
                _fulfilledResponses[profile.BundleUrl] = fulfilled;
                try
                {
                    await _connection.SendAsync(
                        "Fetch.fulfillRequest",
                        new
                        {
                            requestId,
                            responseCode = responseStatusCode,
                            responseHeaders,
                            body = Convert.ToBase64String(patchedBytes)
                        },
                        CommandTimeout,
                        CancellationToken.None);
                }
                catch
                {
                    FailConnectionForUncertainResponse();
                    throw;
                }
                _pausedFetchRequests.TryRemove(requestId, out _);
                lock (_stateSync)
                {
                    if (!_disposed)
                    {
                        _readinessState.MarkFulfilled(snapshot);
                        PublishReadiness();
                    }
                }
                Log(
                    "renderer_response_fulfilled",
                    $"target={_targetId}; profile={profile.Name}; loader={snapshot.LoaderId}; " +
                    $"sha256={originalFingerprint}; patched_sha256={patchedFingerprint}");
            }
            catch (Exception ex)
            {
                if (requestUrl != null &&
                    _fulfilledResponses.TryGetValue(requestUrl, out var current) &&
                    current.RequestId.Equals(requestId, StringComparison.Ordinal))
                {
                    _fulfilledResponses.TryRemove(requestUrl, out _);
                }
                lock (_stateSync)
                {
                    if (!_disposed)
                    {
                        _readinessState.MarkResponseFailed(snapshot);
                        PublishReadiness();
                    }
                }
                Log(
                    "renderer_response_rejected",
                    $"target={_targetId}; request={requestId}; error={ex.Message}");
            }
            finally
            {
                await ContinuePausedResponseAsync(requestId);
                _responsePatchLock.Release();
            }
        }

        private async Task ContinuePausedResponseAsync(
            string requestId,
            TimeSpan? commandTimeout = null)
        {
            if (!_pausedFetchRequests.TryUpdate(
                    requestId,
                    PausedFetchResolutionState.Continuing,
                    PausedFetchResolutionState.Pending))
            {
                return;
            }
            try
            {
                if (!_connection.IsClosed)
                {
                    await _connection.SendAsync(
                        "Fetch.continueResponse",
                        new { requestId },
                        commandTimeout ?? CommandTimeout,
                        CancellationToken.None);
                }
                _pausedFetchRequests.TryRemove(requestId, out _);
            }
            catch (Exception ex)
            {
                FailConnectionForUncertainResponse();
                Log(
                    "renderer_response_continue_failed",
                    $"target={_targetId}; request={requestId}; error={ex.Message}");
            }
        }

        private void FailConnectionForUncertainResponse()
        {
            lock (_stateSync)
            {
                if (!_disposed)
                {
                    _readinessState.Fault();
                    _requiresReconnect = true;
                    PublishReadiness();
                }
            }
            _connection.Abort();
        }

        private async Task VerifyFulfilledScriptAsync(
            string scriptId,
            string url,
            FulfilledRendererResponse fulfilled)
        {
            try
            {
                var sourceResult = await _connection.SendAsync(
                    "Debugger.getScriptSource",
                    new { scriptId },
                    CommandTimeout,
                    CancellationToken.None);
                var source = sourceResult.TryGetProperty("scriptSource", out var sourceValue)
                    ? sourceValue.GetString()
                    : null;
                if (source == null || StrictUtf8.GetByteCount(source) > MaximumRendererSourceBytes)
                {
                    throw new InvalidOperationException("Codex did not return a bounded patched renderer source.");
                }
                var fingerprint = SourceFingerprint(source);
                var profile = _selectedProfile ??
                              throw new InvalidOperationException(
                                  "Renderer read-back verification has no selected profile.");
                if (!fingerprint.Equals(fulfilled.PatchedSha256, StringComparison.Ordinal) ||
                    PatchRendererSource(profile, source, out _).Status !=
                    RendererPatchStatus.AlreadyPatched)
                {
                    throw new InvalidOperationException(
                        "The fulfilled renderer did not pass source read-back verification.");
                }

                lock (_stateSync)
                {
                    if (_disposed ||
                        !_readinessState.MarkScriptVerified(
                            fulfilled.Epoch,
                            fulfilled.LoaderId))
                    {
                        return;
                    }
                    PublishReadiness();
                }
                Log(
                    "renderer_patched",
                    $"target={_targetId}; url={url}; loader={fulfilled.LoaderId}; sha256={fingerprint}");
            }
            catch (Exception ex)
            {
                lock (_stateSync)
                {
                    if (!_disposed)
                    {
                        _readinessState.MarkVerificationFailed(
                            fulfilled.Epoch,
                            fulfilled.LoaderId);
                        PublishReadiness();
                    }
                }
                Log(
                    "renderer_patch_verification_failed",
                    $"target={_targetId}; script={scriptId}; error={ex.Message}");
            }
        }

        private void OnConnectionClosed()
        {
            lock (_stateSync)
            {
                if (_disposed)
                {
                    return;
                }
                _readinessState.Fault();
                _requiresReconnect = true;
                PublishReadiness();
            }
        }

        private void PublishReadiness()
        {
            if (_readinessState.IsReady)
            {
                _notifyRendererReady();
            }
            else
            {
                _notifyRendererPending();
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_stateSync)
            {
                if (_disposed)
                {
                    return;
                }
                _readinessState.Fault();
                PublishReadiness();
                _disposed = true;
            }
            _connection.EventReceived -= OnEventReceived;
            _connection.Closed -= OnConnectionClosed;
            var continuationTasks = _pausedFetchRequests.Keys
                .Select(requestId => ContinuePausedResponseAsync(requestId, ShutdownCommandTimeout))
                .ToArray();
            if (continuationTasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(continuationTasks)
                        .WaitAsync(ShutdownCommandTimeout + TimeSpan.FromSeconds(1));
                }
                catch
                {
                    _connection.Abort();
                }
            }
            await _connection.DisposeAsync();
        }

        private enum PausedFetchResolutionState
        {
            Pending,
            Fulfilling,
            Continuing
        }
    }

    private sealed class ControlledReloadGate
    {
        private int _consumed;

        internal bool TryConsume()
        {
            return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
        }
    }

    private readonly record struct RendererFrameSnapshot(
        long Epoch,
        string FrameId,
        string LoaderId,
        string PageUrl);

    private readonly record struct RendererNavigationSnapshot(
        long Epoch,
        string FrameId,
        string LoaderId,
        string PageUrl);

    private sealed record RendererResponseBody(string Source, byte[] Bytes);

    // Keep the lower-case positional member names: System.Text.Json must emit CDP's exact
    // { "name": ..., "value": ... } response-header shape without a custom serializer.
    private sealed record CdpResponseHeader(string name, string value);

    private sealed record ObservedRendererScript(string ScriptId, string Url);

    private sealed record FulfilledRendererResponse(
        string RequestId,
        long Epoch,
        string LoaderId,
        string PatchedSha256);

    private sealed class RendererWorkerReadinessState
    {
        private bool _activated;
        private bool _armed;
        private bool _preflightVerified;
        private bool _faulted;
        private bool _navigationPending;
        private bool _fetchPending;
        private long _epoch;
        private long _fulfilledEpoch;
        private long _scriptVerifiedEpoch;
        private long _loadedEpoch;
        private string? _mainFrameId;
        private string? _loaderId;
        private string? _pageUrl;

        internal bool IsReady =>
            _activated &&
            _armed &&
            _preflightVerified &&
            !_faulted &&
            !_navigationPending &&
            !_fetchPending &&
            _epoch > 0 &&
            _fulfilledEpoch == _epoch &&
            _scriptVerifiedEpoch == _epoch &&
            _loadedEpoch == _epoch;

        internal void Activate()
        {
            _activated = true;
        }

        internal void Arm(string frameId, string loaderId, string pageUrl)
        {
            if (_armed)
            {
                throw new InvalidOperationException("The renderer readiness state is already armed.");
            }
            if (string.IsNullOrWhiteSpace(frameId) ||
                string.IsNullOrWhiteSpace(loaderId) ||
                !IsReviewedOfficialCodexPageUrl(pageUrl))
            {
                throw new ArgumentException("A reviewed renderer frame is required before arming readiness.");
            }

            _armed = true;
            _epoch = 1;
            _mainFrameId = frameId;
            _loaderId = loaderId;
            _pageUrl = pageUrl;
            ResetDocumentEvidence();
        }

        internal void MarkPreflightVerified(bool alreadyPatched)
        {
            // An already-patched preflight is useful version evidence only. It must never stand
            // in for a Fetch fulfillment, same-loader source verification, or lifecycle load
            // observed by this worker generation.
            _ = alreadyPatched;
            if (_armed && !_faulted)
            {
                _preflightVerified = true;
            }
        }

        internal bool TryBeginControlledReload(out RendererFrameSnapshot snapshot)
        {
            snapshot = default;
            if (!CanUseCurrentDocument() ||
                !_preflightVerified ||
                _fetchPending ||
                _mainFrameId == null ||
                _loaderId == null ||
                _pageUrl == null)
            {
                return false;
            }

            snapshot = new RendererFrameSnapshot(
                _epoch,
                _mainFrameId,
                _loaderId,
                _pageUrl);
            BeginNavigation();
            return true;
        }

        internal bool BeginFrameLoading(string? frameId)
        {
            if (!_armed ||
                _faulted ||
                string.IsNullOrWhiteSpace(frameId) ||
                !string.Equals(frameId, _mainFrameId, StringComparison.Ordinal))
            {
                return false;
            }
            if (_navigationPending)
            {
                return false;
            }

            BeginNavigation();
            return true;
        }

        internal bool FrameNavigated(
            string? frameId,
            string? loaderId,
            string? pageUrl,
            bool pageIsReviewed)
        {
            if (!_armed || _faulted)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(frameId))
            {
                Fault();
                return true;
            }
            if (!string.Equals(frameId, _mainFrameId, StringComparison.Ordinal))
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(loaderId) ||
                string.IsNullOrWhiteSpace(pageUrl) ||
                !pageIsReviewed ||
                !IsReviewedOfficialCodexPageUrl(pageUrl))
            {
                Fault();
                return true;
            }

            var documentChanged =
                _navigationPending ||
                !string.Equals(loaderId, _loaderId, StringComparison.Ordinal) ||
                !string.Equals(pageUrl, _pageUrl, StringComparison.Ordinal);
            if (!documentChanged)
            {
                return false;
            }
            if (!_navigationPending)
            {
                AdvanceEpoch();
            }

            _loaderId = loaderId;
            _pageUrl = pageUrl;
            _navigationPending = false;
            _fetchPending = false;
            ResetDocumentEvidence();
            return true;
        }

        internal bool LifecycleEvent(string? frameId, string? loaderId, string? name)
        {
            if (!CanUseCurrentDocument() ||
                !string.Equals(name, "load", StringComparison.Ordinal) ||
                !string.Equals(frameId, _mainFrameId, StringComparison.Ordinal) ||
                !string.Equals(loaderId, _loaderId, StringComparison.Ordinal) ||
                _loadedEpoch == _epoch)
            {
                return false;
            }

            _loadedEpoch = _epoch;
            return true;
        }

        internal void ExecutionContextsCleared()
        {
            if (!_armed || _faulted)
            {
                return;
            }

            // Chromium normally clears contexts during the frame-loading transition. If the
            // companion Page event was missed or arrives later, advance once here so contexts
            // from the previous document can never verify the current response.
            if (!_navigationPending)
            {
                AdvanceEpoch();
            }
            _fetchPending = false;
            ResetDocumentEvidence();
        }

        internal bool TryGetCurrentEpoch(string? frameId, out long epoch)
        {
            epoch = 0;
            if (!CanUseCurrentDocument() ||
                !string.Equals(frameId, _mainFrameId, StringComparison.Ordinal))
            {
                return false;
            }

            epoch = _epoch;
            return true;
        }

        internal bool IsCurrentEpoch(long epoch)
        {
            return CanUseCurrentDocument() && epoch == _epoch;
        }

        internal bool TryBeginFetch(
            string? frameId,
            string? pageUrl,
            out RendererNavigationSnapshot snapshot)
        {
            snapshot = default;
            if (!CanUseCurrentDocument() ||
                !_preflightVerified ||
                _fetchPending ||
                _mainFrameId == null ||
                _loaderId == null ||
                _pageUrl == null ||
                !string.Equals(frameId, _mainFrameId, StringComparison.Ordinal) ||
                !string.Equals(pageUrl, _pageUrl, StringComparison.Ordinal))
            {
                return false;
            }

            _fetchPending = true;
            ResetDocumentEvidence();
            snapshot = new RendererNavigationSnapshot(
                _epoch,
                _mainFrameId,
                _loaderId,
                _pageUrl);
            return true;
        }

        internal bool IsCurrent(RendererNavigationSnapshot snapshot)
        {
            return _fetchPending && SnapshotMatches(snapshot);
        }

        internal void MarkFulfilled(RendererNavigationSnapshot snapshot)
        {
            if (!_fetchPending || !SnapshotMatches(snapshot))
            {
                return;
            }

            _fetchPending = false;
            _fulfilledEpoch = _epoch;
        }

        internal void MarkResponseFailed(RendererNavigationSnapshot snapshot)
        {
            if (!SnapshotMatches(snapshot))
            {
                return;
            }

            _fetchPending = false;
            ResetDocumentEvidence();
        }

        internal bool MarkScriptVerified(long epoch, string? loaderId)
        {
            if (!CanUseCurrentDocument() ||
                epoch != _epoch ||
                !string.Equals(loaderId, _loaderId, StringComparison.Ordinal) ||
                _scriptVerifiedEpoch == _epoch)
            {
                return false;
            }

            _scriptVerifiedEpoch = _epoch;
            return true;
        }

        internal void MarkVerificationFailed(long epoch, string? loaderId)
        {
            if (CanUseCurrentDocument() &&
                epoch == _epoch &&
                string.Equals(loaderId, _loaderId, StringComparison.Ordinal))
            {
                _scriptVerifiedEpoch = 0;
            }
        }

        internal void Fault()
        {
            _faulted = true;
            _navigationPending = false;
            _fetchPending = false;
            ResetDocumentEvidence();
        }

        private bool CanUseCurrentDocument()
        {
            return _armed &&
                   !_faulted &&
                   !_navigationPending &&
                   _epoch > 0 &&
                   _mainFrameId != null &&
                   _loaderId != null &&
                   _pageUrl != null;
        }

        private bool SnapshotMatches(RendererNavigationSnapshot snapshot)
        {
            return CanUseCurrentDocument() &&
                   snapshot.Epoch == _epoch &&
                   string.Equals(snapshot.FrameId, _mainFrameId, StringComparison.Ordinal) &&
                   string.Equals(snapshot.LoaderId, _loaderId, StringComparison.Ordinal) &&
                   string.Equals(snapshot.PageUrl, _pageUrl, StringComparison.Ordinal);
        }

        private void BeginNavigation()
        {
            AdvanceEpoch();
            _navigationPending = true;
            _fetchPending = false;
            ResetDocumentEvidence();
        }

        private void AdvanceEpoch()
        {
            _epoch = checked(_epoch + 1);
        }

        private void ResetDocumentEvidence()
        {
            _fulfilledEpoch = 0;
            _scriptVerifiedEpoch = 0;
            _loadedEpoch = 0;
        }
    }

    private sealed class CdpConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly Task _receiveLoop;
        private int _nextId;
        private int _disposed;
        private volatile bool _receiveLoopStopped;
        private int _closedPublished;

        private CdpConnection(ClientWebSocket socket)
        {
            _socket = socket;
            _receiveLoop = Task.Run(ReceiveLoopAsync);
        }

        internal event Action<string, JsonElement>? EventReceived;
        internal event Action? Closed;

        internal bool IsClosed =>
            Volatile.Read(ref _disposed) != 0 ||
            _lifetime.IsCancellationRequested ||
            _receiveLoopStopped ||
            _socket.State != WebSocketState.Open;

        internal static async Task<CdpConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(uri, timeout.Token);
                return new CdpConnection(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        internal void Abort()
        {
            try
            {
                _lifetime.Cancel();
                _socket.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        internal async Task<JsonElement> SendAsync(
            string method,
            object? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (IsClosed)
            {
                throw new IOException("CDP connection is closed.");
            }
            using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            commandTimeout.CancelAfter(timeout);
            var commandToken = commandTimeout.Token;
            var id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion))
            {
                throw new InvalidOperationException("CDP request identity collision.");
            }

            try
            {
                if (IsClosed)
                {
                    throw new IOException("CDP connection closed before the command was sent.");
                }
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    id,
                    method,
                    @params = parameters ?? new { }
                });
                await _sendLock.WaitAsync(commandToken);
                try
                {
                    await _socket.SendAsync(
                        payload,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        commandToken);
                }
                finally
                {
                    _sendLock.Release();
                }

                return await completion.Task.WaitAsync(commandToken);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (!_lifetime.IsCancellationRequested &&
                       _socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, _lifetime.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }
                        if (result.MessageType != WebSocketMessageType.Text)
                        {
                            throw new IOException("CDP returned a non-text WebSocket message.");
                        }
                        message.Write(buffer, 0, result.Count);
                        if (message.Length > MaximumRendererSourceBytes * 3L)
                        {
                            throw new IOException("CDP message exceeded the bridge safety limit.");
                        }
                    } while (!result.EndOfMessage);

                    using var document = JsonDocument.Parse(message.ToArray());
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var idValue) && idValue.TryGetInt32(out var id))
                    {
                        if (!_pending.TryGetValue(id, out var completion))
                        {
                            continue;
                        }
                        if (root.TryGetProperty("error", out var error))
                        {
                            var messageText = error.TryGetProperty("message", out var errorMessage)
                                ? errorMessage.GetString()
                                : "Unknown CDP error";
                            completion.TrySetException(new InvalidOperationException(messageText));
                        }
                        else
                        {
                            var response = root.TryGetProperty("result", out var responseValue)
                                ? responseValue.Clone()
                                : JsonDocument.Parse("{}").RootElement.Clone();
                            completion.TrySetResult(response);
                        }
                        continue;
                    }

                    var method = root.TryGetProperty("method", out var methodValue)
                        ? methodValue.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(method))
                    {
                        continue;
                    }
                    var parameters = root.TryGetProperty("params", out var paramsValue)
                        ? paramsValue.Clone()
                        : JsonDocument.Parse("{}").RootElement.Clone();
                    EventReceived?.Invoke(method, parameters);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                foreach (var completion in _pending.Values)
                {
                    completion.TrySetException(ex);
                }
            }
            finally
            {
                _receiveLoopStopped = true;
                try
                {
                    _lifetime.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                foreach (var completion in _pending.Values)
                {
                    completion.TrySetException(new IOException("CDP connection closed."));
                }
                PublishClosed();
            }
        }

        private void PublishClosed()
        {
            if (Interlocked.Exchange(ref _closedPublished, 1) != 0)
            {
                return;
            }
            var handlers = Closed;
            if (handlers == null)
            {
                return;
            }
            foreach (Action handler in handlers.GetInvocationList())
            {
                try
                {
                    handler();
                }
                catch
                {
                    // A readiness observer must not fault the CDP receive loop.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _lifetime.Cancel();
            var sendLockHeld = false;
            try
            {
                using var sendQuiesceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _sendLock.WaitAsync(sendQuiesceTimeout.Token);
                sendLockHeld = true;
            }
            catch
            {
                _socket.Abort();
            }
            var receiveLoopCompleted = false;
            try
            {
                await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(3));
                receiveLoopCompleted = true;
            }
            catch
            {
                _socket.Abort();
                try
                {
                    await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2));
                    receiveLoopCompleted = true;
                }
                catch
                {
                }
            }
            if (sendLockHeld &&
                _socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "bridge stopping",
                        closeTimeout.Token);
                }
                catch
                {
                    _socket.Abort();
                }
            }
            if (!sendLockHeld)
            {
                try
                {
                    using var postAbortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _sendLock.WaitAsync(postAbortTimeout.Token);
                    sendLockHeld = true;
                }
                catch
                {
                    // Do not dispose a semaphore that an interrupted sender may still release.
                }
            }
            _socket.Dispose();
            if (!receiveLoopCompleted)
            {
                try
                {
                    await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2));
                    receiveLoopCompleted = true;
                }
                catch
                {
                }
            }
            if (sendLockHeld)
            {
                _sendLock.Release();
                _sendLock.Dispose();
            }
            if (receiveLoopCompleted)
            {
                _lifetime.Dispose();
            }
        }
    }

    private sealed record CdpBrowserVersion(string BrowserId, Uri WebSocketUrl);
    private sealed record CdpTarget(string Id, string PageUrl, Uri WebSocketUrl);
}

internal enum RendererPatchStatus
{
    Patched,
    AlreadyPatched,
    Rejected
}

internal sealed record RendererPatchResult(RendererPatchStatus Status, string Detail)
{
    internal static RendererPatchResult Patched() => new(RendererPatchStatus.Patched, "patched");
    internal static RendererPatchResult AlreadyPatched() =>
        new(RendererPatchStatus.AlreadyPatched, "already patched");
    internal static RendererPatchResult Rejected(string detail) =>
        new(RendererPatchStatus.Rejected, detail);
}

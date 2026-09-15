using System.Text.Json;
using ChopItUp.Desktop;
using ChopItUp.Desktop.Hub;

namespace ChopItUp.Desktop.Bridge;

/// <summary>Row 12 T5: the page &lt;-&gt; host wire protocol, as a pure function from JSON to JSON. Kept
/// free of WebView2 and the window (<c>MainWindow</c>'s message handler does nothing but trust-check
/// with <see cref="IsTrusted"/>, call <see cref="Handle"/>, and post the reply / push
/// <see cref="StateEvent"/>), so the whole grammar is tested without one.
///
/// Wire grammar (Curio's): request <c>{id?, cmd, nonce?}</c>; reply
/// <c>{id, ok:true, result}</c> or <c>{id, ok:false, error}</c>; event
/// <c>{event:"state", maximized, hub:{state, port, reason}}</c>.</summary>
public static class HostBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class Request
    {
        public string? Id { get; set; }
        public string? Cmd { get; set; }
        public string? Nonce { get; set; }
    }

    /// <summary>Dispatches one page message onto <paramref name="host"/> and returns the JSON reply.
    /// Never throws: malformed JSON and an unknown command both come back as an <c>ok:false</c> reply
    /// rather than an exception reaching WebView2's event handler.</summary>
    public static string Handle(string json, IHostActions host)
    {
        Request? request;
        try
        {
            request = JsonSerializer.Deserialize<Request>(json, JsonOptions);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || string.IsNullOrEmpty(request.Cmd))
            return Reply(request?.Id, ok: false, error: "malformed");

        object? result;
        switch (request.Cmd)
        {
            case "minimize": host.Minimize(); result = null; break;
            case "maximize": host.Maximize(); result = null; break;
            case "restore": host.Restore(); result = null; break;
            case "toggleMaximize":
                if (host.IsMaximized) host.Restore(); else host.Maximize();
                result = null;
                break;
            case "close": host.Close(); result = null; break;
            case "quit": host.Quit(); result = null; break;
            case "getState": result = StatePayload(host); break;
            default: return Reply(request.Id, ok: false, error: $"unknown command '{request.Cmd}'");
        }

        return Reply(request.Id, ok: true, result: result);
    }

    /// <summary>Pushed to the page whenever <see cref="IHostActions.Hub"/> or the window's maximized
    /// flag changes; <c>getState</c>'s reply carries the same <c>maximized</c>/<c>hub</c> shape.</summary>
    public static string StateEvent(IHostActions host)
    {
        var payload = new Dictionary<string, object?> { ["event"] = "state" };
        foreach (var (key, value) in StatePayload(host)) payload[key] = value;
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>B8: the source's authority alone decides trust for the hub origin, no nonce needed. Any
    /// other source is trusted only when it is one of the two shapes a <c>NavigateToString</c> boot page
    /// is known to report (<see cref="NavigationPolicy.IsBootPageUri"/>: the documented <c>about:blank</c>,
    /// or the <c>data:text/html;charset=utf-8;base64,...</c> URI this WebView2 runtime (152.0.4191.66)
    /// actually raises — without this, a boot-page Close/Quit is refused as untrusted on that runtime)
    /// carrying the current launch's nonce — the boot page is the one widening past authority-only trust,
    /// and the nonce bounds it (pass 1, finding 12; boot-page trust fix, review pass).</summary>
    public static bool IsTrusted(string? source, Uri hubOrigin, string? messageNonce, string launchNonce)
    {
        if (source is null) return false;

        if (Uri.TryCreate(source, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            && string.Equals(
                parsed.GetLeftPart(UriPartial.Authority),
                hubOrigin.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return NavigationPolicy.IsBootPageUri(source)
            && messageNonce is not null
            && string.Equals(messageNonce, launchNonce, StringComparison.Ordinal);
    }

    /// <summary>Pulls the <c>nonce</c> field out of a raw page message so the caller can trust-check
    /// before <see cref="Handle"/> ever parses the command. Null on malformed JSON or a missing/non-
    /// string field — never throws.</summary>
    public static string? ExtractNonce(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("nonce", out var nonce) && nonce.ValueKind == JsonValueKind.String
                ? nonce.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> StatePayload(IHostActions host) => new()
    {
        ["maximized"] = host.IsMaximized,
        ["hub"] = new Dictionary<string, object?>
        {
            ["state"] = HubStateName(host.Hub.State),
            ["port"] = host.Hub.Port,
            ["reason"] = host.Hub.Reason,
        },
    };

    private static string HubStateName(HubState state) => state switch
    {
        HubState.Starting => "starting",
        HubState.Ready => "ready",
        HubState.Attached => "attached",
        HubState.Failed => "failed",
        HubState.Stopped => "stopped",
        _ => state.ToString().ToLowerInvariant(),
    };

    private static string Reply(string? id, bool ok, object? result = null, string? error = null)
    {
        var obj = new Dictionary<string, object?> { ["id"] = id, ["ok"] = ok };
        if (ok) obj["result"] = result;
        else obj["error"] = error;
        return JsonSerializer.Serialize(obj, JsonOptions);
    }
}

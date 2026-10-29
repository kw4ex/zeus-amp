// SPDX-License-Identifier: GPL-2.0-or-later
//
// The amplifier presses (OPERATE/STANDBY, ATU tune, antenna, DUMMY OK,
// Standby-on-Tune, safe TUNE) are done by the Elecraft KPA1500 Zeus feature.
// Features cannot call each other directly, so this talks to that feature's
// own HTTP API on Zeus's web server (/api/plugins/<id>/...), the same calls
// its panel makes. The KPA1500 feature keeps all of its safety checks.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeus.Community.FlexKnob;

/// <summary>What the knob needs to know about the amplifier.</summary>
public sealed record AmpStatus(
    bool Connected,      // KPA1500 feature is talking to the amp
    string Power,        // on | off | unknown
    bool? Operate,
    int? Antenna,
    string NoAntState,   // off | nodata | settling | ok | low | tx | dummy | nocheck
    bool Tx,
    string TunePhase);   // idle | armed | active | restoring

public interface IAmpLink
{
    /// <summary>null when the KPA1500 feature cannot be reached.</summary>
    Task<AmpStatus?> StatusAsync(CancellationToken ct);
    Task<(bool Ok, string Message)> PostAsync(string path, object? body, CancellationToken ct);
    Task<(bool Ok, string Message)> ToggleStandbyOnTuneAsync(CancellationToken ct);
    string LastError { get; }
}

public sealed class AmpLink : IAmpLink, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Func<string?> _baseUrl;
    private readonly Func<string> _pluginId;

    public AmpLink(Func<string?> baseUrl, Func<string> pluginId)
    {
        _baseUrl = baseUrl;
        _pluginId = pluginId;
    }

    public string LastError { get; private set; } = "";

    private string? Url(string path)
    {
        var b = _baseUrl();
        if (string.IsNullOrEmpty(b)) { LastError = "Zeus's web address is not known yet"; return null; }
        return $"{b.TrimEnd('/')}/api/plugins/{_pluginId()}/{path.TrimStart('/')}";
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public async Task<AmpStatus?> StatusAsync(CancellationToken ct)
    {
        var url = Url("status");
        if (url is null) return null;
        try
        {
            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                LastError = res.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "the KPA1500 feature is not installed or not started in Zeus"
                    : $"the KPA1500 feature answered {(int)res.StatusCode}";
                return null;
            }
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var r = doc.RootElement;
            var amp = Obj(r, "amp");
            LastError = "";
            return new AmpStatus(
                Connected: Str(Obj(r, "connection"), "state") == "open",
                Power: Str(amp, "power") ?? "unknown",
                Operate: Bool(amp, "operate"),
                Antenna: Int(amp, "antenna"),
                NoAntState: Str(Obj(r, "noAnt"), "state") ?? "off",
                Tx: Bool(r, "tx") ?? false,
                TunePhase: Str(Obj(r, "tuneGuard"), "phase") ?? "idle");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            LastError = ex is TaskCanceledException ? "no answer from Zeus" : ex.Message;
            return null;
        }
    }

    public async Task<(bool Ok, string Message)> PostAsync(string path, object? body, CancellationToken ct)
    {
        var url = Url(path);
        if (url is null) return (false, "KPA1500 feature not reachable: " + LastError);
        try
        {
            using var res = await _http.PostAsJsonAsync(url, body ?? new { }, ct).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string msg = "";
            try
            {
                using var d = JsonDocument.Parse(text);
                msg = Str(d.RootElement, "message") ?? "";
            }
            catch (JsonException) { }
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound && msg.Length == 0)
                return (false, "the KPA1500 feature is not installed or not started in Zeus");
            return (res.IsSuccessStatusCode, msg.Length > 0 ? msg : $"KPA1500 feature answered {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            LastError = ex is TaskCanceledException ? "no answer from Zeus" : ex.Message;
            return (false, "KPA1500 feature not reachable: " + LastError);
        }
    }

    public async Task<(bool Ok, string Message)> ToggleStandbyOnTuneAsync(CancellationToken ct)
    {
        var url = Url("config");
        if (url is null) return (false, "KPA1500 feature not reachable: " + LastError);
        try
        {
            var node = await _http.GetFromJsonAsync<JsonNode>(url, ct).ConfigureAwait(false);
            if (node is not JsonObject cfg || cfg["standbyOnTune"] is not JsonValue v || !v.TryGetValue<bool>(out var on))
                return (false, "Could not read the KPA1500 settings");
            cfg["standbyOnTune"] = !on;
            using var res = await _http.PostAsJsonAsync(url, cfg, ct).ConfigureAwait(false);
            return res.IsSuccessStatusCode
                ? (true, "Standby-on-Tune " + (on ? "OFF" : "ON"))
                : (false, $"KPA1500 settings not saved ({(int)res.StatusCode})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return (false, "KPA1500 feature not reachable: " + ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}

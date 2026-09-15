using System.Text.Json;
using ChopItUp.Desktop.Bridge;
using ChopItUp.Desktop.Hub;
using Xunit;

namespace ChopItUp.Desktop.Tests;

/// <summary>Row 12 T5: HostBridge is a pure function from a page message to a host action plus a JSON
/// reply, so the whole wire protocol (dispatch, ids, trust) is tested here without a window.</summary>
public sealed class HostBridgeTests
{
    private const string HubOrigin = "http://127.0.0.1:8795/";
    private const string LaunchNonce = "abc123ff";

    private static FakeHostActions NewHost(bool maximized = false) =>
        new() { IsMaximized = maximized, Hub = new HubStatus(HubState.Ready, 1234, 8795, null) };

    [Theory]
    [InlineData("minimize", "Minimize")]
    [InlineData("maximize", "Maximize")]
    [InlineData("restore", "Restore")]
    [InlineData("close", "Close")]
    [InlineData("quit", "Quit")]
    public void Each_command_calls_exactly_its_method(string cmd, string expectedCall)
    {
        var host = NewHost();
        HostBridge.Handle($$"""{"id":"1","cmd":"{{cmd}}"}""", host);
        Assert.Equal(new[] { expectedCall }, host.Calls);
    }

    [Fact]
    public void ToggleMaximize_restores_when_already_maximized()
    {
        var host = NewHost(maximized: true);
        HostBridge.Handle("""{"id":"1","cmd":"toggleMaximize"}""", host);
        Assert.Equal(new[] { "Restore" }, host.Calls);
    }

    [Fact]
    public void ToggleMaximize_maximizes_when_not_maximized()
    {
        var host = NewHost(maximized: false);
        HostBridge.Handle("""{"id":"1","cmd":"toggleMaximize"}""", host);
        Assert.Equal(new[] { "Maximize" }, host.Calls);
    }

    [Fact]
    public void GetState_returns_maximized_and_hub_shape_including_port()
    {
        var host = NewHost(maximized: true);
        host.Hub = new HubStatus(HubState.Attached, null, 8811, "attached to :8811");
        var reply = HostBridge.Handle("""{"id":"9","cmd":"getState"}""", host);

        using var doc = JsonDocument.Parse(reply);
        var root = doc.RootElement;
        Assert.Equal("9", root.GetProperty("id").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        var result = root.GetProperty("result");
        Assert.True(result.GetProperty("maximized").GetBoolean());
        var hub = result.GetProperty("hub");
        Assert.Equal("attached", hub.GetProperty("state").GetString());
        Assert.Equal(8811, hub.GetProperty("port").GetInt32());
        Assert.Equal("attached to :8811", hub.GetProperty("reason").GetString());
    }

    [Fact]
    public void Id_round_trips()
    {
        var host = NewHost();
        var reply = HostBridge.Handle("""{"id":"round-trip-me","cmd":"minimize"}""", host);
        using var doc = JsonDocument.Parse(reply);
        Assert.Equal("round-trip-me", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void Missing_id_round_trips_as_null()
    {
        var host = NewHost();
        var reply = HostBridge.Handle("""{"cmd":"minimize"}""", host);
        using var doc = JsonDocument.Parse(reply);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("id").ValueKind);
    }

    [Fact]
    public void Unknown_command_answers_ok_false_and_calls_nothing()
    {
        var host = NewHost();
        var reply = HostBridge.Handle("""{"id":"1","cmd":"launchNukes"}""", host);
        using var doc = JsonDocument.Parse(reply);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("launchNukes", doc.RootElement.GetProperty("error").GetString());
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Malformed_json_answers_ok_false()
    {
        var host = NewHost();
        var reply = HostBridge.Handle("{not json", host);
        using var doc = JsonDocument.Parse(reply);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Missing_cmd_answers_ok_false()
    {
        var host = NewHost();
        var reply = HostBridge.Handle("""{"id":"1"}""", host);
        using var doc = JsonDocument.Parse(reply);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    // ===== trust matrix (ticket 05) ===========================================================

    [Fact]
    public void Hub_origin_any_path_is_trusted_without_a_nonce() =>
        Assert.True(HostBridge.IsTrusted("http://127.0.0.1:8795/some/path", new Uri(HubOrigin), null, LaunchNonce));

    [Fact]
    public void Hub_origin_is_trusted_case_insensitively() =>
        Assert.True(HostBridge.IsTrusted("HTTP://127.0.0.1:8795/", new Uri(HubOrigin), null, LaunchNonce));

    [Fact]
    public void Other_port_is_not_trusted_even_with_the_right_nonce() =>
        Assert.False(HostBridge.IsTrusted("http://127.0.0.1:8796/", new Uri(HubOrigin), LaunchNonce, LaunchNonce));

    [Fact]
    public void Https_elsewhere_is_not_trusted() =>
        Assert.False(HostBridge.IsTrusted("https://example.com", new Uri(HubOrigin), null, LaunchNonce));

    [Fact]
    public void About_blank_with_the_launch_nonce_is_trusted() =>
        Assert.True(HostBridge.IsTrusted("about:blank", new Uri(HubOrigin), LaunchNonce, LaunchNonce));

    [Fact]
    public void About_blank_with_the_wrong_nonce_is_not_trusted() =>
        Assert.False(HostBridge.IsTrusted("about:blank", new Uri(HubOrigin), "not-the-nonce", LaunchNonce));

    [Fact]
    public void About_blank_without_a_nonce_is_not_trusted() =>
        Assert.False(HostBridge.IsTrusted("about:blank", new Uri(HubOrigin), null, LaunchNonce));

    [Fact]
    public void Null_source_is_not_trusted() =>
        Assert.False(HostBridge.IsTrusted(null, new Uri(HubOrigin), LaunchNonce, LaunchNonce));

    [Fact]
    public void ExtractNonce_reads_the_nonce_field()
    {
        Assert.Equal("n1", HostBridge.ExtractNonce("""{"cmd":"close","nonce":"n1"}"""));
    }

    [Fact]
    public void ExtractNonce_returns_null_for_malformed_or_absent()
    {
        Assert.Null(HostBridge.ExtractNonce("{not json"));
        Assert.Null(HostBridge.ExtractNonce("""{"cmd":"close"}"""));
    }
}

/// <summary>A recording fake: every call is appended to <see cref="Calls"/> so tests assert exactly one
/// method fired.</summary>
internal sealed class FakeHostActions : IHostActions
{
    public List<string> Calls { get; } = new();
    public bool IsMaximized { get; set; }
    public HubStatus Hub { get; set; } = new(HubState.Starting, null, 8790, null);

    public void Minimize() => Calls.Add("Minimize");
    public void Maximize() => Calls.Add("Maximize");
    public void Restore() => Calls.Add("Restore");
    public void Close() => Calls.Add("Close");
    public void Quit() => Calls.Add("Quit");
}

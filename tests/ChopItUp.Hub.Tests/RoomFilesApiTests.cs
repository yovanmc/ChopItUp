using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Web;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

/// <summary><c>GET /api/rooms/{roomId}/files</c>. Each refusal case puts a real file where the ref
/// points, so only the rule under test can stop it.</summary>
public sealed class RoomFilesApiTests : IAsyncLifetime
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4];
    private static readonly byte[] Foreign = "FOREIGN-BYTES"u8.ToArray();

    private readonly string _data = Path.Combine(Path.GetTempPath(), "chopitup_roomfiles_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = [];
    private HubTestHost _host = null!;
    private string Files => _data + "_files";
    private string RoomDir => Path.Combine(Files, "lab");
    private string ArtifactsDir => RoomDir + ".artifacts";

    public async Task InitializeAsync()
    {
        _host = await HubTestHost.StartAsync(_data);
        Directory.CreateDirectory(RoomDir);
        Store.CreateRoom("lab", "Lab", RoomDir);
    }

    public async Task DisposeAsync()
    {
        foreach (var link in Enumerable.Reverse(_links))
            if (Directory.Exists(link) || File.Exists(link)) { if (File.Exists(link)) File.Delete(link); else Directory.Delete(link); }
        await _host.DisposeAsync();
        TestDirs.DeleteTree(Files);
    }

    private MessageStore Store => _host.Services.GetRequiredService<MessageStore>();

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private void Junction(string link, string target)
    {
        _links.Add(link);
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    private Task<HttpResponseMessage> Get(string reference, string room = "lab", Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"api/rooms/{room}/files?ref={Uri.EscapeDataString(reference)}");
        configure?.Invoke(request);
        return _host.Client.SendAsync(request);
    }

    private static void AssertGuarded(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Resource-Policy")));
    }

    private static async Task AssertRefused(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        AssertGuarded(response);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Contains("\"error\"", System.Text.Encoding.UTF8.GetString(body));
        Assert.False(body.AsSpan().IndexOf(Foreign) >= 0 || body.AsSpan().IndexOf(Png) >= 0, "a refusal carried file bytes");
    }

    [Fact]
    public async Task A_range_request_on_an_mp4_answers_206_with_those_bytes()
    {
        var video = Enumerable.Range(0, 4000).Select(i => (byte)(i % 251)).ToArray();
        Write(Path.Combine(ArtifactsDir, "clip.mp4"), video);

        var response = await Get("artifact:clip.mp4", configure: r => r.Headers.Range = new RangeHeaderValue(100, 199));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"bytes 100-199/{video.Length}", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(video[100..200], await response.Content.ReadAsByteArrayAsync());
        AssertGuarded(response);
    }

    [Theory]
    [InlineData("artifact:renders/a.png", true)]
    [InlineData("artifact:./renders/a.png", true)]
    [InlineData("docs/a.png", false)]
    [InlineData("./docs/a.png", false)]
    public async Task A_png_in_either_root_is_served_with_its_type_and_guard_headers(string reference, bool inArtifacts)
    {
        var path = inArtifacts ? Path.Combine(ArtifactsDir, "renders", "a.png") : Path.Combine(RoomDir, "docs", "a.png");
        Write(path, Png);

        var response = await Get(reference);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync());
        AssertGuarded(response);
        Assert.Equal("default-src 'none'; sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.NotNull(response.Content.Headers.LastModified);
        Assert.Equal(File.GetLastWriteTimeUtc(path), response.Content.Headers.LastModified!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
    }

    [Fact]
    public async Task A_ref_that_escapes_by_shape_is_refused_400_even_when_its_target_exists()
    {
        Write(Path.Combine(Files, "outside.png"), Foreign);
        Write(Path.Combine(RoomDir, "x.txt"), "text"u8.ToArray());
        File.WriteAllBytes(Path.Combine(RoomDir, "x.txt:y.png"), Png);       // a real alternate stream
        Assert.Equal(Png, File.ReadAllBytes(Path.Combine(RoomDir, "x.txt:y.png")));
        Directory.CreateDirectory(Path.Combine(RoomDir, "sub"));

        foreach (var reference in new[] { "../outside.png", @"sub\..\..\outside.png", "x.txt:y.png", "artifact:../lab/x.txt:y.png" })
            await AssertRefused(await Get(reference), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_type_the_route_does_not_serve_is_refused_415()
    {
        Write(Path.Combine(ArtifactsDir, "x.svg"), "<svg/>"u8.ToArray());
        await AssertRefused(await Get("artifact:x.svg"), HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task A_junction_out_of_the_room_or_the_artifacts_folder_is_refused_403()
    {
        var outside = Path.Combine(Files, "outside");
        Write(Path.Combine(outside, "foreign.png"), Foreign);
        Junction(Path.Combine(RoomDir, "escape"), outside);
        Directory.CreateDirectory(ArtifactsDir);
        Junction(Path.Combine(ArtifactsDir, "escape"), outside);

        await AssertRefused(await Get("escape/foreign.png"), HttpStatusCode.Forbidden);
        await AssertRefused(await Get("artifact:escape/foreign.png"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_artifacts_folder_that_is_itself_a_junction_is_refused_403()
    {
        var elsewhere = Path.Combine(Files, "pictures");
        Write(Path.Combine(elsewhere, "foreign.png"), Foreign);
        Junction(ArtifactsDir, elsewhere);

        await AssertRefused(await Get("artifact:foreign.png"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_room_reached_through_a_junction_serves_unless_the_junction_leads_into_a_refused_folder()
    {
        var real = Path.Combine(Files, "real");
        Write(Path.Combine(real, "a.png"), Png);
        var linked = Path.Combine(Files, "linked");
        Junction(linked, real);
        Store.CreateRoom("viaLink", "Via link", linked);

        var hubData = Path.Combine(_data, "room-in-data");
        Write(Path.Combine(hubData, "a.png"), Foreign);
        var intoData = Path.Combine(Files, "into-data");
        Junction(intoData, hubData);
        Store.CreateRoom("intoData", "Into data", intoData);

        var served = await Get("a.png", room: "viaLink");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(Png, await served.Content.ReadAsByteArrayAsync());
        await AssertRefused(await Get("a.png", room: "intoData"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_file_symlink_is_refused_403_when_this_machine_can_create_one()
    {
        // The target is inside the room, so only the link rule can refuse it.
        var target = Path.Combine(RoomDir, "target.png");
        Write(target, Png);
        var link = Path.Combine(RoomDir, "link.png");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Symlink creation needs developer mode or elevation. Early return, not Assert.Skip: this
            // stack is xunit 2.9.3, which has no such API.
            return;
        }
        _links.Add(link);

        await AssertRefused(await Get("link.png"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Missing_rooms_folders_and_files_answer_404_with_the_guard_headers()
    {
        Write(Path.Combine(RoomDir, "docs", "a.png"), Png);

        await AssertRefused(await Get("docs/a.png", room: "no-such-room"), HttpStatusCode.NotFound);
        await AssertRefused(await Get("docs/a.png", room: "general"), HttpStatusCode.NotFound);       // no directory
        await AssertRefused(await Get("artifact:a.png"), HttpStatusCode.NotFound);                    // no artifacts folder yet
        await AssertRefused(await Get("docs/none.png"), HttpStatusCode.NotFound);
        await AssertRefused(await Get("nodir/a.png"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_file_under_a_path_longer_than_the_first_final_path_buffer_is_served()
    {
        var deep = Path.Combine(RoomDir, string.Join('\\', Enumerable.Repeat(new string('d', 60), 9)));
        var path = Path.Combine(deep, "a.png");
        Assert.True(path.Length > 600);
        Write(path, Png);

        var relative = Path.GetRelativePath(RoomDir, path).Replace('\\', '/');
        var response = await Get(relative);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_png_can_be_replaced_by_rename_while_its_body_is_still_on_the_way()
    {
        var path = Path.Combine(ArtifactsDir, "a.png");
        var original = Enumerable.Range(0, 8 * 1024 * 1024).Select(i => (byte)(i % 253)).ToArray();
        Write(path, original);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/rooms/lab/files?ref={Uri.EscapeDataString("artifact:a.png")}");
        using var response = await _host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var next = path + ".tmp";
        File.WriteAllBytes(next, Foreign);
        File.Move(next, path, overwrite: true);

        Assert.Equal(Foreign, File.ReadAllBytes(path));
        Assert.Equal(original, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task A_png_under_another_programs_range_lock_answers_503_with_retry_after()
    {
        var path = Path.Combine(ArtifactsDir, "locked.png");
        Write(path, Png);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        held.Lock(0, Png.Length);

        var response = await Get("artifact:locked.png");

        await AssertRefused(response, HttpStatusCode.ServiceUnavailable);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task A_room_linked_into_a_data_folder_the_hub_reached_through_a_junction_is_refused_403()
    {
        var realData = Path.Combine(Files, "real-data");
        Directory.CreateDirectory(realData);
        var linkedData = Path.Combine(Files, "linked-data");
        Junction(linkedData, realData);
        Write(Path.Combine(realData, "rooms-here", "a.png"), Foreign);
        var room = Path.Combine(Files, "into-real-data");
        Junction(room, Path.Combine(realData, "rooms-here"));

        await using var host = await HubTestHost.StartAsync(linkedData, deleteOnDispose: false, roomsRoot: Path.Combine(Files, "rooms2"));
        host.Services.GetRequiredService<MessageStore>().CreateRoom("sneaky", "Sneaky", room);
        var response = await host.Client.GetAsync($"api/rooms/sneaky/files?ref={Uri.EscapeDataString("a.png")}");

        await AssertRefused(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_png_over_the_size_cap_is_refused_413()
    {
        Directory.CreateDirectory(ArtifactsDir);
        using (var big = File.Create(Path.Combine(ArtifactsDir, "big.png"))) big.SetLength(RoomFilesApi.MaxImageBytes + 1);

        await AssertRefused(await Get("artifact:big.png"), HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_file_another_program_holds_unshared_answers_503_with_retry_after()
    {
        var path = Path.Combine(ArtifactsDir, "busy.png");
        Write(path, Png);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var response = await Get("artifact:busy.png");

        await AssertRefused(response, HttpStatusCode.ServiceUnavailable);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task A_request_beyond_the_concurrent_limit_answers_503_at_once()
    {
        Write(Path.Combine(ArtifactsDir, "a.png"), Png);
        var gate = _host.Services.GetRequiredService<RoomFileGate>();
        for (var i = 0; i < RoomFileGate.Permits; i++) Assert.True(gate.Slots.Wait(0));
        try
        {
            var response = await Get("artifact:a.png");
            await AssertRefused(response, HttpStatusCode.ServiceUnavailable);
            Assert.NotNull(response.Headers.RetryAfter);
        }
        finally { gate.Slots.Release(RoomFileGate.Permits); }

        Assert.Equal(HttpStatusCode.OK, (await Get("artifact:a.png")).StatusCode);
    }
}

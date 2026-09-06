using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using ChopItUp.Hub.Web;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

public sealed class RoomsApiTests : IAsyncLifetime
{
    private static readonly SpawnLimits Fast = new(Budget: 4, Debounce: TimeSpan.FromMilliseconds(150), MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_rooms_" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir, processRunner: _runner, limits: Fast);
    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        TestDirs.DeleteTree(_dir + "_side");
    }

    private string Sibling(string name) => Path.Combine(_dir + "_side", name);   // outside the data dir and the rooms root; deleted below
    private MessageStore Store => _host.Services.GetRequiredService<MessageStore>();

    private async Task<JsonElement> Rooms(bool archived = false)
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms" + (archived ? "?archived=true" : "")));
        return doc.RootElement.Clone();
    }

    private static string[] Ids(JsonElement rooms) => rooms.EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray();

    private async Task<(HttpStatusCode Status, JsonElement Body)> Post(string path, object? body = null)
    {
        var r = body is null ? await _host.Client.PostAsync(path, null) : await _host.Client.PostAsJsonAsync(path, body);
        var text = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text.Length == 0 ? "null" : text);
        return (r.StatusCode, doc.RootElement.Clone());
    }

    [Fact]
    public async Task M9_A2_A3_creating_a_room_slugs_the_title_makes_a_git_repository_under_the_rooms_root_and_lists_it_first()
    {
        var (status, room) = await Post("api/rooms", new { name = "Lab Notes" });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("lab-notes", room.GetProperty("id").GetString());
        Assert.Equal("Lab Notes", room.GetProperty("name").GetString());
        var directory = room.GetProperty("directory").GetString()!;
        Assert.Equal(Path.Combine(_host.RoomsRoot, "lab-notes"), directory);
        Assert.True(Directory.Exists(Path.Combine(directory, ".git")));
        Assert.Equal(JsonValueKind.Null, room.GetProperty("archivedAt").ValueKind);
        Assert.Equal(0, room.GetProperty("unread").GetInt64());

        Assert.Equal(["lab-notes", "general"], Ids(await Rooms()));            // newest activity first
        var (again, second) = await Post("api/rooms", new { name = "Lab Notes" });
        Assert.Equal(HttpStatusCode.Created, again);
        Assert.Equal("lab-notes-2", second.GetProperty("id").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await Post("api/rooms", new { name = "   " })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("api/rooms", new { name = new string('x', 81) })).Status);
    }

    [Fact]
    public async Task M9_A2_refused_directories_come_back_400_with_the_reason_and_no_room_is_created()
    {
        foreach (var (typed, fragment) in new[]
                 {
                     (@"C:\", "drive root"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "user profile folder"),
                     (Path.Combine(_dir, "inside"), "the hub's data folder"),
                     (@"C:\Self Apps\ChopItUp", @"C:\Self Apps"),
                     (@"\\server\share\x", "Network and device paths"),
                     ("relative", "absolute local path"),
                 })
        {
            var (status, body) = await Post("api/rooms", new { name = "Nope", directory = typed });
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains(fragment, body.GetProperty("error").GetString());
        }
        Assert.Equal(["general"], Ids(await Rooms(archived: true)));
    }

    [Fact]
    public async Task M9_A2_a_directory_inside_another_repository_or_overlapping_another_room_is_refused_and_an_existing_repository_is_adopted()
    {
        var outer = Sibling("outer");
        Assert.True(await new GitTrail(outer).InitAsync());
        Directory.CreateDirectory(Path.Combine(outer, "inner"));
        var (nested, nestedBody) = await Post("api/rooms", new { name = "Nested", directory = Path.Combine(outer, "inner") });
        Assert.Equal(HttpStatusCode.BadRequest, nested);
        Assert.Contains("inside the repository at", nestedBody.GetProperty("error").GetString());

        var (adopted, adoptedBody) = await Post("api/rooms", new { name = "Outer", directory = outer });   // root of its own repo: fine
        Assert.Equal(HttpStatusCode.Created, adopted);
        Assert.Equal(RoomPaths.Normalize(outer), adoptedBody.GetProperty("directory").GetString());

        var (overlapBelow, b1) = await Post("api/rooms", new { name = "Below", directory = Path.Combine(outer, "inner") });
        Assert.Equal(HttpStatusCode.BadRequest, overlapBelow);
        Assert.Contains("overlaps room 'outer'", b1.GetProperty("error").GetString());
        var (overlapAbove, b2) = await Post("api/rooms", new { name = "Above", directory = Path.GetDirectoryName(outer)! });
        Assert.Equal(HttpStatusCode.BadRequest, overlapAbove);
        Assert.Contains("overlaps room 'outer'", b2.GetProperty("error").GetString());

        var orphan = Path.Combine(Sibling("missing-parent"), "child");
        var (noParent, b3) = await Post("api/rooms", new { name = "Orphan", directory = orphan });
        Assert.Equal(HttpStatusCode.BadRequest, noParent);
        Assert.Contains("parent folder", b3.GetProperty("error").GetString());
    }

    [Fact]
    public async Task M9_A4_archive_hides_a_room_keeps_it_reachable_and_unarchive_restores_it_and_general_stays()
    {
        await Post("api/rooms", new { name = "Old" });
        var (status, archived) = await Post("api/rooms/old/archive");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEqual(JsonValueKind.Null, archived.GetProperty("archivedAt").ValueKind);
        Assert.Equal(["general"], Ids(await Rooms()));
        Assert.Equal(["old", "general"], Ids(await Rooms(archived: true)));

        Assert.Equal(HttpStatusCode.Created, (await Post("api/rooms/old/messages", new { body = "still here" })).Status);   // hides, never blocks
        Assert.True(Directory.Exists(Path.Combine(_host.RoomsRoot, "old", ".git")));
        Assert.Equal(HttpStatusCode.OK, (await Post("api/rooms/old/archive")).Status);                                      // idempotent

        var (back, restored) = await Post("api/rooms/old/unarchive");
        Assert.Equal(HttpStatusCode.OK, back);
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("archivedAt").ValueKind);
        Assert.Equal(["old", "general"], Ids(await Rooms()));

        var (general, body) = await Post("api/rooms/general/archive");
        Assert.Equal(HttpStatusCode.BadRequest, general);
        Assert.Equal(RoomsApi.GeneralStays, body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Post("api/rooms/nope/archive")).Status);
    }

    [Fact]
    public async Task M9_A5_a_legacy_room_binds_a_directory_once_then_409()
    {
        Assert.Equal(JsonValueKind.Null, (await Rooms()).EnumerateArray().Single().GetProperty("directory").ValueKind);   // general is NULL after migration
        var typed = Sibling("gen");
        Directory.CreateDirectory(Path.GetDirectoryName(typed)!);
        var (status, room) = await Post("api/rooms/general/directory", new { directory = typed });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(RoomPaths.Normalize(typed), room.GetProperty("directory").GetString());
        Assert.True(Directory.Exists(Path.Combine(typed, ".git")));

        var (again, body) = await Post("api/rooms/general/directory", new { directory = Sibling("gen2") });
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Contains("bound once", body.GetProperty("error").GetString());
        Assert.Equal(RoomPaths.Normalize(typed), Store.GetRoom("general")!.Directory);

        var (blank, hubMade) = await Post("api/rooms/blank-legacy/directory", new { directory = "" });   // unknown room
        Assert.Equal(HttpStatusCode.NotFound, blank);
        Store.CreateRoom("legacy", "Legacy", null);
        var (made, madeBody) = await Post("api/rooms/legacy/directory", new { directory = "" });        // blank = hub-created
        Assert.Equal(HttpStatusCode.OK, made);
        Assert.Equal(Path.Combine(_host.RoomsRoot, "legacy"), madeBody.GetProperty("directory").GetString());
    }

    [Fact]
    public async Task M9_A3_A5_unread_is_what_lies_past_the_owners_cursor_and_mark_read_zeroes_it()
    {
        await using var claude = await _host.ClientFor("claude");
        foreach (var body in new[] { "one", "two" })
            HubTestHost.Json(await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = body, ["client_key"] = Guid.NewGuid().ToString() }));
        Assert.Equal(2, (await Rooms()).EnumerateArray().Single().GetProperty("unread").GetInt64());

        await Post("api/rooms/general/messages", new { body = "mine" });                   // own post moves own cursor past everything before it (claim 5)
        Assert.Equal(0, (await Rooms()).EnumerateArray().Single().GetProperty("unread").GetInt64());
        HubTestHost.Json(await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "three", ["client_key"] = Guid.NewGuid().ToString() }));
        Assert.Equal(1, (await Rooms()).EnumerateArray().Single().GetProperty("unread").GetInt64());

        var (status, read) = await Post("api/rooms/general/read");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, read.GetProperty("unread").GetInt64());
        Assert.Equal(0, (await Rooms()).EnumerateArray().Single().GetProperty("unread").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, (await Post("api/rooms/nope/read")).Status);
    }

    [Fact]
    public async Task M9_A11_the_trail_is_empty_for_a_fresh_or_directoryless_room_and_lists_commits_newest_first()
    {
        var (_, general) = (HttpStatusCode.OK, JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/general/trail")).RootElement);
        Assert.Equal(JsonValueKind.Null, general.GetProperty("directory").ValueKind);
        Assert.Empty(general.GetProperty("commits").EnumerateArray());

        var (_, room) = await Post("api/rooms", new { name = "Lab" });
        var directory = room.GetProperty("directory").GetString()!;
        var fresh = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/lab/trail")).RootElement;
        Assert.Equal(directory, fresh.GetProperty("directory").GetString());
        Assert.Empty(fresh.GetProperty("commits").EnumerateArray());

        var trail = _host.Services.GetRequiredService<RoomTrails>().For(directory);
        File.WriteAllText(Path.Combine(directory, "a.txt"), "a");
        await trail.CommitAllAsync("owner: edits before the next spawn in room lab", new GitIdentity("Owner", "owner@chopitup.local"), allowEmpty: false);
        await trail.CommitAllAsync("opus: turn 1/4 in room lab\n\nShell commands run: none.\n", new GitIdentity("Opus", "opus@chopitup.local"), allowEmpty: true);

        var commits = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/lab/trail")).RootElement.GetProperty("commits").EnumerateArray().ToList();
        Assert.Equal(2, commits.Count);
        Assert.Equal("opus: turn 1/4 in room lab", commits[0].GetProperty("subject").GetString());
        Assert.Equal("Opus <opus@chopitup.local>", commits[0].GetProperty("author").GetString());
        Assert.Equal("owner: edits before the next spawn in room lab", commits[1].GetProperty("subject").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.GetAsync("api/rooms/nope/trail")).StatusCode);
    }

    [Fact]
    public async Task M9_A2_A4_A5_create_bind_and_archive_are_409_while_a_spawn_is_in_flight_and_work_after_it_ends()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (_, _, ct) => { await release.Task.WaitAsync(ct); return FakeProcessRunner.Ok("""{"result":"done"}"""); };
        Store.CreateRoom("side", "Side", null);
        Assert.Equal(HttpStatusCode.Created, (await Post("api/rooms/general/messages", new { body = "@opus hi" })).Status);
        await _runner.NextSpecAsync(Wait);
        var spawner = _host.Services.GetRequiredService<SpawnerService>();
        Assert.True(spawner.AnySpawnInFlight);

        foreach (var (path, body) in new (string, object?)[]
                 {
                     ("api/rooms", new { name = "Planted" }),
                     ("api/rooms/side/directory", new { directory = "" }),
                     ("api/rooms/side/archive", null),
                     ("api/rooms/side/unarchive", null),
                 })
        {
            var (status, reply) = await Post(path, body);
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal(RoomsApi.SpawnRunning, reply.GetProperty("error").GetString());
        }
        Assert.Equal(["general", "side"], Ids(await Rooms(archived: true)));   // general's message is newer than side's creation
        Assert.Null(Store.GetRoom("side")!.Directory);

        release.SetResult();
        var deadline = DateTime.UtcNow + Wait;
        while (spawner.AnySpawnInFlight && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(spawner.AnySpawnInFlight);
        Assert.Equal(HttpStatusCode.Created, (await Post("api/rooms", new { name = "Planted" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post("api/rooms/side/archive")).Status);
    }

    [Fact]
    public async Task M9_A2_without_git_creating_or_binding_a_directory_room_is_400_and_leaves_no_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_roomsnogit_" + Guid.NewGuid().ToString("N"));
        await using var host = await HubTestHost.StartAsync(dir, roomGit: d => new GitTrail(d, () => throw new FileNotFoundException("'git' was not found on PATH")));
        var r = await host.Client.PostAsJsonAsync("api/rooms", new { name = "No Git" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("git was not found on PATH", await r.Content.ReadAsStringAsync());
        Assert.False(Directory.Exists(Path.Combine(host.RoomsRoot, "no-git")));
        Assert.DoesNotContain(host.Services.GetRequiredService<MessageStore>().ListRooms(includeArchived: true), x => x.Id == "no-git");
        var bind = await host.Client.PostAsJsonAsync("api/rooms/general/directory", new { directory = "" });
        Assert.Equal(HttpStatusCode.BadRequest, bind.StatusCode);
        Assert.Null(host.Services.GetRequiredService<MessageStore>().GetRoom("general")!.Directory);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodexHistoryArchive.Tests
{
    internal static class ClientTests
    {
        private static string executable, root;
        private static int passed, failed;
        private static readonly List<object> results = new List<object>();
        private const string FixtureId = "synthetic-fixture";
        private const string FixtureText = "{\"synthetic\":true,\"message\":\"preserve these bytes\"}\n";

        private static int Main(string[] args)
        {
            if (args.Length != 2) { Console.Error.WriteLine("Expected fake server and isolated test directory."); return 2; }
            executable = Path.GetFullPath(args[0]); root = Path.GetFullPath(args[1]);
            if (!File.Exists(Path.Combine(root, ".synthetic-test-root"))) return 3;
            RunAll().GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(root, "test-report.json"), Json.Serializer().Serialize(new { passed = passed, failed = failed, scope = "Synthetic protocol tests; no real Codex data or model requests", tests = results }), new UTF8Encoding(false));
            Console.WriteLine(String.Format("RESULT: {0} passed, {1} failed. All fixtures were synthetic.", passed, failed));
            return failed == 0 ? 0 : 1;
        }

        private static async Task RunAll()
        {
            await Test("pagination-deduplicates-235-rows", "pagination", async delegate(NativeClient client, string home) {
                var rows = await client.List(false);
                Require(rows.Count == 235 && rows.Select(r => r.Id).Distinct().Count() == 235, "Three pages must produce all 235 unique histories.");
                Require(rows.First().Id == "history-000" && rows.Last().Id == "history-234", "Page order must be preserved.");
                Require(Count(home, "thread/list") == 3, "Pagination must stop after the third page.");
            });
            await Test("cancel-before-list-does-not-request", "pagination", async delegate(NativeClient client, string home) {
                bool cancelled = false;
                try { await client.List(false, delegate { return true; }); } catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && Count(home, "thread/list") == 0, "Cancelling before a list starts must not send a list request.");
            });
            await Test("cancel-after-response-stops-pagination", "pagination", async delegate(NativeClient client, string home) {
                bool cancelled = false;
                try { await client.List(false, delegate { return Count(home, "thread/list") > 0; }); } catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && Count(home, "thread/list") == 1, "Cancellation after a response must stop before requesting another page.");
                Require(Count(home, "thread/archive") == 0, "Cancelling a list cannot mutate history.");
            });
            foreach (string scenario in new[] { "repeat-cursor", "wrong-cursor", "missing-data", "wrong-data", "wrong-item", "missing-id", "unknown-source", "read-mismatch", "metadata-error" })
            {
                string current = scenario;
                await Test("rejects-" + current, current, async delegate(NativeClient client, string home) {
                    var error = await ExpectNative(async delegate { await client.List(false); });
                    Require(!error.Unknown, "A list compatibility failure must not be reported as a mutation with an unknown result.");
                    if (current == "repeat-cursor") Require(Count(home, "thread/list") == 2, "Repeated cursor must fail without looping.");
                });
            }
            await Test("source-filter-and-read-fallback", "source-filter", async delegate(NativeClient client, string home) {
                var rows = await client.List(false);
                Require(String.Join(",", rows.Select(r => r.Id).OrderBy(id => id)) == "read-user,root-uppercase,root-user", "Only root user histories should remain.");
                Require(Count(home, "thread/read") == 3, "Missing sources need metadata reads; deleted records should be skipped.");
                Require(Requests(home).Where(r => Json.Text(r, "method") == "thread/read").All(r => ObjectFalse(Json.Obj(Json.Get(r, "params")), "includeTurns")), "Metadata reads must explicitly exclude conversation turns.");
            });
            await Test("whitelist-blocks-turns-before-transmission", "success", async delegate(NativeClient client, string home) {
                foreach (string method in new[] { "turn/start", "thread/start", "thread/resume", "thread/compact/start", "fs/remove" })
                {
                    bool rejected = false;
                    try { await client.Request(method, new { }); } catch (InvalidOperationException) { rejected = true; }
                    Require(rejected, "Unexpected methods must be rejected before transmission.");
                }
                Require(Requests(home).All(r => new[] { "initialize", "initialized" }.Contains(Json.Text(r, "method"))), "No forbidden request may reach the server.");
            });
            foreach (string scenario in new[] { "success", "fallback" })
            {
                string current = scenario;
                await Test("archive-restore-" + current, current, async delegate(NativeClient client, string home) {
                    byte[] before = File.ReadAllBytes(Path.Combine(home, "sessions", "fixture.jsonl"));
                    await client.Verify(FixtureId, false);
                    await client.Request("thread/archive", new { threadId = FixtureId });
                    await client.Verify(FixtureId, true);
                    Require(File.Exists(Path.Combine(home, "archived_sessions", "fixture.jsonl")), "Archive must move fixture to the archived state.");
                    await client.Request("thread/unarchive", new { threadId = FixtureId });
                    await client.Verify(FixtureId, false);
                    Require(before.SequenceEqual(File.ReadAllBytes(Path.Combine(home, "sessions", "fixture.jsonl"))), "Archive and restore must preserve every history byte.");
                    Require(Count(home, "thread/archive") == 1 && Count(home, "thread/unarchive") == 1, "Each mutation is sent once.");
                    if (current == "fallback") Require(Count(home, "thread/list") == 3, "Missing storage paths should use the native list fallback.");
                });
            }
            await Test("verification-mismatch-is-unknown", "unconfirmed", async delegate(NativeClient client, string home) {
                await client.Request("thread/archive", new { threadId = FixtureId });
                var error = await ExpectNative(async delegate { await client.Verify(FixtureId, true); });
                Require(error.Unknown, "A success response without verified final state must be unknown.");
                Require(Count(home, "thread/archive") == 1, "An unconfirmed mutation must never be automatically repeated.");
            });
            await Test("verification-read-failure-is-unknown", "verify-read-error", async delegate(NativeClient client, string home) {
                await client.Request("thread/archive", new { threadId = FixtureId });
                var error = await ExpectNative(async delegate { await client.Verify(FixtureId, true); });
                Require(error.Unknown, "A verification read failure must stop the batch as unknown.");
            });
            foreach (string scenario in new[] { "missing-result", "wrong-result" })
            {
                string current = scenario;
                await Test("mutation-" + current + "-is-unknown", current, async delegate(NativeClient client, string home) {
                    var error = await ExpectNative(async delegate { await client.Request("thread/archive", new { threadId = FixtureId }); });
                    Require(error.Unknown, "Malformed mutation responses must be treated as unknown.");
                    Require(File.Exists(Path.Combine(home, "archived_sessions", "fixture.jsonl")), "This synthetic mutation happened despite its unrecognized response.");
                    Require(Count(home, "thread/archive") == 1, "An unrecognized response cannot cause an automatic retry.");
                    await client.Verify(FixtureId, true);
                });
            }
            await Test("active-writer-is-safe-failure", "active-writer", async delegate(NativeClient client, string home) {
                var error = await ExpectNative(async delegate { await client.Request("thread/archive", new { threadId = FixtureId }); });
                Require(error.ActiveWriter && !error.Unknown, "An active writer must be classified as an explicit refusal.");
                Require(File.Exists(Path.Combine(home, "sessions", "fixture.jsonl")), "A refused archive must leave the fixture active.");
                Require(Count(home, "thread/archive") == 1, "A refused mutation must not be retried.");
            });
            await Test("missing-record-classification", "not-found", async delegate(NativeClient client, string home) {
                var error = await ExpectNative(async delegate { await client.Request("thread/read", new { threadId = FixtureId, includeTurns = false }); });
                Require(error.NotFound && !error.Unknown && !error.ActiveWriter, "Missing record classification must be distinct.");
            });
            await Test("server-errors-hide-private-details", "refused", async delegate(NativeClient client, string home) {
                var error = await ExpectNative(async delegate { await client.Request("thread/archive", new { threadId = FixtureId }); });
                Require(error.Unknown && !error.ActiveWriter && !error.NotFound, "A generic mutation error can follow partial changes and must be unknown.");
                Require(error.Message.IndexOf("synthetic private", StringComparison.Ordinal) < 0, "Raw server error details must not be surfaced.");
            });
            await Test("generic-read-error-is-not-mutation-unknown", "metadata-error", async delegate(NativeClient client, string home) {
                var error = await ExpectNative(async delegate { await client.Request("thread/read", new { threadId = FixtureId, includeTurns = false }); });
                Require(!error.Unknown && !error.ActiveWriter && !error.NotFound, "A generic read error has not submitted a mutation.");
                Require(error.Message.IndexOf("synthetic private", StringComparison.Ordinal) < 0, "Raw server read errors must not be surfaced.");
            });
            await Test("server-exit-is-unknown-without-retry", "server-exit", async delegate(NativeClient client, string home) {
                var error = await ExpectNative(async delegate { await client.Request("thread/archive", new { threadId = FixtureId }); });
                Require(error.Unknown, "Connection loss during a mutation must be unknown.");
                Require(Count(home, "thread/archive") == 1, "Disconnected mutations must not be retried.");
            });
            await Test("row-title-and-unknown-source", "success", delegate(NativeClient client, string home) {
                var row = HistoryRow.From(new Dictionary<string, object> { { "id", "synthetic-title" }, { "threadSource", "user" }, { "name", " \r\nExample\r\nTitle\n " } });
                Require(row != null && row.Title == "Example  Title", "Titles should be trimmed to one line.");
                Require(HistoryRow.From(new Dictionary<string, object> { { "id", "unclassified" } }) == null, "Unclassified rows must not be actionable.");
                return Task.FromResult(0);
            });
        }

        private static async Task Test(string name, string scenario, Func<NativeClient, string, Task> action)
        {
            string home = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(home, "sessions"));
            File.WriteAllText(Path.Combine(home, ".synthetic-test-home"), "synthetic only");
            File.WriteAllText(Path.Combine(home, "scenario.txt"), scenario);
            File.WriteAllText(Path.Combine(home, "sessions", "fixture.jsonl"), FixtureText, new UTF8Encoding(false));
            NativeClient client = null;
            try
            {
                client = new NativeClient(home, executable);
                await client.Initialize();
                await action(client, home);
                passed++; results.Add(new { name = name, passed = true }); Console.WriteLine("PASS " + name);
            }
            catch (Exception error)
            {
                failed++; results.Add(new { name = name, passed = false, error = error.GetType().Name, message = error.Message }); Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + " - " + error.Message);
            }
            if (client != null) await client.CloseAsync();
        }
        private static bool ObjectFalse(Dictionary<string, object> value, string key) { object item = Json.Get(value, key); return item is bool && !(bool)item; }
        private static IEnumerable<Dictionary<string, object>> Requests(string home)
        { return File.ReadAllLines(Path.Combine(home, "requests.jsonl")).Where(line => !String.IsNullOrWhiteSpace(line)).Select(line => Json.Obj(Json.Serializer().DeserializeObject(line))).ToArray(); }
        private static int Count(string home, string method) { return Requests(home).Count(r => Json.Text(r, "method") == method); }
        private static async Task<NativeError> ExpectNative(Func<Task> action)
        { try { await action(); } catch (NativeError error) { return error; } throw new Exception("Expected a NativeError, but the operation returned successfully."); }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}

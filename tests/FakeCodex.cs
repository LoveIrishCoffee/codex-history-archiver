// Synthetic JSON-RPC server for the tests. Never use this with a real Codex home.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexHistoryArchive.Tests
{
    internal static class FakeCodex
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static readonly StreamReader Input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        private static readonly StreamWriter Output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        private static string home, scenario;
        private static bool archived;
        internal const string FixtureId = "synthetic-fixture";

        private static Dictionary<string, object> Object(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        private static object Get(Dictionary<string, object> value, string key) { object item; return value.TryGetValue(key, out item) ? item : null; }
        private static string Text(Dictionary<string, object> value, string key) { return Convert.ToString(Get(value, key)); }
        private static Dictionary<string, object> Row(string id)
        {
            return new Dictionary<string, object> {
                { "id", id }, { "name", "Synthetic history " + id }, { "threadSource", "user" },
                { "source", "cli" }, { "parentThreadId", null }, { "updatedAt", 1700000000 }
            };
        }
        private static void Reply(object id, object result) { Output.WriteLine(Serializer.Serialize(new { id = id, result = result })); }
        private static void Error(object id, string message) { Output.WriteLine(Serializer.Serialize(new { id = id, error = new { code = -32000, message = message } })); }

        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--version") { Output.WriteLine("codex-cli 0.0.0-synthetic"); return 0; }
            if (args.Length != 2 || args[0] != "app-server" || args[1] != "--stdio") return 2;
            home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrEmpty(home) || !File.Exists(Path.Combine(home, ".synthetic-test-home"))) return 3;
            scenario = File.ReadAllText(Path.Combine(home, "scenario.txt")).Trim();
            // Use redirected streams directly: console code-page setters require an
            // attached Windows console, which is absent on headless CI runners.
            string line;
            while ((line = Input.ReadLine()) != null)
            {
                var request = Object(Serializer.DeserializeObject(line));
                File.AppendAllText(Path.Combine(home, "requests.jsonl"), line + Environment.NewLine, new UTF8Encoding(false));
                string method = Text(request, "method");
                if (method == "initialized") continue;
                object id = Get(request, "id");
                var parameters = Object(Get(request, "params"));
                if (method == "initialize") { Reply(id, new { userAgent = "Synthetic test server" }); continue; }
                if (method == "thread/list") { List(id, parameters); continue; }
                if (method == "thread/read") { Read(id, parameters); continue; }
                if (method == "thread/archive" || method == "thread/unarchive") { Mutate(id, method, parameters); continue; }
                Error(id, "Unexpected method in synthetic server");
            }
            return 0;
        }

        private static void List(object id, Dictionary<string, object> parameters)
        {
            if (!(Get(parameters, "archived") is bool) || !ObjectHasFalseOrMissingTurns(parameters) || Text(parameters, "limit") != "100")
            { Error(id, "Unexpected list parameters"); return; }
            bool requestedArchived = (bool)Get(parameters, "archived");
            string cursor = Text(parameters, "cursor");
            if (scenario == "pagination")
            {
                int start = 0, count = 100;
                string next = "page-two";
                if (cursor == "page-two") { start = 99; count = 100; next = "page-three"; }
                else if (cursor == "page-three") { start = 199; count = 36; next = null; }
                else if (!String.IsNullOrEmpty(cursor)) { Error(id, "Unknown test cursor"); return; }
                Reply(id, new { data = Enumerable.Range(start, count).Select(i => Row("history-" + i.ToString("D3"))).ToArray(), nextCursor = next });
                return;
            }
            if (scenario == "repeat-cursor") { Reply(id, new { data = new[] { Row("history-one") }, nextCursor = "repeated" }); return; }
            if (scenario == "wrong-cursor") { Reply(id, new { data = new[] { Row("history-one") }, nextCursor = 7 }); return; }
            if (scenario == "missing-data") { Reply(id, new { nextCursor = (string)null }); return; }
            if (scenario == "wrong-data") { Reply(id, new { data = "not-an-array", nextCursor = (string)null }); return; }
            if (scenario == "wrong-item") { Reply(id, new { data = new object[] { 7 }, nextCursor = (string)null }); return; }
            if (scenario == "missing-id") { Reply(id, new { data = new object[] { new { name = "Missing ID", threadSource = "user" } }, nextCursor = (string)null }); return; }
            if (scenario == "source-filter")
            {
                var rows = new List<Dictionary<string, object>>();
                rows.Add(Row("root-user"));
                var upper = Row("root-uppercase"); upper["threadSource"] = "USER"; rows.Add(upper);
                var child = Row("child-tagged"); child["threadSource"] = "subAgent"; rows.Add(child);
                var parent = Row("child-parent"); parent["parentThreadId"] = "synthetic-parent"; rows.Add(parent);
                var source = Row("child-source"); source["source"] = new Dictionary<string, object> { { "subAgent", new { spawn = new { parent_thread_id = "synthetic-parent" } } } }; rows.Add(source);
                var snakeSource = Row("child-source-snake"); snakeSource["source"] = new Dictionary<string, object> { { "sub_agent", new { parent_thread_id = "synthetic-parent" } } }; rows.Add(snakeSource);
                var textSource = Row("child-source-text"); textSource["source"] = "subAgent"; rows.Add(textSource);
                var snakeTextSource = Row("child-source-text-snake"); snakeTextSource["source"] = "sub_agent"; rows.Add(snakeTextSource);
                foreach (string name in new[] { "read-user", "read-child", "read-deleted" }) { var row = Row(name); row["threadSource"] = null; rows.Add(row); }
                Reply(id, new { data = rows.ToArray(), nextCursor = (string)null }); return;
            }
            if (scenario == "unknown-source" || scenario == "read-mismatch" || scenario == "metadata-error")
            { var row = Row(FixtureId); row["threadSource"] = null; Reply(id, new { data = new[] { row }, nextCursor = (string)null }); return; }
            var results = requestedArchived == archived ? new[] { Row(FixtureId) } : new Dictionary<string, object>[0];
            Reply(id, new { data = results, nextCursor = (string)null });
        }

        private static bool ObjectHasFalseOrMissingTurns(Dictionary<string, object> parameters)
        { object value = Get(parameters, "includeTurns"); return value == null || (value is bool && !(bool)value); }

        private static void Read(object id, Dictionary<string, object> parameters)
        {
            if (!(Get(parameters, "includeTurns") is bool) || (bool)Get(parameters, "includeTurns")) { Error(id, "Reading conversation bodies is forbidden in this test"); return; }
            string threadId = Text(parameters, "threadId");
            if (scenario == "not-found" || threadId == "read-deleted") { Error(id, "thread not found"); return; }
            if (scenario == "metadata-error" || scenario == "verify-read-error") { Error(id, "synthetic private server details must not leak"); return; }
            var row = Row(threadId);
            if (scenario == "source-filter" && threadId == "read-child") row["threadSource"] = "subAgent";
            if (scenario == "unknown-source") row["threadSource"] = null;
            if (scenario == "read-mismatch") row["id"] = "different-synthetic-id";
            row["path"] = scenario == "fallback" ? null : Path.Combine(home, archived ? "archived_sessions" : "sessions", "fixture.jsonl");
            Reply(id, new { thread = row });
        }

        private static void Mutate(object id, string method, Dictionary<string, object> parameters)
        {
            if (Text(parameters, "threadId") != FixtureId) { Error(id, "Unknown synthetic fixture"); return; }
            if (scenario == "active-writer") { Error(id, "already has an active writer"); return; }
            if (scenario == "refused") { Error(id, "synthetic private server details must not leak"); return; }
            if (scenario == "server-exit") Environment.Exit(42);
            if (scenario != "unconfirmed" && scenario != "verify-read-error")
            {
                bool next = method == "thread/archive";
                if (next != archived)
                {
                    string source = Path.Combine(home, archived ? "archived_sessions" : "sessions", "fixture.jsonl");
                    string target = Path.Combine(home, next ? "archived_sessions" : "sessions", "fixture.jsonl");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Move(source, target);
                    archived = next;
                }
            }
            if (scenario == "missing-result") { Console.WriteLine(Serializer.Serialize(new { id = id })); return; }
            if (scenario == "wrong-result") { Reply(id, new object[0]); return; }
            Reply(id, new { });
        }
    }
}

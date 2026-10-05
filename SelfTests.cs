using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StreamSwitch {
    internal sealed class FakeTransport : ITransport {
        readonly BlockingCollection<string> incoming = new BlockingCollection<string>();
        public readonly ConcurrentQueue<string> Sent = new ConcurrentQueue<string>();
        public bool Ack = true, Invalid, Bot, RejectPresence;
        public int Interval = 1000;
        public bool Closed;
        public Task Connect(CancellationToken ct) {
            incoming.Add(Protocol.Json(new { op = 10, d = new { heartbeat_interval = Interval } }));
            return Task.FromResult(0);
        }
        public Task<string> Receive(CancellationToken ct) { return Task.Run(() => incoming.Take(ct), ct); }
        public Task Send(string text, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            int op = Convert.ToInt32(Protocol.Read(text)["op"]);
            if (op == 3 && RejectPresence) throw new IOException("Transport test failure");
            Sent.Enqueue(text);
            if (op == 2) {
                if (Invalid) incoming.Add("{\"op\":9,\"d\":false}");
                else incoming.Add(Protocol.Json(new { op = 0, s = 42, t = "READY", d = new { user = new { username = "test-user", bot = Bot } } }));
            }
            if (op == 1 && Ack) incoming.Add("{\"op\":11,\"d\":null}");
            return Task.FromResult(0);
        }
        public void Enqueue(string text) { incoming.Add(text); }
        public Task Close(CancellationToken ct) { Closed = true; return Task.FromResult(0); }
        public void Dispose() { Closed = true; }
    }
    internal static class SelfTests {
        static readonly List<string> report = new List<string>();
        static void Check(bool condition, string label) { if (!condition) throw new Exception(label); report.Add("PASS " + label); }
        static void Reject(Action action, string label) {
            try { action(); } catch (ArgumentException) { report.Add("PASS " + label); return; }
            throw new Exception(label);
        }
        static async Task RejectAsync(Func<Task> action, string label) {
            try { await action(); } catch (GatewayException) { report.Add("PASS " + label); return; }
            throw new Exception(label);
        }
        static async Task Until(Func<bool> condition) {
            for (int i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(30); }
            throw new Exception("Timed out waiting for test event");
        }
        public static int Run(string path) {
            try { RunAsync().GetAwaiter().GetResult(); report.Add("All tests passed. No real Discord connection or credential was used."); File.WriteAllLines(path, report); return 0; }
            catch (Exception ex) { report.Add("FAIL " + ex.Message); File.WriteAllLines(path, report); return 1; }
        }
        static async Task RunAsync() {
            Check(Protocol.StreamUrl("https://www.twitch.tv/example") == "https://www.twitch.tv/example", "Twitch URL accepted");
            Check(Protocol.StreamUrl("https://www.youtube.com/watch?v=abc").Contains("watch"), "YouTube URL accepted");
            foreach (var bad in new[] { "http://twitch.tv/name", "https://twitch.tv.evil.test/name", "https://twitch.tv@evil.test/name", "file:///tmp/a", "https://twitch.tv", "https://twitch.tv:8443/name", "https://person@twitch.tv/name" })
                Reject(() => Protocol.StreamUrl(bad), "Unsafe or invalid URL rejected: " + bad);
            Reject(() => Protocol.Title("a"), "Short title rejected");
            Reject(() => Protocol.Title(new string('x', 129)), "Long title rejected");
            Reject(() => Protocol.Title("bad\ntitle"), "Control characters rejected");
            var payload = Protocol.Map(Protocol.Read(Protocol.Presence(true, "Sesión \"especial\" ñ", "https://twitch.tv/example"))["d"]);
            var activities = (System.Collections.ArrayList)payload["activities"];
            Check(Convert.ToInt32(Protocol.Map(activities[0])["type"]) == 1, "Streaming activity type is 1");
            Check(Convert.ToString(Protocol.Map(activities[0])["name"]) == "Sesión \"especial\" ñ", "Unicode and quotes survive encoding");
            var off = Protocol.Map(Protocol.Read(Protocol.Presence(false, "", ""))["d"]);
            Check(((System.Collections.ArrayList)off["activities"]).Count == 0, "Removal uses empty activities");
            Check(!Protocol.Map(activities[0]).ContainsKey("assets"), "Empty image keeps existing streaming payload");
            Check(Protocol.ImageUrl("   ") == "", "Image is optional");
            var withImage = Protocol.Map(Protocol.Read(Protocol.Presence(true, "Mi directo", "https://twitch.tv/example", "https://example.com/photo.png?size=512"))["d"]);
            var activityWithImage = Protocol.Map(((System.Collections.ArrayList)withImage["activities"])[0]);
            Check(Convert.ToString(Protocol.Map(activityWithImage["assets"])["large_image"]) == "https://example.com/photo.png?size=512", "Image URL and query are included in large_image");
            Check(Convert.ToString(Protocol.Map(activityWithImage["assets"])["large_text"]) == "Mi directo", "Image hover text follows title");
            foreach (var badImage in new[] { "C:\\photo.png", "file:///C:/photo.png", "http://example.com/photo.png", "https://user:secret@example.com/photo.png", "https://localhost/photo.png", "https://127.0.0.1/photo.png" })
                Reject(() => Protocol.ImageUrl(badImage), "Non-public or insecure image rejected: " + badImage);
            Check(!Protocol.Presence(false, "", "", "invalid").Contains("assets"), "Clearing ignores image and removes activity");
            var transport = new FakeTransport();
            using (var session = new Session(transport)) {
                Check(await session.Start("FAKE_TEST_CREDENTIAL") == "test-user", "READY confirms the account name");
                Check(session.Connected, "Connected after READY");
                Check(!transport.Sent.Any(s => Convert.ToInt32(Protocol.Read(s)["op"]) == 3), "Connecting does not publish a presence");
                await session.SetPresence(true, "Mi directo", "https://twitch.tv/example", "https://example.com/photo.png");
                Check(transport.Sent.Any(s => s.Contains("large_image") && s.Contains("https://example.com/photo.png")), "Session transmits configured image");
                Check(session.Active, "Presence send changes local state");
                await RejectAsync(() => session.SetPresence(false, "", ""), "Rapid presence change is limited");
                await Until(() => transport.Sent.Any(s => Convert.ToInt32(Protocol.Read(s)["op"]) == 1));
                Check(transport.Sent.Where(s => Convert.ToInt32(Protocol.Read(s)["op"]) == 1).Any(s => Convert.ToInt64(Protocol.Read(s)["d"]) == 42), "Heartbeat carries last sequence");
                await session.Disconnect();
                Check(!session.Connected && transport.Closed, "Disconnect closes the transport");
                string lastPresence = transport.Sent.Where(s => Convert.ToInt32(Protocol.Read(s)["op"]) == 3).Last();
                Check(((System.Collections.ArrayList)Protocol.Map(Protocol.Read(lastPresence)["d"])["activities"]).Count == 0, "Disconnect removes active presence first");
            }
            using (var session = new Session(new FakeTransport { Invalid = true }))
                await RejectAsync(() => session.Start("FAKE_TEST_CREDENTIAL"), "Invalid session is rejected without retry");
            using (var session = new Session(new FakeTransport { Bot = true }))
                await RejectAsync(() => session.Start("FAKE_TEST_CREDENTIAL"), "Bot token is rejected by personal-account app");
            using (var session = new Session(new FakeTransport { Ack = false, Interval = 100 })) {
                await session.Start("FAKE_TEST_CREDENTIAL"); await Until(() => !session.Connected);
                Check(!session.Connected, "Missing heartbeat acknowledgement ends session");
            }
            using (var session = new Session(new FakeTransport { RejectPresence = true })) {
                await session.Start("FAKE_TEST_CREDENTIAL");
                await RejectAsync(() => session.SetPresence(true, "Mi directo", "https://twitch.tv/example"), "Send failure is reported");
                Check(!session.Connected, "Send failure clears connected state");
            }
            var reconnectTransport = new FakeTransport();
            using (var session = new Session(reconnectTransport)) {
                await session.Start("FAKE_TEST_CREDENTIAL"); reconnectTransport.Enqueue("{\"op\":7,\"d\":null}");
                await Until(() => !session.Connected); Check(!session.Connected, "Reconnect request stops instead of looping");
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StreamSwitch {
    internal sealed class FakeImageAssets : IImageAssets {
        public bool Fail;
        public string LastUrl, LastApplication;
        public readonly List<string> Urls = new List<string>();
        public Task<string> Resolve(string token, string applicationId, string imageUrl, CancellationToken ct) {
            ImageAssets.ApplicationId(applicationId);
            LastUrl = Protocol.ImageUrl(imageUrl); LastApplication = applicationId;
            Urls.Add(imageUrl);
            if (Fail) throw new GatewayException("Simulated asset failure");
            return Task.FromResult(imageUrl == ImageAssets.TwitchIconUrl ? "mp:external/test/twitch.png" : imageUrl == ImageAssets.KickIconUrl ? "mp:external/test/kick.png" : "mp:external/test/https/example.com/photo.png");
        }
    }
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
        static byte[] Capture(System.Windows.Forms.Control control) {
            using(var bitmap=new System.Drawing.Bitmap(control.Width,control.Height)) {
                control.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,control.Width,control.Height));
                using(var memory=new MemoryStream()){bitmap.Save(memory,System.Drawing.Imaging.ImageFormat.Png);return memory.ToArray();}
            }
        }
        static void AnimateFrames() { for(int i=0;i<15;i++){System.Windows.Forms.Application.DoEvents();Thread.Sleep(20);} }
        public static int RunUI(string path) {
            try {
                using(var host=new System.Windows.Forms.Form { ClientSize=new System.Drawing.Size(520,180) })
                using(var hero=new StudioHero()) {
                    host.Controls.Add(hero); host.Show(); System.Windows.Forms.Application.DoEvents();
                    byte[] first=Capture(hero); AnimateFrames(); byte[] second=Capture(hero);
                    Check(!first.SequenceEqual(second),"Hero animation produces distinct frames");
                    hero.Motion=false; first=Capture(hero); AnimateFrames(); second=Capture(hero);
                    Check(first.SequenceEqual(second),"Animation switch freezes the hero");
                }
                using(var picker=new StudioLogoPicker { Size=new System.Drawing.Size(300,34) }) {
                    picker.CreateControl();picker.Selected="Twitch";byte[] twitch=Capture(picker);picker.Selected="Kick";byte[] kick=Capture(picker);
                    Check(!twitch.SequenceEqual(kick),"Logo selector visibly distinguishes Twitch and Kick");
                }
                using(var icon=System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath)) Check(icon!=null,"Executable contains an application icon");
                using(var form=new MainForm()){form.Show();System.Windows.Forms.Application.DoEvents();Check(form.ClientSize.Width>=1100,"Studio window lays out at the intended size");}
                foreach(var control in new System.Windows.Forms.Control[] { new StudioCard(), new StudioField(new System.Windows.Forms.TextBox()), new StudioLogoPicker(), new StudioHero { Motion=false }, new BrandMark(), new AppButton() }) {
                    using(control)
                    using(var host=new System.Windows.Forms.Form { ClientSize=new System.Drawing.Size(850,300) }) {
                        control.Dock=System.Windows.Forms.DockStyle.None;
                        control.Bounds=new System.Drawing.Rectangle(10,10,300,160);
                        host.Controls.Add(control);host.Show();System.Windows.Forms.Application.DoEvents();
                        bool full=false;
                        control.Invalidated+=delegate(object sender,System.Windows.Forms.InvalidateEventArgs e) { if(e.InvalidRect.Contains(control.ClientRectangle))full=true; };
                        foreach(int width in new[]{700,340,760,300}) {
                            full=false;control.Size=new System.Drawing.Size(width,180);
                            Check(full,control.GetType().Name+" invalidates its full surface on resize to "+width);
                            control.Update();System.Windows.Forms.Application.DoEvents();
                        }
                    }
                }
                report.Add("UI smoke checks passed; no credentials or Discord connection used.");File.WriteAllLines(path,report);return 0;
            }catch(Exception ex){report.Add("FAIL "+ex.Message);File.WriteAllLines(path,report);return 1;}
        }
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
            Check(Convert.ToString(Protocol.Map(activities[0])["details"]) == "Sesión \"especial\" ñ", "Unicode and quotes survive encoding in title details");
            Check(Convert.ToString(Protocol.Map(activities[0])["name"]) == "Twitch", "Platform name is separate from title");
            var off = Protocol.Map(Protocol.Read(Protocol.Presence(false, "", ""))["d"]);
            Check(((System.Collections.ArrayList)off["activities"]).Count == 0, "Removal uses empty activities");
            Check(!Protocol.Map(activities[0]).ContainsKey("assets"), "Empty image keeps existing streaming payload");
            Check(Protocol.ImageUrl("   ") == "", "Image is optional");
            var withImage = Protocol.Map(Protocol.Read(Protocol.Presence(true, "My stream", "https://twitch.tv/example", "mp:external/test/https/example.com/photo.png", "123456789012345678"))["d"]);
            var activityWithImage = Protocol.Map(((System.Collections.ArrayList)withImage["activities"])[0]);
            Check(Convert.ToString(Protocol.Map(activityWithImage["assets"])["large_image"]) == "mp:external/test/https/example.com/photo.png", "Converted media proxy asset is included in large_image");
            Check(Convert.ToString(activityWithImage["application_id"]) == "123456789012345678", "Application ID accompanies external image");
            Reject(() => Protocol.Presence(true, "My stream", "https://twitch.tv/example", "https://example.com/photo.png"), "Raw external URL cannot be sent as an asset");
            Check(ImageAssets.ParseResponse("[{\"external_asset_path\":\"external/hash/https/example.com/image.png\"}]") == "mp:external/hash/https/example.com/image.png", "External asset response gets mp prefix");
            await RejectAsync(() => { ImageAssets.ParseResponse("[]"); return Task.FromResult(0); }, "Empty asset response rejected");
            await RejectAsync(() => { ImageAssets.ParseResponse("[{\"external_asset_path\":\"https://example.com/image.png\"}]"); return Task.FromResult(0); }, "Malformed asset response rejected");
            Reject(() => ImageAssets.ApplicationId(""), "Missing application ID is explained");
            Check(ImageAssets.DirectAsset("https://cdn.discordapp.com/attachments/123/456/image.png?ex=abc") == "mp:attachments/123/456/image.png?ex=abc", "Discord CDN URL converts directly and retains parameters");
            Check(ImageAssets.DirectAsset("https://i.postimg.cc/example/image.png") == null, "External host requires conversion");
            Check(!Protocol.Map(activityWithImage["assets"]).ContainsKey("large_text"), "Image hover text does not repeat title");
            var badgePayload = Protocol.Read(Protocol.Presence(true, "My stream", "https://twitch.tv/example", "mp:external/image", "123456789012345678", "mp:external/twitch"));
            var badgeActivity = Protocol.Map(((System.Collections.ArrayList)Protocol.Map(badgePayload["d"])["activities"])[0]);
            Check(Convert.ToString(Protocol.Map(badgeActivity["assets"])["small_image"]) == "mp:external/twitch", "Twitch badge occupies small_image");
            Check(Convert.ToString(Protocol.Map(badgeActivity["assets"])["small_text"]) == "Twitch", "Badge label identifies Twitch");
            Check(Protocol.Json(badgePayload).Split(new[] { "My stream" }, StringSplitOptions.None).Length == 2, "Custom title occurs only once in activity payload");
            Check(!Protocol.Map(activityWithImage["assets"]).ContainsKey("small_image"), "Badge may be omitted independently");
            Check(ImageAssets.LogoUrl("Kick") == ImageAssets.KickIconUrl, "Kick selector uses official Kick icon");
            Check(ImageAssets.LogoUrl("None") == "", "No-logo option has no asset URL");
            Reject(() => ImageAssets.LogoUrl("Other"), "Unknown logo rejected");
            var kickJson = Protocol.Presence(true, "My stream", "https://twitch.tv/example", "mp:external/main", "123456789012345678", "mp:external/kick", "Kick");
            Check(kickJson.Contains("\"small_text\":\"Kick\""), "Kick asset is labelled Kick");
            foreach (var badImage in new[] { "C:\\photo.png", "file:///C:/photo.png", "http://example.com/photo.png", "https://user:secret@example.com/photo.png", "https://localhost/photo.png", "https://127.0.0.1/photo.png" })
                Reject(() => Protocol.ImageUrl(badImage), "Non-public or insecure image rejected: " + badImage);
            Check(!Protocol.Presence(false, "", "", "invalid").Contains("assets"), "Clearing ignores image and removes activity");
            var transport = new FakeTransport();
            var assetResolver = new FakeImageAssets();
            using (var session = new Session(transport, assetResolver)) {
                Check(await session.Start("FAKE_TEST_CREDENTIAL") == "test-user", "READY confirms the account name");
                Check(session.Connected, "Connected after READY");
                Check(!transport.Sent.Any(s => Convert.ToInt32(Protocol.Read(s)["op"]) == 3), "Connecting does not publish a presence");
                await session.SetPresence(true, "My stream", "https://twitch.tv/example", "https://example.com/photo.png", "123456789012345678");
                Check(transport.Sent.Any(s => s.Contains("large_image") && s.Contains("mp:external/test/https/example.com/photo.png")), "Session sends converted image instead of raw URL");
                Check(assetResolver.LastApplication == "123456789012345678" && assetResolver.Urls.Contains("https://example.com/photo.png"), "Session resolves the selected image with the selected application");
                Check(assetResolver.Urls.Contains(ImageAssets.TwitchIconUrl), "Session resolves the padded Twitch icon");
                Check(transport.Sent.Any(s => s.Contains("small_image") && s.Contains("mp:external/test/twitch.png")), "Session transmits the badge independently of the main image");
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
                await RejectAsync(() => session.SetPresence(true, "My stream", "https://twitch.tv/example"), "Send failure is reported");
                Check(!session.Connected, "Send failure clears connected state");
            }
            var failedAssetTransport = new FakeTransport();
            using (var session = new Session(failedAssetTransport, new FakeImageAssets { Fail = true })) {
                await session.Start("FAKE_TEST_CREDENTIAL");
                await RejectAsync(() => session.SetPresence(true, "My stream", "https://twitch.tv/example", "https://example.com/photo.png", "123456789012345678"), "Asset error is returned before presence send");
                Check(session.Connected, "Asset error preserves the working connection");
                Check(!failedAssetTransport.Sent.Any(s => Convert.ToInt32(Protocol.Read(s)["op"]) == 3), "Failed conversion does not silently send broken image");
            }
            var reconnectTransport = new FakeTransport();
            foreach (string choice in new[] { "Kick", "None" }) {
                var logoTransport = new FakeTransport(); var logoAssets = new FakeImageAssets();
                using (var session = new Session(logoTransport, logoAssets)) {
                    await session.Start("FAKE_TEST_CREDENTIAL");
                    await session.SetPresence(true, "My stream", "https://twitch.tv/example", "https://example.com/photo.png", "123456789012345678", choice);
                    var sent = logoTransport.Sent.Last(s => Convert.ToInt32(Protocol.Read(s)["op"]) == 3);
                    Check(choice == "Kick" ? sent.Contains("mp:external/test/kick.png") && !logoAssets.Urls.Contains(ImageAssets.TwitchIconUrl) : !sent.Contains("small_image") && logoAssets.Urls.Count == 1, "Selected logo only: " + choice);
                    Check(sent.Contains("mp:external/test/https/example.com/photo.png"), "Main image preserved with logo: " + choice);
                }
            }
            using (var session = new Session(reconnectTransport)) {
                await session.Start("FAKE_TEST_CREDENTIAL"); reconnectTransport.Enqueue("{\"op\":7,\"d\":null}");
                await Until(() => !session.Connected); Check(!session.Connected, "Reconnect request stops instead of looping");
            }
        }
    }
}

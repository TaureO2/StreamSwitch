using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace StreamSwitch {
    internal static class Protocol {
        public static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
        public static Dictionary<string,object> Read(string value) {
            return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<Dictionary<string,object>>(value);
        }
        public static Dictionary<string,object> Map(object value) { return (Dictionary<string,object>)value; }
        public static string StreamUrl(string value) {
            Uri uri;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) || uri.Scheme != "https" ||
                !String.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || value.Length > 512)
                throw new ArgumentException("Use an HTTPS link from Twitch or YouTube.");
            var host = uri.DnsSafeHost.ToLowerInvariant();
            var allowed = new[] { "twitch.tv", "www.twitch.tv", "youtube.com", "www.youtube.com", "m.youtube.com", "youtu.be" };
            if (!allowed.Contains(host) || uri.AbsolutePath.Trim('/').Length == 0)
                throw new ArgumentException("Enter the full link to a Twitch or YouTube channel or video.");
            return uri.AbsoluteUri;
        }
        public static string Title(string value) {
            value = value.Trim();
            if (value.Length < 2 || value.Length > 128 || value.Any(Char.IsControl))
                throw new ArgumentException("The title must contain 2 to 128 characters with no line breaks.");
            return value;
        }
        public static string Identify(string token) {
            return Json(new { op = 2, d = new { token = token, compress = false,
                properties = new { os = "Windows", browser = "StreamSwitch", device = "StreamSwitch" } } });
        }
        public static string ImageUrl(string value) {
            if (String.IsNullOrWhiteSpace(value)) return "";
            Uri uri;
            if (value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) ||
                uri.Scheme != "https" || !String.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
                uri.IsLoopback || uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains(".") || uri.Host.EndsWith(".local"))
                throw new ArgumentException("The image needs a public HTTPS link, not a file on your PC.");
            return uri.AbsoluteUri;
        }
        public static string Presence(bool active, string title, string url, string imageUrl = "", string applicationId = "", string smallImage = "", string badgeLabel = "Twitch") {
            object[] activities = new object[0];
            if (active) {
                string streamUrl = StreamUrl(url);
                string platform = new Uri(streamUrl).Host.EndsWith("twitch.tv") ? "Twitch" : "YouTube";
                var activity = new Dictionary<string, object> { { "name", platform }, { "details", Title(title) }, { "type", 1 }, { "url", streamUrl } };
                if (!String.IsNullOrEmpty(imageUrl)) {
                    if (!imageUrl.StartsWith("mp:") || imageUrl.Length > 2051 || imageUrl.Any(Char.IsControl))
                        throw new ArgumentException("The image has not been prepared as a Discord asset yet.");
                    var assets = new Dictionary<string, object> { { "large_image", imageUrl } };
                    if (!String.IsNullOrEmpty(smallImage)) {
                        if (!smallImage.StartsWith("mp:") || smallImage.Length > 2051 || smallImage.Any(Char.IsControl))
                            throw new ArgumentException("The logo is not ready for Discord yet.");
                        assets["small_image"] = smallImage;
                        ImageAssets.LogoUrl(badgeLabel);
                        assets["small_text"] = badgeLabel;
                    }
                    activity["assets"] = assets;
                    if (!String.IsNullOrWhiteSpace(applicationId)) activity["application_id"] = ImageAssets.ApplicationId(applicationId);
                }
                activities = new object[] { activity };
            }
            return Json(new { op = 3, d = new { since = (object)null, activities = activities, status = "online", afk = false } });
        }
        public static string CloseMessage(int code) {
            if (code == 4004) return "Discord rejected authentication. Check the token in the app.";
            if (code == 4008) return "Discord rate-limited requests. Wait before reconnecting.";
            if (code == 4009) return "The session expired. Reconnect.";
            return "Discord closed the connection (" + code + "). The status may take a moment to disappear.";
        }
    }

    internal interface ITransport : IDisposable {
        Task Connect(CancellationToken ct);
        Task<string> Receive(CancellationToken ct);
        Task Send(string text, CancellationToken ct);
        Task Close(CancellationToken ct);
    }
    internal sealed class DiscordTransport : ITransport {
        readonly ClientWebSocket socket = new ClientWebSocket();
        public Task Connect(CancellationToken ct) {
            return socket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), ct);
        }
        public async Task<string> Receive(CancellationToken ct) {
            byte[] buffer = new byte[16384];
            using (var stream = new MemoryStream()) {
                WebSocketReceiveResult part;
                do {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (part.MessageType == WebSocketMessageType.Close)
                        throw new GatewayException(Protocol.CloseMessage((int)(part.CloseStatus ?? WebSocketCloseStatus.Empty)));
                    if (part.MessageType != WebSocketMessageType.Text)
                        throw new GatewayException("Discord sent an unsupported format.");
                    stream.Write(buffer, 0, part.Count);
                    if (stream.Length > 8 * 1024 * 1024)
                        throw new GatewayException("The Discord response exceeds the allowed size.");
                } while (!part.EndOfMessage);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        public Task Send(string text, CancellationToken ct) {
            var data = Encoding.UTF8.GetBytes(text);
            if (data.Length > 4096) throw new GatewayException("The status is too long.");
            return socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, ct);
        }
        public Task Close(CancellationToken ct) {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                return socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct);
            return Task.FromResult(0);
        }
        public void Dispose() { socket.Dispose(); }
    }
    internal sealed class GatewayException : Exception {
        public GatewayException(string text) : base(text) { }
    }
    internal sealed class Session : IDisposable {
        readonly ITransport transport;
        readonly IImageAssets imageAssets;
        string sessionToken;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        readonly TaskCompletionSource<string> ready = new TaskCompletionSource<string>();
        long sequence = -1;
        int awaitingAck;
        int stopped;
        bool hello;
        bool active;
        DateTime lastPresence = DateTime.MinValue;
        Task reader;
        Task heartbeat;
        public event Action<string> Disconnected;
        public bool Connected { get; private set; }
        public bool Active { get { return active; } }
        public Session(ITransport transport, IImageAssets imageAssets = null) { this.transport = transport; this.imageAssets = imageAssets ?? new ImageAssets(); }
        public async Task<string> Start(string token) {
            if (String.IsNullOrWhiteSpace(token) || token.Length > 2048 || token.Any(Char.IsWhiteSpace))
                throw new ArgumentException("Enter your personal token without spaces or quotes.");
            try {
                sessionToken = token;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)) {
                    timeout.CancelAfter(20000);
                    await transport.Connect(timeout.Token).ConfigureAwait(false);
                }
                reader = ReadLoop(token);
                var completed = await Task.WhenAny(ready.Task, Task.Delay(20000, lifetime.Token)).ConfigureAwait(false);
                if (completed != ready.Task) throw new GatewayException("Discord did not confirm the connection. Try again later.");
                return await ready.Task.ConfigureAwait(false);
            } catch (Exception ex) {
                string message = SafeError(ex);
                Stop(message);
                throw new GatewayException(message);
            }
        }
        static string SafeError(Exception ex) {
            if (ex is GatewayException) return ex.Message;
            if (ex is OperationCanceledException) return "The connection was canceled or timed out.";
            return "Could not maintain the Discord connection. Check your Internet connection and reconnect.";
        }
        async Task ReadLoop(string token) {
            try {
                while (!lifetime.IsCancellationRequested) {
                    var packet = Protocol.Read(await transport.Receive(lifetime.Token).ConfigureAwait(false));
                    if (packet.ContainsKey("s") && packet["s"] != null) Interlocked.Exchange(ref sequence, Convert.ToInt64(packet["s"]));
                    int op = Convert.ToInt32(packet["op"]);
                    if (op == 10) {
                        if (hello) throw new GatewayException("Discord restarted the session. Reconnect.");
                        hello = true;
                        int interval = Convert.ToInt32(Protocol.Map(packet["d"])["heartbeat_interval"]);
                        if (interval < 100 || interval > 300000) throw new GatewayException("Invalid connection interval.");
                        heartbeat = HeartbeatLoop(interval);
                        await Send(Protocol.Identify(token)).ConfigureAwait(false);
                        token = null;
                    } else if (op == 11) {
                        Interlocked.Exchange(ref awaitingAck, 0);
                    } else if (op == 1) {
                        await SendHeartbeat().ConfigureAwait(false);
                    } else if (op == 7 || op == 9) {
                        throw new GatewayException(op == 9 ? "Discord rejected the session. It will not retry automatically." : "Discord requires a new connection. Click Connect.");
                    } else if (op == 0 && packet.ContainsKey("t") && Convert.ToString(packet["t"]) == "READY") {
                        var user = Protocol.Map(Protocol.Map(packet["d"])["user"]);
                        if (user.ContainsKey("bot") && Convert.ToBoolean(user["bot"])) throw new GatewayException("This version is designed for a personal account, not a bot.");
                        lifetime.Token.ThrowIfCancellationRequested();
                        Connected = true;
                        ready.TrySetResult(Convert.ToString(user["username"]));
                    }
                    // Other events are discarded and never written to disk or shown in the UI.
                }
            } catch (Exception ex) { if (!lifetime.IsCancellationRequested) Stop(SafeError(ex)); }
        }
        async Task HeartbeatLoop(int interval) {
            try {
                await Task.Delay(new Random().Next(0, interval), lifetime.Token).ConfigureAwait(false);
                while (!lifetime.IsCancellationRequested) {
                    if (Interlocked.CompareExchange(ref awaitingAck, 0, 0) != 0)
                        throw new GatewayException("Discord stopped responding. Reconnect; the status may take a moment to disappear.");
                    await SendHeartbeat().ConfigureAwait(false);
                    await Task.Delay(interval, lifetime.Token).ConfigureAwait(false);
                }
            } catch (Exception ex) { if (!lifetime.IsCancellationRequested) Stop(SafeError(ex)); }
        }
        Task SendHeartbeat() {
            Interlocked.Exchange(ref awaitingAck, 1);
            long seq = Interlocked.Read(ref sequence);
            return Send(Protocol.Json(new { op = 1, d = seq < 0 ? (object)null : seq }));
        }
        async Task Send(string text) {
            await sendLock.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)) {
                    timeout.CancelAfter(10000);
                    await transport.Send(text, timeout.Token).ConfigureAwait(false);
                }
            } finally { sendLock.Release(); }
        }
        public async Task SetPresence(bool value, string title, string url, string imageUrl = "", string applicationId = "", string logoChoice = "Twitch") {
            if (!Connected) throw new GatewayException("Connect to Discord first.");
            if ((DateTime.UtcNow - lastPresence).TotalSeconds < 6)
                throw new GatewayException("Wait a few seconds before making another change.");
            string asset = "", badge = "";
            if (value) {
                Protocol.Title(title); Protocol.StreamUrl(url);
                string logoUrl = ImageAssets.LogoUrl(logoChoice);
                if (!String.IsNullOrWhiteSpace(imageUrl)) {
                    asset = await imageAssets.Resolve(sessionToken, applicationId, imageUrl, lifetime.Token).ConfigureAwait(false);
                    if (logoUrl.Length > 0) badge = await imageAssets.Resolve(sessionToken, applicationId, logoUrl, lifetime.Token).ConfigureAwait(false);
                }
            }
            string payload = Protocol.Presence(value, title, url, asset, applicationId, badge, logoChoice);
            try {
                await Send(payload).ConfigureAwait(false);
                active = value;
                lastPresence = DateTime.UtcNow;
            } catch (Exception ex) { Stop(SafeError(ex)); throw new GatewayException(SafeError(ex)); }
        }
        public async Task Disconnect() {
            try {
                if (Connected && active) {
                    var wait = 6 - (DateTime.UtcNow - lastPresence).TotalSeconds;
                    if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), lifetime.Token).ConfigureAwait(false);
                    await Send(Protocol.Presence(false, "", "")).ConfigureAwait(false);
                    active = false;
                }
                using (var timeout = new CancellationTokenSource(2000)) {
                    await sendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
                    try { await transport.Close(timeout.Token).ConfigureAwait(false); }
                    finally { sendLock.Release(); }
                }
            } catch { /* Closing the transport also ends this presence session. */ }
            finally { Stop(null); }
        }
        void Stop(string error) {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            Connected = false;
            sessionToken = null;
            active = false;
            lifetime.Cancel();
            transport.Dispose();
            ready.TrySetException(new GatewayException(error ?? "Connection closed."));
            var handler = Disconnected;
            if (handler != null) handler(error);
        }
        public void Dispose() { Stop(null); }
    }

    internal sealed class MainForm : Form {
        static readonly Color Bg = Color.FromArgb(12, 14, 23);
        static readonly Color Card = Color.FromArgb(22, 25, 38);
        static readonly Color Muted = Color.FromArgb(169, 177, 197);
        static readonly Color Purple = Color.FromArgb(141, 94, 255);
        readonly TextBox token = Input(true), title = Input(false), url = Input(false), imageUrl = Input(false), applicationId = Input(false);
        readonly PictureBox picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(35, 39, 54), Margin = new Padding(0, 4, 0, 8) };
        readonly Button loadImage = Button("View image", Color.FromArgb(49, 54, 72));
        readonly ComboBox logoChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, BackColor = Color.White, ForeColor = Color.Black, AccessibleName = "Small logo" };
        readonly PictureBox logoPreview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(5, 0, 0, 0), AccessibleName = "Logo preview" };
        readonly Label connection = Label("DISCONNECTED", 10, Muted);
        readonly Label account = Label("Your Discord account", 17, Color.White);
        readonly Label previewTitle = Label("My stream", 14, Color.White);
        readonly Label previewUrl = Label("twitch.tv / youtube.com", 10, Muted);
        readonly Label status = Label("Connect your account to get started.", 10, Muted);
        readonly Label activityState = Label("PREVIEW · NOT PUBLISHED", 9, Muted);
        readonly Button connect = Button("Connect", Purple);
        readonly Button activate = Button("Enable streaming", Purple);
        readonly Button deactivate = Button("Remove status", Color.FromArgb(49, 54, 72));
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 200 };
        readonly StudioHero hero = new StudioHero();
        readonly StudioLogoPicker logoPicker = new StudioLogoPicker();
        readonly bool animations = true;
        readonly string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preferencias.json");
        Session session;
        bool busy, closing;
        DateTime nextChange = DateTime.MinValue;
        public MainForm() {
            Text = "StreamSwitch";
            ClientSize = new Size(1140, 900);
            MinimumSize = new Size(1100, 940);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            DoubleBuffered = true;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32), ColumnCount = 1, RowCount = 4 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            Controls.Add(root);
            var heading = new Panel { Dock = DockStyle.Fill };
            var mark = new BrandMark { Location = new Point(0,3), Size = new Size(52,52) }; heading.Controls.Add(mark);
            var brand = Label("StreamSwitch", 25, Color.White); brand.Dock = DockStyle.None; brand.Location = new Point(68, -2); brand.Size = new Size(450, 46);
            var subtitle = Label("Presence tool for Discord", 10, Muted); subtitle.Dock = DockStyle.None; subtitle.Location = new Point(70, 47); subtitle.Size = new Size(500, 28);
            heading.Controls.Add(brand); heading.Controls.Add(subtitle); root.Controls.Add(heading, 0, 0);
            var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            root.Controls.Add(columns, 0, 1);
            var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 17, BackColor = Card, Padding = new Padding(0), Margin = Padding.Empty };
            int[] heights = { 37, 23, 42, 35, 42, 23, 42, 23, 42, 23, 42, 35, 23, 42, 34, 55 };
            for (int i = 0; i < heights.Length; i++) form.RowStyles.Add(new RowStyle(SizeType.Absolute, heights[i]));
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            form.Controls.Add(Label("01    YOUR SETTINGS", 11, Color.FromArgb(195,180,247)), 0, 0);
            form.Controls.Add(Label("Account token", 10, Color.White), 0, 1);
            form.Controls.Add(new StudioField(token), 0, 2); token.AccessibleName = "Personal Discord token"; token.MaxLength = 2048;
            form.Controls.Add(Label("Kept in memory only. Cleared from the field on connection.\nDo not share it in chat.", 9, Muted), 0, 3);
            form.Controls.Add(connect, 0, 4);
            form.Controls.Add(Label("Stream title", 10, Color.White), 0, 5);
            form.Controls.Add(new StudioField(title), 0, 6); title.AccessibleName = "Stream title"; title.MaxLength = 128; title.Text = "My stream";
            form.Controls.Add(Label("Twitch or YouTube link", 10, Color.White), 0, 7);
            form.Controls.Add(new StudioField(url), 0, 8); url.AccessibleName = "Stream link"; url.MaxLength = 512;
            form.Controls.Add(Label("Activity image (optional)", 10, Color.White), 0, 9);
            var imageRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            imageRow.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            imageRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); imageRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
            imageRow.Controls.Add(new StudioField(imageUrl), 0, 0); imageRow.Controls.Add(loadImage, 1, 0);
            imageUrl.AccessibleName = "Public image link"; imageUrl.MaxLength = 2048;
            form.Controls.Add(imageRow, 0, 10);
            form.Controls.Add(Label("Paste a direct link to a public image.\nLeave blank to use the default icon.", 9, Muted), 0, 11);
            form.Controls.Add(Label("Application ID for external images", 10, Color.White), 0, 12);
            form.Controls.Add(new StudioField(applicationId), 0, 13); applicationId.MaxLength = 20; applicationId.AccessibleName = "Public Application ID of your Discord application";
            var logoRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            logoRow.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            logoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106)); logoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); logoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            logoRow.Controls.Add(Label("Small logo", 10, Color.White), 0, 0); logoRow.Controls.Add(logoPicker, 1, 0); logoRow.Controls.Add(logoPreview, 2, 0);
            logoPicker.SelectionChanged += delegate { logoChoice.SelectedItem=logoPicker.Selected; };
            logoChoice.Items.AddRange(new object[] { "Twitch", "Kick", "None" }); logoChoice.SelectedIndex = 0;
            logoChoice.DrawMode = DrawMode.OwnerDrawFixed;
            logoChoice.FlatStyle = FlatStyle.Flat;
            logoChoice.BackColor = Color.FromArgb(29,33,49); logoChoice.ForeColor = Color.White;
            logoChoice.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                using (var background = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Color.FromArgb(77,56,124) : Color.FromArgb(29,33,49))) e.Graphics.FillRectangle(background,e.Bounds);
                if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, Convert.ToString(logoChoice.Items[e.Index]), logoChoice.Font, e.Bounds,
                    Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                e.DrawFocusRectangle();
            };
            logoChoice.SelectedIndexChanged += delegate { UpdateLogoPreview(); };
            form.Controls.Add(logoRow, 0, 14);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 12, 0, 0) };
            actions.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            actions.Controls.Add(activate, 0, 0); actions.Controls.Add(deactivate, 1, 0); form.Controls.Add(actions, 0, 15);
            var formCard = new StudioCard { Dock = DockStyle.Fill, Padding = new Padding(24), Margin = new Padding(0,0,18,0) }; formCard.Controls.Add(form); columns.Controls.Add(formCard, 0, 0);
            var right = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=2, Margin=Padding.Empty };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute,184)); right.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            hero.Margin = new Padding(0,0,0,16); right.Controls.Add(hero,0,0);
            var previewCard = new StudioCard { Dock=DockStyle.Fill, Padding=new Padding(24), Margin=Padding.Empty };
            var preview = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=8, BackColor=Card, Margin=Padding.Empty };
            int[] pHeights = { 24, 32, 132, 26, 40, 30, 47 };
            foreach (int h in pHeights) preview.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
            preview.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            connection.Font = new Font("Segoe UI",9,FontStyle.Bold);
            preview.Controls.Add(connection,0,0); preview.Controls.Add(account,0,1);
            picture.BackColor=Color.FromArgb(29,33,49);
            preview.Controls.Add(picture,0,2); preview.Controls.Add(activityState,0,3);
            previewTitle.Font = new Font("Segoe UI",15,FontStyle.Bold);
            preview.Controls.Add(previewTitle,0,4); preview.Controls.Add(previewUrl,0,5);
            preview.Controls.Add(Label("PREVIEW\nDiscord may display the card differently.",9,Muted),0,6);
            preview.Controls.Add(Label("Enabling or removing presence sets your session to Online.",9,Muted),0,7);
            previewCard.Controls.Add(preview); right.Controls.Add(previewCard,0,1); columns.Controls.Add(right,1,0);

            var notice = Label("UNOFFICIAL CONNECTION\nAutomating a personal account violates Discord rules and may result in account suspension.", 10, Color.FromArgb(235, 190, 121));
            notice.Margin = new Padding(0, 18, 0, 0); root.Controls.Add(notice, 0, 2);
            status.Padding = new Padding(0, 8, 0, 0); root.Controls.Add(status, 0, 3);
            title.TextChanged += delegate { previewTitle.Text = title.Text.Trim().Length == 0 ? "My stream" : title.Text; };
            url.TextChanged += delegate { previewUrl.Text = url.Text.Trim().Length == 0 ? "twitch.tv / youtube.com" : url.Text; };
            imageUrl.TextChanged += delegate { var old = picture.Image; picture.Image = null; if (old != null) old.Dispose(); };
            loadImage.Click += async delegate { await LoadImage(); };
            connect.Click += async delegate { await ConnectClicked(); };
            activate.Click += async delegate { await ChangePresence(true); };
            deactivate.Click += async delegate { await ChangePresence(false); };
            timer.Tick += delegate { UpdateButtons(); hero.Live = session != null && session.Connected && session.Active; }; timer.Start();
            FormClosing += OnClosing;
            LoadPreferences(); UpdateLogoPreview(); UpdateButtons();
            var fade = new System.Windows.Forms.Timer { Interval=16 };
            fade.Tick += delegate { Opacity=Math.Min(1,Opacity+.09); if(Opacity>=1)fade.Stop(); };
            Shown += delegate { if(animations && !Environment.GetCommandLineArgs().Contains("--preview")){Opacity=.1;fade.Start();} };
            FormClosed += delegate { fade.Dispose(); timer.Dispose(); if(picture.Image!=null)picture.Image.Dispose(); if(logoPreview.Image!=null)logoPreview.Image.Dispose(); };
        }
        void UpdateLogoPreview() {
            var old = logoPreview.Image; logoPreview.Image = null; if (old != null) old.Dispose();
            string selected = Convert.ToString(logoChoice.SelectedItem);
            logoPicker.Selected=selected;logoPicker.Invalidate();
            if (selected == "None") return;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logos", selected.ToLowerInvariant() + ".png");
            try { using (var source = Image.FromFile(path)) logoPreview.Image = new Bitmap(source); } catch { }
        }
        static Label Label(string text, float size, Color color) {
            return new Label { Text = text, Dock = DockStyle.Fill, Font = new Font("Segoe UI", size), ForeColor = color, AutoEllipsis = true, Margin = Padding.Empty };
        }
        static TextBox Input(bool secret) {
            return new TextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(35, 39, 54), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 11), UseSystemPasswordChar = secret, Margin = new Padding(0, 0, 0, 6), ShortcutsEnabled = true };
        }
        static Button Button(string text, Color color) {
            var b = new AppButton { Text = text, Dock = DockStyle.Fill, BackColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 6, 0) };
            b.FlatAppearance.BorderSize = 0; return b;
        }
        void UpdateButtons() {
            bool connected = session != null && session.Connected;
            connect.Text = connected ? "Disconnect" : "Connect";
            connect.Enabled = !busy;
            token.Enabled = !busy && !connected;
            activate.Enabled = deactivate.Enabled = connected && !busy && DateTime.UtcNow >= nextChange;
            if (!connected) { connection.Text = busy ? "CONNECTING…" : "DISCONNECTED"; connection.ForeColor = Muted; }
        }
        void ShowStatus(string text, bool error) { status.Text = text; status.ForeColor = error ? Color.FromArgb(255, 164, 164) : Muted; }
        async Task LoadImage() {
            loadImage.Enabled = false;
            string selected = imageUrl.Text;
            try {
                string address = Protocol.ImageUrl(selected);
                if (address.Length == 0) { ShowStatus("Paste a direct image link first.", false); return; }
                ShowStatus("Loading preview…", false);
                var request = (HttpWebRequest)WebRequest.Create(address);
                request.AllowAutoRedirect = false;
                request.UseDefaultCredentials = false;
                using (var timeout = new CancellationTokenSource(10000))
                using (timeout.Token.Register(request.Abort))
                using (var response = (HttpWebResponse)await request.GetResponseAsync()) {
                    if (response.StatusCode != HttpStatusCode.OK || !response.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("The link must open an image directly, not a webpage.");
                    if (response.ContentLength > 5 * 1024 * 1024) throw new ArgumentException("Use an image smaller than 5 MB for the preview.");
                    using (var source = response.GetResponseStream())
                    using (var memory = new MemoryStream()) {
                        var buffer = new byte[8192]; int count;
                        while ((count = await source.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) > 0) {
                            if (memory.Length + count > 5 * 1024 * 1024) throw new ArgumentException("Use an image smaller than 5 MB for the preview.");
                            memory.Write(buffer, 0, count);
                        }
                        memory.Position = 0;
                        using (var decoded = Image.FromStream(memory)) {
                            if (decoded.Width > 4096 || decoded.Height > 4096) throw new ArgumentException("Use an image up to 4096 × 4096 pixels.");
                            if (closing || imageUrl.Text != selected) return;
                            var old = picture.Image; picture.Image = new Bitmap(decoded); if (old != null) old.Dispose();
                        }
                    }
                }
                ShowStatus("Preview loaded. Click Enable streaming to send the image as well.", false);
            } catch (Exception ex) {
                if (!closing && imageUrl.Text == selected) ShowStatus(ex is ArgumentException ? ex.Message : "Could not load the image. Try a direct PNG, JPG or GIF link.", true);
            } finally { if (!IsDisposed) loadImage.Enabled = true; }
        }
        async Task ConnectClicked() {
            busy = true; UpdateButtons();
            try {
                if (session != null && session.Connected) {
                    var old = session; session = null;
                    ShowStatus("Removing status and closing the connection…", false);
                    await old.Disconnect(); old.Dispose();
                    account.Text = "Your Discord account"; activityState.Text = "PREVIEW · NOT PUBLISHED";
                    ShowStatus("Disconnected. Discord may take a few seconds to remove your presence.", false);
                } else {
                    string secret = token.Text.Trim();
                    if (secret.Length == 0) throw new ArgumentException("Enter your token in the app field to connect.");
                    if (session != null) session.Dispose();
                    var current = new Session(new DiscordTransport()); session = current;
                    current.Disconnected += message => {
                        if (IsDisposed || !IsHandleCreated) return;
                        try { BeginInvoke(new Action(delegate {
                            if (!Object.ReferenceEquals(session, current) || closing) return;
                            connection.Text = "DISCONNECTED"; activityState.Text = "OFFLINE · STATUS NOT VERIFIED";
                            account.Text = "Your Discord account";
                            if (message != null) ShowStatus(message, true);
                            UpdateButtons();
                        })); } catch (InvalidOperationException) { }
                    };
                    token.Clear(); ShowStatus("Connecting to Discord…", false);
                    var start = current.Start(secret); secret = null;
                    string username = await start;
                    account.Text = username; connection.Text = "CONNECTED"; connection.ForeColor = Color.FromArgb(110, 219, 171);
                    ShowStatus("Connected. Enter a title and link to enable streaming.", false);
                }
            } catch (Exception ex) { ShowStatus(ex is ArgumentException || ex is GatewayException ? ex.Message : "Could not connect.", true); }
            finally { busy = false; UpdateButtons(); }
        }
        async Task ChangePresence(bool active) {
            busy = true; UpdateButtons();
            try {
                if (session == null) throw new GatewayException("Connect to Discord first.");
                ShowStatus(active && imageUrl.Text.Trim().Length > 0 ? "Preparing the image in Discord…" : "Sending status…", false);
                await session.SetPresence(active, title.Text, url.Text, imageUrl.Text, applicationId.Text, Convert.ToString(logoChoice.SelectedItem));
                nextChange = DateTime.UtcNow.AddSeconds(6);
                activityState.Text = active ? "REQUEST SENT · NOT VERIFIED" : "REMOVAL REQUESTED";
                ShowStatus(active ? "Status sent. Check from another account; Discord does not confirm how it appears." : "Removal request sent. Your session remains online.", false);
                SavePreferences();
            } catch (Exception ex) { ShowStatus(ex is ArgumentException || ex is GatewayException ? ex.Message : "Could not change the status.", true); }
            finally { busy = false; UpdateButtons(); }
        }
        void LoadPreferences() {
            try {
                if (!File.Exists(settingsPath)) return;
                var saved = Protocol.Read(File.ReadAllText(settingsPath)); if (saved.ContainsKey("logoChoice") && Convert.ToString(saved["logoChoice"]) == "Ninguno") saved["logoChoice"] = "None";
                title.Text = Protocol.Title(Convert.ToString(saved["title"]));
                url.Text = Protocol.StreamUrl(Convert.ToString(saved["url"]));
                if (saved.ContainsKey("imageUrl")) imageUrl.Text = Protocol.ImageUrl(Convert.ToString(saved["imageUrl"]));
                if (saved.ContainsKey("applicationId")) applicationId.Text = Convert.ToString(saved["applicationId"]);
                if (saved.ContainsKey("logoChoice") && logoChoice.Items.Contains(Convert.ToString(saved["logoChoice"]))) logoChoice.SelectedItem = Convert.ToString(saved["logoChoice"]);
                else if (saved.ContainsKey("showTwitchBadge")) logoChoice.SelectedItem = Convert.ToBoolean(saved["showTwitchBadge"]) ? "Twitch" : "None";
            } catch { }
        }
        void SavePreferences() {
            try { File.WriteAllText(settingsPath, Protocol.Json(new { title = title.Text.Trim(), url = url.Text.Trim(), imageUrl = imageUrl.Text.Trim(), applicationId = applicationId.Text.Trim(), logoChoice = Convert.ToString(logoChoice.SelectedItem) }), Encoding.UTF8); }
            catch { ShowStatus(status.Text + " Could not save the title and link.", false); }
        }
        async void OnClosing(object sender, FormClosingEventArgs e) {
            if (closing) return;
            e.Cancel = true; closing = true; busy = true; timer.Stop(); UpdateButtons();
            token.Clear(); ShowStatus("Closing the session and removing the status…", false);
            try { if (session != null) { await session.Disconnect(); session.Dispose(); } }
            finally { BeginInvoke(new Action(Close)); }
        }
    }

    internal static class Program {
        [STAThread]
        static int Main(string[] args) {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if(args.Contains("--make-icon")){StudioArt.SaveIcon(args[1]);return 0;}
            if (args.Contains("--self-test")) return SelfTests.Run(args.Length > 1 ? args[1] : "tests.txt");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if(args.Contains("--ui-test"))return SelfTests.RunUI(args[1]);
            if (args.Contains("--preview")) {
                using (var preview = new MainForm()) {
                    preview.Show(); Application.DoEvents();
                    using (var bitmap = new Bitmap(preview.Width, preview.Height)) { preview.DrawToBitmap(bitmap, new Rectangle(0, 0, preview.Width, preview.Height)); bitmap.Save(args[1]); }
                }
                return 0;
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\StreamSwitch-Personal", out created)) {
                if (!created) { MessageBox.Show("StreamSwitch is already open.", "StreamSwitch"); return 0; }
                using (var form = new MainForm()) {
                    if (args.Contains("--preview")) {
                        form.Show(); Application.DoEvents();
                        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(args[1]); }
                        return 0;
                    }
                    Application.Run(form);
                }
            }
            return 0;
        }
    }
}

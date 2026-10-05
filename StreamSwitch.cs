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
                throw new ArgumentException("Usa un enlace https de Twitch o YouTube.");
            var host = uri.DnsSafeHost.ToLowerInvariant();
            var allowed = new[] { "twitch.tv", "www.twitch.tv", "youtube.com", "www.youtube.com", "m.youtube.com", "youtu.be" };
            if (!allowed.Contains(host) || uri.AbsolutePath.Trim('/').Length == 0)
                throw new ArgumentException("Introduce el enlace completo de un canal o vídeo de Twitch o YouTube.");
            return uri.AbsoluteUri;
        }
        public static string Title(string value) {
            value = value.Trim();
            if (value.Length < 2 || value.Length > 128 || value.Any(Char.IsControl))
                throw new ArgumentException("El título debe tener entre 2 y 128 caracteres, sin saltos de línea.");
            return value;
        }
        public static string Identify(string token) {
            return Json(new { op = 2, d = new { token = token, compress = false,
                properties = new { os = "Windows", browser = "StreamSwitch", device = "StreamSwitch" } } });
        }
        public static string Presence(bool active, string title, string url) {
            object[] activities = active ? new object[] { new { name = Title(title), type = 1, url = StreamUrl(url) } } : new object[0];
            return Json(new { op = 3, d = new { since = (object)null, activities = activities, status = "online", afk = false } });
        }
        public static string CloseMessage(int code) {
            if (code == 4004) return "Discord ha rechazado la autenticación. Revisa el token en la app.";
            if (code == 4008) return "Discord ha limitado las solicitudes. Espera antes de volver a conectar.";
            if (code == 4009) return "La sesión ha caducado. Vuelve a conectar.";
            return "Discord ha cerrado la conexión (" + code + "). El estado puede tardar en desaparecer.";
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
                        throw new GatewayException("Discord ha enviado un formato no compatible.");
                    stream.Write(buffer, 0, part.Count);
                    if (stream.Length > 8 * 1024 * 1024)
                        throw new GatewayException("La respuesta de Discord supera el tamaño permitido.");
                } while (!part.EndOfMessage);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        public Task Send(string text, CancellationToken ct) {
            var data = Encoding.UTF8.GetBytes(text);
            if (data.Length > 4096) throw new GatewayException("El estado es demasiado largo.");
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
        public Session(ITransport transport) { this.transport = transport; }
        public async Task<string> Start(string token) {
            if (String.IsNullOrWhiteSpace(token) || token.Length > 2048 || token.Any(Char.IsWhiteSpace))
                throw new ArgumentException("Introduce tu token personal, sin espacios ni comillas.");
            try {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)) {
                    timeout.CancelAfter(20000);
                    await transport.Connect(timeout.Token).ConfigureAwait(false);
                }
                reader = ReadLoop(token);
                var completed = await Task.WhenAny(ready.Task, Task.Delay(20000, lifetime.Token)).ConfigureAwait(false);
                if (completed != ready.Task) throw new GatewayException("Discord no ha confirmado la conexión. Vuelve a intentarlo más tarde.");
                return await ready.Task.ConfigureAwait(false);
            } catch (Exception ex) {
                string message = SafeError(ex);
                Stop(message);
                throw new GatewayException(message);
            }
        }
        static string SafeError(Exception ex) {
            if (ex is GatewayException) return ex.Message;
            if (ex is OperationCanceledException) return "La conexión se ha cancelado o ha agotado el tiempo de espera.";
            return "No se ha podido mantener la conexión con Discord. Comprueba Internet y vuelve a conectar.";
        }
        async Task ReadLoop(string token) {
            try {
                while (!lifetime.IsCancellationRequested) {
                    var packet = Protocol.Read(await transport.Receive(lifetime.Token).ConfigureAwait(false));
                    if (packet.ContainsKey("s") && packet["s"] != null) Interlocked.Exchange(ref sequence, Convert.ToInt64(packet["s"]));
                    int op = Convert.ToInt32(packet["op"]);
                    if (op == 10) {
                        if (hello) throw new GatewayException("Discord ha reiniciado la sesión. Vuelve a conectar.");
                        hello = true;
                        int interval = Convert.ToInt32(Protocol.Map(packet["d"])["heartbeat_interval"]);
                        if (interval < 100 || interval > 300000) throw new GatewayException("Intervalo de conexión no válido.");
                        heartbeat = HeartbeatLoop(interval);
                        await Send(Protocol.Identify(token)).ConfigureAwait(false);
                        token = null;
                    } else if (op == 11) {
                        Interlocked.Exchange(ref awaitingAck, 0);
                    } else if (op == 1) {
                        await SendHeartbeat().ConfigureAwait(false);
                    } else if (op == 7 || op == 9) {
                        throw new GatewayException(op == 9 ? "Discord ha rechazado la sesión. No se reintentará automáticamente." : "Discord solicita una nueva conexión. Pulsa Conectar.");
                    } else if (op == 0 && packet.ContainsKey("t") && Convert.ToString(packet["t"]) == "READY") {
                        var user = Protocol.Map(Protocol.Map(packet["d"])["user"]);
                        if (user.ContainsKey("bot") && Convert.ToBoolean(user["bot"])) throw new GatewayException("Esta versión está preparada para tu cuenta personal, no para un bot.");
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
                        throw new GatewayException("Discord ha dejado de responder. Vuelve a conectar; el estado puede tardar en desaparecer.");
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
        public async Task SetPresence(bool value, string title, string url) {
            if (!Connected) throw new GatewayException("Conecta primero con Discord.");
            if ((DateTime.UtcNow - lastPresence).TotalSeconds < 6)
                throw new GatewayException("Espera unos segundos antes del siguiente cambio.");
            string payload = Protocol.Presence(value, title, url);
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
            active = false;
            lifetime.Cancel();
            transport.Dispose();
            ready.TrySetException(new GatewayException(error ?? "Conexión cerrada."));
            var handler = Disconnected;
            if (handler != null) handler(error);
        }
        public void Dispose() { Stop(null); }
    }

    internal sealed class MainForm : Form {
        static readonly Color Bg = Color.FromArgb(15, 17, 25);
        static readonly Color Card = Color.FromArgb(24, 27, 39);
        static readonly Color Muted = Color.FromArgb(169, 177, 197);
        static readonly Color Purple = Color.FromArgb(141, 94, 255);
        readonly TextBox token = Input(true), title = Input(false), url = Input(false);
        readonly Label connection = Label("DESCONECTADO", 10, Muted);
        readonly Label account = Label("Tu cuenta de Discord", 17, Color.White);
        readonly Label previewTitle = Label("Mi directo", 14, Color.White);
        readonly Label previewUrl = Label("twitch.tv / youtube.com", 10, Muted);
        readonly Label status = Label("Conecta tu cuenta para empezar.", 10, Muted);
        readonly Label activityState = Label("VISTA PREVIA · SIN PUBLICAR", 9, Muted);
        readonly Button connect = Button("Conectar", Purple);
        readonly Button activate = Button("Activar streaming", Purple);
        readonly Button deactivate = Button("Quitar estado", Color.FromArgb(49, 54, 72));
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 200 };
        readonly string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preferencias.json");
        Session session;
        bool busy, closing;
        DateTime nextChange = DateTime.MinValue;
        public MainForm() {
            Text = "StreamSwitch";
            ClientSize = new Size(1000, 800);
            MinimumSize = new Size(940, 830);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10);
            Icon = SystemIcons.Application;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32), ColumnCount = 1, RowCount = 4 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            Controls.Add(root);
            var heading = new Panel { Dock = DockStyle.Fill };
            var brand = Label("StreamSwitch", 27, Color.White); brand.Dock = DockStyle.None; brand.Location = new Point(0, 0); brand.Size = new Size(450, 46);
            var subtitle = Label("Tu presencia, a un clic.", 11, Muted); subtitle.Dock = DockStyle.None; subtitle.Location = new Point(2, 51); subtitle.Size = new Size(500, 28);
            heading.Controls.Add(brand); heading.Controls.Add(subtitle); root.Controls.Add(heading, 0, 0);
            var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            root.Controls.Add(columns, 0, 1);
            var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11, BackColor = Card, Padding = new Padding(22), Margin = new Padding(0, 0, 12, 0) };
            int[] heights = { 35, 25, 38, 42, 33, 25, 38, 25, 38, 54 };
            for (int i = 0; i < heights.Length; i++) form.RowStyles.Add(new RowStyle(SizeType.Absolute, heights[i]));
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            form.Controls.Add(Label("01   CONEXIÓN Y ESTADO", 10, Muted), 0, 0);
            form.Controls.Add(Label("Token de tu cuenta", 10, Color.White), 0, 1);
            form.Controls.Add(token, 0, 2); token.AccessibleName = "Token personal de Discord"; token.MaxLength = 2048;
            form.Controls.Add(Label("Solo en memoria. Se borra del campo al conectar.\nNo lo compartas por chat.", 9, Muted), 0, 3);
            form.Controls.Add(connect, 0, 4);
            form.Controls.Add(Label("Título del streaming", 10, Color.White), 0, 5);
            form.Controls.Add(title, 0, 6); title.AccessibleName = "Título del streaming"; title.MaxLength = 128; title.Text = "Mi directo";
            form.Controls.Add(Label("Enlace de Twitch o YouTube", 10, Color.White), 0, 7);
            form.Controls.Add(url, 0, 8); url.AccessibleName = "Enlace del streaming"; url.MaxLength = 512;
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 12, 0, 0) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            actions.Controls.Add(activate, 0, 0); actions.Controls.Add(deactivate, 1, 0); form.Controls.Add(actions, 0, 9);
            columns.Controls.Add(form, 0, 0);
            var preview = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, BackColor = Card, Padding = new Padding(24), Margin = Padding.Empty };
            int[] pHeights = { 36, 36, 50, 48, 31, 54, 42, 70 };
            for (int i = 0; i < pHeights.Length; i++) preview.RowStyles.Add(new RowStyle(SizeType.Absolute, pHeights[i]));
            preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            preview.Controls.Add(Label("02   VISTA PREVIA", 10, Muted), 0, 0);
            preview.Controls.Add(connection, 0, 1);
            preview.Controls.Add(account, 0, 2);
            var icon = Label("●  STREAMING", 17, Purple); preview.Controls.Add(icon, 0, 3);
            preview.Controls.Add(activityState, 0, 4);
            preview.Controls.Add(previewTitle, 0, 5);
            preview.Controls.Add(previewUrl, 0, 6);
            preview.Controls.Add(Label("La vista previa es orientativa. Enviar el estado no confirma que Discord muestre el icono morado.", 10, Muted), 0, 7);
            preview.Controls.Add(Label("Al activar o quitar el estado, esta sesión pasa a «En línea».", 9, Muted), 0, 8);
            columns.Controls.Add(preview, 1, 0);
            var notice = Label("CONEXIÓN NO OFICIAL\nAutomatizar una cuenta personal infringe las normas de Discord y puede provocar su suspensión.", 10, Color.FromArgb(235, 190, 121));
            notice.Margin = new Padding(0, 18, 0, 0); root.Controls.Add(notice, 0, 2);
            status.Padding = new Padding(0, 8, 0, 0); root.Controls.Add(status, 0, 3);
            title.TextChanged += delegate { previewTitle.Text = title.Text.Trim().Length == 0 ? "Mi directo" : title.Text; };
            url.TextChanged += delegate { previewUrl.Text = url.Text.Trim().Length == 0 ? "twitch.tv / youtube.com" : url.Text; };
            connect.Click += async delegate { await ConnectClicked(); };
            activate.Click += async delegate { await ChangePresence(true); };
            deactivate.Click += async delegate { await ChangePresence(false); };
            timer.Tick += delegate { UpdateButtons(); }; timer.Start();
            FormClosing += OnClosing;
            LoadPreferences(); UpdateButtons();
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
            connect.Text = connected ? "Desconectar" : "Conectar";
            connect.Enabled = !busy;
            token.Enabled = !busy && !connected;
            activate.Enabled = deactivate.Enabled = connected && !busy && DateTime.UtcNow >= nextChange;
            if (!connected) { connection.Text = busy ? "CONECTANDO…" : "DESCONECTADO"; connection.ForeColor = Muted; }
        }
        void ShowStatus(string text, bool error) { status.Text = text; status.ForeColor = error ? Color.FromArgb(255, 164, 164) : Muted; }
        async Task ConnectClicked() {
            busy = true; UpdateButtons();
            try {
                if (session != null && session.Connected) {
                    var old = session; session = null;
                    ShowStatus("Quitando el estado y cerrando la conexión…", false);
                    await old.Disconnect(); old.Dispose();
                    account.Text = "Tu cuenta de Discord"; activityState.Text = "VISTA PREVIA · SIN PUBLICAR";
                    ShowStatus("Desconectado. Discord puede tardar unos segundos en retirar la presencia.", false);
                } else {
                    string secret = token.Text.Trim();
                    if (secret.Length == 0) throw new ArgumentException("Introduce tu token en el campo de la app para conectar.");
                    if (session != null) session.Dispose();
                    var current = new Session(new DiscordTransport()); session = current;
                    current.Disconnected += message => {
                        if (IsDisposed || !IsHandleCreated) return;
                        try { BeginInvoke(new Action(delegate {
                            if (!Object.ReferenceEquals(session, current) || closing) return;
                            connection.Text = "DESCONECTADO"; activityState.Text = "SIN CONEXIÓN · ESTADO NO VERIFICADO";
                            account.Text = "Tu cuenta de Discord";
                            if (message != null) ShowStatus(message, true);
                            UpdateButtons();
                        })); } catch (InvalidOperationException) { }
                    };
                    token.Clear(); ShowStatus("Conectando con Discord…", false);
                    var start = current.Start(secret); secret = null;
                    string username = await start;
                    account.Text = username; connection.Text = "CONECTADO"; connection.ForeColor = Color.FromArgb(110, 219, 171);
                    ShowStatus("Conectado. Completa el título y el enlace para activar streaming.", false);
                }
            } catch (Exception ex) { ShowStatus(ex is ArgumentException || ex is GatewayException ? ex.Message : "No se ha podido conectar.", true); }
            finally { busy = false; UpdateButtons(); }
        }
        async Task ChangePresence(bool active) {
            busy = true; UpdateButtons();
            try {
                if (session == null) throw new GatewayException("Conecta primero con Discord.");
                await session.SetPresence(active, title.Text, url.Text);
                nextChange = DateTime.UtcNow.AddSeconds(6);
                activityState.Text = active ? "SOLICITUD ENVIADA · SIN VERIFICAR" : "RETIRADA SOLICITADA";
                ShowStatus(active ? "Estado enviado. Comprueba el icono desde otra cuenta; Discord no confirma su visualización." : "Retirada enviada. Tu sesión queda en línea.", false);
                SavePreferences();
            } catch (Exception ex) { ShowStatus(ex is ArgumentException || ex is GatewayException ? ex.Message : "No se ha podido cambiar el estado.", true); }
            finally { busy = false; UpdateButtons(); }
        }
        void LoadPreferences() {
            try {
                if (!File.Exists(settingsPath)) return;
                var saved = Protocol.Read(File.ReadAllText(settingsPath));
                title.Text = Protocol.Title(Convert.ToString(saved["title"]));
                url.Text = Protocol.StreamUrl(Convert.ToString(saved["url"]));
            } catch { }
        }
        void SavePreferences() {
            try { File.WriteAllText(settingsPath, Protocol.Json(new { title = title.Text.Trim(), url = url.Text.Trim() }), Encoding.UTF8); }
            catch { ShowStatus(status.Text + " No se han podido guardar el título y el enlace.", false); }
        }
        async void OnClosing(object sender, FormClosingEventArgs e) {
            if (closing) return;
            e.Cancel = true; closing = true; busy = true; timer.Stop(); UpdateButtons();
            token.Clear(); ShowStatus("Cerrando la sesión y retirando el estado…", false);
            try { if (session != null) { await session.Disconnect(); session.Dispose(); } }
            finally { BeginInvoke(new Action(Close)); }
        }
    }

    internal static class Program {
        [STAThread]
        static int Main(string[] args) {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (args.Contains("--self-test")) return SelfTests.Run(args.Length > 1 ? args[1] : "tests.txt");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using (var mutex = new Mutex(true, "Local\\StreamSwitch-Personal", out created)) {
                if (!created) { MessageBox.Show("StreamSwitch ya está abierto.", "StreamSwitch"); return 0; }
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

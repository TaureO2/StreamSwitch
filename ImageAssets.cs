using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace StreamSwitch {
    internal interface IImageAssets {
        Task<string> Resolve(string token, string applicationId, string imageUrl, CancellationToken ct);
    }
    internal sealed class ImageAssets : IImageAssets {
        public const string TwitchIconUrl = "https://img.icons8.com/color/96/twitch--v1.png";
        public const string KickIconUrl = "https://about.kick.com/apple-icon.png?apple-icon.0ldhg5ovdrppx.png";
        public static string LogoUrl(string choice) {
            if (choice == "Twitch") return TwitchIconUrl;
            if (choice == "Kick") return KickIconUrl;
            if (choice == "Ninguno") return "";
            throw new ArgumentException("Elige Twitch, Kick o Ninguno en el selector de logo.");
        }
        readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        public static string ApplicationId(string value) {
            value = (value ?? "").Trim();
            if (value.Length < 17 || value.Length > 20 || value.Any(c => c < '0' || c > '9'))
                throw new ArgumentException("Para imágenes externas y logos, introduce el Application ID de una aplicación tuya de Discord.");
            return value;
        }
        public static string DirectAsset(string imageUrl) {
            var uri = new Uri(Protocol.ImageUrl(imageUrl));
            if (uri.Host != "cdn.discordapp.com" && uri.Host != "media.discordapp.net") return null;
            return "mp:" + uri.PathAndQuery.TrimStart('/');
        }
        public static string ParseResponse(string text) {
            try {
                var entries = new JavaScriptSerializer { MaxJsonLength = 65536 }.DeserializeObject(text) as object[];
                if (entries == null || entries.Length != 1) throw new Exception();
                var entry = Protocol.Map(entries[0]);
                string path = Convert.ToString(entry["external_asset_path"]);
                if (path.Length > 2048 || !path.StartsWith("external/") || path.Any(Char.IsControl)) throw new Exception();
                return "mp:" + path;
            } catch { throw new GatewayException("Discord no ha devuelto un recurso válido para esta imagen."); }
        }
        public async Task<string> Resolve(string token, string applicationId, string imageUrl, CancellationToken ct) {
            imageUrl = Protocol.ImageUrl(imageUrl);
            var direct = DirectAsset(imageUrl);
            if (direct != null) return direct;
            applicationId = ApplicationId(applicationId);
            string key = applicationId + "\n" + imageUrl;
            string cached;
            if (cache.TryGetValue(key, out cached)) return cached;
            var request = (HttpWebRequest)WebRequest.Create("https://discord.com/api/v9/applications/" + applicationId + "/external-assets");
            request.Method = "POST";
            request.AllowAutoRedirect = false;
            request.UseDefaultCredentials = false;
            request.ContentType = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = token;
            byte[] bytes = Encoding.UTF8.GetBytes(Protocol.Json(new { urls = new[] { imageUrl } }));
            request.ContentLength = bytes.Length;
            try {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                    timeout.CancelAfter(15000);
                    using (timeout.Token.Register(request.Abort)) {
                        using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                            await stream.WriteAsync(bytes, 0, bytes.Length, timeout.Token).ConfigureAwait(false);
                        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false)) {
                            if (response.StatusCode != HttpStatusCode.OK) throw new GatewayException("Discord no ha aceptado la conversión de la imagen.");
                            using (var stream = response.GetResponseStream())
                            using (var memory = new MemoryStream()) {
                                var buffer = new byte[4096]; int count;
                                while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0) {
                                    if (memory.Length + count > 65536) throw new GatewayException("Respuesta de imagen demasiado grande.");
                                    memory.Write(buffer, 0, count);
                                }
                                string asset = ParseResponse(Encoding.UTF8.GetString(memory.ToArray()));
                                cache[key] = asset;
                                return asset;
                            }
                        }
                    }
                }
            } catch (WebException ex) {
                using (var response = ex.Response as HttpWebResponse) {
                    int code = response == null ? 0 : (int)response.StatusCode;
                    if (code == 401) throw new GatewayException("Discord ha rechazado el token al preparar la imagen. Reconecta la cuenta.");
                    if (code == 403 || code == 404) throw new GatewayException("Discord no permite usar esa aplicación. Revisa que el Application ID sea el de una aplicación tuya.");
                    if (code == 429) throw new GatewayException("Discord ha limitado la conversión de imágenes. Espera antes de intentarlo otra vez.");
                    throw new GatewayException("No se pudo preparar la imagen en Discord" + (code == 0 ? ". Comprueba la conexión." : " (HTTP " + code + "). Revisa el Application ID y el enlace."));
                }
            }
        }
    }
}

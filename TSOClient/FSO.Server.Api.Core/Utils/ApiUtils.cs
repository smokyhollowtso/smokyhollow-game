using Microsoft.AspNetCore.Http;
using System.Linq;

namespace FSO.Server.Api.Core.Utils
{
    public class ApiUtils
    {
        private const string HttpContext = "MS_HttpContext";
        private const string RemoteEndpointMessage =
            "System.ServiceModel.Channels.RemoteEndpointMessageProperty";
        private const string OwinContext = "MS_OwinContext";

        // "::ffff:1.2.3.4" and "1.2.3.4" are the same home; store them the same way.
        private static string Plain(System.Net.IPAddress a) =>
            (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString();

        public static string GetIP(HttpRequest request)
        {
            var api = FSO.Server.Api.Core.Api.INSTANCE;
            if (!api.Config.UseProxy)
            {
                var remote = request.HttpContext.Connection.RemoteIpAddress;
                if (remote != null && remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
                // Smoky Hollow: the website reaches us through Caddy (the secure address) on this same PC,
                // so those requests look local. Only then, trust the visitor address Caddy passes along,
                // so sign-up limits and bans still see the real player. Nobody else can fake it.
                if (remote != null && System.Net.IPAddress.IsLoopback(remote))
                {
                    var forwarded = request.Headers["X-Forwarded-For"];
                    if (forwarded.Count != 0)
                    {
                        var last = forwarded.ToString().Split(',').Last().Trim();
                        if (System.Net.IPAddress.TryParse(last, out var real)) return Plain(real);
                    }
                }
                return remote != null ? Plain(remote) : "127.0.0.1";
            }
            else
            {
                var ip = "127.0.0.1";
                var xff = request.Headers["X-Forwarded-For"];
                if (xff.Count != 0)
                {
                    ip = xff.First();
                    ip = ip.Substring(ip.IndexOf(",") + 1);
                    var last = ip.LastIndexOf(":");
                    if (last != -1 && last < ip.Length - 5) ip = ip.Substring(0, last);
                }
                return ip;
            }
        }
    }
}
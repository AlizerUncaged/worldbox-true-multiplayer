using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WorldboxMP.Server.Auth;
using WorldboxMP.Server.Data;
using WorldboxMP.Server.Net;

namespace WorldboxMP.Server.Admin
{
    /// <summary>Admin panel settings (the "Admin" section of the server settings).</summary>
    public class AdminOptions
    {
        public bool Enabled { get; set; }
        public string Bind { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 40480;
        public string User { get; set; } = "admin";
        public string Password { get; set; } = "";
        public bool ResetPassword { get; set; }
    }

    /// <summary>
    /// The server management website: a single page (Tailwind, embedded in the dll) plus a
    /// small JSON api under /api. Separate port from the game so it can stay firewalled.
    /// Logins are admin accounts (not game accounts), seeded from Admin:Password.
    /// No lockouts or rate limits by design; use a strong password and keep it off public ports.
    /// </summary>
    public static class AdminHost
    {
        private const string CookieName = "wbmp_admin";
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
        private static readonly ConcurrentDictionary<string, (string user, DateTime expires)> _sessions = new();

        /// <summary>Create the first admin from the env password (or reset it when asked).
        /// Runs before the auth worker starts, so touching the store directly is safe.</summary>
        public static bool Seed(IPlayerStore db, AdminOptions o)
        {
            string lower = o.User.ToLowerInvariant();
            bool exists = db.FindAdmin(lower) != null;
            if (exists && !o.ResetPassword) return true;
            if (string.IsNullOrEmpty(o.Password) || o.Password.Length < 8)
            {
                Console.WriteLine(exists
                    ? "WARN: Admin:ResetPassword is set but Admin:Password is missing or shorter than 8 characters - password unchanged."
                    : "WARN: admin panel disabled - set Admin:Password (8+ characters, e.g. in appsettings.Local.json) to create the first admin.");
                return exists;
            }
            var (hash, salt) = AuthService.HashPassword(o.Password);
            db.SaveAdmin(new AdminRecord { Username = o.User, UsernameLower = lower, PasswordHash = hash, PasswordSalt = salt });
            Console.WriteLine(exists ? $"Admin '{o.User}': password reset from Admin:Password." : $"Admin '{o.User}' created from Admin:Password.");
            return true;
        }

        public static WebApplication Start(GameServer server, AdminOptions o, ServerSettings.HttpsSettings httpsSettings)
        {
            string pfx = httpsSettings?.CertificatePath;
            string pass = httpsSettings?.CertificatePassword;
            bool https = !string.IsNullOrEmpty(pfx);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                var ip = o.Bind == "0.0.0.0" || o.Bind == "*" ? System.Net.IPAddress.Any : System.Net.IPAddress.Parse(o.Bind);
                k.Listen(ip, o.Port, l =>
                {
                    if (https) l.UseHttps(X509CertificateLoader.LoadPkcs12FromFile(pfx, pass));
                });
                k.Limits.MaxRequestBodySize = 64 * 1024;
            });
            var app = builder.Build();

            // Security headers on everything.
            app.Use(async (ctx, next) =>
            {
                var h = ctx.Response.Headers;
                h["X-Frame-Options"] = "DENY";
                h["X-Content-Type-Options"] = "nosniff";
                h["Referrer-Policy"] = "no-referrer";
                h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'";
                h["Cache-Control"] = "no-store";
                await next();
            });

            // Static page + assets (embedded).
            app.MapGet("/", () => Asset("index.html", "text/html; charset=utf-8"));
            app.MapGet("/assets/app.css", () => Asset("app.css", "text/css"));
            app.MapGet("/assets/app.js", () => Asset("app.js", "application/javascript"));
            app.MapGet("/assets/logo.png", () => Asset("logo.png", "image/png"));
            app.MapGet("/assets/font.woff2", () => Asset("font.woff2", "font/woff2"));

            // ── Auth ──
            app.MapPost("/api/login", async (HttpContext ctx, LoginBody body) =>
            {
                string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
                string user = (body?.Username ?? "").Trim().ToLowerInvariant();
                string pw = body?.Password ?? "";
                bool ok = await server.OnDb(db =>
                {
                    var a = db.FindAdmin(user);
                    return a != null && AuthService.VerifyPassword(pw, a.PasswordHash, a.PasswordSalt);
                });
                if (!ok) return Results.Json(new { error = "Wrong username or password." }, statusCode: 401);
                string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                _sessions[token] = (user, DateTime.UtcNow + SessionLifetime);
                ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = https, Path = "/", MaxAge = SessionLifetime,
                });
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [admin] {user} signed in from {ip}.");
                return Results.Json(new { ok = true, user });
            });

            app.MapPost("/api/logout", (HttpContext ctx) =>
            {
                if (ctx.Request.Cookies.TryGetValue(CookieName, out var t)) _sessions.TryRemove(t, out _);
                ctx.Response.Cookies.Delete(CookieName);
                return Results.Json(new { ok = true });
            });

            // Everything below requires a session (+ the CSRF header on writes).
            var api = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
            {
                var http = ctx.HttpContext;
                if (http.Request.Path.StartsWithSegments("/api/login") || http.Request.Path.StartsWithSegments("/api/logout"))
                    return await next(ctx);
                if (CurrentUser(http) == null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);
                if (!HttpMethods.IsGet(http.Request.Method) && http.Request.Headers["X-Wbmp"] != "1")
                    return Results.Json(new { error = "Bad request." }, statusCode: 400);
                try { return await next(ctx); }
                catch (ArgumentException e) { return Results.Json(new { error = e.Message }, statusCode: 400); }
            });

            api.MapGet("/me", (HttpContext ctx) => Results.Json(new { user = CurrentUser(ctx) }));
            api.MapGet("/overview", async () => Results.Json(await server.AdminOverview()));
            api.MapGet("/accounts", async (string q) => Results.Json(await server.AdminAccounts(q ?? "")));
            api.MapGet("/bans", async () => Results.Json(await server.AdminBans()));

            api.MapPost("/players/{id4}/kick", async (string id4, ReasonBody b) => Ok(await server.AdminKickFromRoom(id4, b?.Reason)));
            api.MapPost("/players/{id4}/disconnect", async (string id4, ReasonBody b) => Ok(await server.AdminDisconnect(id4, b?.Reason)));
            api.MapPost("/accounts/{id4}/signout", async (string id4) => Ok(await server.AdminLogoutEverywhere(id4)));
            api.MapPost("/accounts/{id4}/password", async (string id4, PasswordBody b) => Ok(await server.AdminSetPassword(id4, b?.Password)));
            api.MapPost("/rooms/{id}/close", async (string id, ReasonBody b) => Ok(await server.AdminCloseRoom(id, b?.Reason)));
            api.MapPost("/announce", async (AnnounceBody b) => Results.Json(new { ok = true, sent = await server.AdminAnnounce(b?.Text) }));
            api.MapPost("/bans", async (BanBody b) => Results.Json(await server.AdminBan(b?.Kind, b?.Value, b?.Label, b?.Reason, b?.Hours)));
            api.MapDelete("/bans/{id}", async (string id) => Ok(await server.AdminUnban(id)));

            api.MapPost("/admin/password", async (HttpContext ctx, ChangePasswordBody b) =>
            {
                string user = CurrentUser(ctx);
                string next = b?.Next ?? "";
                if (next.Length < 8) return Results.Json(new { error = "Use at least 8 characters." }, statusCode: 400);
                bool ok = await server.OnDb(db =>
                {
                    var a = db.FindAdmin(user);
                    if (a == null || !AuthService.VerifyPassword(b.Current ?? "", a.PasswordHash, a.PasswordSalt)) return false;
                    (a.PasswordHash, a.PasswordSalt) = AuthService.HashPassword(next);
                    db.SaveAdmin(a);
                    return true;
                });
                return ok ? Results.Json(new { ok = true }) : Results.Json(new { error = "Current password is wrong." }, statusCode: 400);
            });

            app.RunAsync();
            Console.WriteLine($"Admin panel on {(https ? "https" : "http")}://{o.Bind}:{o.Port}/");
            if (!https && o.Bind != "127.0.0.1" && o.Bind != "localhost")
                Console.WriteLine("WARN: the admin panel is plain HTTP on a public address. Put it behind TLS (Https:CertificatePath or a reverse proxy) or bind 127.0.0.1 and use an SSH tunnel.");
            return app;
        }

        private static IResult Ok(bool done) =>
            done ? Results.Json(new { ok = true }) : Results.Json(new { error = "Not found (already gone?)." }, statusCode: 404);

        private static string CurrentUser(HttpContext ctx)
        {
            if (!ctx.Request.Cookies.TryGetValue(CookieName, out var t) || !_sessions.TryGetValue(t, out var s)) return null;
            if (s.expires < DateTime.UtcNow) { _sessions.TryRemove(t, out _); return null; }
            return s.user;
        }

        private static IResult Asset(string name, string type)
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("admin/" + name);
            if (stream == null) return Results.NotFound();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Results.Bytes(ms.ToArray(), type);
        }

        public sealed class LoginBody { public string Username { get; set; } public string Password { get; set; } }
        public sealed class ReasonBody { public string Reason { get; set; } }
        public sealed class PasswordBody { public string Password { get; set; } }
        public sealed class AnnounceBody { public string Text { get; set; } }
        public sealed class ChangePasswordBody { public string Current { get; set; } public string Next { get; set; } }
        public sealed class BanBody
        {
            public string Kind { get; set; }
            public string Value { get; set; }
            public string Label { get; set; }
            public string Reason { get; set; }
            public double? Hours { get; set; }
        }
    }
}

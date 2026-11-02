using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using KnockKnockArena.Server.Game;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Server.Net;

/// <summary>
/// The REST side of the server (Module 10): status and config. Runs Kestrel inside
/// the same headless process, on its own port. JSON in/out (camelCase — the ASP.NET
/// default), documented on the wiki page "REST API". Accounts and the leaderboard
/// come in M11.
/// </summary>
public static class RestApi
{
    public static void Start(ServerConfig config, GameContent content, Func<object> statusProvider)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // our own Log class owns the console
        builder.WebHost.UseUrls($"http://0.0.0.0:{config.HttpPort}");

        WebApplication app = builder.Build();

        // Verbose per-request logging, same spirit as the packet log.
        app.Use(async (context, next) =>
        {
            await next();
            Log.Packet($"REST {context.Request.Method} {context.Request.Path} " +
                       $"-> {context.Response.StatusCode}");
        });

        app.MapGet("/api/status", () => Results.Json(statusProvider()));

        // The config lookup (S01 3.7). A client asks for this before it joins, parses
        // the arena with the shared loader, and checks the hash it computes against
        // mapHash — which the server also sends in ServerInfo over TCP, so a mismatch
        // is caught even by a client that already had the map.
        //
        // The documents are served as the RAW TEXT of the files, not re-serialized, so
        // both sides parse identical bytes. Embedding them as strings keeps this one
        // response self-contained: fetch it and you have everything you need to
        // simulate, which is also what makes it readable in Wireshark or a browser.
        app.MapGet("/api/config", () => Results.Json(new
        {
            protocolVersion = KnockKnockArena.Shared.Protocol.ProtocolConstants.Version,
            tickRate = KnockKnockArena.Shared.Simulation.GameConstants.TickRate,
            mapName = content.Map.Name,
            mapHash = ArenaMapData.FormatHash(content.Map.Hash),
            arena = content.MapJson,
            weapons = content.WeaponsJson,
        }));
        

        // The same two documents on their own, for reading by eye or with curl.
        app.MapGet("/api/config/arena", () => Results.Text(content.MapJson, "application/json"));
        app.MapGet("/api/config/weapons", () => Results.Text(content.WeaponsJson, "application/json"));
        
        // Wait until Kestrel has the port: a port that is taken stops the server with an
        // error, like the TCP listener and the UDP socket do - not a "listening" line for
        // a port someone else answers on.
        app.StartAsync().GetAwaiter().GetResult();
        Log.Info($"REST listening on port {config.HttpPort}");
    }
}

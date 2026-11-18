using AppKeeper;
using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        var store = new AgendaStore(root);
        var sync = new AgendaLanSync(store, root);
        await sync.StartAsync(IPAddress.Loopback, 0);
        void Reply(object value) => Console.WriteLine(JsonSerializer.Serialize(value, AgendaSyncProtocol.Json));
        Reply(new { ready = true, port = sync.Snapshot().Port, serverId = store.DeviceId });
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var command = document.RootElement;
                    object result;
                    switch (command.GetProperty("op").GetString())
                    {
                        case "session":
                            var session = sync.CreateSession(["192.168.1.2"]);
                            result = new { offer = session.Offer, code = session.Code };
                            break;
                        case "append":
                            store.Append(command.GetProperty("kind").GetString()!, command.GetProperty("taskId").GetString()!, command.GetProperty("payload"));
                            result = new { ok = true };
                            break;
                        case "operations": result = store.Operations(); break;
                        case "tasks": result = store.Tasks(); break;
                        case "state": result = sync.Snapshot(); break;
                        case "revoke": sync.Revoke(command.GetProperty("id").GetString()!); result = new { ok = true }; break;
                        case "configure":
                            await sync.ConfigureAsync(command.GetProperty("enabled").GetBoolean(), command.GetProperty("port").GetInt32());
                            result = sync.Snapshot(); break;
                        case "restart":
                            var port = sync.Snapshot().Port;
                            sync.Dispose();
                            store = new AgendaStore(root);
                            sync = new AgendaLanSync(store, root);
                            await sync.StartAsync(IPAddress.Loopback, port);
                            result = new { port = sync.Snapshot().Port, serverId = store.DeviceId };
                            break;
                        case "stop": Reply(new { ok = true }); return;
                        default: throw new InvalidOperationException("Unknown test command");
                    }
                    Reply(result);
                }
                catch (Exception error) { Reply(new { error = error.GetType().Name }); }
            }
        }
        finally { sync.Dispose(); }
    }
}

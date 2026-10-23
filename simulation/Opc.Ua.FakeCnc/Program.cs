using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.FakeCnc;

namespace Opc.Ua.FakeCnc;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        int port = 4840;
        int intervalMs = 200;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "-p" or "--port" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], out port);
            }
            else if (args[i] is "-i" or "--interval" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], out intervalMs);
            }
            else if (args[i] is "-h" or "--help")
            {
                Console.WriteLine("""
                    Fake CNC OPC UA Server v1.0.0
                    Simulates high-fidelity 5-axis CNC metrics (spindle speed, cycle time, part counters, axes motion).

                    USAGE:
                      dotnet run --project tools/Opc.Ua.FakeCnc -- [options]

                    OPTIONS:
                      -p, --port <port>        OPC UA TCP port (default: 4840)
                      -i, --interval <ms>      Physics simulation tick interval in ms (default: 200)
                      -h, --help               Show help
                    """);
                return 0;
            }
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("""
            ========================================================================
                      FAKE CNC OPC UA SERVER — 5-AXIS MACHINING CENTER             
            ========================================================================
            """);
        Console.ResetColor();

        ITelemetryContext telemetry = DefaultTelemetry.Create(_ => { });
        var model = new CncSimulationModel();
        var server = new FakeCncServer(telemetry, model, tickIntervalMs: intervalMs);

        Console.WriteLine($"Configuring server on port {port}...");
        ApplicationConfiguration config = await FakeCncServer.CreateConfigurationAsync(
            port: port,
            telemetry: telemetry).ConfigureAwait(false);

        var app = new ApplicationInstance(config, telemetry);
        await app.CheckApplicationInstanceCertificatesAsync(true).ConfigureAwait(false);
        await app.StartAsync(server).ConfigureAwait(false);

        string endpoint = $"opc.tcp://localhost:{port}/FakeCncServer";

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n✓ Server is LIVE and listening at: {endpoint}");
        Console.ResetColor();

        Console.WriteLine("""
            Identities available:
              - Anonymous (read public metrics)
              - User: operator / Pass: operator123 (elevated write permissions)
              - User: admin    / Pass: admin123    (unrestricted access to DB99)

            Exposed Trees:
              - ns=2;s=CNC_Machine/... (Standard semantic companion tree with units & EURanges)
              - ns=3;s="DB10_Spindle"."Var_27" (Cryptic Siemens S7-1500 / Fanuc DB emulation)

            Press Ctrl+C to stop server.
            """);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Live console status dashboard update
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                Console.Write($"\r[CNC LIVE] State: {model.ExecutionState} | Program: {model.ActiveProgram} | Parts: {model.PartsProduced} | Cycle Rem: {model.PartCycleTimeRemaining:F1}s | Spindle: {model.ActualSpeed:F0} RPM | Load: {model.SpindleLoad:F1}% | Temp: {model.SpindleTemperature:F1}°C    ");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Ctrl+C
        }

        Console.WriteLine("\nShutting down server...");
        await app.StopAsync().ConfigureAwait(false);
        return 0;
    }
}

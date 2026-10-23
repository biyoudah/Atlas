using System.Text.Json;
using Microsoft.Extensions.Logging;
using Opc.Ua.Atlas.Client;
using Opc.Ua.Atlas.Crawler;
using Opc.Ua.Atlas.Dashboard;
using Opc.Ua.Atlas.Exporters;
using Opc.Ua.Atlas.Labeling;
using Opc.Ua.Atlas.Models;
using Opc.Ua.Atlas.Profiling;
using Opc.Ua.Atlas.Serving;
using Opc.Ua.Client;

namespace Opc.Ua.Atlas;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        string[] subArgs = args.Length > 1 ? args[1..] : [];

        try
        {
            return command switch
            {
                "crawl" => await HandleCrawlAsync(subArgs).ConfigureAwait(false),
                "diff" => await HandleDiffAsync(subArgs).ConfigureAwait(false),
                "diff-live" => await HandleDiffLiveAsync(subArgs).ConfigureAwait(false),
                "dashboard" => await HandleDashboardAsync(subArgs).ConfigureAwait(false),
                "serve" => await HandleServeAsync(subArgs).ConfigureAwait(false),
                _ => HandleUnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"\nError: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Atlas — OPC UA Discovery & Catalog Generator Engine v1.0.0

            USAGE:
              atlas <command> [options]

            COMMANDS:
              crawl <endpoint-url>              Crawl address space and generate catalog
              diff <base.json> <target.json>    Compare two catalogs to detect hidden tags / elevations
              diff-live <endpoint-url>          Compare anonymous vs authenticated view on live PLC
              dashboard <catalog.json>          Generate turnkey Grafana v10 JSON dashboard
              serve <endpoint-url>              Serve Prometheus metrics (/metrics) & WebSocket gateway (/ws)

            CRAWL OPTIONS:
              -u, --user <username>             Username for authentication (default: anonymous)
              -p, --password <password>         Password for authentication
              -f, --format <json|csv|nodeset2>  Export format (default: json)
              -o, --output <file>               Output file path (default: stdout)
              -d, --max-depth <depth>           Maximum browse recursion depth (default: 50)
              -b, --batch-size <size>           Batch size for attribute reads (default: 200)
              --include-diagnostics             Include Server diagnostics folder (default: false)

            DASHBOARD OPTIONS:
              -o, --output <file>               Output Grafana JSON file (default: stdout)
              -t, --title <title>               Dashboard title (default: Atlas CNC Machine Overview)
              --datasource <name>               Grafana Prometheus datasource UID (default: Prometheus)
              --machine <name>                  Target machine name filter (default: default_machine)

            SERVE OPTIONS:
              --port <port>                     HTTP/WebSocket listening port (default: 9090)
              --machine <name>                  Machine identifier tag for metrics (default: default_machine)
              --pack <fileOrName>               Mapping pack (e.g. siemens-s7-1500, fanuc-31i)
              --catalog <file.json>             Load pre-crawled catalog (offline simulation mode)
              --allow-write <nodeId>            Allowlist a node for remote writes (audit logged)

            DIFF OPTIONS:
              -f, --format <markdown|json>      Diff report format (default: markdown)
              -o, --output <file>               Output file path (default: stdout)
            """);
    }

    private static int HandleUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: '{command}'. Use 'atlas --help' for usage.");
        return 1;
    }

    private static async Task<int> HandleCrawlAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Error: Endpoint URL is required for 'crawl'.");
            return 1;
        }

        string endpointUrl = args[0];
        string? user = null;
        string? password = null;
        string format = "json";
        string? outputFile = null;
        string? packName = null;
        int profileSeconds = 0;
        int maxDepth = 50;
        int batchSize = 200;
        bool includeDiagnostics = false;

        for (int i = 1; i < args.Length; i++)
        {
            string opt = args[i];
            if (opt is "-u" or "--user" && i + 1 < args.Length) user = args[++i];
            else if (opt is "-p" or "--password" && i + 1 < args.Length) password = args[++i];
            else if (opt is "-f" or "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
            else if (opt is "-o" or "--output" && i + 1 < args.Length) outputFile = args[++i];
            else if (opt is "-d" or "--max-depth" && i + 1 < args.Length) int.TryParse(args[++i], out maxDepth);
            else if (opt is "-b" or "--batch-size" && i + 1 < args.Length) int.TryParse(args[++i], out batchSize);
            else if (opt is "--pack" && i + 1 < args.Length) packName = args[++i];
            else if (opt is "--profile" or "--profile-seconds" && i + 1 < args.Length) int.TryParse(args[++i], out profileSeconds);
            else if (opt is "--include-diagnostics") includeDiagnostics = true;
        }

        Console.Error.WriteLine($"Connecting to {endpointUrl} (User: {user ?? "Anonymous"})...");
        await using ManagedSession session = await AtlasClientHelper.ConnectAsync(
            endpointUrl, user, password).ConfigureAwait(false);

        Console.Error.WriteLine("Connected. Starting address space crawl...");
        var crawler = new AtlasCrawler(session, new CrawlerOptions
        {
            MaxDepth = maxDepth,
            BatchSize = batchSize,
            IncludeServerDiagnostics = includeDiagnostics,
            IdentityType = user ?? "Anonymous"
        });

        AtlasCatalog catalog = await crawler.CrawlAsync().ConfigureAwait(false);
        Console.Error.WriteLine($"Crawl finished: {catalog.Entries.Count} nodes discovered in {catalog.Metadata.Statistics?.DurationMilliseconds}ms.");

        if (profileSeconds > 0)
        {
            Console.Error.WriteLine($"Passive profiling active for {profileSeconds}s...");
            var profiler = new LiveProfiler(session);
            var profileReport = await profiler.ProfileAsync(
                catalog.Entries,
                new ProfilerOptions { Duration = TimeSpan.FromSeconds(profileSeconds) }).ConfigureAwait(false);
            SemanticLabeler.LabelCatalog(catalog, profileReport: profileReport);
        }
        else
        {
            SemanticLabeler.LabelCatalog(catalog);
        }

        if (!string.IsNullOrEmpty(packName))
        {
            var pack = MappingPackLoader.ResolvePack(packName);
            if (pack != null)
            {
                Console.Error.WriteLine($"Applying mapping pack '{pack.Name}' ({pack.Rules.Count} rules)...");
                SemanticLabeler.LabelCatalog(catalog, mappingPack: pack);
            }
            else
            {
                Console.Error.WriteLine($"Warning: Mapping pack '{packName}' could not be resolved.");
            }
        }

        await ExportCatalogAsync(catalog, format, outputFile).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> HandleDiffAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Error: Two catalog JSON files are required for 'diff'.");
            return 1;
        }

        string basePath = args[0];
        string targetPath = args[1];
        string format = "markdown";
        string? outputFile = null;

        for (int i = 2; i < args.Length; i++)
        {
            string opt = args[i];
            if (opt is "-f" or "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
            else if (opt is "-o" or "--output" && i + 1 < args.Length) outputFile = args[++i];
        }

        if (!File.Exists(basePath))
        {
            Console.Error.WriteLine($"File not found: {basePath}");
            return 1;
        }

        if (!File.Exists(targetPath))
        {
            Console.Error.WriteLine($"File not found: {targetPath}");
            return 1;
        }

        using FileStream baseFs = File.OpenRead(basePath);
        AtlasCatalog? baseCatalog = await JsonSerializer.DeserializeAsync<AtlasCatalog>(baseFs).ConfigureAwait(false);

        using FileStream targetFs = File.OpenRead(targetPath);
        AtlasCatalog? targetCatalog = await JsonSerializer.DeserializeAsync<AtlasCatalog>(targetFs).ConfigureAwait(false);

        if (baseCatalog == null || targetCatalog == null)
        {
            Console.Error.WriteLine("Error deserializing one or both catalog files.");
            return 1;
        }

        CatalogDiff diff = IdentityDiffer.Diff(baseCatalog, targetCatalog);
        string outputText = format == "json" ? diff.ToJson() : diff.ToMarkdown();

        if (string.IsNullOrEmpty(outputFile))
        {
            Console.WriteLine(outputText);
        }
        else
        {
            await File.WriteAllTextAsync(outputFile, outputText).ConfigureAwait(false);
            Console.Error.WriteLine($"Diff written to {outputFile}");
        }

        return 0;
    }

    private static async Task<int> HandleDiffLiveAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Error: Endpoint URL is required for 'diff-live'.");
            return 1;
        }

        string endpointUrl = args[0];
        string? targetUser = null;
        string? targetPassword = null;
        string format = "markdown";
        string? outputFile = null;

        for (int i = 1; i < args.Length; i++)
        {
            string opt = args[i];
            if (opt is "--target-user" && i + 1 < args.Length) targetUser = args[++i];
            else if (opt is "--target-password" && i + 1 < args.Length) targetPassword = args[++i];
            else if (opt is "-f" or "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
            else if (opt is "-o" or "--output" && i + 1 < args.Length) outputFile = args[++i];
        }

        Console.Error.WriteLine($"Step 1/2: Crawling anonymously from {endpointUrl}...");
        await using ManagedSession anonSession = await AtlasClientHelper.ConnectAsync(
            endpointUrl).ConfigureAwait(false);
        var anonCrawler = new AtlasCrawler(anonSession, new CrawlerOptions { IdentityType = "Anonymous" });
        AtlasCatalog anonCatalog = await anonCrawler.CrawlAsync().ConfigureAwait(false);

        Console.Error.WriteLine($"Step 2/2: Crawling as '{targetUser ?? "Target"}' from {endpointUrl}...");
        await using ManagedSession authSession = await AtlasClientHelper.ConnectAsync(
            endpointUrl, targetUser, targetPassword).ConfigureAwait(false);
        var authCrawler = new AtlasCrawler(authSession, new CrawlerOptions { IdentityType = targetUser ?? "Target" });
        AtlasCatalog authCatalog = await authCrawler.CrawlAsync().ConfigureAwait(false);

        Console.Error.WriteLine("Computing identity diff...");
        CatalogDiff diff = IdentityDiffer.Diff(anonCatalog, authCatalog);
        string outputText = format == "json" ? diff.ToJson() : diff.ToMarkdown();

        if (string.IsNullOrEmpty(outputFile))
        {
            Console.WriteLine(outputText);
        }
        else
        {
            await File.WriteAllTextAsync(outputFile, outputText).ConfigureAwait(false);
            Console.Error.WriteLine($"Diff report written to {outputFile}");
        }

        return 0;
    }

    private static async Task ExportCatalogAsync(AtlasCatalog catalog, string format, string? outputFile)
    {
        if (string.IsNullOrEmpty(outputFile))
        {
            using var stdout = Console.OpenStandardOutput();
            switch (format)
            {
                case "csv":
                    using (var writer = new StreamWriter(stdout, leaveOpen: true))
                    {
                        CsvCatalogExporter.Export(catalog, writer);
                    }
                    break;
                case "nodeset2":
                    NodeSet2XmlExporter.Export(catalog, stdout);
                    break;
                default:
                    await JsonCatalogExporter.ExportAsync(catalog, stdout).ConfigureAwait(false);
                    break;
            }
        }
        else
        {
            switch (format)
            {
                case "csv":
                    await CsvCatalogExporter.ExportToFileAsync(catalog, outputFile).ConfigureAwait(false);
                    break;
                case "nodeset2":
                    await NodeSet2XmlExporter.ExportToFileAsync(catalog, outputFile).ConfigureAwait(false);
                    break;
                default:
                    await JsonCatalogExporter.ExportToFileAsync(catalog, outputFile).ConfigureAwait(false);
                    break;
            }
            Console.Error.WriteLine($"Catalog saved to {outputFile} ({format})");
        }
    }

    private static async Task<int> HandleDashboardAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Error: Catalog JSON file path is required for 'dashboard'.");
            return 1;
        }

        string catalogPath = args[0];
        string? outputFile = null;
        string title = "Atlas CNC Machine Overview";
        string datasource = "Prometheus";
        string machine = "default_machine";

        for (int i = 1; i < args.Length; i++)
        {
            string opt = args[i];
            if (opt is "-o" or "--output" && i + 1 < args.Length) outputFile = args[++i];
            else if (opt is "-t" or "--title" && i + 1 < args.Length) title = args[++i];
            else if (opt is "--datasource" && i + 1 < args.Length) datasource = args[++i];
            else if (opt is "--machine" && i + 1 < args.Length) machine = args[++i];
        }

        if (!File.Exists(catalogPath))
        {
            Console.Error.WriteLine($"File not found: {catalogPath}");
            return 1;
        }

        string json = await File.ReadAllTextAsync(catalogPath).ConfigureAwait(false);
        var catalog = JsonSerializer.Deserialize<AtlasCatalog>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (catalog == null)
        {
            Console.Error.WriteLine("Error: Unable to deserialize catalog JSON.");
            return 1;
        }

        string dashboardJson = GrafanaDashboardGenerator.Generate(catalog, title, datasource, machine);

        if (string.IsNullOrEmpty(outputFile))
        {
            Console.WriteLine(dashboardJson);
        }
        else
        {
            await File.WriteAllTextAsync(outputFile, dashboardJson).ConfigureAwait(false);
            Console.Error.WriteLine($"Grafana dashboard successfully written to {outputFile}");
        }

        return 0;
    }

    private static async Task<int> HandleServeAsync(string[] args)
    {
        int port = 9090;
        string machine = "default_machine";
        string? catalogFile = null;
        string? endpointUrl = null;
        string? user = null;
        string? password = null;
        string? packName = null;
        var allowList = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase)) endpointUrl = arg;
            else if (arg is "--port" && i + 1 < args.Length) int.TryParse(args[++i], out port);
            else if (arg is "--machine" && i + 1 < args.Length) machine = args[++i];
            else if (arg is "--catalog" && i + 1 < args.Length) catalogFile = args[++i];
            else if (arg is "-u" or "--user" && i + 1 < args.Length) user = args[++i];
            else if (arg is "-p" or "--password" && i + 1 < args.Length) password = args[++i];
            else if (arg is "--pack" && i + 1 < args.Length) packName = args[++i];
            else if (arg is "--allow-write" && i + 1 < args.Length) allowList.Add(args[++i]);
        }

        AtlasCatalog catalog;
        ManagedSession? session = null;

        if (!string.IsNullOrEmpty(catalogFile))
        {
            if (!File.Exists(catalogFile))
            {
                Console.Error.WriteLine($"Catalog file not found: {catalogFile}");
                return 1;
            }
            string json = await File.ReadAllTextAsync(catalogFile).ConfigureAwait(false);
            catalog = JsonSerializer.Deserialize<AtlasCatalog>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else if (!string.IsNullOrEmpty(endpointUrl))
        {
            Console.Error.WriteLine($"Connecting to {endpointUrl}...");
            session = await AtlasClientHelper.ConnectAsync(endpointUrl, user, password).ConfigureAwait(false);
            Console.Error.WriteLine("Connected. Performing initial discovery crawl...");
            var crawler = new AtlasCrawler(session);
            catalog = await crawler.CrawlAsync().ConfigureAwait(false);
        }
        else
        {
            Console.Error.WriteLine("Error: Must provide an OPC UA endpoint URL or a pre-crawled --catalog file to serve.");
            return 1;
        }

        // Apply mapping pack if requested
        if (!string.IsNullOrEmpty(packName))
        {
            var pack = MappingPackLoader.ResolvePack(packName);
            if (pack != null)
            {
                Console.Error.WriteLine($"Applying mapping pack '{pack.Name}' ({pack.Rules.Count} rules)...");
                SemanticLabeler.LabelCatalog(catalog, mappingPack: pack);
            }
            else
            {
                Console.Error.WriteLine($"Warning: Mapping pack '{packName}' could not be resolved.");
            }
        }

        var multiplexer = new SubscriptionMultiplexer(catalog, session, writeAllowlist: allowList);
        await multiplexer.StartAutoMonitoringAsync().ConfigureAwait(false);
        string prefix = $"http://localhost:{port}/";
        await using var server = new AtlasServer(catalog, multiplexer, prefix, machine);

        server.Start();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"""
            ========================================================================
             Atlas Serving Layer Active on {prefix}
            ========================================================================
             - Prometheus Metrics: {prefix}metrics
             - Address Space Catalog: {prefix}catalog
             - Health Status:      {prefix}health
             - WebSocket Stream:   ws://localhost:{port}/ws
            ========================================================================
            Press Ctrl+C to terminate.
            """);
        Console.ResetColor();

        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            tcs.TrySetResult();
        };

        await tcs.Task;
        Console.WriteLine("\nShutting down Atlas server...");
        return 0;
    }
}

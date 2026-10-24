using System.Text.Json;
using Microsoft.Extensions.Logging;
using Atlas.Client;
using Atlas.Crawler;
using Atlas.Dashboard;
using Atlas.Exporters;
using Atlas.Labeling;
using Atlas.Models;
using Atlas.Profiling;
using Atlas.Serving;
using Opc.Ua.Client;

namespace Atlas;

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
                "ui" => await HandleServeAsync(subArgs, openBrowser: true).ConfigureAwait(false),
                "mcp" => await HandleMcpAsync(subArgs).ConfigureAwait(false),
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
            Atlas — OPC UA Discovery & Catalog Generator Engine v1.1.0

            USAGE:
              atlas <command> [options]

            COMMANDS:
              ui [endpoint-url]                 Launch interactive visual cockpit in default browser
              crawl <endpoint-url>              Crawl address space and generate catalog
              diff <base.json> <target.json>    Compare two catalogs to detect hidden tags / elevations
              diff-live <endpoint-url>          Compare anonymous vs authenticated view on live PLC
              dashboard <catalog.json>          Generate turnkey Grafana v10 JSON dashboard
              serve <endpoint-url>              Serve Prometheus metrics (/metrics), Web UI (/) & WebSocket gateway (/ws)
              mcp                               Run native Model Context Protocol (MCP) server over stdio

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
              -d, --max-depth <depth>           Maximum browse recursion depth (default: 6)
              -b, --batch-size <size>           Batch size for attribute reads (default: 200)
              --pack <fileOrName>               Mapping pack (e.g. siemens-s7-1500, fanuc-31i)
              --catalog <file.json>             Load pre-crawled catalog (offline simulation mode)
              --allow-write <nodeId>            Allowlist a node for remote writes (audit logged)
              --of-id <id>                      MES Work Order / OF ID (e.g. OF-2026-9481)
              --part-number <sku>               Work Order Part Number (e.g. VALVE-50)
              --target-parts <count>            Target parts quantity to produce
              --target-cycle-time <sec>         Nominal ideal cycle time in seconds

            MCP OPTIONS:
              --catalog <file.json>             Load catalog JSON for offline MCP inspection
              --endpoint <url>                  OPC UA endpoint to crawl for live MCP session
              --pack <fileOrName>               Mapping pack to apply for semantic labeling

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
        string? passwordEnv = null;
        string? passwordFile = null;
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
            else if (opt is "--password-env" && i + 1 < args.Length) passwordEnv = args[++i];
            else if (opt is "--password-file" && i + 1 < args.Length) passwordFile = args[++i];
            else if (opt is "-f" or "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
            else if (opt is "-o" or "--output" && i + 1 < args.Length) outputFile = args[++i];
            else if (opt is "-d" or "--max-depth" && i + 1 < args.Length) int.TryParse(args[++i], out maxDepth);
            else if (opt is "-b" or "--batch-size" && i + 1 < args.Length) int.TryParse(args[++i], out batchSize);
            else if (opt is "--pack" && i + 1 < args.Length) packName = args[++i];
            else if (opt is "--profile" or "--profile-seconds" && i + 1 < args.Length) int.TryParse(args[++i], out profileSeconds);
            else if (opt is "--include-diagnostics") includeDiagnostics = true;
        }

        string? resolvedPassword = ResolvePassword(password, passwordEnv, passwordFile);

        Console.Error.WriteLine($"Connecting to {endpointUrl} (User: {user ?? "Anonymous"})...");
        await using ManagedSession session = await AtlasClientHelper.ConnectAsync(
            endpointUrl, user, resolvedPassword).ConfigureAwait(false);

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
        string? targetPasswordEnv = null;
        string? targetPasswordFile = null;
        string format = "markdown";
        string? outputFile = null;

        for (int i = 1; i < args.Length; i++)
        {
            string opt = args[i];
            if (opt is "--target-user" && i + 1 < args.Length) targetUser = args[++i];
            else if (opt is "--target-password" && i + 1 < args.Length) targetPassword = args[++i];
            else if (opt is "--target-password-env" && i + 1 < args.Length) targetPasswordEnv = args[++i];
            else if (opt is "--target-password-file" && i + 1 < args.Length) targetPasswordFile = args[++i];
            else if (opt is "-f" or "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
            else if (opt is "-o" or "--output" && i + 1 < args.Length) outputFile = args[++i];
        }

        string? resolvedTargetPassword = ResolvePassword(targetPassword, targetPasswordEnv, targetPasswordFile);

        Console.Error.WriteLine($"Step 1/2: Crawling anonymously from {endpointUrl}...");
        await using ManagedSession anonSession = await AtlasClientHelper.ConnectAsync(
            endpointUrl).ConfigureAwait(false);
        var anonCrawler = new AtlasCrawler(anonSession, new CrawlerOptions { IdentityType = "Anonymous" });
        AtlasCatalog anonCatalog = await anonCrawler.CrawlAsync().ConfigureAwait(false);

        Console.Error.WriteLine($"Step 2/2: Crawling as '{targetUser ?? "Target"}' from {endpointUrl}...");
        await using ManagedSession authSession = await AtlasClientHelper.ConnectAsync(
            endpointUrl, targetUser, resolvedTargetPassword).ConfigureAwait(false);
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

    private static async Task<int> HandleServeAsync(string[] args, bool openBrowser = false)
    {
        int port = 9090;
        string machine = "default_machine";
        string? catalogFile = null;
        string? endpointUrl = null;
        string? user = null;
        string? password = null;
        string? passwordEnv = null;
        string? passwordFile = null;
        string? packName = null;
        var allowList = new List<string>();
        bool lowCardinality = false;

        string? ofId = null;
        string? partNumber = null;
        int targetParts = 0;
        double targetCycleSec = 0;
        double plannedMin = 480;
        int scrapParts = 0;

        int maxDepth = 6;
        int batchSize = 200;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase)) endpointUrl = arg;
            else if (arg is "--port" && i + 1 < args.Length) int.TryParse(args[++i], out port);
            else if (arg is "--machine" && i + 1 < args.Length) machine = args[++i];
            else if (arg is "--catalog" && i + 1 < args.Length) catalogFile = args[++i];
            else if (arg is "-d" or "--max-depth" && i + 1 < args.Length) int.TryParse(args[++i], out maxDepth);
            else if (arg is "-b" or "--batch-size" && i + 1 < args.Length) int.TryParse(args[++i], out batchSize);
            else if (arg is "-u" or "--user" && i + 1 < args.Length) user = args[++i];
            else if (arg is "-p" or "--password" && i + 1 < args.Length) password = args[++i];
            else if (arg is "--password-env" && i + 1 < args.Length) passwordEnv = args[++i];
            else if (arg is "--password-file" && i + 1 < args.Length) passwordFile = args[++i];
            else if (arg is "--low-cardinality" or "--omit-node-id") lowCardinality = true;
            else if (arg is "--pack" && i + 1 < args.Length) packName = args[++i];
            else if (arg is "--allow-write" && i + 1 < args.Length) allowList.Add(args[++i]);
            else if (arg is "--of-id" && i + 1 < args.Length) ofId = args[++i];
            else if (arg is "--part-number" && i + 1 < args.Length) partNumber = args[++i];
            else if (arg is "--target-parts" && i + 1 < args.Length) int.TryParse(args[++i], out targetParts);
            else if (arg is "--target-cycle-time" && i + 1 < args.Length) double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out targetCycleSec);
            else if (arg is "--planned-minutes" && i + 1 < args.Length) double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out plannedMin);
            else if (arg is "--scrap-parts" && i + 1 < args.Length) int.TryParse(args[++i], out scrapParts);
        }

        if (string.IsNullOrEmpty(endpointUrl) && string.IsNullOrEmpty(catalogFile))
        {
            if (File.Exists("umati_depth6.json"))
            {
                catalogFile = "umati_depth6.json";
                Console.Error.WriteLine("Loading pre-discovered umati catalog (umati_depth6.json)...");
            }
            else
            {
                endpointUrl = "opc.tcp://opcua.umati.app:4840";
                Console.Error.WriteLine("Defaulting to official umati testbench (opc.tcp://opcua.umati.app:4840)...");
            }
        }

        string? resolvedPassword = ResolvePassword(password, passwordEnv, passwordFile);

        AtlasCatalog catalog;
        ManagedSession? session = null;

        if (!string.IsNullOrEmpty(endpointUrl))
        {
            Console.Error.WriteLine($"Connecting to {endpointUrl}...");
            session = await AtlasClientHelper.ConnectAsync(endpointUrl, user, resolvedPassword).ConfigureAwait(false);
        }

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
        else if (session != null)
        {
            Console.Error.WriteLine($"Connected. Performing initial discovery crawl (max depth: {maxDepth})...");
            var crawler = new AtlasCrawler(session, new CrawlerOptions
            {
                MaxDepth = maxDepth,
                BatchSize = batchSize,
                IdentityType = user ?? "Anonymous"
            });
            catalog = await crawler.CrawlAsync().ConfigureAwait(false);
        }
        else
        {
            Console.Error.WriteLine("Error: Must provide an OPC UA endpoint URL or a pre-crawled --catalog file to serve.");
            return 1;
        }

        // Apply semantic labeling (Tier 1 umati standards + optional mapping packs)
        UserMappingPack? loadedPack = null;
        if (!string.IsNullOrEmpty(packName))
        {
            loadedPack = MappingPackLoader.ResolvePack(packName);
            if (loadedPack != null)
            {
                Console.Error.WriteLine($"Applying mapping pack '{loadedPack.Name}' ({loadedPack.Rules.Count} rules)...");
            }
            else
            {
                Console.Error.WriteLine($"Warning: Mapping pack '{packName}' could not be resolved.");
            }
        }
        SemanticLabeler.LabelCatalog(catalog, mappingPack: loadedPack);

        // Optional MES Work Order Context
        Mes.WorkOrderContext? workOrder = null;
        if (!string.IsNullOrEmpty(ofId) || targetParts > 0)
        {
            workOrder = new Mes.WorkOrderContext
            {
                OrderId = ofId ?? "OF-LIVE",
                PartNumber = partNumber ?? "PART-001",
                TargetQuantity = targetParts > 0 ? targetParts : 500,
                NominalCycleTimeSeconds = targetCycleSec > 0 ? targetCycleSec : 45.0,
                PlannedProductionMinutes = plannedMin > 0 ? plannedMin : 480.0,
                DeclaredScrapParts = scrapParts
            };
            Console.Error.WriteLine($"Initialized MES Work Order context: {workOrder.OrderId} (Target: {workOrder.TargetQuantity} parts, Nominal Cycle: {workOrder.NominalCycleTimeSeconds}s)");
        }

        var multiplexer = new SubscriptionMultiplexer(catalog, session, writeAllowlist: allowList);
        await multiplexer.StartAutoMonitoringAsync().ConfigureAwait(false);
        string prefix = $"http://localhost:{port}/";
        await using var server = new AtlasServer(catalog, multiplexer, prefix, machine, workOrder: workOrder);
        server.IncludeNodeIdInMetrics = !lowCardinality;

        server.Start();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"""
            ========================================================================
             Atlas Serving Layer Active on {prefix}
            ========================================================================
             - Web UI Cockpit:     {prefix}
             - Prometheus Metrics: {prefix}metrics
             - Address Space Catalog: {prefix}catalog
             - Health Status:      {prefix}health
             - MES Reconciliation: {prefix}reconciliation
             - WebSocket Stream:   ws://localhost:{port}/ws
            ========================================================================
            Press Ctrl+C to terminate.
            """);
        Console.ResetColor();

        if (openBrowser)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = prefix,
                    UseShellExecute = true
                });
            }
            catch { }
        }

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

    private static async Task<int> HandleMcpAsync(string[] args)
    {
        string? catalogFile = null;
        string? endpointUrl = null;
        string? user = null;
        string? password = null;
        string? packName = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase)) endpointUrl = arg;
            else if (arg is "--endpoint" && i + 1 < args.Length) endpointUrl = args[++i];
            else if (arg is "--catalog" && i + 1 < args.Length) catalogFile = args[++i];
            else if (arg is "-u" or "--user" && i + 1 < args.Length) user = args[++i];
            else if (arg is "-p" or "--password" && i + 1 < args.Length) password = args[++i];
            else if (arg is "--pack" && i + 1 < args.Length) packName = args[++i];
        }

        AtlasCatalog catalog;
        ManagedSession? session = null;
        SubscriptionMultiplexer? multiplexer = null;

        if (!string.IsNullOrEmpty(catalogFile) && File.Exists(catalogFile))
        {
            string json = await File.ReadAllTextAsync(catalogFile).ConfigureAwait(false);
            catalog = JsonSerializer.Deserialize<AtlasCatalog>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            multiplexer = new SubscriptionMultiplexer(catalog);
        }
        else if (!string.IsNullOrEmpty(endpointUrl))
        {
            session = await AtlasClientHelper.ConnectAsync(endpointUrl, user, password).ConfigureAwait(false);
            var crawler = new AtlasCrawler(session);
            catalog = await crawler.CrawlAsync().ConfigureAwait(false);
            multiplexer = new SubscriptionMultiplexer(catalog, session);
            await multiplexer.StartAutoMonitoringAsync().ConfigureAwait(false);
        }
        else if (File.Exists("catalog.json"))
        {
            string json = await File.ReadAllTextAsync("catalog.json").ConfigureAwait(false);
            catalog = JsonSerializer.Deserialize<AtlasCatalog>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            multiplexer = new SubscriptionMultiplexer(catalog);
        }
        else
        {
            // Empty initialized catalog ready for MCP queries / mapping pack creation
            catalog = new AtlasCatalog
            {
                Metadata = new CatalogMetadata
                {
                    EndpointUrl = "opc.tcp://localhost:4840",
                    ServerUri = "urn:atlas:mcp:session",
                    CrawledAtUtc = DateTime.UtcNow
                },
                Entries = []
            };
        }

        if (!string.IsNullOrEmpty(packName))
        {
            var pack = MappingPackLoader.ResolvePack(packName);
            if (pack != null)
            {
                SemanticLabeler.LabelCatalog(catalog, mappingPack: pack);
            }
        }

        var mcpServer = new Mcp.AtlasMcpServer(catalog, multiplexer);
        await mcpServer.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Resolves OPC UA credentials safely from file, environment variable, or CLI fallback.
    /// Emits a warning when plaintext password is passed via CLI argument.
    /// </summary>
    public static string? ResolvePassword(string? cliPassword, string? passwordEnv = null, string? passwordFile = null)
    {
        if (!string.IsNullOrEmpty(passwordFile) && File.Exists(passwordFile))
        {
            return File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
        }

        string envName = string.IsNullOrEmpty(passwordEnv) ? "OPC_UA_PASSWORD" : passwordEnv;
        string? fromEnv = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        if (!string.IsNullOrEmpty(cliPassword))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine("[SECURITY NOTICE] Passing credentials via '-p/--password' CLI flag exposes them in the OS process table (e.g. ps -ef). Prefer OPC_UA_PASSWORD env var or '--password-file'.");
            Console.ResetColor();
            return cliPassword;
        }

        return null;
    }
}

using System.Text;

namespace Atlas.Serving;

/// <summary>
/// Provides the embedded HTML/JS/CSS cockpit for Atlas.
/// Serves the interactive address-space explorer, signal-to-noise analyzer, and live telemetry oscilloscope.
/// </summary>
public static class EmbeddedUi
{
    private static byte[]? s_cachedBytes;
    private static readonly object s_lock = new();

    public static byte[] GetHtmlBytes()
    {
        if (s_cachedBytes != null)
        {
            return s_cachedBytes;
        }

        lock (s_lock)
        {
            if (s_cachedBytes != null)
            {
                return s_cachedBytes;
            }

            // 1. Try reading from docs/index.html relative to app or repository root
            string[] searchPaths =
            [
                Path.Combine(AppContext.BaseDirectory, "docs", "index.html"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "docs", "index.html"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "index.html"),
                Path.Combine(Directory.GetCurrentDirectory(), "docs", "index.html")
            ];

            foreach (var path in searchPaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        string html = File.ReadAllText(path, Encoding.UTF8);
                        s_cachedBytes = Encoding.UTF8.GetBytes(html);
                        return s_cachedBytes;
                    }
                }
                catch { }
            }

            // 2. Fallback embedded HTML
            string fallbackHtml = """
                <!DOCTYPE html>
                <html>
                <head><title>Atlas Cockpit</title></head>
                <body style="background:#0a0e17;color:#fff;font-family:sans-serif;padding:2rem;">
                  <h1>ATLAS — Industrial OPC UA Telemetry Cockpit</h1>
                  <p>Serving Prometheus metrics at <a style="color:#06b6d4;" href="/metrics">/metrics</a></p>
                  <p>Catalog at <a style="color:#06b6d4;" href="/catalog">/catalog</a></p>
                  <p>Live API at <a style="color:#06b6d4;" href="/api/live">/api/live</a></p>
                  <p>Health at <a style="color:#06b6d4;" href="/health">/health</a></p>
                </body>
                </html>
                """;

            s_cachedBytes = Encoding.UTF8.GetBytes(fallbackHtml);
            return s_cachedBytes;
        }
    }
}

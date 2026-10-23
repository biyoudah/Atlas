using System.Text;
using Microsoft.Extensions.Logging;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Opc.Ua.Atlas.Client;

/// <summary>
/// Helper to construct and open OPC UA sessions for Atlas discovery and diffing.
/// </summary>
public static class AtlasClientHelper
{
    /// <summary>
    /// Creates a self-contained in-memory <see cref="ApplicationConfiguration"/> for the Atlas CLI tool.
    /// </summary>
    public static async Task<ApplicationConfiguration> CreateClientConfigurationAsync(
        string applicationName = "Atlas",
        bool autoAcceptUntrustedCertificates = true,
        ITelemetryContext? telemetry = null,
        CancellationToken ct = default)
    {
        string pkiRoot = Path.Combine(Path.GetTempPath(), "opcua_atlas_pki");
        ITelemetryContext actualTelemetry = telemetry ?? DefaultTelemetry.Create(_ => { });

        var config = new ApplicationConfiguration(actualTelemetry)
        {
            ApplicationName = applicationName,
            ApplicationUri = $"urn:localhost:OPCFoundation:{applicationName}",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = $"CN={applicationName}, O=OPC Foundation"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "issuer")
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "trusted")
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "rejected")
                },
                AutoAcceptUntrustedCertificates = autoAcceptUntrustedCertificates
            },
            TransportQuotas = new TransportQuotas { MaxMessageSize = 16 * 1024 * 1024 },
            ClientConfiguration = new ClientConfiguration(),
            ServerConfiguration = new ServerConfiguration()
        };

        await config.ValidateAsync(ApplicationType.Client, ct).ConfigureAwait(false);

        var appInstance = new ApplicationInstance(config, actualTelemetry);
        await appInstance.CheckApplicationInstanceCertificatesAsync(true, ct: ct).ConfigureAwait(false);

        if (autoAcceptUntrustedCertificates && config.CertificateManager != null)
        {
            config.CertificateManager.AcceptError = static (_, _) => true;
        }

        return config;
    }

    /// <summary>
    /// Connects to the target OPC UA server and returns a connected <see cref="ManagedSession"/>.
    /// </summary>
    public static async Task<ManagedSession> ConnectAsync(
        string endpointUrl,
        string? username = null,
        string? password = null,
        bool useSecurity = false,
        int timeoutMs = 15000,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpointUrl);

        ITelemetryContext telemetry = DefaultTelemetry.Create(_ => { });
        ApplicationConfiguration config = await CreateClientConfigurationAsync(
            applicationName: "Atlas",
            autoAcceptUntrustedCertificates: true,
            telemetry: telemetry,
            ct: ct).ConfigureAwait(false);

        IUserIdentity identity = string.IsNullOrEmpty(username)
            ? new UserIdentity(new AnonymousIdentityToken())
            : new UserIdentity(username, Encoding.UTF8.GetBytes(password ?? string.Empty));

        ConfiguredEndpoint? configuredEndpoint = null;
        try
        {
            EndpointDescription? selected = await CoreClientUtils.SelectEndpointAsync(
                config,
                endpointUrl,
                useSecurity: useSecurity,
                discoverTimeout: timeoutMs,
                telemetry: telemetry,
                ct: ct).ConfigureAwait(false);

            if (selected != null)
            {
                configuredEndpoint = new ConfiguredEndpoint(null, selected, EndpointConfiguration.Create(config));
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Endpoint discovery query failed; attempting direct connect.");
        }

        var builder = new ManagedSessionBuilder(config, telemetry);

        if (configuredEndpoint != null)
        {
            builder.UseEndpoint(configuredEndpoint);
        }
        else
        {
            builder.UseEndpoint(endpointUrl, MessageSecurityMode.None, SecurityPolicies.None);
        }

        builder.WithSessionName($"Atlas_{Guid.NewGuid():N}")
               .WithSessionTimeout(TimeSpan.FromMilliseconds(timeoutMs))
               .WithUserIdentity(identity);

        return await builder.ConnectAsync(ct).ConfigureAwait(false);
    }
}

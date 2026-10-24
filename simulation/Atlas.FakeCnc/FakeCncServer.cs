using Microsoft.Extensions.Logging;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace Atlas.FakeCnc;

/// <summary>
/// A realistic 5-axis CNC Machining Center OPC UA Server.
/// Simulates live spindle kinetics, part cycle countdowns, axes motion, coolant,
/// and both semantic companion spec nodes and cryptic Siemens/Fanuc DB tags.
/// </summary>
public class FakeCncServer : StandardServer
{
    private readonly CncSimulationModel m_model;
    private readonly int m_tickIntervalMs;

    public CncSimulationModel Model => m_model;

    public FakeCncServer(ITelemetryContext telemetry)
        : this(telemetry, new CncSimulationModel(), tickIntervalMs: 200)
    {
    }

    public FakeCncServer(
        ITelemetryContext telemetry,
        CncSimulationModel model,
        int tickIntervalMs = 200)
        : base(telemetry)
    {
        m_model = model;
        m_tickIntervalMs = tickIntervalMs;
    }

    protected override IMasterNodeManager CreateMasterNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration)
    {
        var cncManager = new FakeCncNodeManager(server, configuration, m_model, m_tickIntervalMs);
        return new MasterNodeManager(server, configuration, null, cncManager);
    }

    protected override void OnServerStarted(IServerInternal server)
    {
        base.OnServerStarted(server);

        // Register authentication delegates: Anonymous, Operator, and Admin
        server.IdentityRegistry.RegisterDefaultAuthenticators(
            verifyUserName: VerifyUserNameAsync,
            verifyX509: null,
            verifyJwt: null);

        m_logger.LogInformation("FakeCncServer started with simulation ticking at {Interval}ms.", m_tickIntervalMs);
    }

    private ValueTask<IUserIdentity> VerifyUserNameAsync(
        UserNameIdentityTokenHandler userNameToken,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string user = userNameToken.UserName ?? string.Empty;
        string pass = userNameToken.DecryptedPassword != null
            ? System.Text.Encoding.UTF8.GetString(userNameToken.DecryptedPassword)
            : string.Empty;

        // Realistic industrial credentials
        if ((user.Equals("operator", StringComparison.OrdinalIgnoreCase) && pass == "operator123") ||
            (user.Equals("admin", StringComparison.OrdinalIgnoreCase) && pass == "admin123"))
        {
            m_logger.LogInformation("User '{User}' authenticated successfully.", user);
            return ValueTask.FromResult<IUserIdentity>(new UserIdentity(userNameToken));
        }

        m_logger.LogWarning("Authentication failed for user '{User}'.", user);
        throw ServiceResultException.Create(StatusCodes.BadUserAccessDenied, "Invalid username or password.");
    }

    /// <summary>
    /// Builds a default self-contained server application configuration.
    /// </summary>
    public static async Task<ApplicationConfiguration> CreateConfigurationAsync(
        int port = 4840,
        string? pkiRoot = null,
        bool autoAccept = true,
        ITelemetryContext? telemetry = null)
    {
        telemetry ??= DefaultTelemetry.Create(_ => { });
        pkiRoot ??= Path.Combine(Path.GetTempPath(), "fake_cnc_pki");

        string endpointUrl = $"{Utils.UriSchemeOpcTcp}://localhost:{port}/FakeCncServer";

        var application = new ApplicationInstance(telemetry)
        {
            ApplicationName = "FakeCncServer",
            ApplicationType = ApplicationType.Server
        };

        var serverConfig = application
            .Build("urn:localhost:OPCFoundation:FakeCncServer", "uri:opcfoundation.org:FakeCncServer")
            .SetMaxByteStringLength(4 * 1024 * 1024)
            .SetMaxArrayLength(1024 * 1024)
            .SetMaxMessageSize(16 * 1024 * 1024)
            .SetChannelLifetime(60000)
            .AsServer([endpointUrl])
            .AddUnsecurePolicyNone()
            .AddSignPolicies()
            .AddSignAndEncryptPolicies()
            .AddUserTokenPolicy(new UserTokenPolicy(UserTokenType.Anonymous))
            .AddUserTokenPolicy(new UserTokenPolicy(UserTokenType.UserName))
            .SetOperationLimits(new OperationLimits
            {
                MaxNodesPerBrowse = 2000,
                MaxNodesPerRead = 1000,
                MaxNodesPerWrite = 1000
            })
            .SetDiagnosticsEnabled(true)
            .SetShutdownDelay(0);

        ArrayOf<CertificateIdentifier> certs = ApplicationConfigurationBuilder.CreateDefaultApplicationCertificates(
            "CN=FakeCncServer, O=OPC Foundation, DC=localhost",
            CertificateStoreType.Directory,
            pkiRoot);

        ApplicationConfiguration config = await serverConfig
            .AddSecurityConfiguration(certs, pkiRoot)
            .SetAutoAcceptUntrustedCertificates(autoAccept)
            .CreateAsync()
            .ConfigureAwait(false);
        return config;
    }
}


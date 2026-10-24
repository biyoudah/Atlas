namespace Atlas.FakeCnc;

/// <summary>
/// Deterministic physics and scenario execution model for CNC machining centers.
/// Simulates real-time spindle kinetics, cutting load variations, multi-axis kinematics,
/// and reproducible telemetry scenarios (Normal, CounterReset, QualityFault, Disruption, NetworkInterruption).
/// </summary>
public class CncSimulationModel
{
    private readonly object m_lock = new();
    private readonly Random m_random;
    private double m_elapsedTimeSeconds;
    private double m_cycleTimer;

    // Deterministic Scenario Configuration
    public SimulationScenario Scenario { get; set; } = SimulationScenario.NormalProduction;
    public long TickCount { get; private set; }
    public StatusCode SpindleStatusCode { get; private set; } = StatusCodes.Good;
    public bool SimulateNetworkDrop { get; set; }

    // Identification
    public string Manufacturer { get; set; } = "DMG MORI / Fanuc";
    public string Model { get; set; } = "MillTap 700 High-Torque 5-Axis";
    public string SerialNumber { get; set; } = "CNC-2026-X884";
    public string ControllerType { get; set; } = "Sinumerik 840D sl / Fanuc 31i-B5";
    public string SoftwareVersion { get; set; } = "V4.8 SP4";

    // Machine Status
    public string OperatingMode { get; set; } = "AUTO";
    public string ExecutionState { get; set; } = "ACTIVE";
    public bool EmergencyStop { get; set; } = false;
    public bool FeedHold { get; set; } = false;
    public int ActiveAlarmsCount { get; set; } = 0;

    // Production & OEE Metrics
    public uint PartsProduced { get; set; } = 142;
    public uint TargetParts { get; set; } = 500;
    public uint GoodParts { get; set; } = 139;
    public uint DefectiveParts { get; set; } = 3;
    public uint ScrapParts => DefectiveParts;
    public double NominalCycleTime { get; set; } = 45.0; // seconds
    public double PartCycleTimeRemaining { get; private set; } = 45.0; // seconds
    public string ActiveProgram { get; set; } = "O1024_TURBINE_BLADE_5AXIS.NC";
    public int CurrentBlock { get; private set; } = 120;
    public double TotalOperatingHours { get; private set; } = 1420.5;
    public double CuttingHours { get; private set; } = 980.2;
    public double OEE_Availability { get; private set; } = 95.2;
    public double OEE_Performance { get; private set; } = 89.4;
    public double OEE_Quality { get; private set; } = 97.9;
    public double OEE_Overall => (OEE_Availability * OEE_Performance * OEE_Quality) / 10000.0;

    // Spindle
    public double CommandedSpeed { get; set; } = 10000.0; // RPM
    public double ActualSpeed { get; private set; } = 10000.0; // RPM
    public double SpindleLoad { get; private set; } = 52.0; // %
    public double SpindleTemperature { get; private set; } = 41.2; // °C
    public double SpindleOverride { get; set; } = 100.0; // % (50 - 120)
    public string SpindleDirection { get; set; } = "CW";
    public double VibrationRMS { get; private set; } = 1.85; // mm/s
    public int ToolInSpindle { get; set; } = 7;
    public string ActiveToolName { get; set; } = "SolidCarbide_BallNose_R6";
    public double ToolLifeRemaining { get; private set; } = 76.5; // %
    public double ToolWearOffset { get; set; } = 0.008; // mm

    // Axes (X, Y, Z)
    public double X_ActualPosition { get; private set; } = 0.0;
    public double X_CommandedPosition { get; private set; } = 0.0;
    public double X_AxisLoad { get; private set; } = 24.5;

    public double Y_ActualPosition { get; private set; } = 0.0;
    public double Y_CommandedPosition { get; private set; } = 0.0;
    public double Y_AxisLoad { get; private set; } = 28.1;

    public double Z_ActualPosition { get; private set; } = -15.0;
    public double Z_CommandedPosition { get; private set; } = -15.0;
    public double Z_AxisLoad { get; private set; } = 35.2;

    public double ActualFeedRate { get; private set; } = 2800.0; // mm/min
    public double FeedRateOverride { get; set; } = 100.0; // % (0 - 150)

    // Auxiliaries & Coolant
    public double CoolantPressure { get; private set; } = 22.4; // bar
    public bool CoolantFlow { get; private set; } = true;
    public double CoolantTemperature { get; private set; } = 22.8; // °C
    public double HydraulicPressure { get; private set; } = 60.2; // bar
    public double PneumaticPressure { get; private set; } = 6.1; // bar
    public double LubricationLevel { get; private set; } = 84.5; // %
    public bool DoorLocked { get; set; } = true;
    public bool ChipConveyorActive { get; set; } = true;

    // Maintenance / Restricted Data
    public double SpindleBearingVibrationThreshold { get; set; } = 4.5; // mm/s
    public string MotorDriveFirmwareKey { get; set; } = "SEC-DRV-FIRMWARE-V9-HASH884B";
    public double DriveDCBusVoltage { get; private set; } = 565.4; // V

    public CncSimulationModel(int seed = 42, SimulationScenario scenario = SimulationScenario.NormalProduction)
    {
        m_random = new Random(seed);
        Scenario = scenario;
        m_cycleTimer = NominalCycleTime;
        PartCycleTimeRemaining = NominalCycleTime;
    }

    /// <summary>
    /// Advances machine physics and scenario event timeline deterministically.
    /// </summary>
    public void Tick(double deltaSeconds)
    {
        lock (m_lock)
        {
            TickCount++;

            // ================================================================
            // Deterministic Scenario Injections
            // ================================================================
            switch (Scenario)
            {
                case SimulationScenario.CounterReset:
                    // At tick 5, simulate operator shift change or M30 reset
                    if (TickCount == 5)
                    {
                        PartsProduced = 0;
                        GoodParts = 0;
                        DefectiveParts = 0;
                    }
                    break;

                case SimulationScenario.QualityFault:
                    // At tick >= 3, sensor fails with bad status code and out-of-range thermal reading
                    if (TickCount >= 3)
                    {
                        SpindleStatusCode = StatusCodes.BadSensorFailure;
                        SpindleTemperature = 125.0; // Exceeds EURange high (100°C)
                        VibrationRMS = 28.5; // High threshold trip
                    }
                    break;

                case SimulationScenario.ProductionDisruption:
                    // At tick >= 4, tool breakage alarm triggers emergency stop / feed hold
                    if (TickCount >= 4)
                    {
                        ExecutionState = "INTERRUPTED";
                        FeedHold = true;
                        ActiveAlarmsCount = 3;
                        ToolLifeRemaining = 0.0;
                        VibrationRMS = 14.8;
                    }
                    break;

                case SimulationScenario.NetworkInterruption:
                    // Simulate frozen socket between tick 3 and 7
                    if (TickCount is >= 3 and <= 7)
                    {
                        SimulateNetworkDrop = true;
                        return; // Freeze state updates
                    }
                    SimulateNetworkDrop = false;
                    break;
            }

            m_elapsedTimeSeconds += deltaSeconds;
            TotalOperatingHours += deltaSeconds / 3600.0;

            if (ExecutionState == "ACTIVE" && !EmergencyStop && !FeedHold)
            {
                CuttingHours += deltaSeconds / 3600.0;

                // --- 1. Cycle Time & Parts Produced ---
                m_cycleTimer -= deltaSeconds;
                if (m_cycleTimer <= 0)
                {
                    // Finished part!
                    PartsProduced++;
                    bool isGood = m_random.NextDouble() < 0.98;
                    if (isGood)
                    {
                        GoodParts++;
                    }
                    else
                    {
                        DefectiveParts++;
                    }

                    // Reset cycle
                    m_cycleTimer = NominalCycleTime;
                    ToolLifeRemaining = Math.Max(0.0, ToolLifeRemaining - 0.05);
                }

                PartCycleTimeRemaining = Math.Round(Math.Max(0.0, m_cycleTimer), 1);
                CurrentBlock = 100 + (int)((1.0 - (m_cycleTimer / NominalCycleTime)) * 850);

                // --- 2. Spindle Kinetics ---
                double targetRpm = CommandedSpeed * (SpindleOverride / 100.0);
                ActualSpeed += (targetRpm - ActualSpeed) * Math.Min(1.0, deltaSeconds * 2.0);

                double rpmJitter = (Math.Sin(m_elapsedTimeSeconds * 4.2) * 8.0) + ((m_random.NextDouble() - 0.5) * 5.0);
                ActualSpeed = Math.Round(Math.Max(0.0, ActualSpeed + rpmJitter), 1);

                double loadPattern = 50.0 + (12.0 * Math.Sin(m_elapsedTimeSeconds * 1.8)) + ((m_random.NextDouble() - 0.5) * 4.0);
                SpindleLoad = Math.Round(Math.Clamp(loadPattern, 5.0, 95.0), 1);

                if (Scenario != SimulationScenario.QualityFault)
                {
                    SpindleTemperature += (41.5 - SpindleTemperature) * 0.001 * deltaSeconds;
                    SpindleTemperature = Math.Round(SpindleTemperature, 2);
                    VibrationRMS = Math.Round(1.2 + (SpindleLoad / 40.0) + ((m_random.NextDouble() - 0.5) * 0.2), 2);
                }

                // --- 3. Axes Toolpath Interpolation ---
                double radius = 120.0; // mm
                double omega = 0.5; // rad/s
                double nominalFeed = 2800.0 * (FeedRateOverride / 100.0);
                ActualFeedRate = Math.Round(nominalFeed + ((m_random.NextDouble() - 0.5) * 30.0), 1);

                X_CommandedPosition = Math.Round(radius * Math.Cos(omega * m_elapsedTimeSeconds), 3);
                Y_CommandedPosition = Math.Round(radius * Math.Sin(omega * m_elapsedTimeSeconds), 3);
                double depthProgress = (NominalCycleTime - m_cycleTimer) / NominalCycleTime;
                Z_CommandedPosition = Math.Round(-5.0 - (depthProgress * 25.0), 3);

                X_ActualPosition = Math.Round(X_CommandedPosition + ((m_random.NextDouble() - 0.5) * 0.003), 3);
                Y_ActualPosition = Math.Round(Y_CommandedPosition + ((m_random.NextDouble() - 0.5) * 0.003), 3);
                Z_ActualPosition = Math.Round(Z_CommandedPosition + ((m_random.NextDouble() - 0.5) * 0.002), 3);

                X_AxisLoad = Math.Round(22.0 + (10.0 * Math.Abs(Math.Sin(omega * m_elapsedTimeSeconds))), 1);
                Y_AxisLoad = Math.Round(24.0 + (10.0 * Math.Abs(Math.Cos(omega * m_elapsedTimeSeconds))), 1);
                Z_AxisLoad = Math.Round(30.0 + (SpindleLoad * 0.1), 1);

                // --- 4. Auxiliaries ---
                CoolantFlow = true;
                CoolantPressure = Math.Round(22.5 + (Math.Sin(m_elapsedTimeSeconds * 6.0) * 0.35), 2);
                HydraulicPressure = Math.Round(60.0 + ((m_random.NextDouble() - 0.5) * 0.4), 2);
                PneumaticPressure = Math.Round(6.1 + ((m_random.NextDouble() - 0.5) * 0.1), 2);
                DriveDCBusVoltage = Math.Round(565.0 + (Math.Sin(m_elapsedTimeSeconds * 2.0) * 4.0), 1);
            }
            else
            {
                // Machine stopped or paused
                ActualSpeed = Math.Max(0.0, ActualSpeed - (deltaSeconds * 2000.0));
                SpindleLoad = 0.0;
                ActualFeedRate = 0.0;
                CoolantFlow = false;
                CoolantPressure = 0.0;
                if (Scenario != SimulationScenario.QualityFault && Scenario != SimulationScenario.ProductionDisruption)
                {
                    VibrationRMS = 0.05;
                }
                SpindleTemperature += (23.0 - SpindleTemperature) * 0.001 * deltaSeconds;
            }

            // OEE Calculations
            if (PartsProduced > 0)
            {
                OEE_Quality = Math.Round(((double)GoodParts / PartsProduced) * 100.0, 1);
            }
        }
    }
}

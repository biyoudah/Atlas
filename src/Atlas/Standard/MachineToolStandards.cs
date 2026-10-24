using System.Collections.Frozen;
using Atlas.Models;

namespace Atlas.Standard;

/// <summary>
/// Standard companion specifications for Machine Tools and Machinery:
/// - OPC 40501-1 (OPC UA for Machine Tools / umati)
/// - OPC 40001-1 (OPC UA for Machinery)
/// - OPC 10000-100 (OPC UA for Devices / DI)
/// - OPC 10000-200 (OPC UA for Industrial Automation / IA)
/// </summary>
public static class MachineToolStandards
{
    // Canonical companion specification namespace URIs
    public const string MachineToolNamespaceUri = "http://opcfoundation.org/UA/MachineTool/";
    public const string MachineryNamespaceUri = "http://opcfoundation.org/UA/Machinery/";
    public const string DeviceIntegrationNamespaceUri = "http://opcfoundation.org/UA/DI/";
    public const string IndustrialAutomationNamespaceUri = "http://opcfoundation.org/UA/IA/";

    public static readonly FrozenSet<string> StandardNamespaces = new[]
    {
        MachineToolNamespaceUri,
        MachineryNamespaceUri,
        DeviceIntegrationNamespaceUri,
        IndustrialAutomationNamespaceUri
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Standard machine tool state machine representation (preserving multi-state fidelity).
    /// Prevents collapsing complex controller states into an over-simplified boolean.
    /// </summary>
    public enum MachineToolState
    {
        Unknown,
        Ready,
        Active,
        Interrupted,
        Moving,
        FeedHold,
        ToolChange,
        Warmup,
        Manual,
        Stopped,
        Fault
    }

    /// <summary>
    /// Definition of a standard MachineTool / Machinery signal mapping rule.
    /// </summary>
    public record StandardSignalDefinition(
        string StandardLabel,
        string CanonicalUnit,
        SignalCategory Category,
        string Description);

    /// <summary>
    /// Standard OPC 40501 MachineTool path components and keywords mapped to canonical semantics.
    /// </summary>
    public static readonly Dictionary<string, StandardSignalDefinition> StandardNodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Production & KPIs (OPC 40501 PartCounters & Production)
        ["PartsProduced"] = new("parts_produced", "count", SignalCategory.Event, "Standard OPC 40501 good part counter"),
        ["PartCounter"] = new("parts_produced", "count", SignalCategory.Event, "Standard machine part counter"),
        ["PartsPerRun"] = new("parts_per_run", "count", SignalCategory.Event, "Standard target parts per production run"),
        ["PartsDefective"] = new("parts_defective", "count", SignalCategory.Event, "Standard rejected/scrap parts counter"),
        ["ActiveProgram"] = new("active_program", "string", SignalCategory.Event, "Current NC part program identifier"),
        ["CycleTime"] = new("cycle_time", "s", SignalCategory.Sample, "Standard workpiece machining cycle duration"),
        ["CycleTimeRemaining"] = new("cycle_time_remaining", "s", SignalCategory.Sample, "Estimated remaining cycle seconds"),

        // Equipment: Spindles (OPC 40501 Spindles & RotaryMotionDevice)
        ["ActualSpeed"] = new("spindle_speed", "rpm", SignalCategory.Sample, "Standard rotary spindle speed feedback"),
        ["CommandedSpeed"] = new("commanded_spindle_speed", "rpm", SignalCategory.Sample, "Programmed spindle speed setpoint"),
        ["SpindleLoad"] = new("spindle_load", "%", SignalCategory.Sample, "Spindle motor drive torque / load ratio"),
        ["SpindleOverride"] = new("spindle_override", "%", SignalCategory.Sample, "Spindle velocity override potentiometer"),
        ["SpindleTemperature"] = new("spindle_temperature", "°C", SignalCategory.Sample, "Spindle front bearing temperature"),

        // Equipment: Axes & Motion (OPC 40501 LinearMotionDevice)
        ["ActualPosition"] = new("axis_actual_position", "mm", SignalCategory.Sample, "Linear encoder position feedback"),
        ["CommandedPosition"] = new("axis_commanded_position", "mm", SignalCategory.Sample, "Programmed position target"),
        ["ActualFeedRate"] = new("feed_rate", "mm/min", SignalCategory.Sample, "Programmed tool feed velocity"),
        ["FeedOverride"] = new("feed_override", "%", SignalCategory.Sample, "Axis feed velocity override ratio"),

        // Equipment: Tooling
        ["ToolInSpindle"] = new("tool_in_spindle", "index", SignalCategory.Event, "Magazine pocket/number of active tool"),
        ["ToolLifeRemaining"] = new("tool_wear", "%", SignalCategory.Sample, "Calculated remaining tool life ratio"),

        // Monitoring & State (OPC 40001 MachineryItemState & ChannelState)
        ["MachineryItemState"] = new("machine_state", "state", SignalCategory.Event, "OPC 40001 Machinery operational state"),
        ["ExecutionState"] = new("machine_state", "state", SignalCategory.Event, "OPC 40501 Channel execution state"),
        ["OperationMode"] = new("operating_mode", "mode", SignalCategory.Event, "Discrete mode (AUTO, MANUAL, MDI, JOG)"),
        ["EmergencyStop"] = new("emergency_stop", "bool", SignalCategory.Condition, "Hardwired safety e-stop trip condition"),
        ["ActiveAlarmsCount"] = new("active_alarms_count", "count", SignalCategory.Condition, "Count of active unacknowledged alarms")
    };

    /// <summary>
    /// Parses a raw machine state string or integer into a normalized MachineToolState.
    /// Preserves full multi-state information.
    /// </summary>
    public static MachineToolState ParseStandardState(object? rawState)
    {
        if (rawState == null)
        {
            return MachineToolState.Unknown;
        }

        string str = rawState.ToString()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(str))
        {
            return MachineToolState.Unknown;
        }

        if (int.TryParse(str, out int numericState))
        {
            return numericState switch
            {
                1 => MachineToolState.Ready,
                2 => MachineToolState.Active,
                3 => MachineToolState.Interrupted,
                4 => MachineToolState.Moving,
                5 => MachineToolState.FeedHold,
                6 => MachineToolState.Stopped,
                7 => MachineToolState.Fault,
                _ => MachineToolState.Unknown
            };
        }

        return str.ToUpperInvariant() switch
        {
            "READY" or "STANDBY" or "IDLE" => MachineToolState.Ready,
            "ACTIVE" or "RUNNING" or "EXECUTING" or "PROCESSING" => MachineToolState.Active,
            "INTERRUPTED" or "PAUSED" or "HOLD" => MachineToolState.Interrupted,
            "FEED_HOLD" or "FEEDHOLD" => MachineToolState.FeedHold,
            "MOVING" or "TRAVERSING" => MachineToolState.Moving,
            "TOOL_CHANGE" or "TOOLCHANGE" => MachineToolState.ToolChange,
            "WARMUP" or "WARM_UP" => MachineToolState.Warmup,
            "MANUAL" or "JOG" or "MDI" => MachineToolState.Manual,
            "STOPPED" or "ABORTED" => MachineToolState.Stopped,
            "FAULT" or "ERROR" or "ALARM" => MachineToolState.Fault,
            _ => MachineToolState.Unknown
        };
    }
}

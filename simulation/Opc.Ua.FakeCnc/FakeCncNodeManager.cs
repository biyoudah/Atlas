using Opc.Ua.Server;

namespace Opc.Ua.FakeCnc;

/// <summary>
/// Node manager that exposes a realistic 5-axis CNC machining center address space.
/// Provides both standard semantic hierarchy (with EURange and EngineeringUnits)
/// and cryptic PLC DB tags (simulating Siemens S7-1500 / Fanuc control registers).
/// </summary>
public class FakeCncNodeManager : CustomNodeManager2
{
    public const string CncNamespaceUri = "http://opcfoundation.org/UA/CNC/";
    public const string PlcNamespaceUri = "http://siemens.com/simatic-s7-1500/cnc/";

    private readonly CncSimulationModel m_model;
    private readonly Timer m_simulationTimer;
    private readonly int m_tickIntervalMs;
    private ushort m_cncNs;
    private ushort m_plcNs;

    // Track dynamic variable nodes to update in the physics loop
    private readonly List<(BaseDataVariableState Node, Func<object> ValueGetter)> m_dynamicVariables = [];

    public FakeCncNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration,
        CncSimulationModel? model = null,
        int tickIntervalMs = 250)
        : base(server, configuration, CncNamespaceUri, PlcNamespaceUri)
    {
        m_model = model ?? new CncSimulationModel();
        m_tickIntervalMs = tickIntervalMs;
        m_simulationTimer = new Timer(OnSimulationTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            m_cncNs = NamespaceIndexes[0];
            m_plcNs = NamespaceIndexes[1];

            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference>? rootRefs))
            {
                externalReferences[ObjectIds.ObjectsFolder] = rootRefs = [];
            }

            // 1. Build Companion Spec / Semantic CNC Tree
            FolderState cncRoot = CreateFolder(null, "CNC_Machine", m_cncNs);
            cncRoot.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            rootRefs.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, cncRoot.NodeId));

            BuildIdentificationFolder(cncRoot);
            BuildStatusFolder(cncRoot);
            BuildProductionFolder(cncRoot);
            BuildSpindleFolder(cncRoot);
            BuildAxesFolder(cncRoot);
            BuildCoolantFolder(cncRoot);

            AddPredefinedNode(SystemContext, cncRoot);

            // 2. Build Cryptic PLC Logic Tree (Siemens DB emulation)
            FolderState plcRoot = CreateFolder(null, "PLC_DataBlocks", m_plcNs);
            plcRoot.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            rootRefs.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, plcRoot.NodeId));

            BuildPlcSpindleDb(plcRoot);
            BuildPlcProductionDb(plcRoot);
            BuildPlcAxesDb(plcRoot);
            BuildPlcAuxDb(plcRoot);
            BuildPlcMaintenanceDb(plcRoot);

            AddPredefinedNode(SystemContext, plcRoot);

            // Start simulation loop
            m_simulationTimer.Change(m_tickIntervalMs, m_tickIntervalMs);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            m_simulationTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnSimulationTick(object? state)
    {
        try
        {
            double deltaSeconds = m_tickIntervalMs / 1000.0;
            m_model.Tick(deltaSeconds);

            lock (Lock)
            {
                DateTime now = DateTime.UtcNow;
                foreach (var (node, valueGetter) in m_dynamicVariables)
                {
                    node.Value = ToVariant(valueGetter());
                    node.Timestamp = now;
                    node.ClearChangeMasks(SystemContext, false);
                }
            }
        }
        catch
        {
            // Simulation tick exceptions swallowed to keep server stable
        }
    }

    #region Semantic Tree Builders

    private void BuildIdentificationFolder(FolderState parent)
    {
        FolderState folder = CreateFolder(parent, "Identification", m_cncNs);
        CreateStringVariable(folder, "Manufacturer", m_model.Manufacturer, m_cncNs);
        CreateStringVariable(folder, "Model", m_model.Model, m_cncNs);
        CreateStringVariable(folder, "SerialNumber", m_model.SerialNumber, m_cncNs);
        CreateStringVariable(folder, "ControllerType", m_model.ControllerType, m_cncNs);
        CreateStringVariable(folder, "SoftwareVersion", m_model.SoftwareVersion, m_cncNs);
    }

    private void BuildStatusFolder(FolderState parent)
    {
        FolderState folder = CreateFolder(parent, "Status", m_cncNs);
        RegisterDynamic(CreateStringVariable(folder, "OperatingMode", m_model.OperatingMode, m_cncNs), () => m_model.OperatingMode);
        RegisterDynamic(CreateStringVariable(folder, "ExecutionState", m_model.ExecutionState, m_cncNs), () => m_model.ExecutionState);
        RegisterDynamic(CreateVariable(folder, "EmergencyStop", DataTypeIds.Boolean, m_model.EmergencyStop, m_cncNs), () => m_model.EmergencyStop);
        RegisterDynamic(CreateVariable(folder, "FeedHold", DataTypeIds.Boolean, m_model.FeedHold, m_cncNs), () => m_model.FeedHold);
        RegisterDynamic(CreateVariable(folder, "ActiveAlarmsCount", DataTypeIds.Int32, m_model.ActiveAlarmsCount, m_cncNs), () => m_model.ActiveAlarmsCount);
    }

    private void BuildProductionFolder(FolderState parent)
    {
        FolderState folder = CreateFolder(parent, "Production", m_cncNs);

        // Core requested metrics
        RegisterDynamic(
            CreateVariable(folder, "PartsProduced", DataTypeIds.UInt32, m_model.PartsProduced, m_cncNs,
                description: "Total parts successfully machined in current shift"),
            () => m_model.PartsProduced);

        CreateVariable(folder, "TargetParts", DataTypeIds.UInt32, m_model.TargetParts, m_cncNs, description: "Planned batch parts");
        RegisterDynamic(CreateVariable(folder, "GoodParts", DataTypeIds.UInt32, m_model.GoodParts, m_cncNs), () => m_model.GoodParts);
        RegisterDynamic(CreateVariable(folder, "DefectiveParts", DataTypeIds.UInt32, m_model.DefectiveParts, m_cncNs), () => m_model.DefectiveParts);

        RegisterDynamic(
            CreateVariable(folder, "CycleTime", DataTypeIds.Double, m_model.NominalCycleTime, m_cncNs,
                unit: "s", range: new Range { Low = 0, High = 3600 }, description: "Nominal part machining cycle time"),
            () => m_model.NominalCycleTime);

        RegisterDynamic(
            CreateVariable(folder, "PartCycleTimeRemaining", DataTypeIds.Double, m_model.PartCycleTimeRemaining, m_cncNs,
                unit: "s", range: new Range { Low = 0, High = 3600 }, description: "Real-time countdown to cycle completion"),
            () => m_model.PartCycleTimeRemaining);

        CreateStringVariable(folder, "ActiveProgram", m_model.ActiveProgram, m_cncNs);
        RegisterDynamic(CreateVariable(folder, "CurrentBlock", DataTypeIds.Int32, m_model.CurrentBlock, m_cncNs), () => m_model.CurrentBlock);

        RegisterDynamic(CreateVariable(folder, "TotalOperatingHours", DataTypeIds.Double, m_model.TotalOperatingHours, m_cncNs, unit: "h"),
            () => Math.Round(m_model.TotalOperatingHours, 2));

        RegisterDynamic(CreateVariable(folder, "CuttingHours", DataTypeIds.Double, m_model.CuttingHours, m_cncNs, unit: "h"),
            () => Math.Round(m_model.CuttingHours, 2));

        RegisterDynamic(CreateVariable(folder, "OEE_Availability", DataTypeIds.Double, m_model.OEE_Availability, m_cncNs, unit: "%", range: new Range { Low = 0, High = 100 }),
            () => m_model.OEE_Availability);
        RegisterDynamic(CreateVariable(folder, "OEE_Performance", DataTypeIds.Double, m_model.OEE_Performance, m_cncNs, unit: "%", range: new Range { Low = 0, High = 100 }),
            () => m_model.OEE_Performance);
        RegisterDynamic(CreateVariable(folder, "OEE_Quality", DataTypeIds.Double, m_model.OEE_Quality, m_cncNs, unit: "%", range: new Range { Low = 0, High = 100 }),
            () => m_model.OEE_Quality);
    }

    private void BuildSpindleFolder(FolderState parent)
    {
        FolderState folder = CreateFolder(parent, "Spindle", m_cncNs);

        // Core requested metric: Spindle Speed
        RegisterDynamic(
            CreateVariable(folder, "ActualSpeed", DataTypeIds.Double, m_model.ActualSpeed, m_cncNs,
                unit: "rpm", range: new Range { Low = 0, High = 18000 }, description: "Main spindle rotational speed with real-time encoder feedback"),
            () => m_model.ActualSpeed);

        CreateVariable(folder, "CommandedSpeed", DataTypeIds.Double, m_model.CommandedSpeed, m_cncNs,
            unit: "rpm", range: new Range { Low = 0, High = 18000 });

        RegisterDynamic(
            CreateVariable(folder, "SpindleLoad", DataTypeIds.Double, m_model.SpindleLoad, m_cncNs,
                unit: "%", range: new Range { Low = 0, High = 150 }, description: "Spindle motor drive torque utilization percentage"),
            () => m_model.SpindleLoad);

        RegisterDynamic(
            CreateVariable(folder, "SpindleTemperature", DataTypeIds.Double, m_model.SpindleTemperature, m_cncNs,
                unit: "°C", range: new Range { Low = 10, High = 100 }, description: "Spindle front bearing temperature"),
            () => m_model.SpindleTemperature);

        BaseDataVariableState overrideVar = CreateVariable(folder, "SpindleOverride", DataTypeIds.Double, m_model.SpindleOverride, m_cncNs,
            unit: "%", range: new Range { Low = 50, High = 120 }, isWritable: true);
        SetupRoleBasedAccess(overrideVar, writeRequiresAuth: true);
        overrideVar.OnSimpleWriteValue = (ISystemContext _, NodeState _, ref Variant value) =>
        {
            if (value.TryGetValue(out double d)) m_model.SpindleOverride = Math.Clamp(d, 50.0, 120.0);
            return ServiceResult.Good;
        };

        CreateStringVariable(folder, "Direction", m_model.SpindleDirection, m_cncNs);
        RegisterDynamic(CreateVariable(folder, "VibrationRMS", DataTypeIds.Double, m_model.VibrationRMS, m_cncNs, unit: "mm/s", range: new Range { Low = 0, High = 25 }),
            () => m_model.VibrationRMS);

        CreateVariable(folder, "ToolInSpindle", DataTypeIds.Int32, m_model.ToolInSpindle, m_cncNs);
        CreateStringVariable(folder, "ActiveToolName", m_model.ActiveToolName, m_cncNs);
        RegisterDynamic(CreateVariable(folder, "ToolLifeRemaining", DataTypeIds.Double, m_model.ToolLifeRemaining, m_cncNs, unit: "%", range: new Range { Low = 0, High = 100 }),
            () => Math.Round(m_model.ToolLifeRemaining, 1));
        CreateVariable(folder, "ToolWearOffset", DataTypeIds.Double, m_model.ToolWearOffset, m_cncNs, unit: "mm");
    }

    private void BuildAxesFolder(FolderState parent)
    {
        FolderState axesFolder = CreateFolder(parent, "Axes", m_cncNs);

        // X Axis
        FolderState xFolder = CreateFolder(axesFolder, "X", m_cncNs);
        RegisterDynamic(CreateVariable(xFolder, "ActualPosition", DataTypeIds.Double, m_model.X_ActualPosition, m_cncNs, unit: "mm", range: new Range { Low = -500, High = 500 }),
            () => m_model.X_ActualPosition);
        RegisterDynamic(CreateVariable(xFolder, "CommandedPosition", DataTypeIds.Double, m_model.X_CommandedPosition, m_cncNs, unit: "mm"),
            () => m_model.X_CommandedPosition);
        RegisterDynamic(CreateVariable(xFolder, "AxisLoad", DataTypeIds.Double, m_model.X_AxisLoad, m_cncNs, unit: "%", range: new Range { Low = 0, High = 100 }),
            () => m_model.X_AxisLoad);

        // Y Axis
        FolderState yFolder = CreateFolder(axesFolder, "Y", m_cncNs);
        RegisterDynamic(CreateVariable(yFolder, "ActualPosition", DataTypeIds.Double, m_model.Y_ActualPosition, m_cncNs, unit: "mm", range: new Range { Low = -500, High = 500 }),
            () => m_model.Y_ActualPosition);
        RegisterDynamic(CreateVariable(yFolder, "CommandedPosition", DataTypeIds.Double, m_model.Y_CommandedPosition, m_cncNs, unit: "mm"),
            () => m_model.Y_CommandedPosition);
        RegisterDynamic(CreateVariable(yFolder, "AxisLoad", DataTypeIds.Double, m_model.Y_AxisLoad, m_cncNs, unit: "%", range: new Range { Low = 0, High = 100 }),
            () => m_model.Y_AxisLoad);

        // Z Axis
        FolderState zFolder = CreateFolder(axesFolder, "Z", m_cncNs);
        RegisterDynamic(CreateVariable(zFolder, "ActualPosition", DataTypeIds.Double, m_model.Z_ActualPosition, m_cncNs, unit: "mm", range: new Range { Low = -300, High = 300 }),
            () => m_model.Z_ActualPosition);
        RegisterDynamic(CreateVariable(zFolder, "CommandedPosition", DataTypeIds.Double, m_model.Z_CommandedPosition, m_cncNs, unit: "mm"),
            () => m_model.Z_CommandedPosition);
        RegisterDynamic(CreateVariable(zFolder, "AxisLoad", DataTypeIds.Double, m_model.Z_AxisLoad, m_cncNs, unit: "%", range: new Range { Low = 0, High = 100 }),
            () => m_model.Z_AxisLoad);

        RegisterDynamic(CreateVariable(axesFolder, "ActualFeedRate", DataTypeIds.Double, m_model.ActualFeedRate, m_cncNs, unit: "mm/min", range: new Range { Low = 0, High = 30000 }),
            () => m_model.ActualFeedRate);

        BaseDataVariableState feedOverride = CreateVariable(axesFolder, "FeedRateOverride", DataTypeIds.Double, m_model.FeedRateOverride, m_cncNs,
            unit: "%", range: new Range { Low = 0, High = 150 }, isWritable: true);
        SetupRoleBasedAccess(feedOverride, writeRequiresAuth: true);
        feedOverride.OnSimpleWriteValue = (ISystemContext _, NodeState _, ref Variant value) =>
        {
            if (value.TryGetValue(out double d)) m_model.FeedRateOverride = Math.Clamp(d, 0.0, 150.0);
            return ServiceResult.Good;
        };
    }

    private void BuildCoolantFolder(FolderState parent)
    {
        FolderState folder = CreateFolder(parent, "Coolant", m_cncNs);
        RegisterDynamic(CreateVariable(folder, "CoolantPressure", DataTypeIds.Double, m_model.CoolantPressure, m_cncNs, unit: "bar", range: new Range { Low = 0, High = 50 }),
            () => m_model.CoolantPressure);
        RegisterDynamic(CreateVariable(folder, "CoolantFlow", DataTypeIds.Boolean, m_model.CoolantFlow, m_cncNs), () => m_model.CoolantFlow);
        RegisterDynamic(CreateVariable(folder, "CoolantTemperature", DataTypeIds.Double, m_model.CoolantTemperature, m_cncNs, unit: "°C"), () => m_model.CoolantTemperature);
        RegisterDynamic(CreateVariable(folder, "HydraulicPressure", DataTypeIds.Double, m_model.HydraulicPressure, m_cncNs, unit: "bar", range: new Range { Low = 0, High = 100 }),
            () => m_model.HydraulicPressure);
        RegisterDynamic(CreateVariable(folder, "PneumaticPressure", DataTypeIds.Double, m_model.PneumaticPressure, m_cncNs, unit: "bar", range: new Range { Low = 0, High = 10 }),
            () => m_model.PneumaticPressure);
        RegisterDynamic(CreateVariable(folder, "DoorLocked", DataTypeIds.Boolean, m_model.DoorLocked, m_cncNs), () => m_model.DoorLocked);
        RegisterDynamic(CreateVariable(folder, "ChipConveyorActive", DataTypeIds.Boolean, m_model.ChipConveyorActive, m_cncNs), () => m_model.ChipConveyorActive);
    }

    #endregion

    #region Cryptic PLC DB Builders (Siemens S7-1500 / Fanuc Emulation)

    private void BuildPlcSpindleDb(FolderState parent)
    {
        FolderState db = CreateFolder(parent, "DB10_Spindle", m_plcNs);
        RegisterDynamic(CreatePlcVariable(db, "\"DB10_Spindle\".\"Var_27\"", DataTypeIds.Double, m_model.ActualSpeed, "Var_27"),
            () => m_model.ActualSpeed);
        RegisterDynamic(CreatePlcVariable(db, "\"DB10_Spindle\".\"Var_28\"", DataTypeIds.Double, m_model.SpindleLoad, "Var_28"),
            () => m_model.SpindleLoad);
        RegisterDynamic(CreatePlcVariable(db, "\"DB10_Spindle\".\"Var_29\"", DataTypeIds.Double, m_model.SpindleTemperature, "Var_29"),
            () => m_model.SpindleTemperature);
    }

    private void BuildPlcProductionDb(FolderState parent)
    {
        FolderState db = CreateFolder(parent, "DB20_Production", m_plcNs);
        RegisterDynamic(CreatePlcVariable(db, "\"DB20_Production\".\"PartCounter\"", DataTypeIds.UInt32, m_model.PartsProduced, "PartCounter"),
            () => m_model.PartsProduced);
        RegisterDynamic(CreatePlcVariable(db, "\"DB20_Production\".\"CycleSec\"", DataTypeIds.Double, m_model.NominalCycleTime, "CycleSec"),
            () => m_model.NominalCycleTime);
        RegisterDynamic(CreatePlcVariable(db, "\"DB20_Production\".\"RemainingSec\"", DataTypeIds.Double, m_model.PartCycleTimeRemaining, "RemainingSec"),
            () => m_model.PartCycleTimeRemaining);
        CreatePlcVariable(db, "\"DB20_Production\".\"ActProg\"", DataTypeIds.String, m_model.ActiveProgram, "ActProg");
    }

    private void BuildPlcAxesDb(FolderState parent)
    {
        FolderState db = CreateFolder(parent, "DB30_Axes", m_plcNs);
        RegisterDynamic(CreatePlcVariable(db, "\"DB30_Axes\".\"X_Pos\"", DataTypeIds.Double, m_model.X_ActualPosition, "X_Pos"),
            () => m_model.X_ActualPosition);
        RegisterDynamic(CreatePlcVariable(db, "\"DB30_Axes\".\"Y_Pos\"", DataTypeIds.Double, m_model.Y_ActualPosition, "Y_Pos"),
            () => m_model.Y_ActualPosition);
        RegisterDynamic(CreatePlcVariable(db, "\"DB30_Axes\".\"Z_Pos\"", DataTypeIds.Double, m_model.Z_ActualPosition, "Z_Pos"),
            () => m_model.Z_ActualPosition);
        RegisterDynamic(CreatePlcVariable(db, "\"DB30_Axes\".\"Feed_Act\"", DataTypeIds.Double, m_model.ActualFeedRate, "Feed_Act"),
            () => m_model.ActualFeedRate);
    }

    private void BuildPlcAuxDb(FolderState parent)
    {
        FolderState db = CreateFolder(parent, "DB40_Aux", m_plcNs);
        RegisterDynamic(CreatePlcVariable(db, "\"DB40_Aux\".\"CoolantBar\"", DataTypeIds.Double, m_model.CoolantPressure, "CoolantBar"),
            () => m_model.CoolantPressure);
        RegisterDynamic(CreatePlcVariable(db, "\"DB40_Aux\".\"DoorClosed\"", DataTypeIds.Boolean, m_model.DoorLocked, "DoorClosed"),
            () => m_model.DoorLocked);
    }

    private void BuildPlcMaintenanceDb(FolderState parent)
    {
        FolderState db = CreateFolder(parent, "DB99_Maintenance", m_plcNs);

        // Protected maintenance nodes: Hidden from anonymous clients (UserAccessLevel = 0)
        BaseDataVariableState vibThreshold = CreatePlcVariable(
            db, "\"DB99_Maintenance\".\"SpindleBearingVibrationThreshold\"", DataTypeIds.Double,
            m_model.SpindleBearingVibrationThreshold, "SpindleBearingVibrationThreshold");
        SetupRoleBasedAccess(vibThreshold, readRequiresAuth: true);

        BaseDataVariableState fwKey = CreatePlcVariable(
            db, "\"DB99_Maintenance\".\"MotorDriveFirmwareKey\"", DataTypeIds.String,
            m_model.MotorDriveFirmwareKey, "MotorDriveFirmwareKey");
        SetupRoleBasedAccess(fwKey, readRequiresAuth: true);

        BaseDataVariableState busVoltage = CreatePlcVariable(
            db, "\"DB99_Maintenance\".\"DriveDCBusVoltage\"", DataTypeIds.Double,
            m_model.DriveDCBusVoltage, "DriveDCBusVoltage");
        SetupRoleBasedAccess(busVoltage, readRequiresAuth: true);
        RegisterDynamic(busVoltage, () => m_model.DriveDCBusVoltage);
    }

    #endregion

    #region Helper Methods

    private FolderState CreateFolder(NodeState? parent, string name, ushort nsIndex)
    {
        var folder = new FolderState(parent)
        {
            NodeId = new NodeId(name, nsIndex),
            BrowseName = new QualifiedName(name, nsIndex),
            DisplayName = new LocalizedText(name),
            TypeDefinitionId = ObjectTypeIds.FolderType
        };

        parent?.AddChild(folder);
        return folder;
    }

    private BaseDataVariableState CreateVariable(
        NodeState parent,
        string name,
        NodeId dataType,
        object initialValue,
        ushort nsIndex,
        string? unit = null,
        Range? range = null,
        string? description = null,
        bool isWritable = false)
    {
        var variable = new BaseDataVariableState(parent)
        {
            NodeId = new NodeId(name, nsIndex),
            BrowseName = new QualifiedName(name, nsIndex),
            DisplayName = new LocalizedText(name),
            Description = !string.IsNullOrEmpty(description) ? new LocalizedText(description) : LocalizedText.Null,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = isWritable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead,
            UserAccessLevel = isWritable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead,
            Value = ToVariant(initialValue),
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow
        };

        if (range != null)
        {
            var rangeProp = new PropertyState(variable)
            {
                NodeId = new NodeId(name + "_EURange", nsIndex),
                BrowseName = new QualifiedName(BrowseNames.EURange),
                DisplayName = new LocalizedText(BrowseNames.EURange),
                TypeDefinitionId = VariableTypeIds.PropertyType,
                ReferenceTypeId = ReferenceTypeIds.HasProperty,
                DataType = DataTypeIds.Range,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentRead,
                UserAccessLevel = AccessLevels.CurrentRead,
                Value = new Variant(new ExtensionObject(new Range { Low = range.Low, High = range.High }))
            };
            variable.AddChild(rangeProp);
        }

        if (!string.IsNullOrEmpty(unit))
        {
            var euProp = new PropertyState(variable)
            {
                NodeId = new NodeId(name + "_EU", nsIndex),
                BrowseName = new QualifiedName(BrowseNames.EngineeringUnits),
                DisplayName = new LocalizedText(BrowseNames.EngineeringUnits),
                TypeDefinitionId = VariableTypeIds.PropertyType,
                ReferenceTypeId = ReferenceTypeIds.HasProperty,
                DataType = DataTypeIds.EUInformation,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentRead,
                UserAccessLevel = AccessLevels.CurrentRead,
                Value = new Variant(new ExtensionObject(new EUInformation
                {
                    DisplayName = new LocalizedText(unit),
                    Description = new LocalizedText(unit)
                }))
            };
            variable.AddChild(euProp);
        }

        parent.AddChild(variable);
        return variable;
    }

    private BaseDataVariableState CreateStringVariable(NodeState parent, string name, string initialValue, ushort nsIndex)
    {
        return CreateVariable(parent, name, DataTypeIds.String, initialValue, nsIndex);
    }

    private BaseDataVariableState CreatePlcVariable(
        NodeState parent,
        string stringNodeId,
        NodeId dataType,
        object initialValue,
        string browseName)
    {
        var variable = new BaseDataVariableState(parent)
        {
            NodeId = new NodeId(stringNodeId, m_plcNs),
            BrowseName = new QualifiedName(browseName, m_plcNs),
            DisplayName = new LocalizedText(browseName),
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = ToVariant(initialValue),
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow
        };

        parent.AddChild(variable);
        return variable;
    }

    private static Variant ToVariant(object? value)
    {
        return value switch
        {
            null => Variant.Null,
            bool b => Variant.From(b),
            sbyte sb => Variant.From(sb),
            byte b => Variant.From(b),
            short s => Variant.From(s),
            ushort us => Variant.From(us),
            int i => Variant.From(i),
            uint u => Variant.From(u),
            long l => Variant.From(l),
            ulong ul => Variant.From(ul),
            float f => Variant.From(f),
            double d => Variant.From(d),
            string str => Variant.From(str),
            DateTime dt => Variant.From((DateTimeUtc)dt),
            DateTimeUtc dtUtc => Variant.From(dtUtc),
            ExtensionObject eo => Variant.From(eo),
            NodeId nid => Variant.From(nid),
            Variant v => v,
            _ => VariantHelper.TryCastFrom(value, out Variant parsed) ? parsed : Variant.Null
        };
    }

    private void RegisterDynamic(BaseDataVariableState node, Func<object> valueGetter)
    {
        m_dynamicVariables.Add((node, valueGetter));
    }

    private static void SetupRoleBasedAccess(
        BaseDataVariableState node,
        bool readRequiresAuth = false,
        bool writeRequiresAuth = false)
    {
        node.OnReadUserAccessLevel = (ISystemContext context, NodeState state, ref byte userAccessLevel) =>
        {
            var serverContext = context as ServerSystemContext;
            IUserIdentity? identity = serverContext?.UserIdentity;
            bool isAuthenticated = identity != null &&
                identity.TokenType != UserTokenType.Anonymous &&
                !string.IsNullOrEmpty(identity.DisplayName);

            if (readRequiresAuth && !isAuthenticated)
            {
                userAccessLevel = 0; // Completely hidden from anonymous
                return ServiceResult.Good;
            }

            byte access = AccessLevels.CurrentRead;
            if (writeRequiresAuth)
            {
                if (isAuthenticated)
                {
                    access |= AccessLevels.CurrentWrite;
                }
            }
            else if ((node.AccessLevel & AccessLevels.CurrentWrite) != 0)
            {
                access |= AccessLevels.CurrentWrite;
            }

            userAccessLevel = access;
            return ServiceResult.Good;
        };
    }

    #endregion
}

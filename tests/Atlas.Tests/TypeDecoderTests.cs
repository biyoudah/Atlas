using Moq;
using NUnit.Framework;
using Opc.Ua;
using Atlas.Decoding;
using Opc.Ua.Client;

namespace Atlas.Tests;

[TestFixture]
public class TypeDecoderTests
{
    [Test]
    public void Flatten_NullOrEmpty_ReturnsEmptyList()
    {
        Assert.That(TypeDecoder.Flatten(null), Is.Empty);
    }

    [Test]
    public void Flatten_SimpleEncodeable_ExtractsCleanFieldPaths()
    {
        var range = new Range { Low = 10.5, High = 500.0 };
        var ext = new ExtensionObject(range);

        IReadOnlyList<FlattenedMember> members = TypeDecoder.Flatten(ext, prefix: "EURange");

        Assert.That(members.Count, Is.EqualTo(2));
        FlattenedMember lowMember = members.First(m => m.Path == "EURange.Low");
        FlattenedMember highMember = members.First(m => m.Path == "EURange.High");

        Assert.That(lowMember.Value, Is.EqualTo(10.5));
        Assert.That(lowMember.DataType, Is.EqualTo("Double"));

        Assert.That(highMember.Value, Is.EqualTo(500.0));
        Assert.That(highMember.DataType, Is.EqualTo("Double"));
    }

    [Test]
    public void Flatten_ArrayOfEncodeables_ProducesIndexedBracketPaths()
    {
        var array = new[]
        {
            new Range { Low = 0.0, High = 100.0 },
            new Range { Low = -50.0, High = 50.0 }
        };

        IReadOnlyList<FlattenedMember> members = TypeDecoder.Flatten(array, prefix: "AxesLimits");

        Assert.That(members.Count, Is.EqualTo(4));
        Assert.That(members.Any(m => m.Path == "AxesLimits[0].Low" && (double)m.Value! == 0.0), Is.True);
        Assert.That(members.Any(m => m.Path == "AxesLimits[0].High" && (double)m.Value! == 100.0), Is.True);
        Assert.That(members.Any(m => m.Path == "AxesLimits[1].Low" && (double)m.Value! == -50.0), Is.True);
        Assert.That(members.Any(m => m.Path == "AxesLimits[1].High" && (double)m.Value! == 50.0), Is.True);
    }

    [Test]
    public void FlattenFromDefinition_ParsesStaticSchemaWithoutLiveInstance()
    {
        var definition = new StructureDefinition
        {
            StructureType = StructureType.Structure,
            Fields = new StructureField[]
            {
                new() { Name = "ActualPosition", DataType = DataTypeIds.Double, Description = new LocalizedText("Current position in mm") },
                new() { Name = "FollowingLag", DataType = DataTypeIds.Double },
                new() { Name = "DriveActive", DataType = DataTypeIds.Boolean }
            }
        };

        IReadOnlyList<FlattenedMember> members = TypeDecoder.FlattenFromDefinition(definition, prefix: "AxisData");

        Assert.That(members.Count, Is.EqualTo(3));
        Assert.That(members[0].Path, Is.EqualTo("AxisData.ActualPosition"));
        Assert.That(members[0].Description, Is.EqualTo("Current position in mm"));
        Assert.That(members[1].Path, Is.EqualTo("AxisData.FollowingLag"));
        Assert.That(members[2].Path, Is.EqualTo("AxisData.DriveActive"));
    }

    [Test]
    public async Task ReadDataTypeDefinitionAsync_ReturnsParsedDefinition()
    {
        var mockSession = new Mock<ISessionClient>(MockBehavior.Strict);
        var expectedDef = new StructureDefinition
        {
            Fields = new StructureField[]
            {
                new() { Name = "Var_A", DataType = DataTypeIds.Int32 }
            }
        };

        var customDataTypeId = new NodeId("CustomUDT", 2);

        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader?>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.Is<ArrayOf<ReadValueId>>(r => r[0].NodeId == customDataTypeId && r[0].AttributeId == Attributes.DataTypeDefinition),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(new ReadResponse
            {
                Results = [new DataValue(new Variant(new ExtensionObject(expectedDef)))]
            }));

        mockSession.SetupGet(s => s.MessageContext).Returns(ServiceMessageContext.Create(null));

        StructureDefinition? result = await TypeDecoder.ReadDataTypeDefinitionAsync(mockSession.Object, customDataTypeId);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Fields.Count, Is.EqualTo(1));
        Assert.That(result.Fields[0].Name, Is.EqualTo("Var_A"));
    }
}

using System.Text.Json;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;

namespace OpenUtauMcp.Contracts.Tests;

public class SerializationTests {
    [Fact]
    public void EditApplyParams_RoundTrip_PreservesPolymorphicOps() {
        var original = new EditApplyParams {
            UndoName = "MCP: edit 2 notes",
            IfRev = new Dictionary<string, int> { ["p3"] = 41 },
            Ops = [
                new NoteUpdateOp { NoteId = "n12", Lyric = "crown", Tone = 74 },
                new CurveReplaceOp { PartId = "p3", Abbr = "dyn", Xs = [0, 480], Ys = [-20, 10] },
            ],
        };

        string json = JsonSerializer.Serialize(original, ContractsJson.Context.EditApplyParams);
        var back = JsonSerializer.Deserialize(json, ContractsJson.Context.EditApplyParams)!;

        Assert.Contains("\"op\":\"note.update\"", json);
        Assert.Equal(2, back.Ops.Count);
        var note = Assert.IsType<NoteUpdateOp>(back.Ops[0]);
        Assert.Equal("crown", note.Lyric);
        Assert.Null(note.Pos);
        var curve = Assert.IsType<CurveReplaceOp>(back.Ops[1]);
        Assert.Equal([0, 480], curve.Xs);
        Assert.Equal(41, back.IfRev!["p3"]);
    }

    [Fact]
    public void EditOp_Deserialize_DiscriminatorNotFirst_Works() {
        const string json = """{"ops":[{"noteId":"n1","tone":60,"op":"note.update"}]}""";

        var p = JsonSerializer.Deserialize(json, ContractsJson.Context.EditApplyParams)!;

        Assert.Equal(60, Assert.IsType<NoteUpdateOp>(p.Ops[0]).Tone);
    }

    [Fact]
    public void Request_Deserialize_UnknownFieldsIgnored() {
        const string json = """{"jsonrpc":"2.0","id":7,"method":"bridge.ping","future":{"x":1},"params":{}}""";

        var req = JsonSerializer.Deserialize(json, ContractsJson.Context.RpcRequest)!;

        Assert.Equal(7, req.Id);
        Assert.Equal(Methods.Ping, req.Method);
    }

    [Fact]
    public void Response_Serialize_OmitsNulls() {
        var resp = new RpcResponse { Id = 1, Result = JsonSerializer.SerializeToNode(new PingResult { Ready = true, Epoch = 2 }, ContractsJson.Context.PingResult) };

        string json = JsonSerializer.Serialize(resp, ContractsJson.Context.RpcResponse);

        Assert.DoesNotContain("\"error\"", json);
        Assert.DoesNotContain("uiLatencyMs", json);
        Assert.Contains("\"jsonrpc\":\"2.0\"", json);
    }
}

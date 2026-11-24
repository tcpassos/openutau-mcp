using System.Text.Json;
using ModelContextProtocol.Protocol;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Tools.Core;

namespace OpenUtauMcp.Server.Tests;

public class DerivedToolsTests {
    static string Text(CallToolResult r) => ((TextContentBlock)r.Content[0]).Text;

    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // Part p1 starts at bar 9 (tick 15360). n1 = A4 for one quarter, n2 = C5 for one quarter.
    static FakeBridge Bridge(bool stale = false) => FakeBridge.WithProject()
        .On(Methods.NotesQuery, _ => new NotesQueryResult {
            PartId = "p1", PartPos = 15360, PartRev = 4, Epoch = 1, Total = 2,
            Notes = [
                new NoteDto {
                    Id = "n1", Pos = 0, Dur = 480, Tone = 69, Lyric = "crown",
                    Phonemes = [
                        new PhonemeDto { Index = 0, Phoneme = "k", Pos = -60, Dur = 60, PosMs = -62.5, DurMs = 62.5 },
                        new PhonemeDto { Index = 1, Phoneme = "r", Pos = 0, Dur = 60, PosMs = 0, DurMs = 62.5, Alias = "w", PreutterDeltaMs = 12 },
                    ],
                    Phonemizer = "DiffSinger English",
                },
                new NoteDto { Id = "n2", Pos = 480, Dur = 480, Tone = 72, Lyric = "+" },
            ],
            Warnings = stale ? [new Warning(WarningCodes.DerivedStale, "not phonemized yet", "p1")] : null,
        }, ContractsJson.Context.NotesQueryResult)
        .On(Methods.EditApply, _ => new EditApplyResult {
            Epoch = 1, Applied = 1, DryRun = false, PartRevs = new Dictionary<string, int> { ["p1"] = 5 }, StructureRev = 1,
        }, ContractsJson.Context.EditApplyResult)
        .On(Methods.PhrasesQuery, _ => new PhrasesQueryResult {
            PartId = "p1", PartRev = 4, Epoch = 1, StepTicks = 5,
            Phrases = [new PhraseDto {
                StartTick = 15360, EndTick = 16320, SeriesStartTick = 15360 - 120, NoteIds = ["n1", "n2"],
                // 24 lead-in samples, then n1 flat at A4 +10c for 96 samples, then n2 rising from C5 -50c to C5.
                Series = new Dictionary<string, IReadOnlyList<float>> {
                    ["pitch"] = Enumerable.Repeat(6900f, 24)
                        .Concat(Enumerable.Repeat(6910f, 96))
                        .Concat(Enumerable.Range(0, 96).Select(i => 7150f + 50f * i / 95))
                        .ToList(),
                },
            }],
        }, ContractsJson.Context.PhrasesQueryResult);

    [Fact]
    public async Task NotesGet_Phonemes_WaitsAndShowsOverrides() {
        var bridge = Bridge(stale: true);

        var result = await NotesGetTool.Run(bridge, new NoteSelector("Lead"), detail: "full", fields: ["phonemes", "phonemizer"]);

        Assert.False(result.IsError, Text(result));
        var query = bridge.Calls.First(c => c.method == Methods.NotesQuery).@params.Deserialize(ContractsJson.Context.NotesQueryParams)!;
        Assert.Equal(5000, query.AwaitMs);
        Assert.Equal(["phonemes"], query.Fields);
        string text = Text(result);
        Assert.Contains("[\"k\",-62.5,62.5]", text);
        Assert.Contains("[\"r\",0,62.5,{\"alias\":\"w\",\"preutterDeltaMs\":12}]", text);
        Assert.Contains("\"DiffSinger English\"", text);
        Assert.Contains("DERIVED_STALE", text);
    }

    [Fact]
    public async Task NotesGet_UnknownField_IsRejected() {
        var result = await NotesGetTool.Run(Bridge(), new NoteSelector("Lead"), detail: "full", fields: ["phonems"]);

        Assert.True(result.IsError);
        Assert.Contains("Unknown field 'phonems'", Text(result));
    }

    [Fact]
    public async Task NotesEdit_PhonemeOps_InOrder() {
        var bridge = Bridge();

        var result = await NotesEditTool.Run(bridge, "p1", update: [
            new NoteUpdateInput("n1", ResetExprs: true, Phonemizer: "default",
                Phonemes: Json("[{\"index\":1,\"alias\":\"w\",\"offsetTicks\":-10,\"preutterDeltaMs\":15}]")),
            new NoteUpdateInput("n2", Phonemes: Json("\"clear\"")),
        ]);

        Assert.False(result.IsError, Text(result));
        var ops = bridge.Calls.Single(c => c.method == Methods.EditApply).@params.Deserialize(ContractsJson.Context.EditApplyParams)!.Ops;
        Assert.IsType<NoteResetExprsOp>(ops[0]);
        Assert.Null(Assert.IsType<NotePhonemizerOp>(ops[1]).Phonemizer);
        var ph = Assert.IsType<NotePhonemesOp>(ops[2]);
        Assert.Equal((1, "w", -10, 15f), (ph.Overrides![0].Index, ph.Overrides[0].Alias, ph.Overrides[0].Offset, ph.Overrides[0].PreutterDeltaMs));
        Assert.Null(Assert.IsType<NotePhonemesOp>(ops[3]).Overrides);
    }

    [Fact]
    public async Task NotesEdit_PhonemeUnknownField_IsRejected() {
        var result = await NotesEditTool.Run(Bridge(), "p1", update: [new NoteUpdateInput("n1", Phonemes: Json("[{\"index\":0,\"offset\":5}]"))]);

        Assert.True(result.IsError);
        Assert.Contains("unknown field 'offset'", Text(result));
    }

    [Fact]
    public async Task CurvesGet_FinalPitch_PerNoteDeviation_WithoutCurveQuery() {
        var bridge = Bridge();

        var result = await CurvesGetTool.Run(bridge, "Lead", curves: ["final_pitch"]);

        Assert.False(result.IsError, Text(result));
        Assert.DoesNotContain(bridge.Calls, c => c.method == Methods.CurvesQuery);
        string text = Text(result);
        Assert.Contains("[\"n1\",\"crown\",10,10,10,10,10]", text);
        // n2: -50c rising to 0c, sampled every 5 ticks.
        Assert.Matches("\\[\"n2\",\"\\+\",-25,-50,0,-4\\d,-\\d\\]", text);
    }

    [Fact]
    public async Task CurvesGet_FinalPitch_PerPhraseNamesRange() {
        var result = await CurvesGetTool.Run(Bridge(), "Lead", curves: ["final_pitch"], per: "phrase");

        Assert.Contains("[\"9:1\",\"9:3\",2,\"A4+10c\",\"C5\"]", Text(result));
    }
}

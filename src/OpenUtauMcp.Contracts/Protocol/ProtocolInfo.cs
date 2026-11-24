namespace OpenUtauMcp.Contracts.Protocol;

public static class ProtocolInfo {
    /// <summary>Major protocol version. Changes only on incompatible changes.</summary>
    public const int Version = 1;

    /// <summary>Pipe name is PipePrefix + OpenUtau process id.</summary>
    public const string PipePrefix = "openutau-mcp-";

    /// <summary>Environment variable that pins the bridge (OpenUtau pid) when several are running.</summary>
    public const string PidEnvironmentVariable = "OPENUTAU_MCP_BRIDGE_PID";

    public const int MaxMessageBytes = 16 * 1024 * 1024;
    public const int MaxOpsPerApply = 20000;
    public const int MaxNotesPerQuery = 2000;
    public const int MaxCurvePoints = 100000;

    /// <summary>OpenUtau resolution: ticks per quarter note.</summary>
    public const int TicksPerQuarter = 480;

    /// <summary>Curve x positions are snapped to this grid by OpenUtau.</summary>
    public const int CurveGridTicks = 5;
}

public static class Methods {
    public const string Hello = "bridge.hello";
    public const string Ping = "bridge.ping";
    public const string Status = "bridge.status";
    public const string Cancel = "$/cancel";
    public const string ProjectGet = "project.get";
    public const string NotesQuery = "notes.query";
    public const string EditApply = "edit.apply";
    public const string HistoryGet = "history.get";
    public const string HistoryUndo = "history.undo";
    public const string HistoryRedo = "history.redo";
    public const string ProjectOpen = "project.open";
    public const string ProjectSave = "project.save";
    public const string ProjectBackup = "project.backup";
    public const string PlaybackControl = "playback.control";
    public const string PlaybackGet = "playback.get";
    public const string JobsStart = "jobs.start";
    public const string JobsGet = "jobs.get";
    public const string JobsWait = "jobs.wait";
    public const string JobsCancel = "jobs.cancel";
    public const string ProjectNew = "project.new";
    public const string CurvesQuery = "curves.query";
    public const string PhrasesQuery = "phrases.query";
    public const string BatchRun = "batch.run";
    public const string CatalogSinger = "catalog.singer";
    public const string CatalogExpressions = "catalog.expressions";
    public const string CatalogSingers = "catalog.singers";
    public const string CatalogPhonemizers = "catalog.phonemizers";
    public const string CatalogRenderers = "catalog.renderers";
    public const string G2pQuery = "g2p.query";
    public const string ProjectImportTracks = "project.importTracks";
    public const string ProjectExport = "project.export";
    public const string DawGet = "daw.get";
    public const string DawScan = "daw.scan";
    public const string DawConnect = "daw.connect";
    public const string DawDisconnect = "daw.disconnect";
    public const string DawBind = "daw.bind";
    /// <summary>Bridge -> client notification method; params are <see cref="EventParams"/>.</summary>
    public const string Event = "event";
}

public static class Capabilities {
    public const string Edit = "edit";
    public const string History = "history";
    public const string EarlyStart = "earlyStart";
    public const string Files = "files";
    public const string Playback = "playback";
    public const string JobsExport = "jobs.export";
    public const string Events = "events";
    public const string Catalog = "catalog";
    public const string StructureOps = "structureOps";
    /// <summary>Phonemes in notes.query, phrases.query, note.phonemes/phonemizer/resetExprs ops.</summary>
    public const string Derived = "derived";
    /// <summary>batch.run and the batch.* job kinds.</summary>
    public const string JobsBatch = "jobs.batch";
    /// <summary>project.importTracks, project.export, part.addWave, catalog lists and g2p.query.</summary>
    public const string FilesExtra = "filesExtra";
    /// <summary>export.custom job and daw.* methods.</summary>
    public const string ExportCustom = "export.custom";
    public const string Daw = "daw";
}

/// <summary>Event topics pushed by the bridge.</summary>
public static class EventTopics {
    public const string ProjectLoaded = "project.loaded";
    public const string ProjectChanged = "project.changed";
    public const string JobProgress = "job.progress";
    public const string JobDone = "job.done";
    public const string Error = "error";
}

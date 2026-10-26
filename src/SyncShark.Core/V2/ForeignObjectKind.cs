namespace Massdex.SyncShark;

/// <summary>How core v2 treats an unsafe-looking listed item (see PilotPath.ClassifyForeign).</summary>
public enum ForeignObjectKind { None, Link, LocalCloud, OnlineOnlyFile }

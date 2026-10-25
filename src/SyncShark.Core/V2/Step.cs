namespace Massdex.SyncShark.V2;

/// <summary>One action's execution slot: an ordered phase and a level inside that phase.</summary>
public sealed record Step(int Phase, int Level);

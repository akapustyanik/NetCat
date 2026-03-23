namespace ServiceLib.Enums;

public enum EUpdateFailureStage
{
    None = 0,
    Check = 1,
    ReleaseLookup = 2,
    ReleaseParsing = 3,
    AssetSelection = 4,
    Download = 5,
    Stage = 6,
    Unpack = 7,
    Launch = 8,
    Restart = 9,
    Cleanup = 10
}

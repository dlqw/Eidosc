namespace Eidosc.Mir;

public readonly record struct StaticBranchInfo(
    string BranchFunctionName,
    TypeId ReturnTypeId,
    FunctionId? BranchFunctionId = null);

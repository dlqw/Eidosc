using Eidosc.Borrow;
using Eidosc.Mir;

namespace Eidosc.Pipeline;

public sealed record SendAnalysisSnapshot(
    string SchemaVersion,
    string MirModuleFingerprint,
    string SendDependencyHash,
    IReadOnlyList<SendAnalysisFunctionSnapshot> Functions)
{
    public const string CurrentSchemaVersion = "send-analysis-snapshot-v1";

    public static SendAnalysisSnapshot Create(
        MirFunctionFingerprintSnapshot mirFingerprints,
        string sendDependencyHash,
        IReadOnlyList<SendAnalysisFunctionSnapshot> functions) =>
        new(CurrentSchemaVersion, mirFingerprints.ModuleFingerprint, sendDependencyHash, functions);

    public static string ComputeDependencyHash(MirModule module)
    {
        var payload = new
        {
            TypeDescriptors = module.TypeDescriptors
                .OrderBy(static pair => pair.Key)
                .Select(static pair => new
                {
                    Id = pair.Key,
                    Descriptor = pair.Value.ToString()
                })
                .ToArray(),
            DynamicTypeKeys = module.DynamicTypeKeys
                .OrderBy(static pair => pair.Key)
                .ToArray(),
            FunctionParameters = module.Functions
                .Select(static function => new
                {
                    Key = MirFunctionIdentity.GetStableKey(function),
                    Parameters = function.Locals.Count(static local => local.IsParameter)
                })
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .ToArray()
        };

        return ModuleArtifactHash.ComputeJsonHash(payload);
    }
}

public sealed record SendAnalysisFunctionSnapshot(
    string FunctionKey,
    string BodyHash,
    IReadOnlyList<SendAnalysisErrorSnapshot> Errors);

public sealed record SendAnalysisErrorSnapshot(
    string Message,
    int Block,
    int InstructionIndex)
{
    public static SendAnalysisErrorSnapshot FromError(SendCheckError error) =>
        new(error.Message, error.Block.Value, error.InstructionIndex);
}

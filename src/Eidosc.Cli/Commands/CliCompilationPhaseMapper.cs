using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

internal static class CliCompilationPhaseMapper
{
    public static CompilationPhase? MapPhase(CompilePhase? phase, CompilationPhase? defaultPhase = null)
    {
        return phase switch
        {
            CompilePhase.Lexer => CompilationPhase.Lexer,
            CompilePhase.Parser => CompilationPhase.Parser,
            CompilePhase.Namer => CompilationPhase.Namer,
            CompilePhase.Types => CompilationPhase.Types,
            CompilePhase.Effects => CompilationPhase.Effects,
            CompilePhase.Borrow => CompilationPhase.Borrow,
            CompilePhase.Hir => CompilationPhase.Hir,
            CompilePhase.Mir => CompilationPhase.Mir,
            CompilePhase.Llvm => CompilationPhase.Llvm,
            CompilePhase.CodeGen => null,
            null => defaultPhase,
            _ => defaultPhase
        };
    }

    public static CompilationPhase? MapTargetToStopPhase(CompileTarget target)
    {
        return target switch
        {
            CompileTarget.Tokens => CompilationPhase.Lexer,
            CompileTarget.Ast => CompilationPhase.Parser,
            CompileTarget.Resolved => CompilationPhase.Namer,
            CompileTarget.Typed => CompilationPhase.Types,
            CompileTarget.Hir => CompilationPhase.Hir,
            CompileTarget.Mir => CompilationPhase.Mir,
            CompileTarget.LlvmIr => CompilationPhase.Llvm,
            CompileTarget.Native => CompilationPhase.Llvm,
            CompileTarget.Cil => null,
            _ => null
        };
    }

    public static CompilationTarget MapTarget(CompileTarget target)
    {
        return target switch
        {
            CompileTarget.Tokens => CompilationTarget.Tokens,
            CompileTarget.Ast => CompilationTarget.Ast,
            CompileTarget.Resolved => CompilationTarget.Resolved,
            CompileTarget.Typed => CompilationTarget.Typed,
            CompileTarget.Hir => CompilationTarget.Hir,
            CompileTarget.Mir => CompilationTarget.Mir,
            CompileTarget.LlvmIr => CompilationTarget.LlvmIr,
            CompileTarget.Native => CompilationTarget.LlvmIr,
            CompileTarget.Cil => CompilationTarget.Cil,
            _ => CompilationTarget.Cil
        };
    }
}

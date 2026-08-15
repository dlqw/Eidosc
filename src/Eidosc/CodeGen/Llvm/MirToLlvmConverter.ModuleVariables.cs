using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Symbols;
using Eidosc.Types;

namespace Eidosc.CodeGen.Llvm;

/// <summary>
/// Module-level mutable variable lowering: MirModuleVar metadata to LlvmGlobal storage.
/// </summary>
public sealed partial class MirToLlvmConverter
{
    private void LowerModuleVariables(MirModule mirModule, LlvmModule llvmModule)
    {
        foreach (var moduleVar in mirModule.ModuleVars)
        {
            var loweredType = LowerStorageTypeIdOrReport(moduleVar.TypeId, "module variable");
            if (loweredType is LlvmVoidType)
            {
                continue;
            }

            // extern(c) 变量：以 C 符号名直连 declaration-only 全局（无定义、无初始化）。
            if (moduleVar.IsExternal)
            {
                var externalGlobal = new LlvmGlobal
                {
                    Name = !string.IsNullOrWhiteSpace(moduleVar.ExternalName)
                        ? moduleVar.ExternalName!
                        : moduleVar.Name,
                    Type = loweredType,
                    Linkage = LlvmLinkage.External,
                    IsExternalDeclaration = true
                };
                llvmModule.Globals.Add(externalGlobal);
                llvmModule.NamedGlobals[externalGlobal.Name] = externalGlobal;

                if (moduleVar.SymbolId.IsValid)
                {
                    _moduleVarGlobalsBySymbol[moduleVar.SymbolId] = externalGlobal;
                }

                if (!string.IsNullOrWhiteSpace(moduleVar.Name))
                {
                    _moduleVarGlobalsByName[moduleVar.Name] = externalGlobal;
                }

                continue;
            }

            var globalName = _nameMangler.MangleGlobalName(mirModule.Name, moduleVar.Name);
            var runtimeInitName = moduleVar.RuntimeInitializerName;
            var initializer = runtimeInitName != null
                ? new LlvmZeroInitializer { Type = loweredType }
                : TryConvertModuleVarInitializer(moduleVar, loweredType, out var constant)
                    ? constant
                    : ReportModuleVarInitializerFallback(moduleVar, loweredType);

            var global = new LlvmGlobal
            {
                Name = globalName,
                Type = loweredType,
                Initializer = initializer,
                Linkage = LlvmLinkage.Internal,
                IsConstant = false
            };
            llvmModule.Globals.Add(global);
            llvmModule.NamedGlobals[globalName] = global;

            if (runtimeInitName != null)
            {
                _runtimeInitModuleVars.Add(new RuntimeInitModuleVarEntry(global, runtimeInitName, moduleVar.RuntimeInitOrder));
            }

            if (moduleVar.SymbolId.IsValid)
            {
                _moduleVarGlobalsBySymbol[moduleVar.SymbolId] = global;
            }

            if (!string.IsNullOrWhiteSpace(moduleVar.Name))
            {
                _moduleVarGlobalsByName[moduleVar.Name] = global;
            }
        }
    }

    private bool TryConvertModuleVarInitializer(
        MirModuleVar moduleVar,
        LlvmType loweredType,
        out LlvmValue initializer)
    {
        if (moduleVar.Initializer is MirConstant
            {
                Value: not MirConstantValue.StringValue and not MirConstantValue.RawStringValue
            } constant &&
            loweredType is LlvmIntType or LlvmFloatType)
        {
            initializer = ConvertConstantToLlvm(constant, loweredType);
            return initializer.Type.Equals(loweredType);
        }

        initializer = null!;
        return false;
    }

    private LlvmValue ReportModuleVarInitializerFallback(MirModuleVar moduleVar, LlvmType loweredType)
    {
        var diagnostic = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.ModuleVariableInitializerNotStaticScalar(moduleVar.Name),
            "E5313");
        if (HasSpan(moduleVar.Span))
        {
            diagnostic.WithLabel(moduleVar.Span, DiagnosticMessages.ModuleVariableInitializerNotStaticScalarLabel);
        }

        Diagnostics.Add(diagnostic);
        return new LlvmZeroInitializer
        {
            Type = loweredType
        };
    }

    private LlvmValue ResolveModuleVarGlobal(MirPlace place)
    {
        if (place.ModuleVarSymbol.IsValid &&
            _moduleVarGlobalsBySymbol.TryGetValue(place.ModuleVarSymbol, out var bySymbol))
        {
            return bySymbol;
        }

        if (!string.IsNullOrWhiteSpace(place.ModuleVarName) &&
            _moduleVarGlobalsByName.TryGetValue(place.ModuleVarName, out var byName))
        {
            return byName;
        }

        return ReportUnsupportedPlaceKindFallback(place, "module variable place conversion");
    }
}

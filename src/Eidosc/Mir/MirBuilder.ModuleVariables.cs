using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

/// <summary>
/// Module-level mutable variable registration and constant initializer lowering.
/// </summary>
public sealed partial class MirBuilder
{
    private void RegisterModuleVars(IReadOnlyList<HirVarDecl> moduleVars)
    {
        foreach (var varDecl in moduleVars)
        {
            var typeId = ResolveModuleVarType(varDecl);
            var moduleVar = new MirModuleVar
            {
                Name = string.IsNullOrWhiteSpace(varDecl.Name) ? "$var" : varDecl.Name,
                SymbolId = varDecl.SymbolId,
                TypeId = typeId,
                IsMutable = true,
                Initializer = ConvertModuleVarInitializer(varDecl, typeId),
                Span = varDecl.Span
            };

            if (varDecl.SymbolId.IsValid)
            {
                _moduleVarsBySymbol[varDecl.SymbolId] = moduleVar;
            }

            if (!string.IsNullOrWhiteSpace(varDecl.Name))
            {
                _moduleVarsByName[varDecl.Name] = moduleVar;
            }
        }
    }

    private List<MirModuleVar> CreateModuleVarList(IReadOnlyList<HirVarDecl> moduleVars)
    {
        var result = new List<MirModuleVar>(moduleVars.Count);
        foreach (var varDecl in moduleVars)
        {
            var moduleVar = ResolveRegisteredModuleVar(varDecl);
            if (moduleVar == null)
            {
                continue;
            }

            result.Add(moduleVar);
        }

        return result;
    }

    private MirModuleVar? ResolveRegisteredModuleVar(HirVarDecl varDecl)
    {
        if (varDecl.SymbolId.IsValid &&
            _moduleVarsBySymbol.TryGetValue(varDecl.SymbolId, out var bySymbol))
        {
            return bySymbol;
        }

        if (!string.IsNullOrWhiteSpace(varDecl.Name) &&
            _moduleVarsByName.TryGetValue(varDecl.Name, out var byName))
        {
            return byName;
        }

        return null;
    }

    private bool TryResolveModuleVarPlace(HirVar variable, out MirPlace place)
    {
        if (variable.SymbolId.IsValid &&
            _moduleVarsBySymbol.TryGetValue(variable.SymbolId, out var bySymbol))
        {
            place = CreateModuleVarPlace(variable, bySymbol);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(variable.Name) &&
            _moduleVarsByName.TryGetValue(variable.Name, out var byName))
        {
            place = CreateModuleVarPlace(variable, byName);
            return true;
        }

        place = null!;
        return false;
    }

    private static MirPlace CreateModuleVarPlace(HirVar variable, MirModuleVar moduleVar)
    {
        return new MirPlace
        {
            Kind = PlaceKind.ModuleVar,
            ModuleVarName = moduleVar.Name,
            ModuleVarSymbol = moduleVar.SymbolId,
            TypeId = variable.TypeId.IsValid ? variable.TypeId : moduleVar.TypeId,
            Span = variable.Span
        };
    }

    private static TypeId ResolveModuleVarType(HirVarDecl varDecl)
    {
        if (varDecl.TypeId.IsValid)
        {
            return varDecl.TypeId;
        }

        return varDecl.Initializer?.TypeId ?? TypeId.None;
    }

    private MirOperand ConvertModuleVarInitializer(HirVarDecl varDecl, TypeId fallbackType)
    {
        if (TryConvertModuleVarConstantInitializer(varDecl.Initializer, out var constant))
        {
            return constant;
        }

        var diagnostic = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.ModuleVariableInitializerNotConstant(varDecl.Name),
            "E5301");
        if (HasSpan(varDecl.Span))
        {
            diagnostic.WithLabel(
                varDecl.Span,
                DiagnosticMessages.ModuleVariableInitializerNotConstantLabel);
        }

        Diagnostics.Add(diagnostic);
        return CreatePoisonOperand(
            fallbackType,
            varDecl.Span,
            DiagnosticMessages.ModuleVariableInitializerNotConstantReason(varDecl.Name));
    }

    private bool TryConvertModuleVarConstantInitializer(HirNode? node, out MirOperand constant)
    {
        switch (node)
        {
            case HirLiteral literal:
                constant = ConvertLiteral(literal);
                return true;

            case HirUnaryOp
            {
                Operator: Eidosc.Hir.UnaryOp.Neg,
                Operand: HirLiteral negatedLiteral
            } unaryOp:
            {
                var operand = ConvertLiteral(negatedLiteral);
                if (operand is not MirConstant { Value: var value } baseConstant)
                {
                    constant = null!;
                    return false;
                }

                constant = value switch
                {
                    MirConstantValue.IntValue intValue =>
                        new MirConstant
                        {
                            Value = new MirConstantValue.IntValue(unchecked(-intValue.Value)),
                            TypeId = unaryOp.TypeId.IsValid ? unaryOp.TypeId : baseConstant.TypeId,
                            Span = unaryOp.Span
                        },
                    MirConstantValue.FloatValue floatValue =>
                        new MirConstant
                        {
                            Value = new MirConstantValue.FloatValue(-floatValue.Value),
                            TypeId = unaryOp.TypeId.IsValid ? unaryOp.TypeId : baseConstant.TypeId,
                            Span = unaryOp.Span
                        },
                    _ => null!
                };
                return constant != null;
            }

            default:
                constant = null!;
                return false;
        }
    }
}

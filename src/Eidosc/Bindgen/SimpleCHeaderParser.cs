using System.Text.RegularExpressions;

namespace Eidosc.Bindgen;

public sealed partial class SimpleCHeaderParser
{
    public CHeaderIr Parse(string headerPath)
    {
        if (!File.Exists(headerPath))
            throw new FileNotFoundException($"Header file not found: {headerPath}", headerPath);

        var source = StripComments(File.ReadAllText(headerPath));
        var functions = new List<CBindingFunction>();
        var structs = ParseStructs(source);
        var enums = ParseEnums(source);

        foreach (Match match in FunctionPattern().Matches(source))
        {
            var returnType = NormalizeWhitespace(match.Groups["ret"].Value);
            var name = match.Groups["name"].Value;
            var parametersText = NormalizeWhitespace(match.Groups["params"].Value);
            if (IsControlKeyword(name))
                continue;

            var parameters = ParseParameters(parametersText, out var isVariadic);
            functions.Add(new CBindingFunction(
                name,
                ParseType(returnType),
                parameters,
                isVariadic));
        }

        return new CHeaderIr(headerPath, functions, structs, enums);
    }

    private static List<CBindingStruct> ParseStructs(string source)
    {
        var structs = new List<CBindingStruct>();
        foreach (Match match in StructPattern().Matches(source))
        {
            var name = FirstNonEmpty(match.Groups["typedef"].Value, match.Groups["tag"].Value);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var fields = new List<CBindingField>();
            foreach (var rawField in match.Groups["body"].Value.Split(';'))
            {
                var fieldText = NormalizeWhitespace(rawField);
                if (fieldText.Length == 0)
                    continue;

                var parsed = ParseTypedName(fieldText);
                if (parsed != null)
                    fields.Add(new CBindingField(parsed.Value.Name, ParseType(parsed.Value.TypeText)));
            }

            structs.Add(new CBindingStruct(name, fields));
        }

        return structs;
    }

    private static List<CBindingEnum> ParseEnums(string source)
    {
        var enums = new List<CBindingEnum>();
        foreach (Match match in EnumPattern().Matches(source))
        {
            var name = FirstNonEmpty(match.Groups["typedef"].Value, match.Groups["tag"].Value);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var values = new List<CBindingEnumValue>();
            long nextValue = 0;
            foreach (var rawValue in match.Groups["body"].Value.Split(','))
            {
                var valueText = NormalizeWhitespace(rawValue);
                if (valueText.Length == 0)
                    continue;

                var parts = valueText.Split('=', 2);
                var valueName = parts[0].Trim();
                if (parts.Length == 2 && long.TryParse(parts[1].Trim(), out var explicitValue))
                    nextValue = explicitValue;

                values.Add(new CBindingEnumValue(valueName, nextValue));
                nextValue++;
            }

            enums.Add(new CBindingEnum(name, values));
        }

        return enums;
    }

    private static IReadOnlyList<CBindingParameter> ParseParameters(string parametersText, out bool isVariadic)
    {
        isVariadic = false;
        if (string.IsNullOrWhiteSpace(parametersText) || parametersText == "void")
            return [];

        var parameters = new List<CBindingParameter>();
        var index = 0;
        foreach (var rawParameter in parametersText.Split(','))
        {
            var parameterText = NormalizeWhitespace(rawParameter);
            if (parameterText == "...")
            {
                isVariadic = true;
                continue;
            }

            var parsed = ParseTypedName(parameterText);
            if (parsed == null)
            {
                parameters.Add(new CBindingParameter($"arg{index}", ParseType(parameterText)));
            }
            else
            {
                parameters.Add(new CBindingParameter(parsed.Value.Name, ParseType(parsed.Value.TypeText)));
            }

            index++;
        }

        return parameters;
    }

    private static (string TypeText, string Name)? ParseTypedName(string text)
    {
        var normalized = NormalizeWhitespace(text.Replace(" *", "*", StringComparison.Ordinal));
        var match = TypedNamePattern().Match(normalized);
        if (!match.Success)
            return null;

        var typeText = NormalizeWhitespace(match.Groups["type"].Value);
        var pointerPrefix = match.Groups["ptr"].Value;
        var name = match.Groups["name"].Value;
        if (pointerPrefix.Length > 0)
            typeText = NormalizeWhitespace($"{typeText} {pointerPrefix}");

        return (typeText, name);
    }

    private static CBindingType ParseType(string text)
    {
        var spelling = NormalizeWhitespace(text)
            .Replace("const ", "", StringComparison.Ordinal)
            .Replace(" volatile", "", StringComparison.Ordinal)
            .Trim();
        var isConst = text.Contains("const", StringComparison.Ordinal);
        var pointerDepth = spelling.Count(static ch => ch == '*');
        var baseName = spelling.Replace("*", "", StringComparison.Ordinal).Trim();

        if (pointerDepth > 0)
            return new CBindingType(CBindingTypeKind.Pointer, baseName, spelling, IsConst: isConst, PointerDepth: pointerDepth);

        if (baseName == "void")
            return new CBindingType(CBindingTypeKind.Void, "void", spelling);

        if (baseName.StartsWith("struct ", StringComparison.Ordinal))
            return new CBindingType(CBindingTypeKind.Struct, baseName["struct ".Length..], spelling);

        if (baseName.StartsWith("enum ", StringComparison.Ordinal))
            return new CBindingType(CBindingTypeKind.Enum, baseName["enum ".Length..], spelling);

        var primitive = baseName switch
        {
            "_Bool" or "bool" or "char" or "signed char" or "unsigned char" or "short" or "unsigned short" or
                "int" or "unsigned int" or "long" or "unsigned long" or "long long" or "unsigned long long" or
                "int8_t" or "int16_t" or "int32_t" or "int64_t" or "uint8_t" or "uint16_t" or "uint32_t" or
                "uint64_t" or "size_t" or "uintptr_t" or "float" or "double" => true,
            _ => false
        };

        if (primitive)
            return new CBindingType(CBindingTypeKind.Primitive, baseName, spelling, baseName.Contains("unsigned", StringComparison.Ordinal) || baseName.StartsWith('u'));

        return new CBindingType(CBindingTypeKind.Typedef, baseName, spelling);
    }

    private static string StripComments(string text)
    {
        var withoutBlocks = BlockCommentPattern().Replace(text, " ");
        return LineCommentPattern().Replace(withoutBlocks, " ");
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string FirstNonEmpty(string first, string second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : second.Trim();

    private static bool IsControlKeyword(string name) =>
        name is "if" or "for" or "while" or "switch" or "return" or "sizeof";

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentPattern();

    [GeneratedRegex(@"//.*?$", RegexOptions.Multiline)]
    private static partial Regex LineCommentPattern();

    [GeneratedRegex(@"(?<ret>[A-Za-z_][A-Za-z0-9_\s\*]*?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<params>[^;{}]*)\)\s*;", RegexOptions.Multiline)]
    private static partial Regex FunctionPattern();

    [GeneratedRegex(@"typedef\s+struct\s+(?<tag>[A-Za-z_][A-Za-z0-9_]*)?\s*\{(?<body>.*?)\}\s*(?<typedef>[A-Za-z_][A-Za-z0-9_]*)?\s*;", RegexOptions.Singleline)]
    private static partial Regex StructPattern();

    [GeneratedRegex(@"typedef\s+enum\s+(?<tag>[A-Za-z_][A-Za-z0-9_]*)?\s*\{(?<body>.*?)\}\s*(?<typedef>[A-Za-z_][A-Za-z0-9_]*)?\s*;", RegexOptions.Singleline)]
    private static partial Regex EnumPattern();

    [GeneratedRegex(@"^(?<type>.+?)\s+(?<ptr>\*+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\[[^\]]*\])?$")]
    private static partial Regex TypedNamePattern();
}

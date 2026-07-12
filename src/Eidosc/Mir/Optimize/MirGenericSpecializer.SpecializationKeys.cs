using System.Text;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private readonly struct SpecializationSignatureKey : IEquatable<SpecializationSignatureKey>
    {
        private readonly int[] _parameterTypeValues;
        private readonly int _hashCode;

        public SpecializationSignatureKey(int returnTypeValue, int[] parameterTypeValues)
        {
            ReturnTypeValue = returnTypeValue;
            _parameterTypeValues = parameterTypeValues;

            var hash = new HashCode();
            hash.Add(returnTypeValue);
            for (var i = 0; i < parameterTypeValues.Length; i++)
            {
                hash.Add(parameterTypeValues[i]);
            }

            _hashCode = hash.ToHashCode();
        }

        public int ReturnTypeValue { get; }

        public ReadOnlySpan<int> ParameterTypeValues => _parameterTypeValues;

        public bool Equals(SpecializationSignatureKey other)
        {
            return ReturnTypeValue == other.ReturnTypeValue &&
                   ParameterTypeValues.SequenceEqual(other.ParameterTypeValues);
        }

        public override bool Equals(object? obj)
        {
            return obj is SpecializationSignatureKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _hashCode;
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append(ReturnTypeValue);
            builder.Append('|');
            for (var i = 0; i < _parameterTypeValues.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(_parameterTypeValues[i]);
            }

            return builder.ToString();
        }
    }

    private readonly record struct SpecializationCacheKey(
        string TemplateKey,
        SpecializationSignatureKey SignatureKey);

    private SpecializationCacheKey CreateSpecializationCacheKey(
        TemplateInfo template,
        SpecializationSignature signature)
    {
        return new SpecializationCacheKey(template.Key, signature.ToKey());
    }
}

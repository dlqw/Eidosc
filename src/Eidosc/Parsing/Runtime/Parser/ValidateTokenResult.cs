namespace Eidosc;

#pragma warning disable CS8524
public readonly ref struct ValidateTokenResult
{
    private enum ResultType
    {
        Success,
        Error,
        Reject
    }

    private readonly ResultType _resultType;
    private readonly string? _errorMessage;

    private ValidateTokenResult(ResultType resultType, string? errorMessage = null)
    {
        _resultType = resultType;
        _errorMessage = errorMessage;
    }

    public static ValidateTokenResult Success()
    {
        return new ValidateTokenResult(ResultType.Success);
    }

    public static ValidateTokenResult Error(string errorMessage)
    {
        return new ValidateTokenResult(ResultType.Error, errorMessage);
    }

    public static ValidateTokenResult Reject(string errorMessage)
    {
        return new ValidateTokenResult(ResultType.Reject);
    }


    /// not pure - change ref token
    public void Apply(ref Token? token, LexerContext context)
    {
        if (token == null) return;
        token = _resultType switch
        {
            ResultType.Success => token,
            ResultType.Error => Token.CreateErrorToken(context.Source, _errorMessage!),
            ResultType.Reject => null,
        };
    }
}